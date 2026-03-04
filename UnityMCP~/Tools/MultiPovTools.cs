using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class MultiPovTools
{
    [McpServerTool(Name = "unity_multi_pov_snapshot")]
    [Description(@"Capture screenshots from multiple camera angles in one call, returning lightweight image handles.
Use this after placing objects to verify spatial correctness from different perspectives — catches clipping,
hidden objects (e.g., characters behind walls), and alignment errors that a single viewpoint misses.

Preset angles orbit around the target object (or current scene pivot if no target):
  front  - From +Z looking toward -Z
  back   - From -Z looking toward +Z
  top    - Looking straight down
  left   - From -X looking toward +X
  right  - From +X looking toward -X
  player - Camera.main game view (captured separately)

Use presets='all' for all 5 cardinal angles + player view (6 total).
Camera is automatically restored to its original position after capture.

Example - Quick verification of a placed object:
  unity_multi_pov_snapshot(targetInstanceId=12345, presets='all')

Example - Just front and top views:
  unity_multi_pov_snapshot(targetInstanceId=12345, presets='[""front"",""top""]')

Example - Custom angles:
  unity_multi_pov_snapshot(povs='[{""name"":""iso"",""rotation"":[30,45,0]}]')

Example - No target, use current camera pivot:
  unity_multi_pov_snapshot(presets='all', includePlayerView=false)")]
    public static async Task<string> MultiPovSnapshot(
        UnityClient client,
        [Description("Instance ID of the GameObject to focus on. Camera orbits this object's bounds. -1 or omit = use current scene view pivot.")] int targetInstanceId = -1,
        [Description("Preset angles: 'all' for all 5 cardinal + player, or JSON array like '[\"front\",\"top\"]'. Valid presets: front, back, top, left, right.")] string? presets = null,
        [Description("Custom POV configs as JSON array: [{\"name\":\"iso\",\"rotation\":[30,45,0],\"size\":8}]. Each POV can have name, rotation (euler), pivot ([x,y,z]), size, or preset.")] string? povs = null,
        [Description("Screenshot width in pixels (64-1920). Default 800.")] int width = 800,
        [Description("Screenshot height in pixels (64-1920). Default 600.")] int height = 600,
        [Description("Image format: 'jpeg' (smaller) or 'png' (lossless). Default 'jpeg'.")] string format = "jpeg",
        [Description("Also capture from Camera.main game view as 'player' POV. Default true.")] bool includePlayerView = true,
        [Description("Minimal response (handles + names only, no camera details). Default false.")] bool brief = false,
        [Description("Bounds-to-camera distance multiplier. Larger = more context around object. Default 1.5.")] float sizeMultiplier = 1.5f,
        [Description("Optional route timeout override in milliseconds. Default 30000.")] int timeoutMs = 30000)
    {
        var request = new Dictionary<string, object>
        {
            ["targetInstanceId"] = targetInstanceId,
            ["width"] = width,
            ["height"] = height,
            ["format"] = format ?? "jpeg",
            ["includePlayerView"] = includePlayerView ? 1 : 0,
            ["brief"] = brief ? 1 : 0,
            ["sizeMultiplier"] = sizeMultiplier
        };

        // Handle presets parameter
        if (!string.IsNullOrWhiteSpace(presets))
        {
            string trimmed = presets.Trim();
            if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                request["presetsShorthand"] = "all";
            }
            else if (trimmed.StartsWith("["))
            {
                // JSON array — pass through for bridge-side parsing
                request["presets"] = trimmed;
            }
            else if (trimmed.Contains(","))
            {
                // Comma-separated preset names like "front,top"
                var names = trimmed.Split(',').Select(n => $"\"{n.Trim()}\"");
                request["presets"] = $"[{string.Join(",", names)}]";
            }
            else
            {
                // Single preset name
                request["presets"] = $"[\"{trimmed}\"]";
            }
        }

        // Handle custom POVs
        if (!string.IsNullOrWhiteSpace(povs))
        {
            request["povs"] = povs.Trim();
        }

        return await client.MultiPovSnapshotAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }
}
