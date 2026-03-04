using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class FrameSequenceTools
{
    [McpServerTool(Name = "unity_capture_frame_sequence")]
    [Description(@"Capture a burst of screenshots at regular intervals during play mode.
Returns lightweight image handles for each frame — the game loop advances between captures so
animations, physics, and particles progress naturally.

Use this to evaluate time-dependent visuals: vehicle underglow sweeping across wet asphalt,
rain particles in motion, bloom spread during lane crossings, character animation sequences.

The tool starts capture, then polls internally until all frames are captured or timeout.
Maximum total duration is 30 seconds (frameCount * captureIntervalMs <= 30000).
Up to 3 concurrent captures are allowed.

Frames that fail to capture are marked as skipped with a reason (not treated as fatal).
Image handles are protected from LRU pruning while the capture is active.

Example - Capture 10 frames at 100ms intervals during gameplay:
  unity_capture_frame_sequence(frameCount=10, captureIntervalMs=100)

Example - High-res 6-frame burst for animation review:
  unity_capture_frame_sequence(frameCount=6, captureIntervalMs=200, width=1080, height=1920)

Example - Scene view capture for lighting progression:
  unity_capture_frame_sequence(viewType=""scene"", frameCount=5, captureIntervalMs=500)")]
    public static async Task<string> CaptureFrameSequence(
        UnityClient client,
        [Description("View to capture: 'game' (requires play mode) or 'scene'. Default: game.")] string viewType = "game",
        [Description("Number of frames to capture (1-60). Default: 10.")] int frameCount = 10,
        [Description("Milliseconds between captures (50-2000). Default: 100.")] int captureIntervalMs = 100,
        [Description("Capture width in pixels (0=auto, 64-1920). Default: 0 (auto).")] int width = 0,
        [Description("Capture height in pixels (0=auto, 64-1920). Default: 0 (auto).")] int height = 0,
        [Description("Image format: 'jpeg' or 'png'. Default: jpeg.")] string format = "jpeg",
        [Description("Source tag stored with image handles. Default: frame-sequence.")] string source = "frame-sequence")
    {
        frameCount = Math.Clamp(frameCount, 1, 60);
        captureIntervalMs = Math.Clamp(captureIntervalMs, 50, 2000);

        long totalMs = (long)frameCount * captureIntervalMs;
        if (totalMs > 30000)
        {
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                error = $"Total duration {totalMs}ms exceeds 30s limit. Reduce frameCount ({frameCount}) or captureIntervalMs ({captureIntervalMs})."
            });
        }

        if (width > 0) width = Math.Clamp(width, 64, 1920);
        if (height > 0) height = Math.Clamp(height, 64, 1920);

        var request = new Dictionary<string, object>
        {
            ["viewType"] = viewType ?? "game",
            ["frameCount"] = frameCount,
            ["captureIntervalMs"] = captureIntervalMs,
            ["width"] = width,
            ["height"] = height,
            ["format"] = format ?? "jpeg",
            ["source"] = source ?? "frame-sequence"
        };

        // Poll interval: half the capture interval but at least 100ms, at most 500ms
        int pollMs = Math.Clamp(captureIntervalMs / 2, 100, 500);
        // Max poll time: total capture time + 10s buffer
        int maxPollMs = (int)Math.Min(totalMs + 10000, 35000);

        return await client.CaptureFrameSequenceAsync(request, pollMs, maxPollMs);
    }
}
