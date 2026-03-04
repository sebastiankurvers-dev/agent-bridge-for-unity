using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class LightingTools
{
    [McpServerTool(Name = "unity_create_light")]
    [Description(@"Create a new light in the scene with full configuration.

Supported light types: Directional, Point, Spot, Area
Supported shadow modes: None, Hard, Soft

Example - Create a warm directional sun light:
  type: ""Directional"", color: [1, 0.95, 0.8], intensity: 1.5, rotation: [50, -30, 0], shadows: ""Soft""

Example - Create a blue point light:
  type: ""Point"", color: [0.3, 0.5, 1], intensity: 2, range: 10, position: [5, 3, 0]")]
    public static async Task<string> CreateLight(
        UnityClient client,
        [Description("Light type: Directional, Point, Spot, or Area.")] string type,
        [Description("Optional name for the light GameObject.")] string? name = null,
        [Description("Light color as [r, g, b] or [r, g, b, a]. Values 0-1.")] float[]? color = null,
        [Description("Light intensity. Default is 1.")] float? intensity = null,
        [Description("Range for Point/Spot lights.")] float? range = null,
        [Description("Spot angle in degrees for Spot lights.")] float? spotAngle = null,
        [Description("Shadow mode: None, Hard, or Soft.")] string? shadows = null,
        [Description("World position as [x, y, z].")] float[]? position = null,
        [Description("Euler rotation as [x, y, z] in degrees.")] float[]? rotation = null,
        [Description("Instance ID of parent GameObject.")] int? parentId = null)
    {
        var data = new Dictionary<string, object?>();
        data["type"] = type;
        if (name != null) data["name"] = name;
        if (color != null) data["color"] = color;
        if (intensity.HasValue) data["intensity"] = intensity.Value;
        if (range.HasValue) data["range"] = range.Value;
        if (spotAngle.HasValue) data["spotAngle"] = spotAngle.Value;
        if (shadows != null) data["shadows"] = shadows;
        if (position != null) data["position"] = position;
        if (rotation != null) data["rotation"] = rotation;
        if (parentId.HasValue) data["parentId"] = parentId.Value;
        return await client.CreateLightAsync(data);
    }

    [McpServerTool(Name = "unity_modify_light")]
    [Description(@"Modify an existing light's properties. Only specified properties are changed.

Example - Change light to softer warm tone:
  instanceId: 12345, color: [1, 0.9, 0.7], intensity: 0.8, shadows: ""Soft""")]
    public static async Task<string> ModifyLight(
        UnityClient client,
        [Description("Instance ID of the GameObject with the Light component.")] int instanceId,
        [Description("Light type: Directional, Point, Spot, or Area.")] string? type = null,
        [Description("Light color as [r, g, b] or [r, g, b, a]. Values 0-1.")] float[]? color = null,
        [Description("Light intensity.")] float? intensity = null,
        [Description("Range for Point/Spot lights.")] float? range = null,
        [Description("Spot angle in degrees for Spot lights.")] float? spotAngle = null,
        [Description("Shadow mode: None, Hard, or Soft.")] string? shadows = null)
    {
        var data = new Dictionary<string, object?>();
        data["instanceId"] = instanceId;
        if (type != null) data["type"] = type;
        if (color != null) data["color"] = color;
        if (intensity.HasValue) data["intensity"] = intensity.Value;
        if (range.HasValue) data["range"] = range.Value;
        if (spotAngle.HasValue) data["spotAngle"] = spotAngle.Value;
        if (shadows != null) data["shadows"] = shadows;
        return await client.ModifyLightAsync(data);
    }

    [McpServerTool(Name = "unity_get_render_settings")]
    [Description(@"Get the current render settings including ambient lighting, fog, skybox, and reflection settings.
Use this to understand the current environment setup before making changes.")]
    public static async Task<string> GetRenderSettings(UnityClient client)
    {
        return await client.GetRenderSettingsAsync();
    }

    [McpServerTool(Name = "unity_set_render_settings")]
    [Description(@"Set render settings for ambient lighting, fog, skybox, and reflections.
Only specified properties are changed.

Ambient modes: Skybox, Trilight, Flat, Custom
Fog modes: Linear, Exponential, ExponentialSquared

Example - Set flat ambient with fog:
  ambientMode: ""Flat"", ambientLight: [0.2, 0.2, 0.3, 1], fog: true, fogMode: ""Exponential"", fogDensity: 0.02

Example - Set trilight ambient:
  ambientMode: ""Trilight"", ambientSkyColor: [0.5, 0.6, 0.8], ambientEquatorColor: [0.3, 0.3, 0.3], ambientGroundColor: [0.1, 0.08, 0.05]")]
    public static async Task<string> SetRenderSettings(
        UnityClient client,
        [Description("Ambient mode: Skybox, Trilight, Flat, or Custom.")] string? ambientMode = null,
        [Description("Ambient light color as [r, g, b, a] (used with Flat mode).")] float[]? ambientLight = null,
        [Description("Sky color for Trilight mode as [r, g, b, a].")] float[]? ambientSkyColor = null,
        [Description("Equator color for Trilight mode as [r, g, b, a].")] float[]? ambientEquatorColor = null,
        [Description("Ground color for Trilight mode as [r, g, b, a].")] float[]? ambientGroundColor = null,
        [Description("Enable or disable fog.")] bool? fog = null,
        [Description("Fog mode: Linear, Exponential, or ExponentialSquared.")] string? fogMode = null,
        [Description("Fog color as [r, g, b, a].")] float[]? fogColor = null,
        [Description("Fog density (for Exponential modes).")] float? fogDensity = null,
        [Description("Fog start distance (for Linear mode).")] float? fogStartDistance = null,
        [Description("Fog end distance (for Linear mode).")] float? fogEndDistance = null,
        [Description("Path to skybox material asset.")] string? skyboxMaterialPath = null,
        [Description("Reflection intensity (0-1).")] float? reflectionIntensity = null)
    {
        var data = new Dictionary<string, object?>();
        if (ambientMode != null) data["ambientMode"] = ambientMode;
        if (ambientLight != null) data["ambientLight"] = ambientLight;
        if (ambientSkyColor != null) data["ambientSkyColor"] = ambientSkyColor;
        if (ambientEquatorColor != null) data["ambientEquatorColor"] = ambientEquatorColor;
        if (ambientGroundColor != null) data["ambientGroundColor"] = ambientGroundColor;
        if (fog.HasValue) data["fog"] = fog.Value ? 1 : 0;
        if (fogMode != null) data["fogMode"] = fogMode;
        if (fogColor != null) data["fogColor"] = fogColor;
        if (fogDensity.HasValue) data["fogDensity"] = fogDensity.Value;
        if (fogStartDistance.HasValue) data["fogStartDistance"] = fogStartDistance.Value;
        if (fogEndDistance.HasValue) data["fogEndDistance"] = fogEndDistance.Value;
        if (skyboxMaterialPath != null) data["skyboxMaterialPath"] = skyboxMaterialPath;
        if (reflectionIntensity.HasValue) data["reflectionIntensity"] = reflectionIntensity.Value;
        return await client.SetRenderSettingsAsync(data);
    }

    [McpServerTool(Name = "unity_get_volume_profile")]
    [Description(@"Get URP Volume profile overrides used for look reproduction.
Returns Bloom, ColorAdjustments (including postExposure), Tonemapping, Vignette, and DepthOfField.
You can target by profile path or by scene Volume instanceId. If omitted, the best available scene volume is used.")]
    public static async Task<string> GetVolumeProfile(
        UnityClient client,
        [Description("Optional VolumeProfile asset path (e.g., 'Assets/Settings/Volumes/CityLook.asset').")] string? profilePath = null,
        [Description("Optional Volume component GameObject instance ID.")] int? volumeInstanceId = null,
        [Description("Include ambient/fog render settings in response for look hooks (default true).")] bool includeRenderHooks = true)
    {
        return await client.GetVolumeProfileAsync(profilePath, volumeInstanceId ?? 0, includeRenderHooks);
    }

    [McpServerTool(Name = "unity_set_volume_profile_overrides")]
    [Description(@"Patch URP Volume overrides in one call.
Supports bloom, colorAdjustments, tonemapping, vignette, depthOfField, exposure, and optional renderSettings hook.
Pass overrides as a JSON object, for example:
{""bloom"":{""active"":true,""intensity"":0.8},""tonemapping"":{""mode"":""ACES""}}")]
    public static async Task<string> SetVolumeProfileOverrides(
        UnityClient client,
        [Description("JSON object with override sections.")] string overridesJson,
        [Description("Optional VolumeProfile asset path.")] string? profilePath = null,
        [Description("Optional target Volume instance ID when profilePath is omitted.")] int? volumeInstanceId = null,
        [Description("Create profile automatically when missing (default true).")] bool createIfMissing = true,
        [Description("Save modified assets to disk (default true).")] bool saveAssets = true)
    {
        if (string.IsNullOrWhiteSpace(overridesJson))
        {
            return ToolErrors.ValidationError("overridesJson is required");
        }

        try
        {
            var overridesElement = JsonSerializer.Deserialize<JsonElement>(overridesJson);
            var request = new Dictionary<string, object?>
            {
                ["overrides"] = overridesElement,
                ["createIfMissing"] = createIfMissing ? 1 : 0,
                ["saveAssets"] = saveAssets ? 1 : 0
            };

            if (!string.IsNullOrWhiteSpace(profilePath)) request["profilePath"] = profilePath;
            if (volumeInstanceId.HasValue && volumeInstanceId.Value != 0) request["volumeInstanceId"] = volumeInstanceId.Value;

            return await client.SetVolumeProfileOverridesAsync(request);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = $"Invalid overridesJson: {ex.Message}" });
        }
    }

    [McpServerTool(Name = "unity_get_camera_rendering")]
    [Description(@"Get URP camera rendering settings for visual matching.
Includes render type, post-processing toggle, antialiasing, depth/color options, volume mask/trigger, and core camera fields.")]
    public static async Task<string> GetCameraRendering(
        UnityClient client,
        [Description("Optional camera GameObject instance ID.")] int? instanceId = null,
        [Description("Optional camera name when instanceId is not provided.")] string? cameraName = null)
    {
        return await client.GetCameraRenderingAsync(instanceId, cameraName);
    }

    [McpServerTool(Name = "unity_set_camera_rendering")]
    [Description(@"Patch URP camera rendering settings in one call.
Pass a JSON object with fields like renderPostProcessing, antialiasing, renderType, volumeLayerMask, clearFlags, fieldOfView, etc.")]
    public static async Task<string> SetCameraRendering(
        UnityClient client,
        [Description("JSON object patch for camera rendering settings. Must include instanceId or cameraName.")] string patchJson)
    {
        if (string.IsNullOrWhiteSpace(patchJson))
        {
            return ToolErrors.ValidationError("patchJson is required");
        }

        try
        {
            var patchElement = JsonSerializer.Deserialize<JsonElement>(patchJson);
            return await client.SetCameraRenderingAsync(patchElement);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = $"Invalid patchJson: {ex.Message}" });
        }
    }

    [McpServerTool(Name = "unity_create_procedural_skybox")]
    [Description("Create a procedural skybox material using Unity's built-in Skybox/Procedural shader and optionally apply it to the scene. "
        + "Parameters control sun appearance, atmosphere, sky tint, ground color, and exposure. "
        + "Much easier than creating a skybox material manually — one call sets up the entire sky.")]
    public static async Task<string> CreateProceduralSkybox(
        UnityClient client,
        [Description("Material asset name.")] string? name = null,
        [Description("Save path (e.g., 'Assets/Materials/DuskSky.mat').")] string? path = null,
        [Description("Sun disk size (0-1, default 0.04).")] float sunSize = 0.04f,
        [Description("Sun size convergence (1-10, default 5).")] int sunSizeConvergence = 5,
        [Description("Atmosphere thickness (0-5, default 1).")] float atmosphereThickness = 1f,
        [Description("Sky tint color [r,g,b].")] float[]? skyTint = null,
        [Description("Ground color [r,g,b].")] float[]? groundColor = null,
        [Description("Exposure (0-8, default 1.3).")] float exposure = 1.3f,
        [Description("Apply skybox to current scene (default true).")] bool applySkybox = true)
    {
        var request = new Dictionary<string, object?>
        {
            ["sunSize"] = sunSize,
            ["sunSizeConvergence"] = sunSizeConvergence,
            ["atmosphereThickness"] = atmosphereThickness,
            ["exposure"] = exposure,
            ["applySkybox"] = applySkybox ? 1 : 0
        };
        if (!string.IsNullOrWhiteSpace(name)) request["name"] = name;
        if (!string.IsNullOrWhiteSpace(path)) request["path"] = path;
        if (skyTint != null) request["skyTint"] = skyTint;
        if (groundColor != null) request["groundColor"] = groundColor;
        return await client.CreateProceduralSkyboxAsync(request);
    }
}
