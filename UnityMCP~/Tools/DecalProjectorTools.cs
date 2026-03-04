using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class DecalProjectorTools
{
    [McpServerTool(Name = "unity_create_decal_projector")]
    [Description("Create a URP DecalProjector. Useful for underglow-style projections and wet-road light accents. Mobile support varies by renderer and feature setup.")]
    public static async Task<string> CreateDecalProjector(
        UnityClient client,
        [Description("Optional GameObject name.")] string? name = null,
        [Description("Material asset path for decal projection.")] string? material = null,
        [Description("Decal box size [x,y,z].")] float[]? size = null,
        [Description("Decal pivot [x,y,z].")] float[]? pivot = null,
        [Description("Decal fade factor (0-1).")] float? fadeFactor = null,
        [Description("Decal draw distance.")] float? drawDistance = null,
        [Description("Start angle fade in degrees.")] float? startAngleFade = null,
        [Description("End angle fade in degrees.")] float? endAngleFade = null,
        [Description("UV scale [x,y].")] float[]? uvScale = null,
        [Description("UV bias [x,y].")] float[]? uvBias = null,
        [Description("Scale mode: ScaleInvariant or InheritFromHierarchy.")] string? scaleMode = null,
        [Description("Rendering layer mask as int bits.")] int? renderingLayerMask = null,
        [Description("World position [x,y,z].")] float[]? position = null,
        [Description("World rotation Euler [x,y,z].")] float[]? rotation = null,
        [Description("Parent GameObject instance ID.")] int? parentId = null,
        [Description("Auto-enable DecalRendererFeature on default URP renderer.")] bool autoEnableFeature = true)
    {
        var data = new Dictionary<string, object?>
        {
            ["autoEnableFeature"] = autoEnableFeature
        };

        if (!string.IsNullOrWhiteSpace(name)) data["name"] = name;
        if (!string.IsNullOrWhiteSpace(material)) data["material"] = material;
        if (size != null) data["size"] = size;
        if (pivot != null) data["pivot"] = pivot;
        if (fadeFactor.HasValue) data["fadeFactor"] = fadeFactor.Value;
        if (drawDistance.HasValue) data["drawDistance"] = drawDistance.Value;
        if (startAngleFade.HasValue) data["startAngleFade"] = startAngleFade.Value;
        if (endAngleFade.HasValue) data["endAngleFade"] = endAngleFade.Value;
        if (uvScale != null) data["uvScale"] = uvScale;
        if (uvBias != null) data["uvBias"] = uvBias;
        if (!string.IsNullOrWhiteSpace(scaleMode)) data["scaleMode"] = scaleMode;
        if (renderingLayerMask.HasValue) data["renderingLayerMask"] = renderingLayerMask.Value;
        if (position != null) data["position"] = position;
        if (rotation != null) data["rotation"] = rotation;
        if (parentId.HasValue && parentId.Value != 0) data["parentId"] = parentId.Value;

        return await client.CreateDecalProjectorAsync(data);
    }

    [McpServerTool(Name = "unity_modify_decal_projector")]
    [Description("Modify an existing URP DecalProjector.")]
    public static async Task<string> ModifyDecalProjector(
        UnityClient client,
        [Description("Target GameObject instance ID with DecalProjector component.")] int instanceId,
        [Description("Material asset path for decal projection.")] string? material = null,
        [Description("Decal box size [x,y,z].")] float[]? size = null,
        [Description("Decal pivot [x,y,z].")] float[]? pivot = null,
        [Description("Decal fade factor (0-1).")] float? fadeFactor = null,
        [Description("Decal draw distance.")] float? drawDistance = null,
        [Description("Start angle fade in degrees.")] float? startAngleFade = null,
        [Description("End angle fade in degrees.")] float? endAngleFade = null,
        [Description("UV scale [x,y].")] float[]? uvScale = null,
        [Description("UV bias [x,y].")] float[]? uvBias = null,
        [Description("Scale mode: ScaleInvariant or InheritFromHierarchy.")] string? scaleMode = null,
        [Description("Rendering layer mask as int bits.")] int? renderingLayerMask = null,
        [Description("World position [x,y,z].")] float[]? position = null,
        [Description("World rotation Euler [x,y,z].")] float[]? rotation = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId
        };

        if (!string.IsNullOrWhiteSpace(material)) data["material"] = material;
        if (size != null) data["size"] = size;
        if (pivot != null) data["pivot"] = pivot;
        if (fadeFactor.HasValue) data["fadeFactor"] = fadeFactor.Value;
        if (drawDistance.HasValue) data["drawDistance"] = drawDistance.Value;
        if (startAngleFade.HasValue) data["startAngleFade"] = startAngleFade.Value;
        if (endAngleFade.HasValue) data["endAngleFade"] = endAngleFade.Value;
        if (uvScale != null) data["uvScale"] = uvScale;
        if (uvBias != null) data["uvBias"] = uvBias;
        if (!string.IsNullOrWhiteSpace(scaleMode)) data["scaleMode"] = scaleMode;
        if (renderingLayerMask.HasValue) data["renderingLayerMask"] = renderingLayerMask.Value;
        if (position != null) data["position"] = position;
        if (rotation != null) data["rotation"] = rotation;

        return await client.ModifyDecalProjectorAsync(data);
    }
}
