using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.IO;
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
        // ==================== Frame Sequence Capture ====================
        #region Frame Sequence Capture

        private static readonly Dictionary<string, PendingFrameCapture> _pendingFrameCaptures = new Dictionary<string, PendingFrameCapture>(StringComparer.Ordinal);
        private static readonly object _frameCapturesLock = new object();
        private static bool _frameSequenceTickRegistered;

        private const int MaxConcurrentCaptures = 3;

        private sealed class PendingFrameCapture
        {
            public string captureId;
            public string viewType;
            public int frameCount;
            public int captureIntervalMs;
            public int width, height;
            public string format, source;
            public List<Dictionary<string, object>> frames = new List<Dictionary<string, object>>();
            public HashSet<string> activeHandles = new HashSet<string>(StringComparer.Ordinal);
            public bool completed, failed, cancelled;
            public string error;
            public System.Diagnostics.Stopwatch stopwatch;
            public long lastCaptureElapsedMs;
        }

        /// <summary>Check if a handle is protected by an active frame sequence capture (call under _imageStoreLock).</summary>
        internal static bool IsHandleProtectedByCapture(string handle)
        {
            lock (_frameCapturesLock)
            {
                foreach (var kv in _pendingFrameCaptures)
                {
                    var cap = kv.Value;
                    if (!cap.completed && !cap.failed && !cap.cancelled && cap.activeHandles.Contains(handle))
                        return true;
                }
            }
            return false;
        }

        [BridgeRoute("POST", "/screenshot/sequence/start", Category = "screenshot", Description = "Start frame sequence capture", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string StartFrameSequence(string jsonData)
        {
            try
            {
                var data = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (data == null)
                    return JsonError("Failed to parse frame sequence request");

                string viewType = (ReadString(data, "viewType") ?? "game").ToLowerInvariant();
                if (viewType != "game" && viewType != "scene")
                    return JsonError("viewType must be 'game' or 'scene'");

                int frameCount = Mathf.Clamp(TryReadInt(data, "frameCount", out var fc) ? fc : 10, 1, 60);
                int captureIntervalMs = Mathf.Clamp(TryReadInt(data, "captureIntervalMs", out var ci) ? ci : 100, 50, 2000);
                int width = TryReadInt(data, "width", out var rw) ? rw : 0;
                int height = TryReadInt(data, "height", out var rh) ? rh : 0;
                string format = (ReadString(data, "format") ?? "jpeg").ToLowerInvariant();
                string source = ReadString(data, "source") ?? "frame-sequence";

                if (format != "jpeg" && format != "png")
                    format = "jpeg";

                long totalDurationMs = (long)frameCount * captureIntervalMs;
                if (totalDurationMs > 30000)
                    return JsonError($"Total duration {totalDurationMs}ms exceeds 30s limit. Reduce frameCount or captureIntervalMs.");

                // Play mode check for game view
                if (viewType == "game" && !EditorApplication.isPlaying)
                    return JsonError("Play mode required for game view capture");

                // Camera availability
                if (viewType == "game" && Camera.main == null)
                    return JsonError("Camera.main is null — no camera available for game view capture");

                if (viewType == "scene")
                {
                    var sceneView = SceneView.lastActiveSceneView;
                    if (sceneView == null)
                        return JsonError("No active SceneView available for scene view capture");
                }

                // Concurrency gate
                int activeCount;
                lock (_frameCapturesLock)
                {
                    activeCount = 0;
                    foreach (var kv in _pendingFrameCaptures)
                    {
                        var cap = kv.Value;
                        if (!cap.completed && !cap.failed && !cap.cancelled)
                            activeCount++;
                    }

                    if (activeCount >= MaxConcurrentCaptures)
                    {
                        return MiniJSON.Json.Serialize(new Dictionary<string, object>
                        {
                            { "success", false },
                            { "error", $"Maximum concurrent captures ({MaxConcurrentCaptures}) reached. Wait for existing captures to complete or cancel them." },
                            { "activeCaptures", activeCount },
                            { "maxConcurrent", MaxConcurrentCaptures }
                        });
                    }
                }

                string captureId = "seq_" + Guid.NewGuid().ToString("N");

                var pending = new PendingFrameCapture
                {
                    captureId = captureId,
                    viewType = viewType,
                    frameCount = frameCount,
                    captureIntervalMs = captureIntervalMs,
                    width = width,
                    height = height,
                    format = format,
                    source = source,
                    stopwatch = System.Diagnostics.Stopwatch.StartNew(),
                    lastCaptureElapsedMs = -captureIntervalMs // capture first frame immediately
                };

                lock (_frameCapturesLock)
                {
                    _pendingFrameCaptures[captureId] = pending;

                    if (!_frameSequenceTickRegistered)
                    {
                        EditorApplication.update += CaptureFrameSequenceTick;
                        _frameSequenceTickRegistered = true;
                    }
                }

                return MiniJSON.Json.Serialize(new Dictionary<string, object>
                {
                    { "success", true },
                    { "captureId", captureId },
                    { "status", "started" },
                    { "frameCount", frameCount },
                    { "estimatedDurationMs", totalDurationMs }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static void CaptureFrameSequenceTick()
        {
            // 1. Snapshot active capture IDs under lock
            List<string> activeCaptureIds;
            lock (_frameCapturesLock)
            {
                activeCaptureIds = new List<string>();
                foreach (var kv in _pendingFrameCaptures)
                {
                    var cap = kv.Value;
                    if (!cap.completed && !cap.failed && !cap.cancelled)
                        activeCaptureIds.Add(kv.Key);
                }
            }

            if (activeCaptureIds.Count == 0)
            {
                // Nothing active — check for orphans and unregister
                CleanupOrphanCaptures();
                return;
            }

            // 2. Process each capture outside lock
            foreach (var captureId in activeCaptureIds)
            {
                PendingFrameCapture capture;
                lock (_frameCapturesLock)
                {
                    if (!_pendingFrameCaptures.TryGetValue(captureId, out capture))
                        continue;
                }

                // Already done?
                if (capture.completed || capture.failed || capture.cancelled)
                    continue;

                // Safety timeout (35s wall clock)
                if (capture.stopwatch.ElapsedMilliseconds > 35000)
                {
                    capture.failed = true;
                    capture.error = "Frame sequence capture timed out (35s wall-clock limit)";
                    capture.activeHandles.Clear();
                    continue;
                }

                // Check if it's time for the next frame
                long elapsed = capture.stopwatch.ElapsedMilliseconds;
                if (elapsed < capture.lastCaptureElapsedMs + capture.captureIntervalMs)
                    continue;

                // Capture one frame
                int frameIndex = capture.frames.Count;
                long frameTimestamp = elapsed;

                try
                {
                    string result = TakeScreenshot(
                        capture.viewType,
                        includeBase64: false,
                        includeHandle: true,
                        source: capture.source,
                        requestedWidth: capture.width,
                        requestedHeight: capture.height,
                        imageFormat: capture.format);

                    var parsed = MiniJSON.Json.Deserialize(result) as Dictionary<string, object>;

                    if (parsed != null && parsed.ContainsKey("imageHandle") && parsed["imageHandle"] is string handle && !string.IsNullOrEmpty(handle))
                    {
                        int w = TryReadInt(parsed, "width", out var pw) ? pw : 0;
                        int h = TryReadInt(parsed, "height", out var ph) ? ph : 0;

                        capture.activeHandles.Add(handle);
                        capture.frames.Add(new Dictionary<string, object>
                        {
                            { "frameIndex", frameIndex },
                            { "imageHandle", handle },
                            { "timestampMs", frameTimestamp },
                            { "width", w },
                            { "height", h }
                        });
                    }
                    else
                    {
                        // Frame failed — record skip
                        string reason = parsed != null && parsed.ContainsKey("error") ? parsed["error"]?.ToString() : "Unknown capture failure";
                        capture.frames.Add(new Dictionary<string, object>
                        {
                            { "frameIndex", frameIndex },
                            { "skipped", true },
                            { "reason", reason },
                            { "timestampMs", frameTimestamp }
                        });
                    }
                }
                catch (Exception ex)
                {
                    capture.frames.Add(new Dictionary<string, object>
                    {
                        { "frameIndex", frameIndex },
                        { "skipped", true },
                        { "reason", ex.Message },
                        { "timestampMs", frameTimestamp }
                    });
                }

                capture.lastCaptureElapsedMs = elapsed;

                // Check completion
                if (capture.frames.Count >= capture.frameCount)
                {
                    capture.completed = true;
                    capture.activeHandles.Clear(); // release pruning protection
                }
            }

            // 3. Cleanup
            CleanupOrphanCaptures();
        }

        private static void CleanupOrphanCaptures()
        {
            lock (_frameCapturesLock)
            {
                // Remove completed/failed/cancelled captures that have been polled (removed by GetStatus)
                // Clean up orphans older than 60s
                var toRemove = new List<string>();
                bool anyActive = false;
                foreach (var kv in _pendingFrameCaptures)
                {
                    var cap = kv.Value;
                    if (!cap.completed && !cap.failed && !cap.cancelled)
                    {
                        if (cap.stopwatch.ElapsedMilliseconds > 60000)
                        {
                            cap.failed = true;
                            cap.error = "Orphaned capture (>60s)";
                            cap.activeHandles.Clear();
                            toRemove.Add(kv.Key);
                        }
                        else
                        {
                            anyActive = true;
                        }
                    }
                    else if (cap.stopwatch.ElapsedMilliseconds > 60000)
                    {
                        // Old completed/failed captures that were never polled — clean up
                        toRemove.Add(kv.Key);
                    }
                }

                foreach (var id in toRemove)
                    _pendingFrameCaptures.Remove(id);

                if (!anyActive && _pendingFrameCaptures.Count == 0 && _frameSequenceTickRegistered)
                {
                    EditorApplication.update -= CaptureFrameSequenceTick;
                    _frameSequenceTickRegistered = false;
                }
            }
        }

        public static string GetFrameSequenceStatus(string jsonData)
        {
            try
            {
                var data = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (data == null)
                    return JsonError("Failed to parse status request");
                string captureId = ReadString(data, "captureId") ?? "";

                if (string.IsNullOrWhiteSpace(captureId))
                    return JsonError("captureId is required");

                PendingFrameCapture capture;
                lock (_frameCapturesLock)
                {
                    if (!_pendingFrameCaptures.TryGetValue(captureId, out capture))
                        return JsonError($"Capture not found: {captureId}");
                }

                // Completed
                if (capture.completed)
                {
                    int skipped = 0;
                    foreach (var f in capture.frames)
                    {
                        if (f.ContainsKey("skipped") && f["skipped"] is bool s && s)
                            skipped++;
                    }

                    var result = new Dictionary<string, object>
                    {
                        { "success", true },
                        { "completed", true },
                        { "captureId", captureId },
                        { "frameCount", capture.frames.Count },
                        { "totalCaptureMs", capture.stopwatch.ElapsedMilliseconds },
                        { "frames", capture.frames },
                        { "skippedFrames", skipped }
                    };

                    lock (_frameCapturesLock)
                    {
                        _pendingFrameCaptures.Remove(captureId);
                    }

                    return MiniJSON.Json.Serialize(result);
                }

                // Failed
                if (capture.failed)
                {
                    var result = new Dictionary<string, object>
                    {
                        { "success", false },
                        { "failed", true },
                        { "captureId", captureId },
                        { "error", capture.error ?? "Unknown error" },
                        { "capturedFrames", capture.frames.Count },
                        { "frames", capture.frames }
                    };

                    lock (_frameCapturesLock)
                    {
                        _pendingFrameCaptures.Remove(captureId);
                    }

                    return MiniJSON.Json.Serialize(result);
                }

                // Cancelled
                if (capture.cancelled)
                {
                    var result = new Dictionary<string, object>
                    {
                        { "success", true },
                        { "cancelled", true },
                        { "captureId", captureId },
                        { "capturedFrames", capture.frames.Count },
                        { "frames", capture.frames }
                    };

                    lock (_frameCapturesLock)
                    {
                        _pendingFrameCaptures.Remove(captureId);
                    }

                    return MiniJSON.Json.Serialize(result);
                }

                // In progress
                return MiniJSON.Json.Serialize(new Dictionary<string, object>
                {
                    { "success", true },
                    { "captureId", captureId },
                    { "status", "capturing" },
                    { "capturedFrames", capture.frames.Count },
                    { "totalFrames", capture.frameCount }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        public static string CancelFrameSequence(string jsonData)
        {
            try
            {
                var data = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (data == null)
                    return JsonError("Failed to parse cancel request");
                string captureId = ReadString(data, "captureId") ?? "";

                if (string.IsNullOrWhiteSpace(captureId))
                    return JsonError("captureId is required");

                PendingFrameCapture capture;
                lock (_frameCapturesLock)
                {
                    if (!_pendingFrameCaptures.TryGetValue(captureId, out capture))
                        return JsonError($"Capture not found or already completed: {captureId}");

                    if (capture.completed || capture.failed || capture.cancelled)
                        return JsonError($"Capture already finished: {captureId}");
                }

                capture.cancelled = true;
                capture.activeHandles.Clear();

                return MiniJSON.Json.Serialize(new Dictionary<string, object>
                {
                    { "success", true },
                    { "captureId", captureId },
                    { "cancelled", true },
                    { "capturedFrames", capture.frames.Count }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        #endregion

    }
}
