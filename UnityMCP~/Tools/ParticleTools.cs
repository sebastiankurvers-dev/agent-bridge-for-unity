using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class ParticleTools
{
    [McpServerTool(Name = "unity_get_particle_system")]
    [Description(@"Read ParticleSystem module configuration from a GameObject.

Returns structured JSON with per-module properties. MinMaxCurve values are returned as
{""mode"":""Constant"",""value"":5} or {""mode"":""TwoConstants"",""min"":3,""max"":7}.

Modules: main, emission, shape, renderer, colorOverLifetime, sizeOverLifetime.
Filter with 'modules' param (comma-separated) or omit for all.

Example:
  instanceId: 12345, modules: ""main,emission""")]
    public static async Task<string> GetParticleSystem(
        UnityClient client,
        [Description("Instance ID of the GameObject with ParticleSystem.")] int instanceId,
        [Description("Comma-separated module filter (e.g. 'main,emission'). Omit for all modules.")] string? modules = null)
    {
        return await client.GetParticleSystemAsync(instanceId, modules);
    }

    [McpServerTool(Name = "unity_configure_particle_system")]
    [Description(@"Configure ParticleSystem modules on a GameObject. Adds ParticleSystem if createIfMissing is true (default).
Only specified properties are changed. Supports main, emission, shape, renderer, colorOverLifetime, sizeOverLifetime modules.

MinMaxCurve properties accept two forms:
  - Constant: startSpeed: 10
  - TwoConstants (random between): startSpeedRange: [3, 7]
If both constant and range are provided, range wins.

Shape types: Sphere, Hemisphere, Cone, Box, Mesh, MeshRenderer, SkinnedMeshRenderer, Circle, Edge
Simulation spaces: Local, World, Custom
Scaling modes: Hierarchy, Local, Shape
Renderer modes: Billboard, Stretch, HorizontalBillboard, VerticalBillboard, Mesh

Example - Fire effect:
  instanceId: 12345, startLifetimeRange: [0.5, 1.5], startSpeedRange: [3, 7],
  startColor: [1, 0.5, 0, 1], maxParticles: 200, emissionRateOverTime: 50,
  shapeEnabled: true, shapeType: ""Cone"", shapeAngle: 25

Example - Simple burst:
  instanceId: 12345, duration: 0.5, looping: false, startSpeed: 15, startSize: 0.3")]
    public static async Task<string> ConfigureParticleSystem(
        UnityClient client,
        [Description("Instance ID of the GameObject.")] int instanceId,
        [Description("Add ParticleSystem if missing (default true).")] bool? createIfMissing = null,
        // ---- Main module ----
        [Description("Duration in seconds.")] float? duration = null,
        [Description("Whether the system loops.")] bool? looping = null,
        [Description("Play automatically on awake.")] bool? playOnAwake = null,
        [Description("Start lifetime constant value.")] float? startLifetime = null,
        [Description("Start lifetime random range as [min, max].")] float[]? startLifetimeRange = null,
        [Description("Start speed constant value.")] float? startSpeed = null,
        [Description("Start speed random range as [min, max].")] float[]? startSpeedRange = null,
        [Description("Start size constant value.")] float? startSize = null,
        [Description("Start size random range as [min, max].")] float[]? startSizeRange = null,
        [Description("Start color as [r, g, b, a]. Values 0-1.")] float[]? startColor = null,
        [Description("Max particles alive at once.")] int? maxParticles = null,
        [Description("Gravity modifier constant value.")] float? gravityModifier = null,
        [Description("Gravity modifier random range as [min, max].")] float[]? gravityModifierRange = null,
        [Description("Simulation speed multiplier.")] float? simulationSpeed = null,
        [Description("Simulation space: Local, World, or Custom.")] string? simulationSpace = null,
        [Description("Scaling mode: Hierarchy, Local, or Shape.")] string? scalingMode = null,
        [Description("Start rotation constant (degrees).")] float? startRotation = null,
        [Description("Start rotation random range as [min, max] (degrees).")] float[]? startRotationRange = null,
        // ---- Emission module ----
        [Description("Enable/disable emission module.")] bool? emissionEnabled = null,
        [Description("Emission rate over time constant.")] float? emissionRateOverTime = null,
        [Description("Emission rate over time range as [min, max].")] float[]? emissionRateOverTimeRange = null,
        [Description("Emission rate over distance constant.")] float? emissionRateOverDistance = null,
        [Description("Emission rate over distance range as [min, max].")] float[]? emissionRateOverDistanceRange = null,
        // ---- Shape module ----
        [Description("Enable/disable shape module.")] bool? shapeEnabled = null,
        [Description("Shape type: Sphere, Hemisphere, Cone, Box, Mesh, Circle, Edge, etc.")] string? shapeType = null,
        [Description("Shape radius.")] float? shapeRadius = null,
        [Description("Shape radius thickness (0-1).")] float? shapeRadiusThickness = null,
        [Description("Shape angle (for Cone).")] float? shapeAngle = null,
        [Description("Shape arc (degrees, 0-360).")] float? shapeArc = null,
        [Description("Shape scale as [x, y, z].")] float[]? shapeScale = null,
        [Description("Shape position offset as [x, y, z].")] float[]? shapePosition = null,
        [Description("Shape rotation as [x, y, z] degrees.")] float[]? shapeRotation = null,
        // ---- Renderer module ----
        [Description("Render mode: Billboard, Stretch, HorizontalBillboard, VerticalBillboard, Mesh.")] string? rendererMode = null,
        [Description("Material asset path for the renderer.")] string? rendererMaterial = null,
        [Description("Renderer sorting order.")] int? rendererSortingOrder = null,
        [Description("Min particle size (0-1 of viewport).")] float? rendererMinParticleSize = null,
        [Description("Max particle size (0-1 of viewport).")] float? rendererMaxParticleSize = null,
        // ---- Color over Lifetime module ----
        [Description("Enable/disable Color over Lifetime module.")] bool? colorOverLifetimeEnabled = null,
        [Description("Gradient start color as [r, g, b, a].")] float[]? colorOverLifetimeStart = null,
        [Description("Gradient end color as [r, g, b, a].")] float[]? colorOverLifetimeEnd = null,
        // ---- Size over Lifetime module ----
        [Description("Enable/disable Size over Lifetime module.")] bool? sizeOverLifetimeEnabled = null,
        [Description("Size over lifetime multiplier.")] float? sizeOverLifetimeSizeMultiplier = null)
    {
        var data = new Dictionary<string, object?>();
        data["instanceId"] = instanceId;
        if (createIfMissing.HasValue) data["createIfMissing"] = createIfMissing.Value ? 1 : 0;

        // ---- Main module ----
        if (duration.HasValue) data["duration"] = duration.Value;
        if (looping.HasValue) data["looping"] = looping.Value ? 1 : 0;
        if (playOnAwake.HasValue) data["playOnAwake"] = playOnAwake.Value ? 1 : 0;
        AddMinMaxCurve(data, "startLifetime", startLifetime, startLifetimeRange);
        AddMinMaxCurve(data, "startSpeed", startSpeed, startSpeedRange);
        AddMinMaxCurve(data, "startSize", startSize, startSizeRange);
        if (startColor != null) data["startColor"] = startColor;
        if (maxParticles.HasValue) data["maxParticles"] = maxParticles.Value;
        AddMinMaxCurve(data, "gravityModifier", gravityModifier, gravityModifierRange);
        if (simulationSpeed.HasValue) data["simulationSpeed"] = simulationSpeed.Value;
        if (simulationSpace != null) data["simulationSpace"] = simulationSpace;
        if (scalingMode != null) data["scalingMode"] = scalingMode;
        AddMinMaxCurve(data, "startRotation", startRotation, startRotationRange);

        // ---- Emission module ----
        if (emissionEnabled.HasValue) data["emissionEnabled"] = emissionEnabled.Value ? 1 : 0;
        AddMinMaxCurve(data, "emissionRateOverTime", emissionRateOverTime, emissionRateOverTimeRange);
        AddMinMaxCurve(data, "emissionRateOverDistance", emissionRateOverDistance, emissionRateOverDistanceRange);

        // ---- Shape module ----
        if (shapeEnabled.HasValue) data["shapeEnabled"] = shapeEnabled.Value ? 1 : 0;
        if (shapeType != null) data["shapeType"] = shapeType;
        if (shapeRadius.HasValue) data["shapeRadius"] = shapeRadius.Value;
        if (shapeRadiusThickness.HasValue) data["shapeRadiusThickness"] = shapeRadiusThickness.Value;
        if (shapeAngle.HasValue) data["shapeAngle"] = shapeAngle.Value;
        if (shapeArc.HasValue) data["shapeArc"] = shapeArc.Value;
        if (shapeScale != null) data["shapeScale"] = shapeScale;
        if (shapePosition != null) data["shapePosition"] = shapePosition;
        if (shapeRotation != null) data["shapeRotation"] = shapeRotation;

        // ---- Renderer module ----
        if (rendererMode != null) data["rendererMode"] = rendererMode;
        if (rendererMaterial != null) data["rendererMaterial"] = rendererMaterial;
        if (rendererSortingOrder.HasValue) data["rendererSortingOrder"] = rendererSortingOrder.Value;
        if (rendererMinParticleSize.HasValue) data["rendererMinParticleSize"] = rendererMinParticleSize.Value;
        if (rendererMaxParticleSize.HasValue) data["rendererMaxParticleSize"] = rendererMaxParticleSize.Value;

        // ---- Color over Lifetime module ----
        if (colorOverLifetimeEnabled.HasValue) data["colorOverLifetimeEnabled"] = colorOverLifetimeEnabled.Value ? 1 : 0;
        if (colorOverLifetimeStart != null) data["colorOverLifetimeStart"] = colorOverLifetimeStart;
        if (colorOverLifetimeEnd != null) data["colorOverLifetimeEnd"] = colorOverLifetimeEnd;

        // ---- Size over Lifetime module ----
        if (sizeOverLifetimeEnabled.HasValue) data["sizeOverLifetimeEnabled"] = sizeOverLifetimeEnabled.Value ? 1 : 0;
        if (sizeOverLifetimeSizeMultiplier.HasValue) data["sizeOverLifetimeSizeMultiplier"] = sizeOverLifetimeSizeMultiplier.Value;

        return await client.ConfigureParticleSystemAsync(data);
    }

    [McpServerTool(Name = "unity_create_particle_template")]
    [Description(@"Create a complete particle effect from a preset template in one call. Much faster than manually configuring 20+ particle properties.

Available templates:
  fire — Rising flames with orange-red color-over-lifetime gradient
  smoke — Slow rising gray particles that expand over time
  rain — Fast downward streaks from a wide box emitter above
  sparks — Fast bright particles with gravity, stretch rendering
  snow — Gentle falling flakes with random rotation
  dust — Slow ambient floating particles with fade-in/fade-out
  fountain — Upward water jets that arc down with gravity
  fireflies — Slow glowing particles with pulsing alpha

Use 'scale' to resize the entire effect proportionally.
Use 'intensity' to increase/decrease particle count and emission rate.
Use 'color' to override the template's default start color.

Example: unity_create_particle_template(template=""fire"", position=[0,0,0], scale=2, intensity=1.5)")]
    public static async Task<string> CreateParticleTemplate(
        UnityClient client,
        [Description("Template name: fire, smoke, rain, sparks, snow, dust, fountain, fireflies.")] string template,
        [Description("GameObject name.")] string? name = null,
        [Description("World position as [x, y, z].")] float[]? position = null,
        [Description("Euler rotation as [x, y, z] in degrees.")] float[]? rotation = null,
        [Description("Override start color as [r, g, b] or [r, g, b, a]. Values 0-1.")] float[]? color = null,
        [Description("Instance ID of parent GameObject.")] int parentId = -1,
        [Description("Uniform scale multiplier for the entire effect (default 1).")] float scale = 1f,
        [Description("Intensity multiplier — scales emission rate and max particles (default 1).")] float intensity = 1f)
    {
        var validTemplates = new[] { "fire", "smoke", "rain", "sparks", "snow", "dust", "fountain", "fireflies" };
        if (string.IsNullOrWhiteSpace(template) ||
            !Array.Exists(validTemplates, t => t.Equals(template, StringComparison.OrdinalIgnoreCase)))
        {
            return ToolErrors.ValidationError(
                $"template must be one of: {string.Join(", ", validTemplates)}");
        }

        var data = new Dictionary<string, object?>
        {
            ["template"] = template
        };

        if (!string.IsNullOrEmpty(name)) data["name"] = name;
        if (position != null) data["position"] = position;
        if (rotation != null) data["rotation"] = rotation;
        if (color != null) data["color"] = color;
        if (parentId != -1) data["parentId"] = parentId;
        if (scale != 1f) data["scale"] = scale;
        if (intensity != 1f) data["intensity"] = intensity;

        return await client.CreateParticleTemplateAsync(data);
    }

    /// <summary>
    /// Adds a MinMaxCurve field to the data dictionary. Range wins over constant if both provided.
    /// </summary>
    private static void AddMinMaxCurve(Dictionary<string, object?> data, string fieldName, float? constant, float[]? range)
    {
        if (range != null && range.Length >= 2)
        {
            data[$"{fieldName}Range"] = range;
        }
        else if (constant.HasValue)
        {
            data[fieldName] = constant.Value;
        }
    }
}
