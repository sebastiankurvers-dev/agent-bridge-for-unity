using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge
{
    // HTTP bridge between external MCP clients and Unity Editor operations.
    [InitializeOnLoad]
    public static partial class UnityAgentBridgeServer
    {
        private static HttpListener _listener;
        private static Thread _listenerThread;
        private static Thread _keepAliveThread;
        private static volatile bool _isRunning;
        private static volatile bool _keepAliveThreadRunning;
        private const int Port = 5847;
        private const string Prefix = "http://127.0.0.1:5847/";

        // Compilation state tracking
        private static bool _isCompiling = false;
        private static List<CompilationErrorInfo> _lastCompilationErrors = new List<CompilationErrorInfo>();
        private static readonly object _compilationLock = new object();
        private static DateTime? _lastCompilationTime = null;

        // Event stream buffer
        private static readonly List<UnityCommands.UnityEvent> _eventBuffer = new List<UnityCommands.UnityEvent>();
        private static readonly object _eventLock = new object();
        private static int _nextEventId = 1;
        private const int MaxEvents = 200;
        private static readonly ManualResetEventSlim _eventSignal = new ManualResetEventSlim(false);

        // Main-thread work queues (cancellable items to prevent timeout-orphaned execution)
        private static readonly ConcurrentQueue<MainThreadWorkItem> _mainThreadQueue = new ConcurrentQueue<MainThreadWorkItem>();
        private static readonly ConcurrentQueue<MainThreadWorkItem> _readQueue = new ConcurrentQueue<MainThreadWorkItem>();
        private static long _nextWorkItemId = 0;
        private static long _timedOutRequestCount = 0;
        private static long _canceledBeforeExecutionCount = 0;
        private static long _completedAfterTimeoutCount = 0;
        private static long _activeMainThreadRequests = 0;

        // Per-request context (set at RouteRequest entry, read by RunOnMainThread)
        [ThreadStatic] private static string _currentRoute;
        [ThreadStatic] private static bool _currentDebug;

        // Security: auth, allowlist, audit log (opt-in via env vars)
        private static HashSet<string> _allowedRoutes;
        private static StreamWriter _auditLog;
        private static readonly object _auditLogLock = new object();

        // Keepalive / tick tracking
        private static DateTime _serverStartTime;
        private static DateTime _lastTickTime;
        private static bool _ensureServerRetryScheduled;
        private static double _ensureServerRetryAt;
        private static bool _cachedIsPlaying;
        private static bool _cachedIsPlayModeTransitioning;
        private static int _domainReloadCount;

        public static bool IsRunning => _isRunning;
        public static int CurrentPort => Port;
        public static int PendingQueueSize => _mainThreadQueue.Count;
        public static int ActiveMainThreadRequests => Math.Max(0, (int)Interlocked.Read(ref _activeMainThreadRequests) - 1);
        public static int TimedOutRequestCount => (int)Interlocked.Read(ref _timedOutRequestCount);
        public static int DomainReloadCount => _domainReloadCount;
        public static float ServerUptimeSeconds => (float)(DateTime.UtcNow - _serverStartTime).TotalSeconds;
        public static event Action<bool> OnConnectionStateChanged;

        private sealed class MainThreadWorkItem
        {
            public long id;
            public string route;
            public Func<string> work;
            public TaskCompletionSource<MainThreadWorkResult> completion;
            public DateTime enqueuedAtUtc;
            public volatile bool canceled;
            public volatile bool started;
        }

        private sealed class MainThreadWorkResult
        {
            public string result;
            public Exception error;
        }

        // Compilation state accessors
        public static bool IsCompiling => _isCompiling;
        public static List<CompilationErrorInfo> LastCompilationErrors
        {
            get { lock (_compilationLock) { return new List<CompilationErrorInfo>(_lastCompilationErrors); } }
        }
        public static DateTime? LastCompilationTime => _lastCompilationTime;

        static UnityAgentBridgeServer()
        {
            _domainReloadCount = SessionState.GetInt("AgentBridge.DomainReloadCount", 0) + 1;
            SessionState.SetInt("AgentBridge.DomainReloadCount", _domainReloadCount);

            // Start immediately on domain load so bridge availability does not depend on editor focus.
            ScheduleEnsureServerRunning(0);
            // Retry once on delayCall in case Unity is still initializing editor services.
            EditorApplication.delayCall += () => ScheduleEnsureServerRunning(0);
            EditorApplication.quitting += StopServer;

            // Process main-thread work queue every editor tick
            EditorApplication.update += ProcessMainThreadQueue;
            EditorApplication.update += ProcessEnsureServerRetry;

            // Subscribe to compilation events
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;

            // Subscribe to play mode and scene change events
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;

            // Attribute-based route discovery
            BridgeRouter.Initialize();

            // Security: load allowlist if configured
            string allowlistPath = System.Environment.GetEnvironmentVariable("BRIDGE_ALLOWLIST_FILE");
            if (!string.IsNullOrEmpty(allowlistPath) && System.IO.File.Exists(allowlistPath))
            {
                try
                {
                    string allowlistJson = System.IO.File.ReadAllText(allowlistPath);
                    var parsed = MiniJSON.Json.Deserialize(allowlistJson) as Dictionary<string, object>;
                    _allowedRoutes = ParseAllowlist(parsed);
                    Debug.Log($"[AgentBridge] Loaded allowlist from {allowlistPath}: {_allowedRoutes.Count} routes");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[AgentBridge] Failed to load allowlist: {ex.Message}");
                }
            }

            // Security: open audit log if configured
            string auditPath = System.Environment.GetEnvironmentVariable("BRIDGE_AUDIT_LOG");
            if (!string.IsNullOrEmpty(auditPath))
            {
                try
                {
                    _auditLog = new StreamWriter(auditPath, append: true) { AutoFlush = true };
                    Debug.Log($"[AgentBridge] Audit log opened: {auditPath}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[AgentBridge] Failed to open audit log: {ex.Message}");
                }
            }
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            ScheduleEnsureServerRunning(250);
        }

        private static void ScheduleEnsureServerRunning(int delayMs)
        {
            if (delayMs < 0) delayMs = 0;
            var targetTime = EditorApplication.timeSinceStartup + (delayMs / 1000.0);
            if (_ensureServerRetryScheduled && _ensureServerRetryAt <= targetTime)
            {
                return;
            }

            _ensureServerRetryScheduled = true;
            _ensureServerRetryAt = targetTime;
        }

        private static void ProcessEnsureServerRetry()
        {
            if (!_ensureServerRetryScheduled)
                return;

            if (EditorApplication.timeSinceStartup < _ensureServerRetryAt)
                return;

            _ensureServerRetryScheduled = false;
            EnsureServerRunning();
        }

        private static void EnsureServerRunning()
        {
            if (_isRunning)
            {
                return;
            }

            if (EditorApplication.isCompiling)
            {
                ScheduleEnsureServerRunning(500);
                return;
            }

            StartServer();
            if (!_isRunning)
            {
                ScheduleEnsureServerRunning(1000);
            }
        }

        private static void ProcessMainThreadQueue()
        {
            _lastTickTime = DateTime.UtcNow;

            // Auto-save dirty scenes before processing write operations to prevent
            // "Scene(s) Have Been Modified" modal dialogs from blocking the main thread.
            // Only saves when there are pending writes AND a scene is actually dirty.
            if (!_mainThreadQueue.IsEmpty)
            {
                var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (activeScene.isDirty)
                {
                    EditorSceneManager.SaveOpenScenes();
                }
            }

            // Phase 1: batch reads (up to 8 per tick)
            int readBudget = 8;
            while (readBudget > 0 && _readQueue.TryDequeue(out var readItem))
            {
                ExecuteWorkItem(readItem);
                readBudget--;
            }

            // Phase 2: one write
            if (_mainThreadQueue.TryDequeue(out var writeItem))
            {
                ExecuteWorkItem(writeItem);
            }
        }

        private static void ExecuteWorkItem(MainThreadWorkItem item)
        {
            try
            {
                if (item == null) return;

                if (item.canceled && !item.started)
                {
                    item.completion.TrySetResult(new MainThreadWorkResult
                    {
                        result = null,
                        error = new TimeoutException("Canceled before execution")
                    });
                    return;
                }

                item.started = true;
                string result = item.work?.Invoke();
                if (item.canceled)
                {
                    Interlocked.Increment(ref _completedAfterTimeoutCount);
                }

                item.completion.TrySetResult(new MainThreadWorkResult
                {
                    result = result,
                    error = null
                });
            }
            catch (Exception ex)
            {
                if (item != null)
                {
                    item.completion.TrySetResult(new MainThreadWorkResult
                    {
                        result = null,
                        error = ex
                    });
                }
                else
                {
                    Debug.LogError($"[AgentBridge] Main thread work item failed: {ex.Message}");
                }
            }
        }

        private static void OnCompilationStarted(object context)
        {
            _isCompiling = true;
            lock (_compilationLock) { _lastCompilationErrors.Clear(); }
            PushEvent("compilation_started", "{}");
        }

        private static void OnCompilationFinished(object context)
        {
            _isCompiling = false;
            _lastCompilationTime = DateTime.Now;
            int errorCount;
            lock (_compilationLock) { errorCount = _lastCompilationErrors.Count; }
            var payload = new Dictionary<string, object>
            {
                { "errorCount", errorCount }
            };
            PushEvent("compilation_finished", MiniJSON.Json.Serialize(payload));
            ScheduleEnsureServerRunning(250);
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            foreach (var msg in messages)
            {
                if (msg.type == CompilerMessageType.Error)
                {
                    lock (_compilationLock)
                    {
                        _lastCompilationErrors.Add(new CompilationErrorInfo
                        {
                            message = msg.message,
                            file = msg.file,
                            line = msg.line,
                            column = msg.column
                        });
                    }

                    var payload = new Dictionary<string, object>
                    {
                        { "message", msg.message },
                        { "file", msg.file },
                        { "line", msg.line },
                        { "column", msg.column }
                    };
                    PushEvent("compilation_error", MiniJSON.Json.Serialize(payload));
                }
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    _cachedIsPlaying = true;
                    _cachedIsPlayModeTransitioning = false;
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                case PlayModeStateChange.ExitingEditMode:
                    _cachedIsPlayModeTransitioning = true;
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    _cachedIsPlaying = false;
                    _cachedIsPlayModeTransitioning = false;
                    break;
            }

            var payload = new Dictionary<string, object>
            {
                { "state", state.ToString() }
            };
            PushEvent("play_mode_changed", MiniJSON.Json.Serialize(payload));
        }

        private static void OnActiveSceneChanged(Scene oldScene, Scene newScene)
        {
            var payload = new Dictionary<string, object>
            {
                { "oldScene", oldScene.name },
                { "newScene", newScene.name },
                { "newScenePath", newScene.path }
            };
            PushEvent("scene_changed", MiniJSON.Json.Serialize(payload));
        }

        /// <summary>
        /// Push an event into the event buffer. Thread-safe.
        /// </summary>
        public static void PushEvent(string type, string data)
        {
            lock (_eventLock)
            {
                var evt = new UnityCommands.UnityEvent
                {
                    id = _nextEventId++,
                    type = type,
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    data = data
                };
                _eventBuffer.Add(evt);

                while (_eventBuffer.Count > MaxEvents)
                {
                    _eventBuffer.RemoveAt(0);
                }
            }
            _eventSignal.Set();
        }

        /// <summary>
        /// Get events since a given ID, with optional long-poll timeout.
        /// Runs on ThreadPool — does not require main thread.
        /// </summary>
        private static string GetEvents(int sinceId, int timeoutMs, bool includeStackTrace)
        {
            // First check if events are already available
            lock (_eventLock)
            {
                var immediate = CollectEventsSince(sinceId);
                if (immediate.Count > 0)
                    return BuildEventsJson(immediate, sinceId, includeStackTrace);
            }

            // Wait for signal or timeout
            if (timeoutMs > 0)
            {
                _eventSignal.Wait(timeoutMs);
            }

            // Collect whatever is available (reset inside lock to avoid lost-wakeup race)
            lock (_eventLock)
            {
                _eventSignal.Reset();
                var results = CollectEventsSince(sinceId);
                return BuildEventsJson(results, sinceId, includeStackTrace);
            }
        }

        private static List<UnityCommands.UnityEvent> CollectEventsSince(int sinceId)
        {
            var results = new List<UnityCommands.UnityEvent>();
            foreach (var evt in _eventBuffer)
            {
                if (evt.id > sinceId)
                    results.Add(evt);
            }
            return results;
        }

        private static string BuildEventsJson(List<UnityCommands.UnityEvent> events, int sinceId, bool includeStackTrace)
        {
            // Deduplicate consecutive identical events (same type + data) into a single entry with repeatCount.
            // This prevents log spam (e.g., Input System errors during play mode) from generating 17k+ tokens.
            var serializedEvents = new List<object>();
            string prevKey = null;
            Dictionary<string, object> prevEntry = null;
            int repeatCount = 0;

            foreach (var evt in events)
            {
                string key = (evt.type ?? "") + "||" + (evt.data ?? "");
                if (key == prevKey && prevEntry != null)
                {
                    repeatCount++;
                    prevEntry["repeatCount"] = repeatCount;
                    prevEntry["lastId"] = evt.id;
                    prevEntry["lastTimestamp"] = evt.timestamp ?? string.Empty;
                }
                else
                {
                    var entry = new Dictionary<string, object>
                    {
                        { "id", evt.id },
                        { "type", evt.type ?? string.Empty },
                        { "timestamp", evt.timestamp ?? string.Empty },
                        { "data", DeserializeEventData(evt, includeStackTrace) }
                    };
                    serializedEvents.Add(entry);
                    prevEntry = entry;
                    prevKey = key;
                    repeatCount = 1;
                }
            }

            return MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "events", serializedEvents },
                { "lastId", events.Count > 0 ? events[events.Count - 1].id : Math.Max(0, sinceId) }
            });
        }

        private static object DeserializeEventData(UnityCommands.UnityEvent evt, bool includeStackTrace)
        {
            if (string.IsNullOrWhiteSpace(evt?.data))
                return string.Empty;

            var raw = evt.data;
            var trimmed = raw.TrimStart();
            if (!trimmed.StartsWith("{") && !trimmed.StartsWith("["))
                return raw;

            try
            {
                var parsed = MiniJSON.Json.Deserialize(raw);
                if (parsed is Dictionary<string, object> payload
                    && !includeStackTrace
                    && string.Equals(evt.type, "log_error", StringComparison.OrdinalIgnoreCase)
                    && payload.ContainsKey("stackTrace"))
                {
                    payload.Remove("stackTrace");
                    payload["stackTraceOmitted"] = true;
                }

                return parsed ?? raw;
            }
            catch
            {
                return raw;
            }
        }

        public static void StartServer()
        {
            if (_isRunning) return;

            // Ensure log callback is registered on the main thread
            UnityCommands.EnsureLogCallback();

            // Keep Unity running when backgrounded so EditorApplication.update keeps firing
            Application.runInBackground = true;
            _cachedIsPlaying = EditorApplication.isPlaying;
            _cachedIsPlayModeTransitioning = EditorApplication.isPlayingOrWillChangePlaymode && !_cachedIsPlaying;

            _serverStartTime = DateTime.UtcNow;
            _lastTickTime = DateTime.UtcNow;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add(Prefix);
                _listener.Start();
                _isRunning = true;
                StartKeepAliveThread();

                _listenerThread = new Thread(ListenForRequests)
                {
                    IsBackground = true,
                    Name = "AgentBridge HTTP Server"
                };
                _listenerThread.Start();

                Debug.Log($"[AgentBridge] Server started on port {Port}");
                _ensureServerRetryScheduled = false;
                OnConnectionStateChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentBridge] Failed to start server: {ex.Message}");
                _isRunning = false;
                ScheduleEnsureServerRunning(1000);
                OnConnectionStateChanged?.Invoke(false);
            }
        }

        public static void StopServer()
        {
            if (!_isRunning) return;

            try
            {
                _isRunning = false;
                StopKeepAliveThread();

                _auditLog?.Dispose();
                _auditLog = null;

                _listener?.Stop();
                _listener?.Close();
                _listenerThread?.Join(1000);
                Debug.Log("[AgentBridge] Server stopped");
                OnConnectionStateChanged?.Invoke(false);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentBridge] Error stopping server: {ex.Message}");
            }
        }

        private static void StartKeepAliveThread()
        {
            if (_keepAliveThreadRunning)
            {
                return;
            }

            _keepAliveThreadRunning = true;
            _keepAliveThread = new Thread(() =>
            {
                while (_keepAliveThreadRunning)
                {
                    Thread.Sleep(50);
                    if (_isRunning && (_mainThreadQueue.Count > 0 || _readQueue.Count > 0))
                    {
                        EditorApplication.delayCall += ProcessMainThreadQueue;
                    }
                }
            })
            {
                IsBackground = true,
                Name = "AgentBridge Keepalive"
            };
            _keepAliveThread.Start();
        }

        private static void StopKeepAliveThread()
        {
            _keepAliveThreadRunning = false;
            try
            {
                _keepAliveThread?.Join(500);
            }
            catch { }
            _keepAliveThread = null;
        }

    }
}
