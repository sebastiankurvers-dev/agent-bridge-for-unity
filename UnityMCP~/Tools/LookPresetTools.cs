using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class LookPresetTools
{
    [McpServerTool(Name = "unity_save_look_preset")]
    [Description(@"Capture the complete visual 'look' of the current scene as a reusable JSON preset.
Saves URP volume overrides (bloom, color adjustments, tonemapping, vignette, depth of field),
render settings (fog, ambient, skybox), camera rendering config, and ALL scene lights
to Assets/Editor/LookPresets/{name}.json.

Use this to learn from demo scenes: load a polished demo scene, then save its look as a preset
that can be applied to any new scene.

Returns the file path and a summary (not the full preset inline) to stay token-safe.
Read the saved file with standard file tools if you need the full data.")]
    public static async Task<string> SaveLookPreset(
        UnityClient client,
        [Description("Name for the preset (e.g., 'CyberCity_Night'). Used as filename.")] string name,
        [Description("Optional description of the look/mood.")] string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolErrors.ValidationError("Preset name is required");
        }

        return await client.SaveLookPresetAsync(new { name, description = description ?? "" });
    }

    [McpServerTool(Name = "unity_load_look_preset")]
    [Description(@"Apply a saved look preset to the current scene.
Loads a previously saved preset from Assets/Editor/LookPresets/{name}.json and applies
the visual settings to the current scene.

Control what gets applied with boolean flags:
- applyLights: Apply light configuration
- applyVolume: Apply URP volume overrides
- applyRenderSettings: Apply fog, ambient, skybox
- applyCamera: Apply camera rendering settings

Light merge behavior:
- replaceLights=true + matchBy=none: Delete all existing lights, spawn preset lights (default)
- replaceLights=false: Keep existing lights, add preset lights
- matchBy=name: Match lights by name (update existing, spawn missing)
- matchBy=type: Match lights by type (update first match, spawn unmatched)")]
    public static async Task<string> LoadLookPreset(
        UnityClient client,
        [Description("Name of the preset to load.")] string name,
        [Description("Load mode: 'replace' (clear matching state first) or 'merge' (add on top). Default: replace.")] string? mode = null,
        [Description("Apply light configuration (default true).")] bool applyLights = true,
        [Description("Apply URP volume overrides (default true).")] bool applyVolume = true,
        [Description("Apply render settings - fog, ambient, skybox (default true).")] bool applyRenderSettings = true,
        [Description("Apply camera rendering config (default true).")] bool applyCamera = true,
        [Description("Delete existing lights before spawning preset lights (default true). Only used when matchBy=none.")] bool replaceLights = true,
        [Description("Light matching strategy: 'none' (default), 'name', or 'type'.")] string? matchBy = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolErrors.ValidationError("Preset name is required");
        }

        return await client.LoadLookPresetAsync(new
        {
            name,
            mode = mode ?? "replace",
            applyLights = applyLights ? 1 : 0,
            applyVolume = applyVolume ? 1 : 0,
            applyRenderSettings = applyRenderSettings ? 1 : 0,
            applyCamera = applyCamera ? 1 : 0,
            replaceLights = replaceLights ? 1 : 0,
            matchBy = matchBy ?? "none"
        });
    }

    [McpServerTool(Name = "unity_list_look_presets")]
    [Description(@"List all saved look presets with metadata.
Returns name, description, source scene name, creation date, and file path for each preset.
Use this to discover available presets before loading one.")]
    public static async Task<string> ListLookPresets(UnityClient client)
    {
        return await client.ListLookPresetsAsync();
    }

    [McpServerTool(Name = "unity_apply_separation_safe_look")]
    [Description(@"Apply a fast 'separation-safe' post look guardrail for neon tile scenes.
This keeps the neon style but reduces visual tile fusion by:
- raising bloom threshold
- reducing bloom intensity
- reducing post exposure and saturation slightly

Use this after geometry/layout tuning to preserve dark gaps under heavy glow.")]
    public static async Task<string> ApplySeparationSafeLook(
        UnityClient client,
        [Description("Optional VolumeProfile asset path. If omitted, auto-resolve active scene volume.")] string? profilePath = null,
        [Description("Optional target Volume component instance ID.")] int? volumeInstanceId = null,
        [Description("Create profile automatically when missing (default true).")] bool createIfMissing = true,
        [Description("Save modified assets to disk (default true).")] bool saveAssets = true,
        [Description("Minimum bloom threshold to enforce (default 1.05).")] float bloomThresholdMin = 1.05f,
        [Description("Bloom intensity scale multiplier (default 0.6).")] float bloomIntensityScale = 0.6f,
        [Description("Maximum bloom scatter (default 0.7).")] float bloomScatterMax = 0.7f,
        [Description("Post-exposure offset applied to current value (default -0.2).")] float postExposureOffset = -0.2f,
        [Description("Saturation offset applied to current value (default -8).")] float saturationOffset = -8f)
    {
        var request = new Dictionary<string, object?>
        {
            ["createIfMissing"] = createIfMissing ? 1 : 0,
            ["saveAssets"] = saveAssets ? 1 : 0,
            ["bloomThresholdMin"] = bloomThresholdMin,
            ["bloomIntensityScale"] = bloomIntensityScale,
            ["bloomScatterMax"] = bloomScatterMax,
            ["postExposureOffset"] = postExposureOffset,
            ["saturationOffset"] = saturationOffset
        };

        if (!string.IsNullOrWhiteSpace(profilePath))
        {
            request["profilePath"] = profilePath;
        }

        if (volumeInstanceId.HasValue && volumeInstanceId.Value != 0)
        {
            request["volumeInstanceId"] = volumeInstanceId.Value;
        }

        return await client.ApplySeparationSafeLookAsync(request);
    }
}
