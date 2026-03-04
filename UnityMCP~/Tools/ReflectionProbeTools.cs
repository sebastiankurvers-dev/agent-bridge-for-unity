using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class ReflectionProbeTools
{
    [McpServerTool(Name = "unity_create_reflection_probe")]
    [Description("Create a ReflectionProbe for wet-road reflections and local environment lighting response.")]
    public static async Task<string> CreateReflectionProbe(
        UnityClient client,
        [Description("Optional GameObject name.")] string? name = null,
        [Description("Probe mode: Baked, Realtime, Custom.")] string? mode = null,
        [Description("Refresh mode for realtime probes: OnAwake, EveryFrame, ViaScripting.")] string? refreshMode = null,
        [Description("Time slicing mode for realtime probes.")] string? timeSlicingMode = null,
        [Description("Cubemap resolution (power of two, 16-2048).")] int? resolution = null,
        [Description("Enable HDR capture.")] bool? hdr = null,
        [Description("Probe box size [x,y,z].")] float[]? size = null,
        [Description("Probe box center offset [x,y,z].")] float[]? center = null,
        [Description("Near clip plane.")] float? nearClipPlane = null,
        [Description("Far clip plane.")] float? farClipPlane = null,
        [Description("Probe intensity multiplier.")] float? intensity = null,
        [Description("Enable box projection.")] bool? boxProjection = null,
        [Description("Background color [r,g,b] or [r,g,b,a].")] float[]? backgroundColor = null,
        [Description("Culling mask bits.")] int? cullingMask = null,
        [Description("Probe importance.")] int? importance = null,
        [Description("World position [x,y,z].")] float[]? position = null,
        [Description("World rotation Euler [x,y,z].")] float[]? rotation = null,
        [Description("Optional parent GameObject instance ID.")] int? parentId = null)
    {
        var data = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(name)) data["name"] = name;
        if (!string.IsNullOrWhiteSpace(mode)) data["mode"] = mode;
        if (!string.IsNullOrWhiteSpace(refreshMode)) data["refreshMode"] = refreshMode;
        if (!string.IsNullOrWhiteSpace(timeSlicingMode)) data["timeSlicingMode"] = timeSlicingMode;
        if (resolution.HasValue) data["resolution"] = resolution.Value;
        if (hdr.HasValue) data["hdr"] = hdr.Value;
        if (size != null) data["size"] = size;
        if (center != null) data["center"] = center;
        if (nearClipPlane.HasValue) data["nearClipPlane"] = nearClipPlane.Value;
        if (farClipPlane.HasValue) data["farClipPlane"] = farClipPlane.Value;
        if (intensity.HasValue) data["intensity"] = intensity.Value;
        if (boxProjection.HasValue) data["boxProjection"] = boxProjection.Value;
        if (backgroundColor != null) data["backgroundColor"] = backgroundColor;
        if (cullingMask.HasValue) data["cullingMask"] = cullingMask.Value;
        if (importance.HasValue) data["importance"] = importance.Value;
        if (position != null) data["position"] = position;
        if (rotation != null) data["rotation"] = rotation;
        if (parentId.HasValue && parentId.Value != 0) data["parentId"] = parentId.Value;

        return await client.CreateReflectionProbeAsync(data);
    }

    [McpServerTool(Name = "unity_modify_reflection_probe")]
    [Description("Modify an existing ReflectionProbe.")]
    public static async Task<string> ModifyReflectionProbe(
        UnityClient client,
        [Description("Target GameObject instance ID with ReflectionProbe component.")] int instanceId,
        [Description("Probe mode: Baked, Realtime, Custom.")] string? mode = null,
        [Description("Refresh mode for realtime probes: OnAwake, EveryFrame, ViaScripting.")] string? refreshMode = null,
        [Description("Time slicing mode for realtime probes.")] string? timeSlicingMode = null,
        [Description("Cubemap resolution (power of two, 16-2048).")] int? resolution = null,
        [Description("Enable HDR capture.")] bool? hdr = null,
        [Description("Probe box size [x,y,z].")] float[]? size = null,
        [Description("Probe box center offset [x,y,z].")] float[]? center = null,
        [Description("Near clip plane.")] float? nearClipPlane = null,
        [Description("Far clip plane.")] float? farClipPlane = null,
        [Description("Probe intensity multiplier.")] float? intensity = null,
        [Description("Enable box projection.")] bool? boxProjection = null,
        [Description("Background color [r,g,b] or [r,g,b,a].")] float[]? backgroundColor = null,
        [Description("Culling mask bits.")] int? cullingMask = null,
        [Description("Probe importance.")] int? importance = null,
        [Description("World position [x,y,z].")] float[]? position = null,
        [Description("World rotation Euler [x,y,z].")] float[]? rotation = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId
        };

        if (!string.IsNullOrWhiteSpace(mode)) data["mode"] = mode;
        if (!string.IsNullOrWhiteSpace(refreshMode)) data["refreshMode"] = refreshMode;
        if (!string.IsNullOrWhiteSpace(timeSlicingMode)) data["timeSlicingMode"] = timeSlicingMode;
        if (resolution.HasValue) data["resolution"] = resolution.Value;
        if (hdr.HasValue) data["hdr"] = hdr.Value;
        if (size != null) data["size"] = size;
        if (center != null) data["center"] = center;
        if (nearClipPlane.HasValue) data["nearClipPlane"] = nearClipPlane.Value;
        if (farClipPlane.HasValue) data["farClipPlane"] = farClipPlane.Value;
        if (intensity.HasValue) data["intensity"] = intensity.Value;
        if (boxProjection.HasValue) data["boxProjection"] = boxProjection.Value;
        if (backgroundColor != null) data["backgroundColor"] = backgroundColor;
        if (cullingMask.HasValue) data["cullingMask"] = cullingMask.Value;
        if (importance.HasValue) data["importance"] = importance.Value;
        if (position != null) data["position"] = position;
        if (rotation != null) data["rotation"] = rotation;

        return await client.ModifyReflectionProbeAsync(data);
    }

    [McpServerTool(Name = "unity_bake_reflection_probe")]
    [Description("Bake or refresh a reflection probe cubemap. Uses Lightmapping.BakeReflectionProbe when available.")]
    public static async Task<string> BakeReflectionProbe(
        UnityClient client,
        [Description("Target GameObject instance ID with ReflectionProbe component.")] int instanceId,
        [Description("Optional output EXR path in Assets/. If omitted, a generated path is used.")] string? path = null,
        [Description("Optional route timeout in ms. Default 30000.")] int? timeoutMs = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId
        };
        if (!string.IsNullOrWhiteSpace(path)) data["path"] = path;
        return await client.BakeReflectionProbeAsync(data, timeoutMs ?? 30000);
    }
}
