using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class HierarchyTools
{
    [McpServerTool(Name = "unity_get_hierarchy")]
    [Description("Get the Unity scene hierarchy as a tree structure. Returns all GameObjects in the current scene with their names, instance IDs, components, and children. Use depth and brief params to reduce output size.")]
    public static async Task<string> GetHierarchy(
        UnityClient client,
        [Description("Maximum tree depth to return (-1 = unlimited, 0 = root only, 1 = roots + direct children). Default 0 for token efficiency on large scenes.")] int depth = 0,
        [Description("If true, returns only name + instanceId + childCount per node (no components/layer/tag). Much smaller output.")] bool brief = true,
        [Description("If true, pretty-prints JSON for readability. Default false for token efficiency.")] bool pretty = false)
    {
        return await client.GetHierarchyAsync(depth, brief, pretty);
    }

    [McpServerTool(Name = "unity_get_scene")]
    [Description("Get information about the current scene including its name, path, and list of all available scenes in the project.\n\n"
        + "TIP: Before building, call unity_workflow_guide(task=\"...\") to get the recommended tool sequence and pitfalls for your task.")]
    public static async Task<string> GetScene(UnityClient client)
    {
        var json = await client.GetSceneAsync();
        try
        {
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (dict != null)
            {
                dict["_tip"] = "Call unity_workflow_guide(task=\"your goal\") BEFORE building to get the correct tool sequence and avoid common pitfalls.";
                return System.Text.Json.JsonSerializer.Serialize(dict);
            }
        }
        catch { }
        return json;
    }

    [McpServerTool(Name = "unity_get_scene_dirty_state")]
    [Description("Get dirty/unsaved state for the active scene and loaded scenes.")]
    public static async Task<string> GetSceneDirtyState(UnityClient client)
    {
        return await client.GetSceneDirtyStateAsync();
    }

    [McpServerTool(Name = "unity_save_scene")]
    [Description("Save the active scene. Optionally save to a specific scenePath or save as copy.")]
    public static async Task<string> SaveScene(
        UnityClient client,
        [Description("Optional scene asset path to save to (e.g., 'Assets/Scenes/MyScene.unity').")] string? scenePath = null,
        [Description("If true, save as copy when scenePath is provided (does not change active scene path).")] bool saveAsCopy = false,
        [Description("If true, skip save when scene is not dirty.")] bool onlyIfDirty = false)
    {
        return await client.SaveSceneAsync(new
        {
            scenePath,
            saveAsCopy,
            onlyIfDirty
        });
    }

    [McpServerTool(Name = "unity_load_scene")]
    [Description("Load a scene by name or path. Can optionally save the current scene before loading.")]
    public static async Task<string> LoadScene(
        UnityClient client,
        [Description("The full path to the scene asset (e.g., 'Assets/Scenes/MainMenu.unity')")] string? scenePath = null,
        [Description("The name of the scene to load (e.g., 'MainMenu')")] string? sceneName = null,
        [Description("Whether to prompt to save the current scene before loading (default: true)")] bool saveCurrentScene = true)
    {
        if (string.IsNullOrEmpty(scenePath) && string.IsNullOrEmpty(sceneName))
        {
            return ToolErrors.ValidationError("Either scenePath or sceneName must be provided");
        }

        return await client.LoadSceneAsync(scenePath, sceneName, saveCurrentScene);
    }
}
