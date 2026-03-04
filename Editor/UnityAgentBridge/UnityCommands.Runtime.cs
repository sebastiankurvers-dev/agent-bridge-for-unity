using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Profiling;
using Unity.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Audio;
using TMPro;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        public static string GetConsoleLogs(int count, string typeFilter = null, string textFilter = null, bool includeStackTrace = false)
        {
            lock (_logBuffer)
            {
                IEnumerable<LogEntry> filtered = _logBuffer;

                if (!string.IsNullOrEmpty(typeFilter))
                {
                    filtered = filtered.Where(l => string.Equals(l.type, typeFilter, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrEmpty(textFilter))
                {
                    filtered = filtered.Where(l => l.message != null && l.message.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) >= 0);
                }

                var filteredList = filtered.ToList();
                var logs = filteredList.Skip(Math.Max(0, filteredList.Count - count)).ToList();
                var serializedLogs = new List<object>(logs.Count);
                foreach (var log in logs)
                {
                    var entry = new Dictionary<string, object>
                    {
                        { "message", log.message ?? string.Empty },
                        { "type", log.type ?? string.Empty },
                        { "timestamp", log.timestamp ?? string.Empty }
                    };

                    if (includeStackTrace && !string.IsNullOrEmpty(log.stackTrace))
                    {
                        entry["stackTrace"] = log.stackTrace;
                    }

                    serializedLogs.Add(entry);
                }

                return JsonResult(new Dictionary<string, object>
                {
                    { "logs", serializedLogs }
                });
            }
        }

        [BridgeRoute("DELETE", "/console", Category = "debug", Description = "Clear console", Direct = true)]
        public static string ClearConsoleLogs()
        {
            lock (_logBuffer)
            {
                int cleared = _logBuffer.Count;
                _logBuffer.Clear();
                return JsonResult(new Dictionary<string, object> { { "success", true }, { "clearedCount", cleared } });
            }
        }


        [BridgeRoute("POST", "/playmode", Category = "runtime", Description = "Control play mode")]
        public static string ControlPlayMode(string jsonData)
        {
            var request = JsonUtility.FromJson<PlayModeRequest>(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData);
            string action = request?.action?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
            {
                return JsonError("action is required");
            }

            bool beforePlaying = EditorApplication.isPlaying;
            bool beforePaused = EditorApplication.isPaused;
            bool beforeWillChange = EditorApplication.isPlayingOrWillChangePlaymode;

            bool success = true;
            bool applied = false;
            bool transitionPending = false;
            string transitionOutcome;
            string reason = null;

            switch (action)
            {
                case "enter":
                case "start":
                case "play":
                    if (beforePlaying)
                    {
                        transitionOutcome = "noop_already_playing";
                        reason = "Editor is already in play mode.";
                        break;
                    }

                    // Auto-save open scenes to prevent "Scene(s) Have Been Modified" modal dialog
                    EditorSceneManager.SaveOpenScenes();

                    EditorApplication.isPlaying = true;
                    if (EditorApplication.isPlaying)
                    {
                        applied = true;
                        transitionOutcome = "entered_play_mode";
                    }
                    else if (EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        transitionPending = true;
                        transitionOutcome = "transition_pending";
                        reason = "Play mode transition requested; editor has not entered play mode yet.";
                    }
                    else
                    {
                        success = false;
                        transitionOutcome = "play_request_not_applied";
                        reason = "Play mode request was not applied (isPlaying=false and no transition pending).";
                    }
                    break;

                case "exit":
                case "stop":
                    if (!beforePlaying && !beforeWillChange)
                    {
                        transitionOutcome = "noop_already_stopped";
                        reason = "Editor is already out of play mode.";
                        break;
                    }

                    EditorApplication.isPlaying = false;
                    if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        applied = true;
                        transitionOutcome = "exited_play_mode";
                    }
                    else if (EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        transitionPending = true;
                        transitionOutcome = "transition_pending";
                        reason = "Stop requested; editor is still transitioning play mode.";
                    }
                    else if (beforePlaying && EditorApplication.isPlaying)
                    {
                        transitionPending = true;
                        transitionOutcome = "transition_pending";
                        reason = "Stop requested; editor has not exited play mode on this tick yet.";
                    }
                    else
                    {
                        success = false;
                        transitionOutcome = "stop_request_not_applied";
                        reason = "Stop request was not applied (editor remained in play mode).";
                    }
                    break;

                case "pause":
                    if (beforePaused)
                    {
                        transitionOutcome = "noop_already_paused";
                        reason = "Editor is already paused.";
                        break;
                    }

                    EditorApplication.isPaused = true;
                    if (EditorApplication.isPaused)
                    {
                        applied = true;
                        transitionOutcome = "paused";
                    }
                    else
                    {
                        success = false;
                        transitionOutcome = "pause_request_not_applied";
                        reason = "Pause request was not applied.";
                    }
                    break;

                case "unpause":
                case "resume":
                    if (!beforePaused)
                    {
                        transitionOutcome = "noop_already_unpaused";
                        reason = "Editor is already unpaused.";
                        break;
                    }

                    EditorApplication.isPaused = false;
                    if (!EditorApplication.isPaused)
                    {
                        applied = true;
                        transitionOutcome = "unpaused";
                    }
                    else
                    {
                        success = false;
                        transitionOutcome = "resume_request_not_applied";
                        reason = "Resume request was not applied.";
                    }
                    break;

                case "step":
                    if (!EditorApplication.isPlaying)
                    {
                        success = false;
                        transitionOutcome = "step_requires_play_mode";
                        reason = "Step requires play mode to be active.";
                        break;
                    }

                    EditorApplication.Step();
                    applied = true;
                    transitionOutcome = "step_requested";
                    break;

                default:
                    return JsonError($"Unknown action: {request.action}");
            }

            bool afterPlaying = EditorApplication.isPlaying;
            bool afterPaused = EditorApplication.isPaused;
            bool afterWillChange = EditorApplication.isPlayingOrWillChangePlaymode;

            var response = new Dictionary<string, object>
            {
                { "success", success },
                { "requestedAction", action },
                { "transitionOutcome", transitionOutcome },
                { "applied", applied },
                { "transitionPending", transitionPending },
                { "isPlaying", afterPlaying },
                { "isPaused", afterPaused },
                { "beforeState", new Dictionary<string, object>
                    {
                        { "isPlaying", beforePlaying },
                        { "isPaused", beforePaused },
                        { "isPlayingOrWillChangePlaymode", beforeWillChange }
                    }
                },
                { "afterState", new Dictionary<string, object>
                    {
                        { "isPlaying", afterPlaying },
                        { "isPaused", afterPaused },
                        { "isPlayingOrWillChangePlaymode", afterWillChange }
                    }
                }
            };

            if (!string.IsNullOrWhiteSpace(reason))
            {
                response["reason"] = reason;
            }

            return JsonResult(response);
        }


        /// <summary>
        /// Core reflection-based method invocation. Shared by InvokeMethod and InvokeSequence.
        /// Returns a result dict on success, or an error string on failure.
        /// </summary>
        private static (Dictionary<string, object> result, string error) InvokeMethodCore(
            GameObject go, string componentType, string methodName, string[] args)
        {
            var component = FindComponentOnGameObject(go, componentType, out var type);
            if (component == null)
                return (null, $"Component {componentType} not found on {go.name}");

            int argCount = args != null ? args.Length : 0;

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo targetMethod = null;

            foreach (var m in methods)
            {
                if (string.Equals(m.Name, methodName, StringComparison.Ordinal)
                    && m.GetParameters().Length == argCount)
                { targetMethod = m; break; }
            }

            if (targetMethod == null)
            {
                foreach (var m in methods)
                {
                    if (string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase)
                        && m.GetParameters().Length == argCount)
                    { targetMethod = m; break; }
                }
            }

            if (targetMethod == null)
            {
                var noisyBases = new HashSet<string>(new[]
                {
                    "Awake", "Start", "Update", "FixedUpdate", "LateUpdate", "OnEnable", "OnDisable",
                    "OnDestroy", "OnGUI", "OnValidate", "Reset", "OnDrawGizmos", "OnDrawGizmosSelected",
                    "OnApplicationFocus", "OnApplicationPause", "OnApplicationQuit",
                    "OnBecameVisible", "OnBecameInvisible", "OnCollisionEnter", "OnCollisionExit",
                    "OnCollisionStay", "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay",
                    "ToString", "GetHashCode", "Equals", "GetType", "MemberwiseClone",
                    "GetInstanceID", "GetComponent", "GetComponentInChildren", "GetComponentInParent",
                    "GetComponents", "GetComponentsInChildren", "GetComponentsInParent",
                    "CompareTag", "SendMessage", "SendMessageUpwards", "BroadcastMessage",
                    "StartCoroutine", "StopCoroutine", "StopAllCoroutines",
                    "Invoke", "InvokeRepeating", "CancelInvoke", "IsInvoking",
                    "TryGetComponent"
                });

                var availableMethods = new List<string>();
                foreach (var m in methods)
                {
                    if (m.IsSpecialName) continue;
                    if (noisyBases.Contains(m.Name)) continue;
                    if (m.DeclaringType == typeof(MonoBehaviour) || m.DeclaringType == typeof(Behaviour)
                        || m.DeclaringType == typeof(Component) || m.DeclaringType == typeof(UnityEngine.Object)
                        || m.DeclaringType == typeof(object))
                        continue;
                    var pars = m.GetParameters();
                    var parStr = string.Join(", ", Array.ConvertAll(pars, p => $"{p.ParameterType.Name} {p.Name}"));
                    availableMethods.Add($"{m.ReturnType.Name} {m.Name}({parStr})");
                }

                var methodList = availableMethods.Count > 0 ? string.Join(", ", availableMethods) : "(none found)";
                return (null, $"Method '{methodName}' with {argCount} argument(s) not found on {type.Name}. Available methods: {methodList}");
            }

            var parameters = targetMethod.GetParameters();
            object[] convertedArgs = new object[argCount];
            for (int idx = 0; idx < argCount; idx++)
            {
                try { convertedArgs[idx] = ConvertArgument(args[idx], parameters[idx].ParameterType); }
                catch (Exception ex)
                { return (null, $"Failed to convert argument {idx} ('{args[idx]}') to {parameters[idx].ParameterType.Name}: {ex.Message}"); }
            }

            try
            {
                var returnValue = targetMethod.Invoke(component, convertedArgs);
                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "gameObjectName", go.name },
                    { "instanceId", go.GetInstanceID() },
                    { "componentType", type.Name },
                    { "methodName", targetMethod.Name },
                    { "returnType", targetMethod.ReturnType.Name },
                    { "isPlayMode", true }
                };
                if (targetMethod.ReturnType != typeof(void) && returnValue != null)
                    result["returnValueJson"] = SerializeValueToJson(returnValue, targetMethod.ReturnType);
                return (result, null);
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException ?? tie;
                return (null, $"Method threw exception: {inner.GetType().Name}: {inner.Message}");
            }
            catch (Exception ex)
            {
                return (null, $"Failed to invoke method: {ex.Message}");
            }
        }

        /// <summary>
        /// Extract a screenshot handle from a TakeScreenshot result JSON string.
        /// Returns null if no handle found.
        /// </summary>
        private static string ExtractScreenshotHandle(string screenshotJson)
        {
            if (string.IsNullOrEmpty(screenshotJson)) return null;
            try
            {
                var dict = MiniJSON.Json.Deserialize(screenshotJson) as Dictionary<string, object>;
                if (dict != null && dict.TryGetValue("imageHandle", out var handle))
                    return handle as string;
            }
            catch { }
            return null;
        }

        [BridgeRoute("POST", "/runtime/invoke", Category = "runtime", Description = "Invoke method on component during play mode")]
        public static string InvokeMethod(string jsonData)
        {
            var request = JsonUtility.FromJson<InvokeMethodRequest>(jsonData);

            if (!EditorApplication.isPlaying)
                return JsonError("InvokeMethod is only allowed during play mode. Use unity_play_mode(action=\"play\") first.");

            var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            if (go == null)
                return JsonError("GameObject not found");
            if (string.IsNullOrEmpty(request.componentType))
                return JsonError("componentType is required");
            if (string.IsNullOrEmpty(request.methodName))
                return JsonError("methodName is required");

            bool wantBefore = request.screenshotBefore == 1;
            bool wantAfter = request.screenshotAfter == 1;
            string ssView = !string.IsNullOrEmpty(request.screenshotView) ? request.screenshotView : "game";

            // Screenshot before
            string beforeHandle = null;
            if (wantBefore)
            {
                var ssResult = TakeScreenshot(ssView, includeBase64: true, includeHandle: true,
                    source: "invoke_before", requestedWidth: request.screenshotWidth, requestedHeight: request.screenshotHeight);
                beforeHandle = ExtractScreenshotHandle(ssResult);
            }

            // Core invoke
            var (invokeResult, error) = InvokeMethodCore(go, request.componentType, request.methodName, request.args);
            if (error != null)
                return JsonError(error);

            // Screenshot after
            string afterHandle = null;
            if (wantAfter)
            {
                var ssResult = TakeScreenshot(ssView, includeBase64: true, includeHandle: true,
                    source: "invoke_after", requestedWidth: request.screenshotWidth, requestedHeight: request.screenshotHeight);
                afterHandle = ExtractScreenshotHandle(ssResult);
            }

            if (beforeHandle != null) invokeResult["beforeScreenshotHandle"] = beforeHandle;
            if (afterHandle != null) invokeResult["afterScreenshotHandle"] = afterHandle;

            return JsonResult(invokeResult);
        }

        [BridgeRoute("POST", "/runtime/invoke-sequence", Category = "runtime", Description = "Multi-step method invocation with timing", TimeoutDefault = 15000, TimeoutMin = 2000, TimeoutMax = 30000)]
        public static string InvokeSequence(string jsonData)
        {
            var request = JsonUtility.FromJson<InvokeSequenceRequest>(jsonData);

            if (!EditorApplication.isPlaying)
                return JsonError("InvokeSequence is only allowed during play mode. Use unity_play_mode(action=\"play\") first.");

            var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            if (go == null)
                return JsonError("GameObject not found");

            if (string.IsNullOrEmpty(request.stepsJson))
                return JsonError("stepsJson is required");

            // Parse steps from JSON array
            List<InvokeStep> steps;
            try
            {
                var parsed = MiniJSON.Json.Deserialize(request.stepsJson) as List<object>;
                if (parsed == null || parsed.Count == 0)
                    return JsonError("stepsJson must be a non-empty JSON array");
                if (parsed.Count > 20)
                    return JsonError("Maximum 20 steps allowed per invoke sequence");

                steps = new List<InvokeStep>();
                int totalDelayMs = 0;
                foreach (var item in parsed)
                {
                    var stepDict = item as Dictionary<string, object>;
                    if (stepDict == null)
                        return JsonError("Each step must be a JSON object");

                    var step = new InvokeStep();
                    if (stepDict.TryGetValue("componentType", out var ct))
                        step.componentType = ct as string;
                    if (stepDict.TryGetValue("methodName", out var mn))
                        step.methodName = mn as string;
                    if (stepDict.TryGetValue("delayMs", out var dm))
                        step.delayMs = Convert.ToInt32(dm);
                    if (stepDict.TryGetValue("args", out var argsObj) && argsObj is List<object> argsList)
                        step.args = argsList.Select(a => a?.ToString()).ToArray();

                    if (string.IsNullOrEmpty(step.methodName))
                        return JsonError("Each step requires a methodName");

                    totalDelayMs += step.delayMs;
                    steps.Add(step);
                }

                if (totalDelayMs > 10000)
                    return JsonError($"Total delay across steps ({totalDelayMs}ms) exceeds maximum of 10000ms");
            }
            catch (Exception ex)
            {
                return JsonError($"Failed to parse stepsJson: {ex.Message}");
            }

            // Execute steps synchronously with delays
            var stepResults = new List<object>();
            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];

                // Delay before this step
                if (step.delayMs > 0)
                    Thread.Sleep(step.delayMs);

                string compType = !string.IsNullOrEmpty(step.componentType)
                    ? step.componentType
                    : null;

                // If componentType not specified on step, we need it from somewhere
                if (string.IsNullOrEmpty(compType))
                {
                    stepResults.Add(new Dictionary<string, object>
                    {
                        { "stepIndex", i },
                        { "success", false },
                        { "methodName", step.methodName ?? "" },
                        { "error", "componentType is required on each step" }
                    });
                    continue;
                }

                var (result, error) = InvokeMethodCore(go, compType, step.methodName, step.args);
                if (error != null)
                {
                    stepResults.Add(new Dictionary<string, object>
                    {
                        { "stepIndex", i },
                        { "success", false },
                        { "methodName", step.methodName },
                        { "error", error }
                    });
                }
                else
                {
                    result["stepIndex"] = i;
                    stepResults.Add(result);
                }
            }

            var response = new Dictionary<string, object>
            {
                { "success", true },
                { "stepCount", steps.Count },
                { "results", stepResults }
            };

            // Optional screenshot after sequence
            if (!string.IsNullOrEmpty(request.screenshotView))
            {
                var ssResult = TakeScreenshot(request.screenshotView, includeBase64: true, includeHandle: true,
                    source: "invoke_sequence", requestedWidth: request.screenshotWidth, requestedHeight: request.screenshotHeight);
                var handle = ExtractScreenshotHandle(ssResult);
                if (handle != null)
                    response["screenshotHandle"] = handle;
            }

            return JsonResult(response);
        }

        private static object ConvertArgument(string jsonArg, Type targetType)
        {
            if (jsonArg == null || jsonArg == "null") return null;

            // Primitives
            if (targetType == typeof(int)) return int.Parse(jsonArg);
            if (targetType == typeof(float)) return float.Parse(jsonArg, System.Globalization.CultureInfo.InvariantCulture);
            if (targetType == typeof(double)) return double.Parse(jsonArg, System.Globalization.CultureInfo.InvariantCulture);
            if (targetType == typeof(long)) return long.Parse(jsonArg);
            if (targetType == typeof(bool))
            {
                var lower = jsonArg.ToLowerInvariant();
                return lower == "true" || lower == "1";
            }
            if (targetType == typeof(string))
            {
                // Strip surrounding quotes if present
                if (jsonArg.StartsWith("\"") && jsonArg.EndsWith("\""))
                    return jsonArg.Substring(1, jsonArg.Length - 2);
                return jsonArg;
            }

            // Enums
            if (targetType.IsEnum)
            {
                var cleaned = jsonArg.Trim('"');
                return Enum.Parse(targetType, cleaned, true);
            }

            // Vector2
            if (targetType == typeof(Vector2))
            {
                var dict = MiniJSON.Json.Deserialize(jsonArg) as Dictionary<string, object>;
                if (dict != null)
                    return new Vector2(Convert.ToSingle(dict["x"]), Convert.ToSingle(dict["y"]));
            }

            // Vector3
            if (targetType == typeof(Vector3))
            {
                var dict = MiniJSON.Json.Deserialize(jsonArg) as Dictionary<string, object>;
                if (dict != null)
                    return new Vector3(Convert.ToSingle(dict["x"]), Convert.ToSingle(dict["y"]), Convert.ToSingle(dict["z"]));
            }

            // Color
            if (targetType == typeof(Color))
            {
                var dict = MiniJSON.Json.Deserialize(jsonArg) as Dictionary<string, object>;
                if (dict != null)
                {
                    float r = Convert.ToSingle(dict.ContainsKey("r") ? dict["r"] : 0);
                    float g = Convert.ToSingle(dict.ContainsKey("g") ? dict["g"] : 0);
                    float bVal = Convert.ToSingle(dict.ContainsKey("b") ? dict["b"] : 0);
                    float a = Convert.ToSingle(dict.ContainsKey("a") ? dict["a"] : 1);
                    return new Color(r, g, bVal, a);
                }
            }

            // Fallback: try Convert.ChangeType
            return Convert.ChangeType(jsonArg, targetType, System.Globalization.CultureInfo.InvariantCulture);
        }

        #region Compilation Operations

        [BridgeRoute("GET", "/compilation/status", Category = "debug", Description = "Compilation status", Direct = true)]
        public static string GetCompilationStatus()
        {
            return JsonUtility.ToJson(new CompilationStatusResponse
            {
                isCompiling = UnityAgentBridgeServer.IsCompiling,
                lastCompilationTime = UnityAgentBridgeServer.LastCompilationTime?.ToString("o"),
                errorCount = UnityAgentBridgeServer.LastCompilationErrors.Count
            });
        }

        [BridgeRoute("GET", "/editor/runtime", Category = "meta", Description = "Editor runtime state", ReadOnly = true)]
        public static string GetEditorRuntimeState()
        {
            var scene = SceneManager.GetActiveScene();
            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "isCompiling", EditorApplication.isCompiling },
                { "isUpdating", EditorApplication.isUpdating },
                { "isPlaying", EditorApplication.isPlaying },
                { "isPaused", EditorApplication.isPaused },
                { "isPlayingOrWillChangePlaymode", EditorApplication.isPlayingOrWillChangePlaymode },
                { "activeSceneName", scene.name },
                { "activeScenePath", scene.path },
                { "activeSceneDirty", scene.IsValid() && scene.isLoaded && scene.isDirty },
                { "pendingQueueSize", UnityAgentBridgeServer.PendingQueueSize },
                { "activeMainThreadRequests", UnityAgentBridgeServer.ActiveMainThreadRequests },
                { "timedOutRequestCount", UnityAgentBridgeServer.TimedOutRequestCount },
                { "domainReloadCount", UnityAgentBridgeServer.DomainReloadCount },
                { "serverUptimeSeconds", UnityAgentBridgeServer.ServerUptimeSeconds }
            });
        }

        [BridgeRoute("GET", "/compilation/errors", Category = "debug", Description = "Compilation errors", Direct = true)]
        public static string GetCompilationErrors()
        {
            var errors = UnityAgentBridgeServer.LastCompilationErrors;
            return JsonUtility.ToJson(new CompilationErrorsResponse { errors = errors }, false);
        }

        [BridgeRoute("POST", "/compilation/trigger", Category = "debug", Description = "Trigger recompilation")]
        public static string TriggerRecompile(string jsonData = null)
        {
            var requestedPaths = new List<string>();
            var reimportedPaths = new List<object>();
            var skippedPaths = new List<object>();
            bool requestedWaitForCompile = false;
            int requestedMaxWaitMs = 0;
            int requestedPollIntervalMs = 0;

            if (!string.IsNullOrWhiteSpace(jsonData))
            {
                try
                {
                    var parsed = MiniJSON.Json.Deserialize(jsonData) as System.Collections.Generic.Dictionary<string, object>;
                    if (parsed != null)
                    {
                        if (parsed.TryGetValue("forceReimportPath", out var pathVal) && pathVal is string singlePath && !string.IsNullOrWhiteSpace(singlePath))
                            requestedPaths.Add(singlePath.Trim());

                        if (parsed.TryGetValue("forceReimportPaths", out var pathListObj) && pathListObj is List<object> pathList)
                        {
                            foreach (var item in pathList)
                            {
                                var path = item as string;
                                if (!string.IsNullOrWhiteSpace(path))
                                    requestedPaths.Add(path.Trim());
                            }
                        }

                        requestedWaitForCompile = ReadBool(parsed, "waitForCompile", false);
                        if (TryReadInt(parsed, "maxWaitMs", out var waitMs))
                            requestedMaxWaitMs = waitMs;
                        if (TryReadInt(parsed, "pollIntervalMs", out var pollMs))
                            requestedPollIntervalMs = pollMs;
                    }
                }
                catch { }
            }

            var uniquePaths = requestedPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var refreshFlags = ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport;
            foreach (var path in uniquePaths)
            {
                if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    skippedPaths.Add(new Dictionary<string, object>
                    {
                        { "path", path },
                        { "reason", "Path must be under Assets/" }
                    });
                    continue;
                }

                if (!System.IO.File.Exists(path))
                {
                    skippedPaths.Add(new Dictionary<string, object>
                    {
                        { "path", path },
                        { "reason", "File does not exist" }
                    });
                    continue;
                }

                try
                {
                    AssetDatabase.ImportAsset(path, refreshFlags);
                    reimportedPaths.Add(path);
                }
                catch (Exception ex)
                {
                    skippedPaths.Add(new Dictionary<string, object>
                    {
                        { "path", path },
                        { "reason", ex.Message }
                    });
                }
            }

            AssetDatabase.Refresh(refreshFlags);
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            EditorApplication.QueuePlayerLoopUpdate();

            var primaryPath = reimportedPaths.Count > 0 ? reimportedPaths[0] as string : string.Empty;
            var msg = reimportedPaths.Count == 0
                ? "Recompilation requested"
                : $"Force-reimported {reimportedPaths.Count} path(s) and requested recompilation";

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "message", msg },
                { "reimportPath", primaryPath ?? string.Empty }, // backwards compatibility
                { "reimportedCount", reimportedPaths.Count },
                { "reimportedPaths", reimportedPaths },
                { "skippedCount", skippedPaths.Count },
                { "skippedPaths", skippedPaths },
                { "requestedWaitForCompile", requestedWaitForCompile },
                { "requestedMaxWaitMs", requestedMaxWaitMs },
                { "requestedPollIntervalMs", requestedPollIntervalMs },
                { "waitHandledByClient", requestedWaitForCompile }
            });
        }

        #endregion

        #region Serialization Operations

        [BridgeRoute("PUT", "/serialization/reference/{id}", Category = "serialization", Description = "Set SerializeReference field")]
        public static string SetManagedReference(int instanceId, string jsonData)
        {
            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            var request = JsonUtility.FromJson<ManagedReferenceRequest>(jsonData);
            if (string.IsNullOrEmpty(request.propertyPath) || string.IsNullOrEmpty(request.typeName))
            {
                return JsonError("propertyPath and typeName are required");
            }

            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null) continue;

                var so = new SerializedObject(component);
                var prop = so.FindProperty(request.propertyPath);

                if (prop != null && prop.propertyType == SerializedPropertyType.ManagedReference)
                {
                    var type = FindType(request.typeName);
                    if (type == null)
                    {
                        return JsonError($"Type '{request.typeName}' not found");
                    }

                    try
                    {
                        var instance = Activator.CreateInstance(type);

                        if (!string.IsNullOrEmpty(request.data))
                        {
                            JsonUtility.FromJsonOverwrite(request.data, instance);
                        }

                        Undo.RecordObject(component, "Agent Bridge Set Managed Reference");
                        prop.managedReferenceValue = instance;
                        so.ApplyModifiedProperties();
                        EditorUtility.SetDirty(component);

                        return JsonResult(new Dictionary<string, object> { { "success", true }, { "propertyPath", request.propertyPath }, { "typeName", type.FullName } });
                    }
                    catch (Exception ex)
                    {
                        return JsonError(ex.Message);
                    }
                }
            }

            return JsonError("Property not found or not a managed reference");
        }

        public static string GetDerivedTypes(string baseTypeName)
        {
            var baseType = FindType(baseTypeName);
            if (baseType == null)
            {
                return JsonError($"Base type '{baseTypeName}' not found");
            }

            var derivedTypes = new List<DerivedTypeInfo>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (baseType.IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
                        {
                            derivedTypes.Add(new DerivedTypeInfo
                            {
                                name = type.Name,
                                fullName = type.FullName,
                                assembly = assembly.GetName().Name
                            });
                        }
                    }
                }
                catch
                {
                    // Skip assemblies that can't be loaded
                }
            }

            var jsonParts = derivedTypes.Select(t => JsonUtility.ToJson(t));
            return "{\"success\":true,\"types\":[" + string.Join(",", jsonParts) + "]}";
        }

        private static Type FindType(string typeName)
        {
            // Try direct lookup first
            var type = Type.GetType(typeName);
            if (type != null) return type;

            // Search all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(typeName);
                    if (type != null) return type;

                    // Try finding by simple name
                    foreach (var t in assembly.GetTypes())
                    {
                        if (t.Name == typeName || t.FullName == typeName)
                        {
                            return t;
                        }
                    }
                }
                catch
                {
                    // Skip assemblies that can't be loaded
                }
            }

            return null;
        }

        #endregion
    }
}
