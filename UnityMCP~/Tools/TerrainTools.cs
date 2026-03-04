using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class TerrainTools
{
    [McpServerTool(Name = "unity_create_terrain")]
    [Description("Create a new Unity Terrain with configurable size and heightmap resolution. "
        + "The terrain is created with a saved TerrainData asset for persistence. "
        + "After creation, use unity_set_terrain_heights to shape it, unity_add_terrain_layer to add textures, "
        + "unity_paint_terrain to paint textures, and unity_place_terrain_trees to add vegetation.\n\n"
        + "Example: unity_create_terrain(name=\"Island\", terrainWidth=200, terrainLength=200, terrainHeight=80)")]
    public static async Task<string> CreateTerrain(
        UnityClient client,
        [Description("Terrain GameObject name.")] string? name = null,
        [Description("World position as [x, y, z].")] float[]? position = null,
        [Description("Terrain width in world units (X axis). Default 100.")] float terrainWidth = 100f,
        [Description("Terrain length in world units (Z axis). Default 100.")] float terrainLength = 100f,
        [Description("Maximum terrain height in world units (Y axis). Default 50.")] float terrainHeight = 50f,
        [Description("Heightmap resolution. Must be 2^n+1: 33, 65, 129, 257, 513, 1025. Default 257.")] int heightmapResolution = 257,
        [Description("Parent instance ID.")] int parentId = -1)
    {
        var data = new Dictionary<string, object?>
        {
            ["terrainWidth"] = terrainWidth,
            ["terrainLength"] = terrainLength,
            ["terrainHeight"] = terrainHeight,
            ["heightmapResolution"] = heightmapResolution
        };

        if (!string.IsNullOrEmpty(name)) data["name"] = name;
        if (position != null) data["position"] = position;
        if (parentId != -1) data["parentId"] = parentId;

        return await client.CreateTerrainAsync(data);
    }

    [McpServerTool(Name = "unity_get_terrain_info")]
    [Description("Get terrain information: heightmap resolution, size, texture layers, tree counts. "
        + "Use this before modifying terrain to understand its current state.")]
    public static async Task<string> GetTerrainInfo(
        UnityClient client,
        [Description("Instance ID of the Terrain GameObject.")] int instanceId)
    {
        return await client.GetTerrainInfoAsync(instanceId);
    }

    [McpServerTool(Name = "unity_set_terrain_heights")]
    [Description(@"Set terrain heightmap using procedural modes or raw data.

Modes:
  flat — Uniform height across entire terrain
  noise — Perlin noise with octaves, scale, amplitude, and seed
  slope — Linear gradient from slopeFrom to slopeTo along X or Z axis
  plateau — Raised circular area in the center with smooth falloff
  raw — Direct heightmap values as flat array (row-major, 0-1 range)

Example - Hilly terrain:
  instanceId: 123, mode: ""noise"", noiseScale: 0.02, noiseAmplitude: 0.4, noiseOctaves: 4

Example - Flat base with center plateau:
  instanceId: 123, mode: ""plateau"", plateauHeight: 0.2, plateauRadius: 0.25, plateauFalloff: 0.1")]
    public static async Task<string> SetTerrainHeights(
        UnityClient client,
        [Description("Instance ID of the Terrain GameObject.")] int instanceId,
        [Description("Height mode: flat, noise, slope, plateau, or raw.")] string mode,
        // Flat
        [Description("Height value for flat mode (0-1). Default 0.1.")] float flatHeight = 0.1f,
        // Noise
        [Description("Noise frequency scale. Smaller = broader hills. Default 0.03.")] float noiseScale = 0.03f,
        [Description("Noise height amplitude (0-1). Default 0.3.")] float noiseAmplitude = 0.3f,
        [Description("Noise random seed (0 = random). Default 0.")] int noiseSeed = 0,
        [Description("Noise octaves (1-8). More = finer detail. Default 3.")] int noiseOctaves = 3,
        [Description("Noise persistence (0-1). Controls octave falloff. Default 0.5.")] float noisePersistence = 0.5f,
        // Slope
        [Description("Slope start height (0-1). Default 0.")] float slopeFrom = 0f,
        [Description("Slope end height (0-1). Default 0.5.")] float slopeTo = 0.5f,
        [Description("Slope direction: 'x' or 'z'. Default 'z'.")] string slopeDirection = "z",
        // Plateau
        [Description("Plateau center height (0-1). Default 0.3.")] float plateauHeight = 0.3f,
        [Description("Plateau radius (0-1 normalized). Default 0.3.")] float plateauRadius = 0.3f,
        [Description("Plateau edge falloff width (0-1). Default 0.15.")] float plateauFalloff = 0.15f,
        // Raw
        [Description("Raw heightmap values as flat array (row-major, resolution*resolution floats, 0-1).")] float[]? heights = null)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return ToolErrors.ValidationError("mode is required (flat, noise, slope, plateau, raw)");

        var data = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["mode"] = mode,
            ["flatHeight"] = flatHeight,
            ["noiseScale"] = noiseScale,
            ["noiseAmplitude"] = noiseAmplitude,
            ["noiseSeed"] = noiseSeed,
            ["noiseOctaves"] = noiseOctaves,
            ["noisePersistence"] = noisePersistence,
            ["slopeFrom"] = slopeFrom,
            ["slopeTo"] = slopeTo,
            ["slopeDirection"] = slopeDirection,
            ["plateauHeight"] = plateauHeight,
            ["plateauRadius"] = plateauRadius,
            ["plateauFalloff"] = plateauFalloff
        };

        if (heights != null) data["heights"] = heights;

        return await client.SetTerrainHeightsAsync(data);
    }

    [McpServerTool(Name = "unity_add_terrain_layer")]
    [Description("Add a texture layer to a terrain. Layers are used for painting (grass, rock, sand, etc.). "
        + "Each layer has a diffuse texture, optional normal map, tile size, and PBR properties. "
        + "The layer is saved as a .terrainlayer asset.\n\n"
        + "Example: unity_add_terrain_layer(instanceId=123, diffusePath=\"Assets/Textures/Grass.png\", tileSizeX=5, tileSizeY=5)")]
    public static async Task<string> AddTerrainLayer(
        UnityClient client,
        [Description("Instance ID of the Terrain GameObject.")] int instanceId,
        [Description("Diffuse texture asset path.")] string diffusePath,
        [Description("Normal map texture asset path (optional).")] string? normalPath = null,
        [Description("Texture tile size X. Default 10.")] float tileSizeX = 10f,
        [Description("Texture tile size Y. Default 10.")] float tileSizeY = 10f,
        [Description("Tint color as [r,g,b,a]. Optional.")] float[]? tint = null,
        [Description("Metallic value (0-1). Default 0.")] float metallic = 0f,
        [Description("Smoothness value (0-1). Default 0.")] float smoothness = 0f)
    {
        if (string.IsNullOrWhiteSpace(diffusePath))
            return ToolErrors.ValidationError("diffusePath is required");

        var data = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["diffusePath"] = diffusePath,
            ["tileSizeX"] = tileSizeX,
            ["tileSizeY"] = tileSizeY,
            ["metallic"] = metallic,
            ["smoothness"] = smoothness
        };

        if (!string.IsNullOrEmpty(normalPath)) data["normalPath"] = normalPath;
        if (tint != null) data["tint"] = tint;

        return await client.AddTerrainLayerAsync(data);
    }

    [McpServerTool(Name = "unity_paint_terrain")]
    [Description("Paint a terrain texture layer onto the terrain. Use 'fill' to cover the entire terrain, "
        + "or brush mode with center/radius for localized painting. Layers are blended — painting one reduces others.\n\n"
        + "Example - Fill with grass: unity_paint_terrain(instanceId=123, layerIndex=0, fill=true)\n"
        + "Example - Paint rock patch: unity_paint_terrain(instanceId=123, layerIndex=1, centerX=0.5, centerY=0.5, radius=0.15, opacity=0.8)")]
    public static async Task<string> PaintTerrain(
        UnityClient client,
        [Description("Instance ID of the Terrain GameObject.")] int instanceId,
        [Description("Layer index to paint (0-based, from unity_get_terrain_info).")] int layerIndex,
        [Description("Normalized center X position (0-1). Default 0.5.")] float centerX = 0.5f,
        [Description("Normalized center Y position (0-1). Default 0.5.")] float centerY = 0.5f,
        [Description("Brush radius (0-1 normalized). Default 0.1.")] float radius = 0.1f,
        [Description("Paint opacity/strength (0-1). Default 1.")] float opacity = 1f,
        [Description("Brush shape: 'circle' or 'square'. Default 'circle'.")] string shape = "circle",
        [Description("Fill entire terrain with this layer instead of brush painting.")] bool fill = false)
    {
        var data = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["layerIndex"] = layerIndex,
            ["centerX"] = centerX,
            ["centerY"] = centerY,
            ["radius"] = radius,
            ["opacity"] = opacity,
            ["shape"] = shape,
            ["fill"] = fill ? 1 : 0
        };

        return await client.PaintTerrainAsync(data);
    }

    [McpServerTool(Name = "unity_place_terrain_trees")]
    [Description("Place tree instances on a terrain with randomized position, scale, and rotation. "
        + "Supports altitude and slope constraints for realistic placement (e.g., no trees on cliffs). "
        + "Tree prototypes are auto-registered from the prefab path.\n\n"
        + "Example: unity_place_terrain_trees(instanceId=123, prefabPath=\"Assets/Prefabs/Oak.prefab\", count=50, minHeight=0.8, maxHeight=1.3)")]
    public static async Task<string> PlaceTerrainTrees(
        UnityClient client,
        [Description("Instance ID of the Terrain GameObject.")] int instanceId,
        [Description("Tree prefab asset path.")] string prefabPath,
        [Description("Number of trees to place (1-5000). Default 10.")] int count = 10,
        [Description("Minimum height scale. Default 0.8.")] float minHeight = 0.8f,
        [Description("Maximum height scale. Default 1.2.")] float maxHeight = 1.2f,
        [Description("Minimum width scale. Default 0.8.")] float minWidth = 0.8f,
        [Description("Maximum width scale. Default 1.2.")] float maxWidth = 1.2f,
        [Description("Tint color as [r,g,b] or [r,g,b,a]. Optional.")] float[]? color = null,
        [Description("Random seed (0 = random). Default 0.")] int seed = 0,
        [Description("Minimum slope in degrees for placement. Default 0.")] float minSlope = 0f,
        [Description("Maximum slope in degrees for placement. Default 90.")] float maxSlope = 90f,
        [Description("Minimum altitude (0-1 normalized height). Default 0.")] float minAltitude = 0f,
        [Description("Maximum altitude (0-1 normalized height). Default 1.")] float maxAltitude = 1f)
    {
        if (string.IsNullOrWhiteSpace(prefabPath))
            return ToolErrors.ValidationError("prefabPath is required");

        var data = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["prefabPath"] = prefabPath,
            ["count"] = count,
            ["minHeight"] = minHeight,
            ["maxHeight"] = maxHeight,
            ["minWidth"] = minWidth,
            ["maxWidth"] = maxWidth,
            ["seed"] = seed,
            ["minSlope"] = minSlope,
            ["maxSlope"] = maxSlope,
            ["minAltitude"] = minAltitude,
            ["maxAltitude"] = maxAltitude
        };

        if (color != null) data["color"] = color;

        return await client.PlaceTerrainTreesAsync(data);
    }
}
