using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class ProjectTools
{
    #region Tag Tools

    [McpServerTool(Name = "unity_get_tags")]
    [Description("Get all tags defined in the Unity project.")]
    public static async Task<string> GetTags(UnityClient client)
    {
        return await client.GetTagsAsync();
    }

    [McpServerTool(Name = "unity_create_tag")]
    [Description("Create a new tag in the Unity project. Tags are used to identify GameObjects.")]
    public static async Task<string> CreateTag(
        UnityClient client,
        [Description("The name for the new tag.")] string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return ToolErrors.ValidationError("Tag name is required");
        }

        return await client.CreateTagAsync(tagName);
    }

    #endregion

    #region Layer Tools

    [McpServerTool(Name = "unity_get_layers")]
    [Description("Get all layers defined in the Unity project. Shows both built-in layers (0-7) and user layers (8-31).")]
    public static async Task<string> GetLayers(UnityClient client)
    {
        return await client.GetLayersAsync();
    }

    [McpServerTool(Name = "unity_create_layer")]
    [Description("Create a new layer in the Unity project. Layers are used for physics, rendering, and raycasting.")]
    public static async Task<string> CreateLayer(
        UnityClient client,
        [Description("The name for the new layer.")] string layerName,
        [Description("Optional specific layer index (8-31). If not specified, uses first available slot.")] int index = -1)
    {
        if (string.IsNullOrWhiteSpace(layerName))
        {
            return ToolErrors.ValidationError("Layer name is required");
        }

        return await client.CreateLayerAsync(layerName, index);
    }

    #endregion

    #region Project Index Tools

    [McpServerTool(Name = "unity_get_project_index")]
    [Description("Get a comprehensive index of the entire Unity project including all scripts, prefabs, scenes, materials, ScriptableObjects, packages, assemblies, tags, and layers. This is useful for understanding the project structure.")]
    public static async Task<string> GetProjectIndex(
        UnityClient client,
        [Description("If true, pretty-prints JSON for readability. Default false for token efficiency.")] bool pretty = false,
        [Description("If true (default), returns compact summary + previews. Set false for full exhaustive index.")] bool summary = true,
        [Description("Maximum preview entries per category when summary=true (5-500).")] int maxEntries = 50,
        [Description("Cache TTL in seconds (0-300). Default 15. Set 0 to force fresh re-scan.")] int cacheSeconds = 15,
        [Description("When summary=false, include public/serialized script members. Default false to keep full index size manageable.")] bool includeScriptMembers = false)
    {
        maxEntries = Math.Clamp(maxEntries, 5, 500);
        cacheSeconds = Math.Clamp(cacheSeconds, 0, 300);
        return await client.GetProjectIndexAsync(pretty, summary, maxEntries, cacheSeconds, includeScriptMembers);
    }

    [McpServerTool(Name = "unity_search_project")]
    [Description("Search for assets in the Unity project by name or type.")]
    public static async Task<string> SearchProject(
        UnityClient client,
        [Description("Search query to match asset names.")] string query,
        [Description("Optional asset type filter (e.g., 'Script', 'Prefab', 'Material', 'Scene', 'Texture').")] string? assetType = null,
        [Description("Maximum results to return (1-1000). Default 50 for token efficiency.")] int maxResults = 50,
        [Description("If true, include GUID values in results. Default false to keep payload compact.")] bool includeGuids = false,
        [Description("Cache TTL in seconds (0-300). Default 10. Set 0 to force fresh search.")] int cacheSeconds = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return ToolErrors.ValidationError("Search query is required");
        }

        maxResults = Math.Clamp(maxResults, 1, 1000);
        cacheSeconds = Math.Clamp(cacheSeconds, 0, 300);
        return await client.SearchProjectAsync(query, assetType, maxResults, includeGuids, cacheSeconds);
    }

    #endregion

    #region Scene Layout Snapshot

    [McpServerTool(Name = "unity_get_scene_layout_snapshot")]
    [Description(@"Single-call scene layout assessment returning camera state, player position, tile spacing statistics, and render summary.
Replaces multiple separate calls (get camera, find player, measure tiles, get render settings) with one roundtrip.
Tile stats use sampled nearest-neighbor spacing (O(n), not O(n²)) and X-clustering for lane estimation.")]
    public static async Task<string> GetSceneLayoutSnapshot(
        UnityClient client,
        [Description("Name of the tile root GameObject. If omitted, auto-detects the root with most children.")] string? tileRootName = null,
        [Description("Maximum tiles to analyze for spacing stats (10-1500). Default 600.")] int maxTiles = 600)
    {
        return await client.GetSceneLayoutSnapshotAsync(tileRootName, maxTiles);
    }

    #endregion
}
