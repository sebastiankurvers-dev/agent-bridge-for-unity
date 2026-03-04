using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class AssetManagementTools
{
    [McpServerTool(Name = "unity_move_asset")]
    [Description("Move or rename an asset. Handles .meta files automatically. Creates destination folders if needed. " +
        "Example: unity_move_asset(sourcePath=\"Assets/Old/MyMaterial.mat\", destinationPath=\"Assets/New/MyMaterial.mat\")")]
    public static async Task<string> MoveAsset(
        UnityClient client,
        [Description("Current asset path (e.g., 'Assets/Old/Thing.mat').")] string sourcePath,
        [Description("New asset path (e.g., 'Assets/New/Thing.mat'). To rename, use same directory with new filename.")] string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return ToolErrors.ValidationError("sourcePath is required");
        if (string.IsNullOrWhiteSpace(destinationPath))
            return ToolErrors.ValidationError("destinationPath is required");

        return await client.MoveAssetAsync(new Dictionary<string, object?>
        {
            ["sourcePath"] = sourcePath,
            ["destinationPath"] = destinationPath
        });
    }

    [McpServerTool(Name = "unity_duplicate_asset")]
    [Description("Duplicate/copy an asset to a new path. Creates destination folders if needed. " +
        "Example: unity_duplicate_asset(sourcePath=\"Assets/Materials/Base.mat\", destinationPath=\"Assets/Materials/BaseCopy.mat\")")]
    public static async Task<string> DuplicateAsset(
        UnityClient client,
        [Description("Path of the asset to duplicate (e.g., 'Assets/Materials/Base.mat').")] string sourcePath,
        [Description("Path for the copy (e.g., 'Assets/Materials/BaseCopy.mat').")] string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return ToolErrors.ValidationError("sourcePath is required");
        if (string.IsNullOrWhiteSpace(destinationPath))
            return ToolErrors.ValidationError("destinationPath is required");

        return await client.DuplicateAssetAsync(new Dictionary<string, object?>
        {
            ["sourcePath"] = sourcePath,
            ["destinationPath"] = destinationPath
        });
    }

    [McpServerTool(Name = "unity_delete_asset")]
    [Description("Delete an asset or folder. Handles .meta files automatically. " +
        "Example: unity_delete_asset(path=\"Assets/Old/Unused.mat\")")]
    public static async Task<string> DeleteAsset(
        UnityClient client,
        [Description("Path of the asset to delete (e.g., 'Assets/Old/Unused.mat').")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolErrors.ValidationError("path is required");

        return await client.DeleteAssetAsync(new Dictionary<string, object?>
        {
            ["path"] = path
        });
    }

    [McpServerTool(Name = "unity_create_folder")]
    [Description("Create a folder in the Assets directory. Creates intermediate folders recursively if needed. " +
        "Example: unity_create_folder(path=\"Assets/Art/Characters/Enemies\")")]
    public static async Task<string> CreateFolder(
        UnityClient client,
        [Description("Folder path to create (e.g., 'Assets/Art/Characters'). Creates parent folders automatically.")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolErrors.ValidationError("path is required");

        return await client.CreateFolderAsync(new Dictionary<string, object?>
        {
            ["path"] = path
        });
    }

    [McpServerTool(Name = "unity_get_asset_info")]
    [Description("Get metadata about an asset: type, GUID, file size, and direct dependencies. " +
        "Example: unity_get_asset_info(path=\"Assets/Materials/Player.mat\")")]
    public static async Task<string> GetAssetInfo(
        UnityClient client,
        [Description("Asset path to inspect (e.g., 'Assets/Materials/Player.mat').")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolErrors.ValidationError("path is required");

        return await client.GetAssetInfoAsync(path);
    }

    [McpServerTool(Name = "unity_create_procedural_texture")]
    [Description("Generate a procedural texture and save it as a PNG asset. "
        + "Types: 'noise' (Perlin), 'gradient' (linear/radial), 'checkerboard', 'bricks', 'stripes'. "
        + "Can optionally assign the texture to an existing material's main texture slot.")]
    public static async Task<string> CreateProceduralTexture(
        UnityClient client,
        [Description("Texture type: 'noise', 'gradient', 'checkerboard', 'bricks', 'stripes'.")] string textureType,
        [Description("Texture asset name.")] string? name = null,
        [Description("Save path (e.g., 'Assets/Textures/myTex.png'). Default: Assets/Textures/<name>.png")] string? path = null,
        [Description("Texture width in pixels (64-512).")] int width = 256,
        [Description("Texture height in pixels (64-512).")] int height = 256,
        [Description("Noise scale (higher = more detail).")] float scale = 4f,
        [Description("Noise octaves for detail layers.")] int octaves = 4,
        [Description("Noise persistence (amplitude falloff per octave).")] float persistence = 0.5f,
        [Description("Noise tint color [r,g,b].")] float[]? tint = null,
        [Description("Gradient/start color [r,g,b].")] float[]? colorA = null,
        [Description("Gradient/end color [r,g,b].")] float[]? colorB = null,
        [Description("Gradient angle in degrees.")] float angle = 0f,
        [Description("Gradient mode: 'linear' or 'radial'.")] string gradientMode = "linear",
        [Description("Pattern primary color [r,g,b].")] float[]? color1 = null,
        [Description("Pattern secondary color [r,g,b].")] float[]? color2 = null,
        [Description("Tiles/repeats along X.")] int tilesX = 4,
        [Description("Tiles/repeats along Y.")] int tilesY = 4,
        [Description("Brick mortar width ratio (0-0.5).")] float mortarWidth = 0.05f,
        [Description("Random seed for noise generation.")] int seed = 0,
        [Description("If true, assign texture to the specified material.")] bool assignToMaterial = false,
        [Description("Material asset path to assign texture to.")] string? materialPath = null)
    {
        if (string.IsNullOrWhiteSpace(textureType))
            return ToolErrors.ValidationError("textureType is required");

        var request = new Dictionary<string, object?>
        {
            ["textureType"] = textureType,
            ["name"] = name,
            ["path"] = path,
            ["width"] = width,
            ["height"] = height,
            ["scale"] = scale,
            ["octaves"] = octaves,
            ["persistence"] = persistence,
            ["tint"] = tint,
            ["colorA"] = colorA,
            ["colorB"] = colorB,
            ["angle"] = angle,
            ["gradientMode"] = gradientMode,
            ["color1"] = color1,
            ["color2"] = color2,
            ["tilesX"] = tilesX,
            ["tilesY"] = tilesY,
            ["mortarWidth"] = mortarWidth,
            ["seed"] = seed,
            ["assignToMaterial"] = assignToMaterial,
            ["materialPath"] = materialPath
        };

        return await client.CreateProceduralTextureAsync(request);
    }
}
