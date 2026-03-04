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
        [BridgeRoute("POST", "/performance/telemetry", Category = "performance", Description = "Performance snapshot", ReadOnly = true, TimeoutDefault = 10000)]
        public static string GetPerformanceTelemetry(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<PerformanceTelemetryRequest>(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData)
                    ?? new PerformanceTelemetryRequest();

                bool includeHotspots = request.includeHotspots != 0;
                bool includeInactive = request.includeInactive != 0;
                bool onlyEnabledBehaviours = request.onlyEnabledBehaviours != 0;
                int maxHotspots = Mathf.Clamp(request.maxHotspots <= 0 ? 12 : request.maxHotspots, 1, 100);

                var snapshot = CapturePerformanceSnapshot(includeHotspots, includeInactive, onlyEnabledBehaviours, maxHotspots);
                return JsonResult(snapshot.ToJson(includeHotspots));
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/performance/baseline", Category = "performance", Description = "Capture performance baseline", TimeoutDefault = 15000)]
        public static string CapturePerformanceBaseline(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<PerformanceBaselineRequest>(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData)
                    ?? new PerformanceBaselineRequest();

                bool includeHotspots = request.includeHotspots != 0;
                bool includeInactive = request.includeInactive != 0;
                bool onlyEnabledBehaviours = request.onlyEnabledBehaviours != 0;
                int maxHotspots = Mathf.Clamp(request.maxHotspots <= 0 ? 12 : request.maxHotspots, 1, 100);
                string baselineName = NormalizeBaselineName(request.name);

                var snapshot = CapturePerformanceSnapshot(includeHotspots, includeInactive, onlyEnabledBehaviours, maxHotspots);

                lock (_performanceBaselineLock)
                {
                    _performanceBaselines[baselineName] = snapshot;
                }

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "name", baselineName },
                    { "capturedAtUtc", snapshot.capturedAtUtc },
                    { "mode", snapshot.mode },
                    { "metrics", snapshot.ToMetricsJson() },
                    { "hotspotCount", snapshot.scriptHotspots.Count },
                    { "includeHotspots", includeHotspots }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/performance/budget-check", Category = "performance", Description = "Check performance budget", ReadOnly = true, TimeoutDefault = 10000)]
        public static string CheckPerformanceBudget(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<PerformanceBudgetCheckRequest>(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData)
                    ?? new PerformanceBudgetCheckRequest();

                bool includeHotspots = request.includeHotspots != 0;
                bool includeInactive = request.includeInactive != 0;
                bool onlyEnabledBehaviours = request.onlyEnabledBehaviours != 0;
                int maxHotspots = Mathf.Clamp(request.maxHotspots <= 0 ? 8 : request.maxHotspots, 1, 100);
                string baselineName = NormalizeBaselineName(request.baselineName);
                bool useBaseline = request.useBaseline != 0;
                bool captureBaselineIfMissing = request.captureBaselineIfMissing != 0;

                var current = CapturePerformanceSnapshot(includeHotspots, includeInactive, onlyEnabledBehaviours, maxHotspots);

                PerformanceMetricSnapshot baseline = null;
                bool baselineFound = false;
                bool baselineCaptured = false;
                if (useBaseline)
                {
                    lock (_performanceBaselineLock)
                    {
                        baselineFound = _performanceBaselines.TryGetValue(baselineName, out baseline);
                        if (!baselineFound && captureBaselineIfMissing)
                        {
                            _performanceBaselines[baselineName] = current.Clone();
                            baseline = _performanceBaselines[baselineName];
                            baselineFound = true;
                            baselineCaptured = true;
                        }
                    }
                }

                var violations = new List<Dictionary<string, object>>();
                var checks = new List<Dictionary<string, object>>();

                void AddAbsoluteCheck(string metric, double value, double threshold, bool enabled, bool lowerIsBetter)
                {
                    if (!enabled) return;
                    bool pass = lowerIsBetter ? value <= threshold : value >= threshold;
                    checks.Add(new Dictionary<string, object>
                    {
                        { "metric", metric },
                        { "kind", "absolute" },
                        { "value", value },
                        { "threshold", threshold },
                        { "passed", pass }
                    });
                    if (!pass)
                    {
                        violations.Add(new Dictionary<string, object>
                        {
                            { "metric", metric },
                            { "kind", "absolute" },
                            { "value", value },
                            { "threshold", threshold },
                            { "delta", 0d },
                            { "message", lowerIsBetter
                                ? $"{metric} exceeded threshold ({value} > {threshold})"
                                : $"{metric} below threshold ({value} < {threshold})" }
                        });
                    }
                }

                void AddDeltaCheck(string metric, double currentValue, double baselineValue, double maxDelta, bool enabled)
                {
                    if (!enabled) return;
                    var delta = currentValue - baselineValue;
                    bool pass = delta <= maxDelta;
                    checks.Add(new Dictionary<string, object>
                    {
                        { "metric", metric },
                        { "kind", "delta" },
                        { "value", currentValue },
                        { "baseline", baselineValue },
                        { "delta", delta },
                        { "maxDelta", maxDelta },
                        { "passed", pass }
                    });
                    if (!pass)
                    {
                        violations.Add(new Dictionary<string, object>
                        {
                            { "metric", metric },
                            { "kind", "delta" },
                            { "value", currentValue },
                            { "baseline", baselineValue },
                            { "delta", delta },
                            { "maxDelta", maxDelta },
                            { "message", $"{metric} delta exceeded max delta ({delta} > {maxDelta})" }
                        });
                    }
                }

                AddAbsoluteCheck("frameTimeMs", current.frameTimeMs, request.maxFrameTimeMs, request.maxFrameTimeMs >= 0d, lowerIsBetter: true);
                AddAbsoluteCheck("fps", current.fps, request.minFps, request.minFps >= 0d, lowerIsBetter: false);
                AddAbsoluteCheck("gcAllocatedInFrameBytes", current.gcAllocatedInFrameBytes, request.maxGcAllocBytesPerFrame, request.maxGcAllocBytesPerFrame >= 0d, lowerIsBetter: true);
                AddAbsoluteCheck("drawCalls", current.drawCalls, request.maxDrawCalls, request.maxDrawCalls >= 0d, lowerIsBetter: true);
                AddAbsoluteCheck("batches", current.batches, request.maxBatches, request.maxBatches >= 0d, lowerIsBetter: true);
                AddAbsoluteCheck("setPassCalls", current.setPassCalls, request.maxSetPassCalls, request.maxSetPassCalls >= 0d, lowerIsBetter: true);
                AddAbsoluteCheck("totalAllocatedMemoryBytes", current.totalAllocatedMemoryBytes, request.maxTotalAllocatedMemoryBytes, request.maxTotalAllocatedMemoryBytes >= 0d, lowerIsBetter: true);

                if (useBaseline && baselineFound && baseline != null && !baselineCaptured)
                {
                    AddDeltaCheck("frameTimeMs", current.frameTimeMs, baseline.frameTimeMs, request.maxFrameTimeDeltaMs, request.maxFrameTimeDeltaMs >= 0d);
                    AddDeltaCheck("gcAllocatedInFrameBytes", current.gcAllocatedInFrameBytes, baseline.gcAllocatedInFrameBytes, request.maxGcAllocDeltaBytesPerFrame, request.maxGcAllocDeltaBytesPerFrame >= 0d);
                    AddDeltaCheck("drawCalls", current.drawCalls, baseline.drawCalls, request.maxDrawCallsDelta, request.maxDrawCallsDelta >= 0d);
                    AddDeltaCheck("batches", current.batches, baseline.batches, request.maxBatchesDelta, request.maxBatchesDelta >= 0d);
                    AddDeltaCheck("setPassCalls", current.setPassCalls, baseline.setPassCalls, request.maxSetPassCallsDelta, request.maxSetPassCallsDelta >= 0d);
                    AddDeltaCheck("totalAllocatedMemoryBytes", current.totalAllocatedMemoryBytes, baseline.totalAllocatedMemoryBytes, request.maxTotalAllocatedMemoryDeltaBytes, request.maxTotalAllocatedMemoryDeltaBytes >= 0d);
                }

                bool passed = violations.Count == 0;
                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "passed", passed },
                    { "failed", !passed },
                    { "baselineName", baselineName },
                    { "baselineUsed", useBaseline },
                    { "baselineFound", baselineFound },
                    { "baselineCaptured", baselineCaptured },
                    { "capturedAtUtc", current.capturedAtUtc },
                    { "mode", current.mode },
                    { "metrics", current.ToMetricsJson() },
                    { "checkCount", checks.Count },
                    { "violationCount", violations.Count },
                    { "checks", checks },
                    { "violations", violations }
                };

                if (includeHotspots)
                {
                    response["scriptHotspots"] = current.scriptHotspots;
                    response["scriptHotspotHeuristic"] = true;
                    response["scriptHotspotCount"] = current.scriptHotspots.Count;
                }

                if (useBaseline && baselineFound && baseline != null)
                {
                    response["baseline"] = new Dictionary<string, object>
                    {
                        { "name", baselineName },
                        { "capturedAtUtc", baseline.capturedAtUtc },
                        { "mode", baseline.mode },
                        { "metrics", baseline.ToMetricsJson() }
                    };
                }
                else if (useBaseline && !baselineFound)
                {
                    response["baselineWarning"] = $"Baseline '{baselineName}' not found.";
                }

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/performance/hotspots", Category = "performance", Description = "Script hotspot ranking", ReadOnly = true, TimeoutDefault = 10000)]
        public static string GetScriptHotspots(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<ScriptHotspotsRequest>(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData)
                    ?? new ScriptHotspotsRequest();
                bool includeInactive = request.includeInactive != 0;
                bool onlyEnabledBehaviours = request.onlyEnabledBehaviours != 0;
                int maxHotspots = Mathf.Clamp(request.maxHotspots <= 0 ? 20 : request.maxHotspots, 1, 200);
                var snapshot = CapturePerformanceSnapshot(includeHotspots: true, includeInactive, onlyEnabledBehaviours, maxHotspots);
                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "capturedAtUtc", snapshot.capturedAtUtc },
                    { "mode", snapshot.mode },
                    { "heuristic", true },
                    { "maxHotspots", maxHotspots },
                    { "hotspots", snapshot.scriptHotspots }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static PerformanceMetricSnapshot CapturePerformanceSnapshot(bool includeHotspots, bool includeInactive, bool onlyEnabledBehaviours, int maxHotspots)
        {
            var snapshot = new PerformanceMetricSnapshot
            {
                capturedAtUtc = DateTime.UtcNow.ToString("o"),
                mode = EditorApplication.isPlaying ? "play" : "edit",
                isPlaying = EditorApplication.isPlaying
            };

            bool hasFrameTime = TryReadProfilerCounter(
                ProfilerCategory.Internal,
                new[] { "Main Thread", "Main Thread Frame Time", "CPU Main Thread Frame Time" },
                out var frameTimeRaw,
                out var frameTimeSource);
            if (!hasFrameTime)
            {
                hasFrameTime = TryReadProfilerCounter(
                    ProfilerCategory.Render,
                    new[] { "CPU Main Thread Frame Time", "CPU Total Frame Time" },
                    out frameTimeRaw,
                    out frameTimeSource);
            }

            snapshot.frameTimeMs = hasFrameTime
                ? Math.Max(0d, ConvertProfilerTimeToMs(frameTimeRaw))
                : (Time.smoothDeltaTime > 0f ? Time.smoothDeltaTime * 1000.0 : 0.0);
            snapshot.fps = snapshot.frameTimeMs > 0d ? 1000d / snapshot.frameTimeMs : 0d;
            snapshot.frameTimeSource = hasFrameTime ? frameTimeSource : "Time.smoothDeltaTime (fallback)";

            if (TryReadProfilerCounter(
                ProfilerCategory.Memory,
                new[] { "GC Allocated In Frame", "GC Allocation In Frame", "GC Allocated In Frame Count" },
                out var gcAllocRaw,
                out var gcAllocStat))
            {
                snapshot.gcAllocatedInFrameBytes = Math.Max(0, gcAllocRaw);
                snapshot.gcAllocatedInFrameSource = gcAllocStat;
            }

            if (TryReadProfilerCounter(
                ProfilerCategory.Render,
                new[] { "Draw Calls Count", "Draw Calls" },
                out var drawRaw,
                out var drawStat))
            {
                snapshot.drawCalls = Math.Max(0, drawRaw);
                snapshot.drawCallsSource = drawStat;
            }

            if (TryReadProfilerCounter(
                ProfilerCategory.Render,
                new[] { "Batches Count", "Batches" },
                out var batchRaw,
                out var batchStat))
            {
                snapshot.batches = Math.Max(0, batchRaw);
                snapshot.batchesSource = batchStat;
            }

            if (TryReadProfilerCounter(
                ProfilerCategory.Render,
                new[] { "SetPass Calls Count", "SetPass Calls" },
                out var setPassRaw,
                out var setPassStat))
            {
                snapshot.setPassCalls = Math.Max(0, setPassRaw);
                snapshot.setPassCallsSource = setPassStat;
            }

            if (TryReadProfilerCounter(
                ProfilerCategory.Render,
                new[] { "Triangles Count", "Triangles" },
                out var trianglesRaw,
                out var trianglesStat))
            {
                snapshot.triangles = Math.Max(0, trianglesRaw);
                snapshot.trianglesSource = trianglesStat;
            }

            if (TryReadProfilerCounter(
                ProfilerCategory.Render,
                new[] { "Vertices Count", "Vertices" },
                out var verticesRaw,
                out var verticesStat))
            {
                snapshot.vertices = Math.Max(0, verticesRaw);
                snapshot.verticesSource = verticesStat;
            }

            try
            {
                snapshot.monoUsedBytes = Profiler.GetMonoUsedSizeLong();
                snapshot.monoHeapBytes = Profiler.GetMonoHeapSizeLong();
                snapshot.totalAllocatedMemoryBytes = Profiler.GetTotalAllocatedMemoryLong();
                snapshot.totalReservedMemoryBytes = Profiler.GetTotalReservedMemoryLong();
            }
            catch
            {
                // Keep defaults.
            }

            snapshot.gcCollectionCountGen0 = GC.CollectionCount(0);
            snapshot.gcCollectionCountGen1 = GC.CollectionCount(1);
            snapshot.gcCollectionCountGen2 = GC.CollectionCount(2);

            if (includeHotspots)
            {
                snapshot.scriptHotspots = BuildScriptHotspotPayload(includeInactive, onlyEnabledBehaviours, maxHotspots);
            }

            return snapshot;
        }

        private static string NormalizeBaselineName(string rawName)
        {
            var name = (rawName ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(name) ? "default" : name;
        }

        private static List<object> BuildScriptHotspotPayload(bool includeInactive, bool onlyEnabledBehaviours, int maxHotspots)
        {
            var behaviours = new List<MonoBehaviour>();

            if (includeInactive)
            {
                var allBehaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                foreach (var behaviour in allBehaviours)
                {
                    if (behaviour == null) continue;
                    if (EditorUtility.IsPersistent(behaviour)) continue;
                    if (behaviour.gameObject == null) continue;
                    if (!behaviour.gameObject.scene.IsValid()) continue;
                    if (onlyEnabledBehaviours && !behaviour.isActiveAndEnabled) continue;
                    behaviours.Add(behaviour);
                }
            }
            else
            {
                var scene = SceneManager.GetActiveScene();
                if (scene.IsValid() && scene.isLoaded)
                {
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        var components = root.GetComponentsInChildren<MonoBehaviour>(false);
                        foreach (var behaviour in components)
                        {
                            if (behaviour == null) continue;
                            if (onlyEnabledBehaviours && !behaviour.isActiveAndEnabled) continue;
                            behaviours.Add(behaviour);
                        }
                    }
                }
            }

            var grouped = behaviours
                .Where(b => b != null)
                .GroupBy(b => b.GetType())
                .ToList();

            var scored = new List<ScriptHotspotSummary>();
            foreach (var group in grouped)
            {
                var type = group.Key;
                if (type == null) continue;

                int instanceCount = group.Count();
                int enabledCount = group.Count(b => b != null && b.isActiveAndEnabled);
                int effectiveCount = onlyEnabledBehaviours ? enabledCount : instanceCount;
                if (effectiveCount <= 0) continue;

                bool hasUpdate = HasMethod(type, "Update");
                bool hasFixedUpdate = HasMethod(type, "FixedUpdate");
                bool hasLateUpdate = HasMethod(type, "LateUpdate");
                bool hasOnGui = HasMethod(type, "OnGUI");

                double activityWeight = 0d;
                if (hasUpdate) activityWeight += 1.0d;
                if (hasFixedUpdate) activityWeight += 1.1d;
                if (hasLateUpdate) activityWeight += 0.6d;
                if (hasOnGui) activityWeight += 1.4d;
                if (activityWeight <= 0d) activityWeight = 0.1d;

                double score = effectiveCount * activityWeight;

                var sample = group.FirstOrDefault();
                string samplePath = sample != null ? GetHierarchyPath(sample.transform) : string.Empty;
                string sampleObject = sample != null ? sample.gameObject.name : string.Empty;
                string scriptPath = string.Empty;
                try
                {
                    if (sample != null)
                    {
                        var monoScript = MonoScript.FromMonoBehaviour(sample);
                        if (monoScript != null)
                        {
                            scriptPath = AssetDatabase.GetAssetPath(monoScript) ?? string.Empty;
                        }
                    }
                }
                catch
                {
                    scriptPath = string.Empty;
                }

                scored.Add(new ScriptHotspotSummary
                {
                    typeName = type.Name,
                    fullTypeName = type.FullName ?? type.Name,
                    scriptPath = scriptPath,
                    instanceCount = instanceCount,
                    enabledInstanceCount = enabledCount,
                    hasUpdate = hasUpdate,
                    hasFixedUpdate = hasFixedUpdate,
                    hasLateUpdate = hasLateUpdate,
                    hasOnGUI = hasOnGui,
                    activityWeight = Math.Round(activityWeight, 3),
                    score = Math.Round(score, 3),
                    sampleObject = sampleObject,
                    samplePath = samplePath
                });
            }

            return scored
                .OrderByDescending(s => s.score)
                .ThenByDescending(s => s.enabledInstanceCount)
                .ThenBy(s => s.typeName, StringComparer.OrdinalIgnoreCase)
                .Take(maxHotspots)
                .Select(s => s.ToJson())
                .Cast<object>()
                .ToList();
        }

        private static bool HasMethod(Type type, string methodName)
        {
            if (type == null || string.IsNullOrWhiteSpace(methodName)) return false;
            return type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
        }

        private static bool TryReadProfilerCounter(
            ProfilerCategory category,
            string[] statNames,
            out long value,
            out string resolvedStat)
        {
            value = 0;
            resolvedStat = "unavailable";

            if (statNames == null || statNames.Length == 0)
            {
                return false;
            }

            foreach (var statName in statNames)
            {
                if (string.IsNullOrWhiteSpace(statName)) continue;

                ProfilerRecorder recorder = default;
                try
                {
                    recorder = ProfilerRecorder.StartNew(category, statName, 1);
                    if (!recorder.Valid)
                    {
                        continue;
                    }

                    value = recorder.LastValue;
                    resolvedStat = $"{category}:{statName}";
                    return true;
                }
                catch
                {
                    // Keep trying other candidate counters.
                }
                finally
                {
                    if (recorder.Valid)
                    {
                        recorder.Dispose();
                    }
                }
            }

            return false;
        }

        private static double ConvertProfilerTimeToMs(long rawValue)
        {
            if (rawValue <= 0) return 0d;
            if (rawValue >= 1_000_000) return rawValue / 1_000_000d; // nanoseconds
            if (rawValue >= 1_000) return rawValue / 1_000d;         // microseconds
            return rawValue;                                           // milliseconds fallback
        }

        private sealed class PerformanceMetricSnapshot
        {
            public string capturedAtUtc;
            public string mode;
            public bool isPlaying;
            public double frameTimeMs;
            public double fps;
            public string frameTimeSource = "unavailable";
            public long gcAllocatedInFrameBytes = -1;
            public string gcAllocatedInFrameSource = "unavailable";
            public long drawCalls = -1;
            public string drawCallsSource = "unavailable";
            public long batches = -1;
            public string batchesSource = "unavailable";
            public long setPassCalls = -1;
            public string setPassCallsSource = "unavailable";
            public long triangles = -1;
            public string trianglesSource = "unavailable";
            public long vertices = -1;
            public string verticesSource = "unavailable";
            public long monoUsedBytes = 0;
            public long monoHeapBytes = 0;
            public long totalAllocatedMemoryBytes = 0;
            public long totalReservedMemoryBytes = 0;
            public int gcCollectionCountGen0 = 0;
            public int gcCollectionCountGen1 = 0;
            public int gcCollectionCountGen2 = 0;
            public List<object> scriptHotspots = new List<object>();

            public Dictionary<string, object> ToMetricsJson()
            {
                return new Dictionary<string, object>
                {
                    { "frameTimeMs", Math.Round(frameTimeMs, 3) },
                    { "fps", Math.Round(fps, 2) },
                    { "frameTimeSource", frameTimeSource },
                    { "gcAllocatedInFrameBytes", gcAllocatedInFrameBytes },
                    { "gcAllocatedInFrameSource", gcAllocatedInFrameSource },
                    { "drawCalls", drawCalls },
                    { "drawCallsSource", drawCallsSource },
                    { "batches", batches },
                    { "batchesSource", batchesSource },
                    { "setPassCalls", setPassCalls },
                    { "setPassCallsSource", setPassCallsSource },
                    { "triangles", triangles },
                    { "trianglesSource", trianglesSource },
                    { "vertices", vertices },
                    { "verticesSource", verticesSource },
                    { "monoUsedBytes", monoUsedBytes },
                    { "monoHeapBytes", monoHeapBytes },
                    { "totalAllocatedMemoryBytes", totalAllocatedMemoryBytes },
                    { "totalReservedMemoryBytes", totalReservedMemoryBytes },
                    { "gcCollectionCountGen0", gcCollectionCountGen0 },
                    { "gcCollectionCountGen1", gcCollectionCountGen1 },
                    { "gcCollectionCountGen2", gcCollectionCountGen2 }
                };
            }

            public Dictionary<string, object> ToJson(bool includeHotspots)
            {
                var payload = new Dictionary<string, object>
                {
                    { "success", true },
                    { "capturedAtUtc", capturedAtUtc },
                    { "mode", mode },
                    { "isPlaying", isPlaying },
                    { "metrics", ToMetricsJson() }
                };

                if (includeHotspots)
                {
                    payload["scriptHotspots"] = scriptHotspots;
                    payload["scriptHotspotHeuristic"] = true;
                    payload["scriptHotspotCount"] = scriptHotspots.Count;
                }

                return payload;
            }

            public PerformanceMetricSnapshot Clone()
            {
                return new PerformanceMetricSnapshot
                {
                    capturedAtUtc = capturedAtUtc,
                    mode = mode,
                    isPlaying = isPlaying,
                    frameTimeMs = frameTimeMs,
                    fps = fps,
                    frameTimeSource = frameTimeSource,
                    gcAllocatedInFrameBytes = gcAllocatedInFrameBytes,
                    gcAllocatedInFrameSource = gcAllocatedInFrameSource,
                    drawCalls = drawCalls,
                    drawCallsSource = drawCallsSource,
                    batches = batches,
                    batchesSource = batchesSource,
                    setPassCalls = setPassCalls,
                    setPassCallsSource = setPassCallsSource,
                    triangles = triangles,
                    trianglesSource = trianglesSource,
                    vertices = vertices,
                    verticesSource = verticesSource,
                    monoUsedBytes = monoUsedBytes,
                    monoHeapBytes = monoHeapBytes,
                    totalAllocatedMemoryBytes = totalAllocatedMemoryBytes,
                    totalReservedMemoryBytes = totalReservedMemoryBytes,
                    gcCollectionCountGen0 = gcCollectionCountGen0,
                    gcCollectionCountGen1 = gcCollectionCountGen1,
                    gcCollectionCountGen2 = gcCollectionCountGen2,
                    scriptHotspots = new List<object>(scriptHotspots)
                };
            }
        }

        private sealed class ScriptHotspotSummary
        {
            public string typeName;
            public string fullTypeName;
            public string scriptPath;

            public int instanceCount;
            public int enabledInstanceCount;
            public bool hasUpdate;
            public bool hasFixedUpdate;
            public bool hasLateUpdate;
            public bool hasOnGUI;
            public double activityWeight;
            public double score;
            public string sampleObject;
            public string samplePath;

            public Dictionary<string, object> ToJson()
            {
                var payload = new Dictionary<string, object>
                {
                    { "typeName", typeName ?? string.Empty },
                    { "fullTypeName", fullTypeName ?? string.Empty },
                    { "instanceCount", instanceCount },
                    { "enabledInstanceCount", enabledInstanceCount },
                    { "hasUpdate", hasUpdate },
                    { "hasFixedUpdate", hasFixedUpdate },
                    { "hasLateUpdate", hasLateUpdate },
                    { "hasOnGUI", hasOnGUI },
                    { "activityWeight", activityWeight },
                    { "score", score }
                };

                if (!string.IsNullOrWhiteSpace(scriptPath)) payload["scriptPath"] = scriptPath;
                if (!string.IsNullOrWhiteSpace(sampleObject)) payload["sampleObject"] = sampleObject;
                if (!string.IsNullOrWhiteSpace(samplePath)) payload["samplePath"] = samplePath;
                return payload;
            }
        }

        private sealed class LifecycleScriptSignal
        {
            public string scriptPath;
            public bool containsSubscription;
            public bool containsUnsubscription;
        }
    }
}
