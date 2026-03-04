using System;
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class PerformanceTools
{
    [McpServerTool(Name = "unity_get_performance_telemetry")]
    [Description(@"Get a fast performance telemetry snapshot from Unity.
Returns frame time/FPS, GC alloc per frame, draw calls, batches, setpass, tris/verts,
and memory totals. Optionally includes a heuristic top-script hotspot list.

This is intended as a quick validation gate after scene or gameplay changes.")]
    public static async Task<string> GetPerformanceTelemetry(
        UnityClient client,
        [Description("Include heuristic top script hotspots in the response (default false for token efficiency).")]
        bool includeHotspots = false,
        [Description("Include inactive objects in hotspot analysis (default false).")]
        bool includeInactive = false,
        [Description("Only count active+enabled MonoBehaviours in hotspot analysis (default true).")]
        bool onlyEnabledBehaviours = true,
        [Description("Max number of hotspot scripts to return (1-100, default 12).")]
        int maxHotspots = 12,
        [Description("Optional route timeout override in milliseconds.")]
        int timeoutMs = 0)
    {
        maxHotspots = Math.Clamp(maxHotspots, 1, 100);
        var request = new
        {
            includeHotspots = includeHotspots ? 1 : 0,
            includeInactive = includeInactive ? 1 : 0,
            onlyEnabledBehaviours = onlyEnabledBehaviours ? 1 : 0,
            maxHotspots
        };

        return await client.GetPerformanceTelemetryAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_capture_performance_baseline")]
    [Description(@"Capture and store a named performance baseline snapshot.
Use this before making changes, then compare with unity_check_performance_budget.")]
    public static async Task<string> CapturePerformanceBaseline(
        UnityClient client,
        [Description("Baseline name (default: 'default').")] string name = "default",
        [Description("Include script hotspots in baseline (default false for token efficiency).")] bool includeHotspots = false,
        [Description("Include inactive objects in hotspot analysis (default false).")] bool includeInactive = false,
        [Description("Only count active+enabled MonoBehaviours in hotspot analysis (default true).")] bool onlyEnabledBehaviours = true,
        [Description("Max hotspot scripts to store (1-100, default 12).")] int maxHotspots = 12,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        maxHotspots = Math.Clamp(maxHotspots, 1, 100);
        var request = new
        {
            name = string.IsNullOrWhiteSpace(name) ? "default" : name.Trim(),
            includeHotspots = includeHotspots ? 1 : 0,
            includeInactive = includeInactive ? 1 : 0,
            onlyEnabledBehaviours = onlyEnabledBehaviours ? 1 : 0,
            maxHotspots
        };

        return await client.CapturePerformanceBaselineAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_check_performance_budget")]
    [Description(@"Run performance budget checks against absolute thresholds and optional baseline deltas.
Returns passed/failed, per-metric checks, violations, current metrics, and baseline context when used.")]
    public static async Task<string> CheckPerformanceBudget(
        UnityClient client,
        [Description("Baseline name to compare against (default: 'default').")] string baseline = "default",
        [Description("Use stored baseline for delta checks (default true).")] bool useBaseline = true,
        [Description("If baseline missing, capture current snapshot as baseline and continue (default false).")] bool captureBaselineIfMissing = false,
        [Description("Include heuristic top script hotspots in response (default false for token efficiency).")] bool includeHotspots = false,
        [Description("Include inactive objects in hotspot analysis (default false).")] bool includeInactive = false,
        [Description("Only count active+enabled MonoBehaviours in hotspot analysis (default true).")] bool onlyEnabledBehaviours = true,
        [Description("Max number of hotspots to include (1-100, default 8).")] int maxHotspots = 8,
        [Description("Absolute threshold: max frame time in ms (-1 disables).")] double maxFrameTimeMs = -1,
        [Description("Absolute threshold: minimum FPS (-1 disables).")] double minFps = -1,
        [Description("Absolute threshold: max GC alloc bytes per frame (-1 disables).")] double maxGcAllocBytesPerFrame = -1,
        [Description("Absolute threshold: max draw calls (-1 disables).")] double maxDrawCalls = -1,
        [Description("Absolute threshold: max batches (-1 disables).")] double maxBatches = -1,
        [Description("Absolute threshold: max setpass calls (-1 disables).")] double maxSetPassCalls = -1,
        [Description("Absolute threshold: max total allocated memory bytes (-1 disables).")] double maxTotalAllocatedMemoryBytes = -1,
        [Description("Delta threshold vs baseline: max frame time increase ms (-1 disables).")] double maxFrameTimeDeltaMs = -1,
        [Description("Delta threshold vs baseline: max GC alloc increase bytes/frame (-1 disables).")] double maxGcAllocDeltaBytesPerFrame = -1,
        [Description("Delta threshold vs baseline: max draw calls increase (-1 disables).")] double maxDrawCallsDelta = -1,
        [Description("Delta threshold vs baseline: max batches increase (-1 disables).")] double maxBatchesDelta = -1,
        [Description("Delta threshold vs baseline: max setpass calls increase (-1 disables).")] double maxSetPassCallsDelta = -1,
        [Description("Delta threshold vs baseline: max total allocated memory increase bytes (-1 disables).")] double maxTotalAllocatedMemoryDeltaBytes = -1,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        maxHotspots = Math.Clamp(maxHotspots, 1, 100);
        var request = new
        {
            baselineName = string.IsNullOrWhiteSpace(baseline) ? "default" : baseline.Trim(),
            useBaseline = useBaseline ? 1 : 0,
            captureBaselineIfMissing = captureBaselineIfMissing ? 1 : 0,
            includeHotspots = includeHotspots ? 1 : 0,
            includeInactive = includeInactive ? 1 : 0,
            onlyEnabledBehaviours = onlyEnabledBehaviours ? 1 : 0,
            maxHotspots,
            maxFrameTimeMs,
            minFps,
            maxGcAllocBytesPerFrame,
            maxDrawCalls,
            maxBatches,
            maxSetPassCalls,
            maxTotalAllocatedMemoryBytes,
            maxFrameTimeDeltaMs,
            maxGcAllocDeltaBytesPerFrame,
            maxDrawCallsDelta,
            maxBatchesDelta,
            maxSetPassCallsDelta,
            maxTotalAllocatedMemoryDeltaBytes
        };

        return await client.CheckPerformanceBudgetAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_get_script_hotspots")]
    [Description(@"Get heuristic top script hotspots.
Hotspots are ranked by script instance count and lifecycle update-method presence
(Update/FixedUpdate/LateUpdate/OnGUI).")]
    public static async Task<string> GetScriptHotspots(
        UnityClient client,
        [Description("Include inactive objects in hotspot analysis (default false).")]
        bool includeInactive = false,
        [Description("Only count active+enabled MonoBehaviours (default true).")]
        bool onlyEnabledBehaviours = true,
        [Description("Max hotspot scripts to return (1-200, default 20).")]
        int maxHotspots = 20,
        [Description("Optional route timeout override in milliseconds.")]
        int timeoutMs = 0)
    {
        maxHotspots = Math.Clamp(maxHotspots, 1, 200);
        var request = new
        {
            includeInactive = includeInactive ? 1 : 0,
            onlyEnabledBehaviours = onlyEnabledBehaviours ? 1 : 0,
            maxHotspots
        };

        return await client.GetScriptHotspotsAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }
}
