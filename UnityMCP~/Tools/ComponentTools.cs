using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class ComponentTools
{
    [McpServerTool(Name = "unity_add_component")]
    [Description("Add a component to a GameObject. Supports all Unity built-in components (Rigidbody, BoxCollider, etc.) and custom scripts.")]
    public static async Task<string> AddComponent(
        UnityClient client,
        [Description("The instance ID of the GameObject to add the component to.")] int instanceId,
        [Description("The component type name (e.g., 'Rigidbody', 'BoxCollider', 'AudioSource', or your custom script name).")] string componentType,
        [Description("Optional JSON object with initial property values to set on the component.")] string? properties = null)
    {
        if (string.IsNullOrWhiteSpace(componentType))
        {
            return ToolErrors.ValidationError("Component type is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["componentType"] = componentType
        };

        if (!string.IsNullOrEmpty(properties))
        {
            request["properties"] = properties;
        }

        return await client.AddComponentAsync(request);
    }

    [McpServerTool(Name = "unity_remove_component")]
    [Description("Remove a component from a GameObject. This operation is undoable in Unity.")]
    public static async Task<string> RemoveComponent(
        UnityClient client,
        [Description("The instance ID of the GameObject.")] int instanceId,
        [Description("The component type name to remove (e.g., 'Rigidbody', 'BoxCollider').")] string componentType)
    {
        if (string.IsNullOrWhiteSpace(componentType))
        {
            return ToolErrors.ValidationError("Component type is required");
        }

        return await client.RemoveComponentAsync(instanceId, componentType);
    }

    [McpServerTool(Name = "unity_modify_component")]
    [Description("Modify properties of a component on a GameObject. Use unity_get_components first to see available properties.")]
    public static async Task<string> ModifyComponent(
        UnityClient client,
        [Description("The instance ID of the GameObject.")] int instanceId,
        [Description("The component type name to modify.")] string componentType,
        [Description("JSON object with property names and values to set (e.g., '{\"mass\": 2.0, \"useGravity\": true}').")] string properties)
    {
        if (string.IsNullOrWhiteSpace(componentType))
        {
            return ToolErrors.ValidationError("Component type is required");
        }

        if (string.IsNullOrWhiteSpace(properties))
        {
            return ToolErrors.ValidationError("Properties are required");
        }

        var request = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["componentType"] = componentType,
            ["properties"] = properties
        };

        return await client.ModifyComponentAsync(request);
    }

    [McpServerTool(Name = "unity_patch_serialized_properties")]
    [Description("Patch serialized properties by exact property paths (including arrays/nested fields) for inspector-level precision.")]
    public static async Task<string> PatchSerializedProperties(
        UnityClient client,
        [Description("The instance ID of the GameObject.")] int instanceId,
        [Description("Component type name to patch (e.g., 'MeshRenderer', 'BoxCollider', custom script class).")] string componentType,
        [Description("JSON array of patch objects. Each patch supports: propertyPath, valueJson, objectRefAssetPath, objectRefInstanceId. Example: [{\"propertyPath\":\"m_Materials.Array.data[0]\",\"objectRefAssetPath\":\"Assets/Materials/My.mat\"}]")] string patchesJson)
    {
        if (string.IsNullOrWhiteSpace(componentType))
        {
            return ToolErrors.ValidationError("Component type is required");
        }

        if (string.IsNullOrWhiteSpace(patchesJson))
        {
            return ToolErrors.ValidationError("patchesJson is required");
        }

        try
        {
            var patches = JsonSerializer.Deserialize<JsonElement>(patchesJson);
            var request = new Dictionary<string, object?>
            {
                ["instanceId"] = instanceId,
                ["componentType"] = componentType,
                ["patches"] = patches
            };

            return await client.PatchSerializedPropertiesAsync(request);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = $"Invalid patchesJson: {ex.Message}" });
        }
    }

    [McpServerTool(Name = "unity_set_renderer_materials")]
    [Description("Set renderer materials with slot control. Use slotIndices for targeted slot replacement, or omit to replace the whole material array.")]
    public static async Task<string> SetRendererMaterials(
        UnityClient client,
        [Description("The instance ID of the GameObject containing the renderer.")] int instanceId,
        [Description("Array of material asset paths.")] string[] materialPaths,
        [Description("Optional slot indices matching materialPaths length for targeted assignment (e.g., [0,2]).")] int[]? slotIndices = null,
        [Description("Optional renderer component type if multiple renderers exist (e.g., 'SkinnedMeshRenderer').")] string? componentType = null)
    {
        if (materialPaths == null || materialPaths.Length == 0)
        {
            return ToolErrors.ValidationError("materialPaths is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["materialPaths"] = materialPaths
        };

        if (slotIndices != null && slotIndices.Length > 0)
        {
            request["slotIndices"] = slotIndices;
        }

        if (!string.IsNullOrWhiteSpace(componentType))
        {
            request["componentType"] = componentType;
        }

        return await client.SetRendererMaterialsAsync(request);
    }

    [McpServerTool(Name = "unity_get_renderer_state")]
    [Description("Inspect renderer state on a GameObject: materials, shader keywords, render queue, MaterialPropertyBlock snapshot, enabled state, bounds. " +
        "The MaterialPropertyBlock section auto-detects overridden properties via ShaderUtil. " +
        "Useful for verifying runtime material changes (e.g., emission color overrides during VFX). " +
        "Example: unity_get_renderer_state(instanceId=1234) " +
        "Example with specific MPB properties: unity_get_renderer_state(instanceId=1234, propertyNames=[\"_EmissionColor\",\"_BaseColor\"])")]
    public static async Task<string> GetRendererState(
        UnityClient client,
        [Description("The instance ID of the GameObject with the renderer.")] int instanceId,
        [Description("Renderer index if multiple renderers exist on the GameObject (default 0).")] int rendererIndex = 0,
        [Description("Optional array of MaterialPropertyBlock property names to check. If omitted, auto-detects all overridden properties from the shader.")] string[]? propertyNames = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["rendererIndex"] = rendererIndex
        };

        if (propertyNames != null && propertyNames.Length > 0)
        {
            request["propertyNames"] = propertyNames;
        }

        return await client.GetRendererStateAsync(request);
    }

    [McpServerTool(Name = "unity_reparent_gameobject")]
    [Description("Change the parent of a GameObject in the hierarchy. Can also move to root level by not specifying a parent.")]
    public static async Task<string> ReparentGameObject(
        UnityClient client,
        [Description("The instance ID of the GameObject to reparent.")] int instanceId,
        [Description("The instance ID of the new parent GameObject. Use 0 or omit to move to scene root.")] int? newParentId = null,
        [Description("The sibling index position. Use -1 for default position.")] int siblingIndex = -1,
        [Description("If true, maintains world position. If false, maintains local position.")] bool worldPositionStays = true)
    {
        var request = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["siblingIndex"] = siblingIndex,
            ["worldPositionStays"] = worldPositionStays
        };

        if (newParentId.HasValue)
        {
            request["newParentId"] = newParentId.Value;
        }

        return await client.ReparentGameObjectAsync(request);
    }

    [McpServerTool(Name = "unity_group_objects")]
    [Description("Create an empty parent GameObject and group multiple objects under it. "
        + "Useful for organizing scene hierarchies — e.g., group all 'Tree' objects, or all pavilion parts. "
        + "By default positions the group at the center of the children.")]
    public static async Task<string> GroupObjects(
        UnityClient client,
        [Description("Instance IDs of objects to group.")] int[] instanceIds,
        [Description("Name for the group parent object.")] string? name = null,
        [Description("Optional parent instance ID for the group itself.")] int parentId = -1,
        [Description("Position group at center of children (default true).")] bool centerOnChildren = true)
    {
        var request = new Dictionary<string, object?>
        {
            ["instanceIds"] = instanceIds,
            ["centerOnChildren"] = centerOnChildren ? 1 : 0,
            ["parentId"] = parentId
        };
        if (!string.IsNullOrWhiteSpace(name)) request["name"] = name;
        return await client.GroupObjectsAsync(request);
    }

    [McpServerTool(Name = "unity_scatter_objects")]
    [Description("Scatter copies of an object or prefab within a bounding box with randomized position, rotation, and scale. "
        + "Perfect for placing rocks, trees, debris, or any repeated elements with natural variation. "
        + "Supports both scene objects (duplicate) and prefabs (instantiate).")]
    public static async Task<string> ScatterObjects(
        UnityClient client,
        [Description("Number of copies to create (1-200).")] int count = 5,
        [Description("Instance ID of a scene object to duplicate. Alternative to prefabPath.")] int sourceInstanceId = 0,
        [Description("Prefab asset path to instantiate. Alternative to sourceInstanceId.")] string? prefabPath = null,
        [Description("Bounds center [x,y,z].")] float[]? boundsCenter = null,
        [Description("Bounds size [x,y,z] — objects are placed randomly within this volume.")] float[]? boundsSize = null,
        [Description("Minimum rotation [x,y,z] euler angles.")] float[]? rotationMin = null,
        [Description("Maximum rotation [x,y,z] euler angles.")] float[]? rotationMax = null,
        [Description("Minimum scale factor.")] float scaleMin = 1f,
        [Description("Maximum scale factor.")] float scaleMax = 1f,
        [Description("Use uniform scale on all axes (default true).")] bool uniformScale = true,
        [Description("Random seed for reproducible placement (0 = random).")] int seed = 0,
        [Description("Parent instance ID for scattered objects.")] int parentId = -1,
        [Description("Base name for scattered objects (appended with _N).")] string? name = null,
        [Description("Color [r,g,b] or [r,g,b,a] applied to all scattered objects.")] float[]? color = null)
    {
        if (boundsCenter == null || boundsCenter.Length < 3)
            return ToolErrors.ValidationError("boundsCenter [x,y,z] is required");
        if (boundsSize == null || boundsSize.Length < 3)
            return ToolErrors.ValidationError("boundsSize [x,y,z] is required");
        if (sourceInstanceId == 0 && string.IsNullOrWhiteSpace(prefabPath))
            return ToolErrors.ValidationError("Either sourceInstanceId or prefabPath is required");

        var request = new Dictionary<string, object?>
        {
            ["count"] = count,
            ["boundsCenter"] = boundsCenter,
            ["boundsSize"] = boundsSize,
            ["scaleMin"] = scaleMin,
            ["scaleMax"] = scaleMax,
            ["uniformScale"] = uniformScale ? 1 : 0,
            ["seed"] = seed,
            ["parentId"] = parentId
        };
        if (sourceInstanceId != 0) request["sourceInstanceId"] = sourceInstanceId;
        if (!string.IsNullOrWhiteSpace(prefabPath)) request["prefabPath"] = prefabPath;
        if (rotationMin != null) request["rotationMin"] = rotationMin;
        if (rotationMax != null) request["rotationMax"] = rotationMax;
        if (!string.IsNullOrWhiteSpace(name)) request["name"] = name;
        if (color != null) request["color"] = color;
        return await client.ScatterObjectsAsync(request);
    }

    // ─── AI Bridge Editor Tools ───

    [McpServerTool(Name = "unity_list_ai_editors")]
    [Description("List all registered AI bridge editors. These provide clean, AI-friendly property inspection " +
        "for common component types (Camera, Light, BoxCollider, Transform, Rigidbody, MeshRenderer). " +
        "Components with AI editors return cleaner property names, types, and descriptions than raw SerializedProperty iteration.")]
    public static async Task<string> ListAiEditors(UnityClient client)
    {
        return await client.GetAiBridgeEditorsAsync();
    }

    [McpServerTool(Name = "unity_ai_inspect_component")]
    [Description("Inspect a component using its AI bridge editor. Returns clean property names, types, descriptions, " +
        "and current values — much more readable than raw SerializedProperty dumps. " +
        "Use GET /components/{id}?names_only=1 first to check which components have aiEditor:true. " +
        "Example: unity_ai_inspect_component(instanceId=12345, componentType=\"Camera\")")]
    public static async Task<string> AiInspectComponent(
        UnityClient client,
        [Description("The instance ID of the GameObject.")] int instanceId,
        [Description("The component type name (e.g., 'Camera', 'Light', 'BoxCollider', 'Transform').")] string componentType)
    {
        if (string.IsNullOrWhiteSpace(componentType))
            return ToolErrors.ValidationError("componentType is required");

        return await client.AiInspectComponentAsync(instanceId, componentType);
    }

    [McpServerTool(Name = "unity_ai_apply_component")]
    [Description("Apply property changes to a component via its AI bridge editor. Uses the same clean property names " +
        "returned by unity_ai_inspect_component. Supports undo. " +
        "Example: unity_ai_apply_component(instanceId=12345, componentType=\"Camera\", values={\"fieldOfView\": 60, \"orthographic\": false})")]
    public static async Task<string> AiApplyComponent(
        UnityClient client,
        [Description("The instance ID of the GameObject.")] int instanceId,
        [Description("The component type name.")] string componentType,
        [Description("JSON object mapping property names to new values. Use the same names from unity_ai_inspect_component.")] string values)
    {
        if (string.IsNullOrWhiteSpace(componentType))
            return ToolErrors.ValidationError("componentType is required");
        if (string.IsNullOrWhiteSpace(values))
            return ToolErrors.ValidationError("values is required");

        // Parse the values string to embed in the request object
        object? parsedValues;
        try
        {
            parsedValues = JsonSerializer.Deserialize<Dictionary<string, object?>>(values);
        }
        catch
        {
            return ToolErrors.ValidationError("values must be valid JSON object");
        }

        var request = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["componentType"] = componentType,
            ["values"] = parsedValues
        };

        return await client.AiApplyComponentAsync(request);
    }
}
