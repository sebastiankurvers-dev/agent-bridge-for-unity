using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class AssetTools
{
    #region ScriptableObject Tools

    [McpServerTool(Name = "unity_create_scriptableobject")]
    [Description("Create a new ScriptableObject asset. The ScriptableObject type must already exist in the project.")]
    public static async Task<string> CreateScriptableObject(
        UnityClient client,
        [Description("The ScriptableObject type name (must be a class that extends ScriptableObject).")] string typeName,
        [Description("The path where the asset should be saved (e.g., 'Assets/Data/PlayerData.asset').")] string? savePath = null,
        [Description("Optional JSON object with property values to set.")] string? properties = null)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return ToolErrors.ValidationError("ScriptableObject type name is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["typeName"] = typeName
        };

        if (!string.IsNullOrEmpty(savePath)) request["savePath"] = savePath;
        if (!string.IsNullOrEmpty(properties)) request["properties"] = properties;

        return await client.CreateScriptableObjectAsync(request);
    }

    #endregion

    #region Material Tools

    [McpServerTool(Name = "unity_find_materials")]
    [Description("Search for materials in the Unity project.")]
    public static async Task<string> FindMaterials(
        UnityClient client,
        [Description("Optional search query to filter materials by name.")] string search = "",
        [Description("Maximum results to return (1-5000, default 100).")] int maxResults = 100)
    {
        maxResults = Math.Clamp(maxResults, 1, 5000);
        return await client.FindMaterialsAsync(search, maxResults);
    }

    [McpServerTool(Name = "unity_find_materials_scoped")]
    [Description("Search materials with folder scope filters. Useful to constrain queries to a specific art pack folder.")]
    public static async Task<string> FindMaterialsScoped(
        UnityClient client,
        [Description("Optional search query. Empty returns all materials in scope (limited by maxResults).")] string search = "",
        [Description("Only include assets under these roots (e.g., ['Assets/Materials']). Empty means project-wide.")] string[]? includeRoots = null,
        [Description("Exclude assets under these roots.")] string[]? excludeRoots = null,
        [Description("If true, includes subfolders recursively. If false, only direct children of the include root directories are considered.")] bool includeSubfolders = true,
        [Description("Maximum results to return (1-5000).")] int maxResults = 200,
        [Description("Search mode: 'contains', 'exact', or 'fuzzy'.")] string matchMode = "contains")
    {
        return await client.FindMaterialsScopedAsync(
            search,
            includeRoots,
            excludeRoots,
            includeSubfolders,
            maxResults,
            matchMode);
    }

    [McpServerTool(Name = "unity_create_material")]
    [Description("Create a new material asset with the specified shader and properties. Supports emission color/intensity, metallic, and smoothness for PBR workflows.")]
    public static async Task<string> CreateMaterial(
        UnityClient client,
        [Description("The path where the material should be saved (e.g., 'Assets/Materials/Red.mat').")] string savePath,
        [Description("The shader name (e.g., 'Standard', 'Unlit/Color', 'Universal Render Pipeline/Lit'). Defaults to 'Standard'.")] string? shaderName = null,
        [Description("Optional name for the material.")] string? name = null,
        [Description("Main color as [r, g, b] or [r, g, b, a] values (0-1 range).")] float[]? color = null,
        [Description("Path to the main texture asset.")] string? mainTexturePath = null,
        [Description("Render queue value.")] int? renderQueue = null,
        [Description("Emission color [r,g,b] or [r,g,b,a] (0-1). Auto-enables _EMISSION keyword.")] float[]? emissionColor = null,
        [Description("Emission HDR intensity multiplier (default 1). Multiplied into emissionColor for HDR values.")] float? emissionIntensity = null,
        [Description("Metallic value (0-1).")] float? metallic = null,
        [Description("Smoothness value (0-1).")] float? smoothness = null)
    {
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return ToolErrors.ValidationError("Save path is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["savePath"] = savePath
        };

        if (!string.IsNullOrEmpty(shaderName)) request["shaderName"] = shaderName;
        if (!string.IsNullOrEmpty(name)) request["name"] = name;
        if (color != null) request["color"] = color;
        if (!string.IsNullOrEmpty(mainTexturePath)) request["mainTexturePath"] = mainTexturePath;
        if (renderQueue.HasValue) request["renderQueue"] = renderQueue.Value;
        if (emissionColor != null) request["emissionColor"] = emissionColor;
        if (emissionIntensity.HasValue) request["emissionIntensity"] = emissionIntensity.Value;
        if (metallic.HasValue) request["metallic"] = metallic.Value;
        if (smoothness.HasValue) request["smoothness"] = smoothness.Value;

        return await client.CreateMaterialAsync(request);
    }

    [McpServerTool(Name = "unity_modify_material")]
    [Description("Modify an existing material's properties (color, emission, metallic, smoothness, texture, shader).")]
    public static async Task<string> ModifyMaterial(
        UnityClient client,
        [Description("The path to the material asset to modify.")] string path,
        [Description("New shader name.")] string? shaderName = null,
        [Description("New main color as [r, g, b] or [r, g, b, a] values (0-1 range).")] float[]? color = null,
        [Description("Path to a new main texture asset.")] string? mainTexturePath = null,
        [Description("New render queue value.")] int? renderQueue = null,
        [Description("Emission color [r,g,b] or [r,g,b,a] (0-1). Auto-enables _EMISSION keyword.")] float[]? emissionColor = null,
        [Description("Emission HDR intensity multiplier (default 1). Multiplied into emissionColor for HDR values.")] float? emissionIntensity = null,
        [Description("Metallic value (0-1).")] float? metallic = null,
        [Description("Smoothness value (0-1).")] float? smoothness = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolErrors.ValidationError("Material path is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["path"] = path
        };

        if (!string.IsNullOrEmpty(shaderName)) request["shaderName"] = shaderName;
        if (color != null) request["color"] = color;
        if (!string.IsNullOrEmpty(mainTexturePath)) request["mainTexturePath"] = mainTexturePath;
        if (renderQueue.HasValue) request["renderQueue"] = renderQueue.Value;
        if (emissionColor != null) request["emissionColor"] = emissionColor;
        if (emissionIntensity.HasValue) request["emissionIntensity"] = emissionIntensity.Value;
        if (metallic.HasValue) request["metallic"] = metallic.Value;
        if (smoothness.HasValue) request["smoothness"] = smoothness.Value;

        return await client.ModifyMaterialAsync(request);
    }

    #endregion

    #region Material Validation

    [McpServerTool(Name = "unity_validate_material")]
    [Description("Check if a material's shader is compatible with the active render pipeline (URP/Built-in). " +
        "Catches URP/built-in mismatches before they cause invisible particles or pink objects. " +
        "Returns compatible flag, issues list, and suggested replacement shader. " +
        "Example: unity_validate_material(materialPath=\"Assets/Materials/MyMat.mat\") " +
        "Example from renderer: unity_validate_material(instanceId=1234)")]
    public static async Task<string> ValidateMaterial(
        UnityClient client,
        [Description("Path to the material asset (e.g., 'Assets/Materials/MyMat.mat').")] string materialPath = "",
        [Description("Alternative: instance ID of a GameObject with a Renderer. Will check its first material.")] int instanceId = 0)
    {
        if (string.IsNullOrWhiteSpace(materialPath) && instanceId == 0)
        {
            return ToolErrors.ValidationError("Either materialPath or instanceId is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["materialPath"] = materialPath ?? "",
            ["instanceId"] = instanceId
        };

        return await client.ValidateMaterialAsync(request);
    }

    #endregion

    #region Import Tools

    [McpServerTool(Name = "unity_import_fbx_to_prefab")]
    [Description("Import an FBX file into Unity and create a prefab from it. " +
        "Configures ModelImporter settings (scale, materials, colliders) and saves as .prefab. " +
        "If prefabPath is omitted, derives it from fbxPath (strips _LOD suffix, changes extension to .prefab). " +
        "Example: unity_import_fbx_to_prefab(fbxPath=\"Assets/Generated/vehicles/CyberSedan_01/CyberSedan_01_LOD0.fbx\")")]
    public static async Task<string> ImportFbxToPrefab(
        UnityClient client,
        [Description("Path to the FBX file under Assets/ (e.g., 'Assets/Generated/vehicles/CyberSedan_01/CyberSedan_01_LOD0.fbx').")] string fbxPath,
        [Description("Path for the output prefab. If empty, derived from fbxPath (strips LOD suffix, .prefab extension).")] string prefabPath = "",
        [Description("Import scale factor (default 1.0).")] float scaleFactor = 1f,
        [Description("Collider generation: -1=default, 0=none, 1=mesh collider, 2=fitted box collider.")] int generateColliders = -1,
        [Description("Import materials: 1=yes (default), 0=no.")] int importMaterials = 1,
        [Description("Material storage location: 'InPrefab' (default) or 'External'.")] string materialLocation = "InPrefab")
    {
        if (string.IsNullOrWhiteSpace(fbxPath))
        {
            return ToolErrors.ValidationError("fbxPath is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["fbxPath"] = fbxPath,
            ["prefabPath"] = prefabPath,
            ["scaleFactor"] = scaleFactor,
            ["generateColliders"] = generateColliders,
            ["importMaterials"] = importMaterials,
            ["materialLocation"] = materialLocation
        };

        return await client.ImportFbxToPrefabAsync(request);
    }

    #endregion

    #region Prefab Tools

    [McpServerTool(Name = "unity_create_prefab")]
    [Description("Save a GameObject from the scene as a prefab asset.")]
    public static async Task<string> CreatePrefab(
        UnityClient client,
        [Description("The instance ID of the GameObject to save as a prefab.")] int instanceId,
        [Description("The path where the prefab should be saved (e.g., 'Assets/Prefabs/Enemy.prefab').")] string? savePath = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId
        };

        if (!string.IsNullOrEmpty(savePath)) request["savePath"] = savePath;

        return await client.CreatePrefabAsync(request);
    }

    [McpServerTool(Name = "unity_modify_prefab")]
    [Description("Modify a prefab asset by adding or removing components.")]
    public static async Task<string> ModifyPrefab(
        UnityClient client,
        [Description("The path to the prefab asset.")] string prefabPath,
        [Description("New name for the prefab root GameObject.")] string? name = null,
        [Description("Array of component type names to add (e.g., ['Rigidbody', 'BoxCollider']).")] string[]? addComponents = null,
        [Description("Array of component type names to remove.")] string[]? removeComponents = null)
    {
        if (string.IsNullOrWhiteSpace(prefabPath))
        {
            return ToolErrors.ValidationError("Prefab path is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["prefabPath"] = prefabPath
        };

        if (!string.IsNullOrEmpty(name)) request["name"] = name;
        if (addComponents != null) request["addComponents"] = addComponents;
        if (removeComponents != null) request["removeComponents"] = removeComponents;

        return await client.ModifyPrefabAsync(request);
    }

    [McpServerTool(Name = "unity_apply_prefab_overrides")]
    [Description("Apply all overrides from a prefab instance in the scene back to its source prefab asset.")]
    public static async Task<string> ApplyPrefabOverrides(
        UnityClient client,
        [Description("Instance ID of the prefab instance in the scene.")] int instanceId)
    {
        if (instanceId == 0)
        {
            return ToolErrors.ValidationError("instanceId is required");
        }

        return await client.ApplyPrefabOverridesAsync(instanceId);
    }

    [McpServerTool(Name = "unity_create_prefab_variant")]
    [Description("Create a prefab variant based on an existing prefab. Variants inherit from the base prefab and can override properties.")]
    public static async Task<string> CreatePrefabVariant(
        UnityClient client,
        [Description("The path to the base prefab asset.")] string basePrefabPath,
        [Description("The path where the variant should be saved.")] string? savePath = null)
    {
        if (string.IsNullOrWhiteSpace(basePrefabPath))
        {
            return ToolErrors.ValidationError("Base prefab path is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["basePrefabPath"] = basePrefabPath
        };

        if (!string.IsNullOrEmpty(savePath)) request["savePath"] = savePath;

        return await client.CreatePrefabVariantAsync(request);
    }

    #endregion
}
