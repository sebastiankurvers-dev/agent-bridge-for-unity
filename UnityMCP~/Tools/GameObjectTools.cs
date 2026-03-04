using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class GameObjectTools
{
    [McpServerTool(Name = "unity_get_gameobject")]
    [Description("Get detailed information about a specific GameObject including its transform, components, and all serialized properties. Use include_components=false to skip component details for a lighter response.")]
    public static async Task<string> GetGameObject(
        UnityClient client,
        [Description("The instance ID of the GameObject to inspect. Get this from unity_get_hierarchy.")] int instanceId,
        [Description("If false (default), skips component details and returns only transform/metadata. Set true for full component/property payload.")] bool includeComponents = false,
        [Description("If true, returns only transform-centric metadata for lowest-latency polling.")] bool transformOnly = false)
    {
        return await client.GetGameObjectAsync(instanceId, includeComponents, transformOnly);
    }

    [McpServerTool(Name = "unity_get_components")]
    [Description("Get detailed information about all components on a GameObject, including their fields and properties. Use names_only=true for a compact list of just component type names.")]
    public static async Task<string> GetComponents(
        UnityClient client,
        [Description("The instance ID of the GameObject to inspect.")] int instanceId,
        [Description("If true (default), returns only component type names (no field values). Set false for full serialized details.")] bool namesOnly = true)
    {
        return await client.GetComponentsAsync(instanceId, namesOnly);
    }

    [McpServerTool(Name = "unity_modify_gameobject")]
    [Description("Modify a GameObject's properties including name, active state, layer, tag, and transform (position, rotation, scale).")]
    public static async Task<string> ModifyGameObject(
        UnityClient client,
        [Description("The instance ID of the GameObject to modify.")] int instanceId,
        [Description("New name for the GameObject.")] string? name = null,
        [Description("Whether the GameObject should be active.")] bool? active = null,
        [Description("The layer index for the GameObject.")] int? layer = null,
        [Description("The tag for the GameObject.")] string? tag = null,
        [Description("World position as [x, y, z].")] float[]? position = null,
        [Description("Local position as [x, y, z].")] float[]? localPosition = null,
        [Description("Euler rotation as [x, y, z] in degrees.")] float[]? rotation = null,
        [Description("Local euler rotation as [x, y, z] in degrees.")] float[]? localRotation = null,
        [Description("Local scale as [x, y, z].")] float[]? localScale = null)
    {
        var modifications = new Dictionary<string, object?>();

        if (name != null) modifications["name"] = name;
        if (active.HasValue) modifications["active"] = active.Value;
        if (layer.HasValue) modifications["layer"] = layer.Value;
        if (tag != null) modifications["tag"] = tag;

        if (position != null || localPosition != null || rotation != null || localRotation != null || localScale != null)
        {
            var transform = new Dictionary<string, object?>();

            if (position != null && position.Length >= 3)
                transform["position"] = new { x = position[0], y = position[1], z = position[2] };
            if (localPosition != null && localPosition.Length >= 3)
                transform["localPosition"] = new { x = localPosition[0], y = localPosition[1], z = localPosition[2] };
            if (rotation != null && rotation.Length >= 3)
                transform["rotation"] = new { x = rotation[0], y = rotation[1], z = rotation[2] };
            if (localRotation != null && localRotation.Length >= 3)
                transform["localRotation"] = new { x = localRotation[0], y = localRotation[1], z = localRotation[2] };
            if (localScale != null && localScale.Length >= 3)
                transform["localScale"] = new { x = localScale[0], y = localScale[1], z = localScale[2] };

            modifications["transform"] = transform;
        }

        var result = await client.ModifyGameObjectAsync(instanceId, modifications);

        // Warn if bridge reports no changes were applied (likely a schema mismatch)
        try
        {
            var json = System.Text.Json.JsonDocument.Parse(result);
            if (json.RootElement.TryGetProperty("changesApplied", out var changes) &&
                changes.GetArrayLength() == 0)
            {
                result = "[WARNING] No fields were applied to the GameObject. " +
                         "If you expected changes, verify the request format. " +
                         "Transform fields (position, localScale, etc.) must be nested inside a 'transform' object.\n\n" + result;
            }
        }
        catch { /* parse failure is not critical — return raw result */ }

        return result;
    }

    [McpServerTool(Name = "unity_spawn_prefab")]
    [Description("Instantiate a prefab in the scene. Use unity_find_prefabs first to find available prefabs.")]
    public static async Task<string> SpawnPrefab(
        UnityClient client,
        [Description("The path or name of the prefab to spawn (e.g., 'Assets/Prefabs/Player.prefab' or just 'Player').")] string prefabPath,
        [Description("Optional name for the spawned GameObject.")] string? name = null,
        [Description("World position as [x, y, z]. Defaults to origin.")] float[]? position = null,
        [Description("Euler rotation as [x, y, z] in degrees. Defaults to no rotation.")] float[]? rotation = null,
        [Description("Scale as [x, y, z]. Defaults to [1,1,1].")] float[]? scale = null,
        [Description("Instance ID of the parent GameObject.")] int? parentId = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["prefabPath"] = prefabPath
        };

        if (name != null) request["name"] = name;
        if (parentId.HasValue) request["parentId"] = parentId.Value;

        if (position != null && position.Length >= 3)
            request["position"] = new { x = position[0], y = position[1], z = position[2] };
        if (rotation != null && rotation.Length >= 3)
            request["rotation"] = new { x = rotation[0], y = rotation[1], z = rotation[2] };
        if (scale != null && scale.Length >= 3)
            request["scale"] = new { x = scale[0], y = scale[1], z = scale[2] };

        return await client.SpawnAsync(request);
    }

    [McpServerTool(Name = "unity_spawn_batch")]
    [Description("Spawn multiple prefabs in one call. More efficient than calling unity_spawn_prefab repeatedly. Each entry can specify prefab, position, rotation, scale, and parent. All spawns are grouped into a single undo operation.")]
    public static async Task<string> SpawnBatch(
        UnityClient client,
        [Description("JSON array of spawn entries. Each entry: {prefabPath: string, name?: string, position?: {x,y,z}, rotation?: {x,y,z}, scale?: {x,y,z}, parentId?: int}. Example: [{\"prefabPath\":\"Assets/Prefabs/Knight.prefab\",\"position\":{\"x\":0,\"y\":0,\"z\":5}},{\"prefabPath\":\"Assets/Prefabs/Knight.prefab\",\"position\":{\"x\":0,\"y\":0,\"z\":10}}]")] string entriesJson,
        [Description("Timeout in ms for the entire batch.")] int timeoutMs = 0)
    {
        if (string.IsNullOrWhiteSpace(entriesJson))
            return ToolErrors.ValidationError("No entries provided");

        object? parsedEntries;
        try
        {
            parsedEntries = JsonSerializer.Deserialize<object>(entriesJson);
        }
        catch (JsonException ex)
        {
            return ToolErrors.ValidationError($"Invalid JSON in entriesJson: {ex.Message}");
        }

        var request = new Dictionary<string, object?>
        {
            ["entries"] = parsedEntries
        };

        return await client.SpawnBatchAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_create_gameobject")]
    [Description("Create a new empty GameObject in the scene. Use this to create parent/folder objects for organizing your hierarchy "
        + "(e.g., 'Platforms', 'Environment', 'Lights') — then pass the returned instanceId as parentId when spawning children.")]
    public static async Task<string> CreateGameObject(
        UnityClient client,
        [Description("Name for the new GameObject.")] string name = "New GameObject",
        [Description("World position as [x, y, z]. Defaults to origin.")] float[]? position = null,
        [Description("Euler rotation as [x, y, z] in degrees.")] float[]? rotation = null,
        [Description("Scale as [x, y, z].")] float[]? scale = null,
        [Description("Instance ID of the parent GameObject.")] int? parentId = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["name"] = name
        };

        if (parentId.HasValue) request["parentId"] = parentId.Value;

        if (position != null && position.Length >= 3)
            request["position"] = new { x = position[0], y = position[1], z = position[2] };
        if (rotation != null && rotation.Length >= 3)
            request["rotation"] = new { x = rotation[0], y = rotation[1], z = rotation[2] };
        if (scale != null && scale.Length >= 3)
            request["scale"] = new { x = scale[0], y = scale[1], z = scale[2] };

        return await client.SpawnAsync(request);
    }

    [McpServerTool(Name = "unity_delete_gameobject")]
    [Description("Delete a GameObject from the scene. This operation is undoable in Unity.")]
    public static async Task<string> DeleteGameObject(
        UnityClient client,
        [Description("The instance ID of the GameObject to delete.")] int instanceId)
    {
        return await client.DeleteGameObjectAsync(instanceId);
    }

    [McpServerTool(Name = "unity_find_prefabs")]
    [Description("Search for available prefabs in the project. Use this to find prefabs before spawning them.")]
    public static async Task<string> FindPrefabs(
        UnityClient client,
        [Description("Optional search query to filter prefabs by name.")] string search = "",
        [Description("Maximum results to return (1-5000, default 100).")] int maxResults = 100)
    {
        maxResults = Math.Clamp(maxResults, 1, 5000);
        return await client.FindPrefabsAsync(search, maxResults);
    }

    [McpServerTool(Name = "unity_find_prefabs_scoped")]
    [Description("Search prefabs with folder scope filters. Use includeRoots to constrain to packs like Assets/MyAssetPack and avoid cross-pack collisions.")]
    public static async Task<string> FindPrefabsScoped(
        UnityClient client,
        [Description("Optional search query. Empty returns all prefabs in scope (limited by maxResults).")] string search = "",
        [Description("Only include assets under these roots (e.g., ['Assets/MyAssetPack']). Empty means project-wide.")] string[]? includeRoots = null,
        [Description("Exclude assets under these roots.")] string[]? excludeRoots = null,
        [Description("If true, includes subfolders recursively. If false, only direct children of the include root directories are considered.")] bool includeSubfolders = true,
        [Description("Maximum results to return (1-5000).")] int maxResults = 200,
        [Description("Search mode: 'contains', 'exact', or 'fuzzy'.")] string matchMode = "contains")
    {
        return await client.FindPrefabsScopedAsync(
            search,
            includeRoots,
            excludeRoots,
            includeSubfolders,
            maxResults,
            matchMode);
    }

    [McpServerTool(Name = "unity_find_gameobjects")]
    [Description("Search for GameObjects in the current scene by name, component type, tag, layer, or active state. Returns matching objects with their hierarchy paths and components.")]
    public static async Task<string> FindGameObjects(
        UnityClient client,
        [Description("Filter by name (case-insensitive substring match).")] string? name = null,
        [Description("Filter by component type name (e.g., 'MeshRenderer', 'Camera', 'BoxCollider').")] string? component = null,
        [Description("Filter by tag (e.g., 'Player', 'MainCamera').")] string? tag = null,
        [Description("Filter by layer name (e.g., 'UI', 'Default', 'Water').")] string? layer = null,
        [Description("Filter by active state in hierarchy (true = active only, false = inactive only).")] bool? active = null,
        [Description("If true, include each object's component name list. Default false for lighter responses.")] bool includeComponents = false,
        [Description("Maximum number of results to return (default: 100, max: 500).")] int maxResults = 100)
    {
        maxResults = Math.Clamp(maxResults, 1, 500);
        return await client.FindGameObjectsAsync(name, component, tag, layer, active, maxResults, includeComponents);
    }

    [McpServerTool(Name = "unity_batch_modify_children")]
    [Description("Bulk-modify transform/active/tag/layer on all children of a parent, with optional name/tag/component filters. More efficient than looping unity_modify_gameobject or execute_csharp for common bulk operations like adjusting ceiling height. Single undo group.")]
    public static async Task<string> BatchModifyChildren(
        UnityClient client,
        [Description("Instance ID of the parent GameObject whose children will be modified.")] int parentInstanceId,
        [Description("Filter: only modify children whose name contains this string (case-insensitive).")] string? nameContains = null,
        [Description("Filter: only modify children with this tag.")] string? tag = null,
        [Description("Filter: only modify children that have this component type.")] string? componentType = null,
        [Description("If true (default), includes all descendants recursively. If false, only direct children.")] bool recursive = true,
        [Description("Set world X position for all matching children.")] float? positionX = null,
        [Description("Set world Y position for all matching children.")] float? positionY = null,
        [Description("Set world Z position for all matching children.")] float? positionZ = null,
        [Description("Set local position as [x, y, z] for all matching children.")] float[]? localPosition = null,
        [Description("Set world euler rotation as [x, y, z] in degrees.")] float[]? rotation = null,
        [Description("Set local scale as [x, y, z] for all matching children.")] float[]? localScale = null,
        [Description("Set active state for all matching children.")] bool? active = null,
        [Description("Set tag for all matching children.")] string? setTag = null,
        [Description("Set layer (by name) for all matching children.")] string? setLayer = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["parentInstanceId"] = parentInstanceId,
            ["recursive"] = recursive ? 1 : 0
        };

        if (nameContains != null) request["nameContains"] = nameContains;
        if (tag != null) request["tag"] = tag;
        if (componentType != null) request["componentType"] = componentType;
        if (positionX.HasValue) request["positionX"] = positionX.Value;
        if (positionY.HasValue) request["positionY"] = positionY.Value;
        if (positionZ.HasValue) request["positionZ"] = positionZ.Value;
        if (localPosition != null && localPosition.Length >= 3)
            request["localPosition"] = new { x = localPosition[0], y = localPosition[1], z = localPosition[2] };
        if (rotation != null && rotation.Length >= 3)
            request["rotation"] = new { x = rotation[0], y = rotation[1], z = rotation[2] };
        if (localScale != null && localScale.Length >= 3)
            request["localScale"] = new { x = localScale[0], y = localScale[1], z = localScale[2] };
        if (active.HasValue) request["active"] = active.Value ? 1 : 0;
        if (setTag != null) request["setTag"] = setTag;
        if (setLayer != null) request["setLayer"] = setLayer;

        return await client.BatchModifyChildrenAsync(request);
    }
}
