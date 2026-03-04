using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static partial class SceneBuilderTools
{
    #region Scene Creation Tools

    [McpServerTool(Name = "unity_create_scene_from_descriptor")]
    [Description("Create a full scene hierarchy from one JSON descriptor — batch-spawn many objects in a single call with shared materials. "
        + "Supports nested children, inline material overrides (color, emission, metallic, smoothness), lights, components, and tags/layers. "
        + "Returns warnings if any objects end up without meshes (e.g., typo in primitiveType).\n\n"
        + "SCENE RECONSTRUCTION: If building from a reference image, use unity_capture_and_compare after each major build pass "
        + "to get spatial feedback. Fix structure and camera angle FIRST (check 'suggestions' array), then materials.")]
    public static async Task<string> CreateSceneFromDescriptor(
        UnityClient client,
        [Description("JSON scene descriptor with name and objects array. "
            + "Top-level 'materials' map (e.g. {\"Stone\": \"Assets/Materials/Stone.mat\"}) lets objects reference by key via 'material' field. "
            + "Each object can have: name, primitiveType (or 'type' or 'primitive') for Cube/Sphere/Cylinder/Capsule/Plane/Quad, "
            + "prefab, position [x,y,z], rotation [x,y,z], scale [x,y,z], material (key from materials map or direct path), "
            + "materialOverrides {color, emissionColor, emissionIntensity, metallic, smoothness}, "
            + "light {type, color, intensity, range, shadows}, components [{type, propertiesJson}], children (recursive).")] string descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor))
        {
            return ToolErrors.ValidationError("Descriptor JSON is required");
        }

        // Parse, resolve aliases and material map, then send to Unity
        try
        {
            var parsed = JsonSerializer.Deserialize<JsonElement>(descriptor);
            var normalized = NormalizeDescriptor(parsed);
            return await client.CreateSceneFromDescriptorAsync(normalized);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = $"Invalid JSON: {ex.Message}" });
        }
    }

    [McpServerTool(Name = "unity_export_scene_descriptor")]
    [Description("Export current scene (or selected objects) as a reusable JSON descriptor for recreate/modify/apply workflows.")]
    public static async Task<string> ExportSceneDescriptor(
        UnityClient client,
        [Description("Optional array of instance IDs to export. If not provided, exports all root objects in the active scene.")] int[]? instanceIds = null)
    {
        return await client.ExportSceneDescriptorAsync(instanceIds);
    }

    [McpServerTool(Name = "unity_spawn_along_path")]
    [Description("Spawn prefabs or primitives along linear/Catmull-Rom paths with optional multi-lane layout, stagger pattern, material alternation, overlap auto-resolve, and autosave. Use primitiveType for built-in shapes (Cube, Sphere, Cylinder, Capsule, Plane, Quad) without needing a prefab asset.")]
    public static async Task<string> SpawnAlongPath(
        UnityClient client,
        [Description("Control points as [[x,y,z], [x,y,z], ...]. Minimum 2 points.")] float[][] controlPoints,
        [Description("Built-in primitive type: Cube, Sphere, Cylinder, Capsule, Plane, Quad. Use instead of prefabPath for quick prototyping.")] string? primitiveType = null,
        [Description("Prefab asset path (used when spawning a single prefab type). Not needed if primitiveType is set.")] string? prefabPath = null,
        [Description("Alternating prefab paths (cycles through). Overrides prefabPath.")] string[]? prefabPaths = null,
        [Description("Material override path for all instances.")] string? materialPath = null,
        [Description("Alternating material paths (cycles through). Overrides materialPath.")] string[]? materialPaths = null,
        [Description("Spacing: world-space distance between spawns along path.")] float? spacing = null,
        [Description("Exact spawn count per lane (overrides spacing).")] int? count = null,
        [Description("Interpolation: 'linear' or 'catmull-rom' (default).")] string? interpolation = null,
        [Description("Rotate objects to face path direction (default true).")] bool? alignToPath = null,
        [Description("Random Y rotation jitter in degrees (default 0).")] float? randomYRotation = null,
        [Description("Per-instance offset [x,y,z] from path.")] float[]? offset = null,
        [Description("Parent container object name.")] string? parentName = null,
        [Description("Number of parallel lanes (default 1 = single center-line).")] int? laneCount = null,
        [Description("Cross-path spacing between lane centers in world units.")] float? laneSpacing = null,
        [Description("Lane pattern: 'aligned' (default) or 'stagger' (odd lanes offset by half-spacing for hex packing).")] string? lanePattern = null,
        [Description("Minimum edge-to-edge gap between adjacent tiles. If set, auto-adjusts laneSpacing from prefab bounds.")] float? targetMinEdgeGap = null,
        [Description("Widen lane spacing on curves by this factor (0 = none, 1 = proportional to curvature). Default 0.")] float? curvatureWidening = null,
        [Description("If true, run overlap resolver after spawning (recommended for multi-lane tracks).")] bool? autoResolveOverlaps = null,
        [Description("Max iterations for overlap resolver (1-64).")] int? resolveMaxIterations = null,
        [Description("Nudge step used by overlap resolver in world units.")] float? resolveNudgeStep = null,
        [Description("If true, save scene after spawn and optional overlap resolution.")] bool? autoSaveScene = null,
        [Description("Timeout in milliseconds (default 30000).")] int timeoutMs = 0)
    {
        if (controlPoints == null || controlPoints.Length < 2)
        {
            return ToolErrors.ValidationError("controlPoints must have at least 2 points");
        }

        if (string.IsNullOrWhiteSpace(primitiveType) && string.IsNullOrWhiteSpace(prefabPath) && (prefabPaths == null || prefabPaths.Length == 0))
        {
            return ToolErrors.ValidationError("Either primitiveType, prefabPath, or prefabPaths is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["controlPoints"] = controlPoints
        };

        if (!string.IsNullOrWhiteSpace(primitiveType)) request["primitiveType"] = primitiveType;
        if (!string.IsNullOrWhiteSpace(prefabPath)) request["prefabPath"] = prefabPath;
        if (prefabPaths != null && prefabPaths.Length > 0) request["prefabPaths"] = prefabPaths;
        if (!string.IsNullOrWhiteSpace(materialPath)) request["materialPath"] = materialPath;
        if (materialPaths != null && materialPaths.Length > 0) request["materialPaths"] = materialPaths;
        if (spacing.HasValue) request["spacing"] = spacing.Value;
        if (count.HasValue) request["count"] = count.Value;
        if (!string.IsNullOrWhiteSpace(interpolation)) request["interpolation"] = interpolation;
        if (alignToPath.HasValue) request["alignToPath"] = alignToPath.Value ? 1 : 0;
        if (randomYRotation.HasValue) request["randomYRotation"] = randomYRotation.Value;
        if (offset != null) request["offset"] = offset;
        if (!string.IsNullOrWhiteSpace(parentName)) request["parentName"] = parentName;
        if (laneCount.HasValue) request["laneCount"] = laneCount.Value;
        if (laneSpacing.HasValue) request["laneSpacing"] = laneSpacing.Value;
        if (!string.IsNullOrWhiteSpace(lanePattern)) request["lanePattern"] = lanePattern;
        if (targetMinEdgeGap.HasValue) request["targetMinEdgeGap"] = targetMinEdgeGap.Value;
        if (curvatureWidening.HasValue) request["curvatureWidening"] = curvatureWidening.Value;
        if (autoResolveOverlaps.HasValue) request["autoResolveOverlaps"] = autoResolveOverlaps.Value ? 1 : 0;
        if (resolveMaxIterations.HasValue) request["resolveMaxIterations"] = resolveMaxIterations.Value;
        if (resolveNudgeStep.HasValue) request["resolveNudgeStep"] = resolveNudgeStep.Value;
        if (autoSaveScene.HasValue) request["autoSaveScene"] = autoSaveScene.Value ? 1 : 0;

        return await client.SpawnAlongPathAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_sample_screenshot_colors")]
    [Description("Sample screenshot colors at normalized coordinates. Returns RGB/hex/HSV/luminance; supports median filtering with sampleRadius > 1.")]
    public static async Task<string> SampleScreenshotColors(
        UnityClient client,
        [Description("Sample points as [[x,y], ...] in normalized coords (0-1). (0,0)=top-left.")] float[][] samplePoints,
        [Description("Screenshot view: 'game' or 'scene' (default 'game').")] string? screenshotView = null,
        [Description("Pixel radius for median filtering (1=single pixel, 3=3x3 median, 5=5x5 median). Default 1.")] int? sampleRadius = null,
        [Description("Base64 image to sample from instead of capturing new screenshot.")] string? imageBase64 = null,
        [Description("Stored image handle to sample from (preferred over base64 when available).")] string? imageHandle = null)
    {
        if (samplePoints == null || samplePoints.Length == 0)
        {
            return ToolErrors.ValidationError("samplePoints must have at least 1 point");
        }

        var request = new Dictionary<string, object?>
        {
            ["samplePoints"] = samplePoints
        };

        if (!string.IsNullOrWhiteSpace(screenshotView)) request["screenshotView"] = screenshotView;
        if (sampleRadius.HasValue) request["sampleRadius"] = sampleRadius.Value;
        if (!string.IsNullOrWhiteSpace(imageBase64)) request["imageBase64"] = imageBase64;
        if (!string.IsNullOrWhiteSpace(imageHandle)) request["imageHandle"] = imageHandle;

        return await client.SampleScreenshotColorsAsync(request);
    }

    [McpServerTool(Name = "unity_store_image_handle")]
    [Description("Store an image in the bridge cache and return an imageHandle for reuse across compare/repro/sampling calls. "
        + "Provide EITHER filePath (preferred for local files — avoids base64 size limits) OR imageBase64.\n\n"
        + "SCENE RECONSTRUCTION: When rebuilding a scene from a reference image, ALWAYS store the reference first with this tool, "
        + "then call unity_workflow_guide(task=\"reconstruct\") for the step-by-step workflow. "
        + "Use unity_capture_and_compare(referenceImageHandle=...) after each build pass to get spatial feedback (camera angle, object placement, lighting).")]
    public static async Task<string> StoreImageHandle(
        UnityClient client,
        [Description("Base64 image payload (raw or data URI). Use filePath instead for local files.")] string? imageBase64 = null,
        [Description("Absolute path to a local image file (png/jpg). The server reads and encodes it — avoids large base64 in tool params.")] string? filePath = null,
        [Description("Optional source label for traceability (e.g., 'reference', 'capture').")] string? source = null)
    {
        // Resolve filePath to base64 if provided
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!File.Exists(filePath))
                return ToolErrors.ValidationError($"File not found: {filePath}");

            try
            {
                var bytes = await File.ReadAllBytesAsync(filePath);
                imageBase64 = Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                return ToolErrors.ValidationError($"Failed to read file: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            return ToolErrors.ValidationError("imageBase64 or filePath is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["imageBase64"] = imageBase64
        };
        if (!string.IsNullOrWhiteSpace(source)) request["source"] = source;
        return await client.StoreImageHandleAsync(request);
    }

    [McpServerTool(Name = "unity_get_image_handle")]
    [Description("Get metadata for an image handle, optionally including base64 payload.")]
    public static async Task<string> GetImageHandle(
        UnityClient client,
        [Description("Image handle id returned by screenshot/store tools.")] string imageHandle,
        [Description("If true, include base64 image payload in response.")] bool includeBase64 = false)
    {
        if (string.IsNullOrWhiteSpace(imageHandle))
        {
            return ToolErrors.ValidationError("imageHandle is required");
        }

        return await client.GetImageHandleAsync(imageHandle, includeBase64);
    }

    [McpServerTool(Name = "unity_delete_image_handle")]
    [Description("Delete an image handle from bridge cache to free memory.")]
    public static async Task<string> DeleteImageHandle(
        UnityClient client,
        [Description("Image handle id returned by screenshot/store tools.")] string imageHandle)
    {
        if (string.IsNullOrWhiteSpace(imageHandle))
        {
            return ToolErrors.ValidationError("imageHandle is required");
        }

        return await client.DeleteImageHandleAsync(imageHandle);
    }

    #endregion

    #region Prefab Preview Tools

    [McpServerTool(Name = "unity_get_prefab_geometry")]
    [Description(@"Get prefab geometry metadata for accurate placement and snapping.
Returns bounds, collider bounds, mesh/collider counts, recommended ground offset, and optional connector/socket transforms.
Set includeAccurateBounds=true to get real renderer bounds via temp-instantiate — use when you need accurate combined
dimensions for auto-scaling (e.g. fitting a vehicle to a lane width). The standard 'bounds' field uses LoadPrefabContents
which may return zero or approximate values for complex hierarchies.
Use this to place modular assets precisely when recreating scenes from references.")]
    public static async Task<string> GetPrefabGeometry(
        UnityClient client,
        [Description("Prefab asset path (e.g., 'Assets/Prefabs/Buildings/SM_Bld_Wall_01.prefab').")] string prefabPath,
        [Description("If true, detect socket/connector transforms by name prefix (Socket_, Snap_, Attach_, Connector_ by default).")] bool includeSockets = true,
        [Description("If true, include immediate child transform metadata in the response.")] bool includeChildren = false,
        [Description("Optional custom socket name prefixes. Example: ['Socket_', 'Snap_'].")] string[]? socketPrefixes = null,
        [Description("If true, temp-instantiates the prefab to measure accurate combined renderer bounds. More reliable than the standard bounds field for auto-scaling calculations.")] bool includeAccurateBounds = false)
    {
        if (string.IsNullOrWhiteSpace(prefabPath))
        {
            return ToolErrors.ValidationError("Prefab path is required");
        }

        return await client.GetPrefabGeometryAsync(prefabPath, includeSockets, includeChildren, socketPrefixes, includeAccurateBounds);
    }

    [McpServerTool(Name = "unity_get_prefab_footprint_2d")]
    [Description(@"Get a prefab's 2D XZ footprint for visual-gap-safe tile/path generation.
Returns convex hull + bounds + area/perimeter and recommended center spacing values
derived from a target minimum edge gap.

Use this before tile generation to avoid 'collision-safe but visually fused' layouts.")]
    public static async Task<string> GetPrefabFootprint2D(
        UnityClient client,
        [Description("Prefab asset path (e.g., 'Assets/Prefabs/Tiles/FloorTile.prefab').")] string prefabPath,
        [Description("Sampling source: hybrid (default), mesh, collider, or rendererBounds.")] string source = "hybrid",
        [Description("Target minimum edge gap in world units used for spacing recommendations.")] float targetMinEdgeGap = 0.04f,
        [Description("Maximum sampled points before hull reduction (128-50000).")] int maxPoints = 6000,
        [Description("Include convex hull vertices in response (default true).")] bool includeHull = true,
        [Description("Include sampled point cloud in response (default false).")] bool includeSamplePoints = false)
    {
        if (string.IsNullOrWhiteSpace(prefabPath))
        {
            return ToolErrors.ValidationError("Prefab path is required");
        }

        return await client.GetPrefabFootprint2DAsync(
            prefabPath,
            source,
            targetMinEdgeGap,
            maxPoints,
            includeHull,
            includeSamplePoints);
    }

    [McpServerTool(Name = "unity_get_prefab_preview")]
    [Description(@"Get a visual thumbnail of a prefab as an image.
Use this to see what a prefab looks like before spawning it.
The preview shows the prefab as it would appear in Unity's asset browser.")]
    public static async Task<IList<AIContent>> GetPrefabPreview(
        UnityClient client,
        [Description("The path to the prefab asset (e.g., 'Assets/Prefabs/Player.prefab').")] string prefabPath,
        [Description("Size of the thumbnail in pixels (default 128, max 256).")] int size = 128)
    {
        if (string.IsNullOrWhiteSpace(prefabPath))
        {
            return new List<AIContent> { new TextContent(ToolErrors.ValidationError("Prefab path is required")) };
        }

        size = Math.Clamp(size, 32, 256);
        var json = await client.GetPrefabPreviewAsync(prefabPath, size);
        return ImageContentHelper.ExtractImageContent(json, $"Prefab preview: {prefabPath} ({size}x{size})");
    }

    [McpServerTool(Name = "unity_get_prefab_previews")]
    [Description(@"Search for prefabs and get thumbnails for visual matching.
Returns prefab names/paths with optional base64 thumbnails.
Use this when you need to find a prefab that looks like something in a reference image,
or to visually browse available prefabs before deciding which to spawn.")]
    public static async Task<string> GetPrefabPreviews(
        UnityClient client,
        [Description("Search query to filter prefabs by name (e.g., 'character', 'tree', 'enemy'). Empty string returns all prefabs.")] string search = "",
        [Description("Maximum number of prefabs to return (default 20, max 50).")] int maxResults = 20,
        [Description("Size of thumbnails in pixels (default 64, smaller for faster results).")] int size = 64,
        [Description("Include base64 thumbnails in each result (default false for token efficiency).")] bool includeThumbnails = false)
    {
        maxResults = Math.Clamp(maxResults, 1, 50);
        size = Math.Clamp(size, 32, 128);
        return await client.GetPrefabPreviewsAsync(search, maxResults, size, includeThumbnails);
    }

    #endregion

    #region Material Preview Tools

    [McpServerTool(Name = "unity_get_material_preview")]
    [Description(@"Get a visual thumbnail of a material rendered on a sphere (material ball).
Returns the image directly as visual content showing how the material looks.
Use this to see what a material looks like before applying it to objects.")]
    public static async Task<IList<AIContent>> GetMaterialPreview(
        UnityClient client,
        [Description("The path to the material asset (e.g., 'Assets/Materials/Red.mat').")] string materialPath,
        [Description("Size of the thumbnail in pixels (default 128, max 256).")] int size = 128)
    {
        if (string.IsNullOrWhiteSpace(materialPath))
        {
            return new List<AIContent> { new TextContent(ToolErrors.ValidationError("Material path is required")) };
        }

        size = Math.Clamp(size, 32, 256);
        var json = await client.GetMaterialPreviewAsync(materialPath, size);
        return ImageContentHelper.ExtractImageContent(json, $"Material preview: {materialPath} ({size}x{size})");
    }

    [McpServerTool(Name = "unity_get_material_previews")]
    [Description(@"Search for materials and get thumbnails for visual matching.
Returns materials with paths, shader names, main colors, and optional base64 thumbnails.
Use this to find materials that match a reference image or to visually browse available materials.
Includes shader name and main color (#RRGGBBAA) for each material when available.")]
    public static async Task<string> GetMaterialPreviews(
        UnityClient client,
        [Description("Search query to filter materials by name (e.g., 'metal', 'wood', 'glass'). Empty string returns all materials.")] string search = "",
        [Description("Maximum number of materials to return (default 20, max 50).")] int maxResults = 20,
        [Description("Size of thumbnails in pixels (default 64, smaller for faster results).")] int size = 64,
        [Description("Include base64 thumbnails in each result (default false for token efficiency).")] bool includeThumbnails = false)
    {
        maxResults = Math.Clamp(maxResults, 1, 50);
        size = Math.Clamp(size, 32, 128);
        return await client.GetMaterialPreviewsAsync(search, maxResults, size, includeThumbnails);
    }

    #endregion

    #region Scene Recreation Pipeline Tools

    [McpServerTool(Name = "unity_get_visual_catalog")]
    [Description(@"Get a combined catalog of available prefabs and materials in a single call.
Use this to discover what assets are available for scene building. Thumbnails are optional and disabled by default for token efficiency.

Combines the functionality of unity_get_prefab_previews and unity_get_material_previews into one efficient call.
Optionally includes a list of available shaders.

Returns:
{
  ""prefabs"": [{ ""name"": ""..."", ""path"": ""..."", ""thumbnail"": ""<base64>"" }],
  ""materials"": [{ ""name"": ""..."", ""path"": ""..."", ""shaderName"": ""..."", ""mainColor"": ""#RRGGBBAA"", ""thumbnail"": ""<base64>"" }],
  ""shaders"": [{ ""name"": ""..."", ""path"": ""..."" }]
}")]
    public static async Task<string> GetVisualCatalog(
        UnityClient client,
        [Description("Filter prefabs by name (e.g., 'tree', 'character'). Empty string returns all.")] string prefabSearch = "",
        [Description("Filter materials by name (e.g., 'metal', 'wood'). Empty string returns all.")] string materialSearch = "",
        [Description("Maximum number of prefabs to return (default 30).")] int maxPrefabs = 30,
        [Description("Maximum number of materials to return (default 30).")] int maxMaterials = 30,
        [Description("Pixel size of thumbnails (default 64, max 128).")] int thumbnailSize = 64,
        [Description("Whether to also list available shaders (default false).")] bool includeShaders = false,
        [Description("Include base64 thumbnails in prefab/material entries (default false for token efficiency).")] bool includeThumbnails = false)
    {
        maxPrefabs = Math.Clamp(maxPrefabs, 1, 100);
        maxMaterials = Math.Clamp(maxMaterials, 1, 100);
        thumbnailSize = Math.Clamp(thumbnailSize, 32, 128);
        return await client.GetAssetCatalogAsync(prefabSearch, materialSearch, maxPrefabs, maxMaterials, thumbnailSize, includeShaders, includeThumbnails);
    }

    [McpServerTool(Name = "unity_build_scene_and_screenshot")]
    [Description(@"Atomically build a scene from a descriptor, position the camera, and take a screenshot — all in one call.
Guarantees the screenshot reflects exactly the objects just created. All objects are created in a single undo group.

Use this for the 'build and verify' step in a reference-to-rebuild workflow:
1. Generate a SceneDescriptor JSON matching your reference image
2. Call this tool to build + screenshot in one round-trip
3. Compare the screenshot against the reference

Returns the screenshot as a visible image plus metadata about created objects.")]
    public static async Task<IList<AIContent>> BuildSceneAndScreenshot(
        UnityClient client,
        [Description("JSON scene descriptor (same format as unity_create_scene_from_descriptor). Must have 'objects' array.")] string descriptor,
        [Description("Screenshot view type: 'scene' (default) or 'game'.")] string screenshotView = "scene",
        [Description("Optional scene camera position as [x,y,z] JSON array.")] string? cameraPosition = null,
        [Description("Optional scene camera rotation as [x,y,z] euler angles JSON array.")] string? cameraRotation = null)
    {
        if (string.IsNullOrWhiteSpace(descriptor))
        {
            return new List<AIContent> { new TextContent(ToolErrors.ValidationError("Descriptor JSON is required")) };
        }

        try
        {
            // Normalize descriptor aliases and material map
            var parsedDesc = JsonSerializer.Deserialize<JsonElement>(descriptor);
            var normalizedDesc = NormalizeDescriptor(parsedDesc);
            var normalizedJson = JsonSerializer.Serialize(normalizedDesc);

            // Build the request object
            var request = new Dictionary<string, object?>
            {
                { "descriptor", normalizedJson },
                { "screenshotView", screenshotView }
            };

            if (!string.IsNullOrWhiteSpace(cameraPosition))
            {
                var pos = JsonSerializer.Deserialize<float[]>(cameraPosition);
                request["cameraPosition"] = pos;
            }

            if (!string.IsNullOrWhiteSpace(cameraRotation))
            {
                var rot = JsonSerializer.Deserialize<float[]>(cameraRotation);
                request["cameraRotation"] = rot;
            }

            var json = await client.BuildSceneAndScreenshotAsync(request);
            return ImageContentHelper.ExtractScreenshotFromResponse(json);
        }
        catch (JsonException ex)
        {
            return new List<AIContent> { new TextContent(JsonSerializer.Serialize(new { success = false, error = $"Invalid JSON: {ex.Message}" })) };
        }
    }

    [McpServerTool(Name = "unity_scene_transaction")]
    [Description(@"Build a scene with automatic checkpoint and rollback safety.
Creates a checkpoint before building, then builds the scene from a descriptor and takes a screenshot.
If anything fails and autoRollbackOnError is true, automatically restores to the checkpoint.

Returns the checkpoint ID so you can manually roll back later if the visual result doesn't match:
  → unity_restore_checkpoint(checkpointId) to undo everything

Workflow:
1. Call this tool with your descriptor — get screenshot + checkpointId
2. Compare screenshot against reference
3. If mismatch: either tweak individual objects, or restore checkpoint and try again

Returns the screenshot as a visible image plus metadata (checkpointId, createdCount, instanceIds).")]
    public static async Task<IList<AIContent>> SceneTransaction(
        UnityClient client,
        [Description("JSON scene descriptor (same format as unity_create_scene_from_descriptor). Must have 'objects' array.")] string descriptor,
        [Description("Screenshot view type: 'scene' (default) or 'game'.")] string screenshotView = "scene",
        [Description("Optional scene camera position as [x,y,z] JSON array.")] string? cameraPosition = null,
        [Description("Optional scene camera rotation as [x,y,z] euler angles JSON array.")] string? cameraRotation = null,
        [Description("Name for the checkpoint (auto-generated if not provided).")] string? checkpointName = null,
        [Description("Whether to auto-rollback on failure (default true).")] bool autoRollbackOnError = true,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        if (string.IsNullOrWhiteSpace(descriptor))
        {
            return new List<AIContent> { new TextContent(ToolErrors.ValidationError("Descriptor JSON is required")) };
        }

        try
        {
            var request = new Dictionary<string, object?>
            {
                { "descriptor", descriptor },
                { "screenshotView", screenshotView },
                { "autoRollbackOnError", autoRollbackOnError ? 1 : 0 }
            };

            if (!string.IsNullOrWhiteSpace(cameraPosition))
            {
                var pos = JsonSerializer.Deserialize<float[]>(cameraPosition);
                request["cameraPosition"] = pos;
            }

            if (!string.IsNullOrWhiteSpace(cameraRotation))
            {
                var rot = JsonSerializer.Deserialize<float[]>(cameraRotation);
                request["cameraRotation"] = rot;
            }

            if (!string.IsNullOrWhiteSpace(checkpointName))
            {
                request["checkpointName"] = checkpointName;
            }

            var json = await client.SceneTransactionAsync(request, timeoutMs > 0 ? timeoutMs : null);
            return ImageContentHelper.ExtractScreenshotFromResponse(json);
        }
        catch (JsonException ex)
        {
            return new List<AIContent> { new TextContent(JsonSerializer.Serialize(new { success = false, error = $"Invalid JSON: {ex.Message}" })) };
        }
    }

    [McpServerTool(Name = "unity_apply_scene_patch_batch")]
    [Description(@"Apply many scene patch operations in one request to reduce round-trips and tokens.
Supports review-first flow (review hash + risk score), dry-run validation, atomic mode,
rollback-on-fail via checkpoint, and brief/diff-only responses.

Supported ops: spawn, delete_gameobject, modify_gameobject, add_component, remove_component,
modify_component, patch_serialized_properties, set_renderer_materials, reparent,
set_render_settings, set_volume_profile_overrides, set_camera_rendering.

Format: each item is {""op"": ""<op_name>"", ...payload fields}. Payload fields are flattened
alongside op (no nested ""payload"" key needed).

Examples:
  Rename:   {""op"": ""modify_gameobject"", ""instanceId"": 123, ""name"": ""NewName""}
  Move:     {""op"": ""modify_gameobject"", ""instanceId"": 123, ""position"": [1,2,3]}
  Delete:   {""op"": ""delete_gameobject"", ""instanceId"": 123}
  Spawn:    {""op"": ""spawn"", ""prefabPath"": ""Assets/Foo.prefab"", ""position"": [0,0,0]}
  Add comp: {""op"": ""add_component"", ""instanceId"": 123, ""componentType"": ""Rigidbody""}
  Reparent: {""op"": ""reparent"", ""instanceId"": 123, ""newParentId"": 456}")]
    public static async Task<string> ApplyScenePatchBatch(
        UnityClient client,
        [Description("JSON array of operations. Each item: {\"op\":\"...\",\"payload\":{...}}.")] string operationsJson,
        [Description("Review-only mode. Runs as dry-run and returns reviewHash + risk report without mutating.")] bool reviewOnly = false,
        [Description("Validate only, do not mutate (default false).")] bool dryRun = false,
        [Description("Stop at first failure (default false).")] bool atomic = false,
        [Description("Restore checkpoint automatically on failure (default true).")] bool rollbackOnFail = true,
        [Description("Auto-create checkpoint before mutation (default true).")] bool autoCheckpoint = true,
        [Description("Require approvedReviewHash for non-dry-run apply (default false).")] bool requireApproval = false,
        [Description("Review hash from a prior reviewOnly run to approve mutation.")] string? approvedReviewHash = null,
        [Description("Return compact per-op results (default true).")] bool brief = true,
        [Description("Include changed target summary (default true).")] bool diffOnly = true,
        [Description("Optional route timeout override in milliseconds for long batch operations.")] int timeoutMs = 0,
        [Description("Optional checkpoint name when checkpoint is created.")] string? checkpointName = null)
    {
        if (string.IsNullOrWhiteSpace(operationsJson))
        {
            return ToolErrors.ValidationError("operationsJson is required");
        }

        try
        {
            var operationsElement = JsonSerializer.Deserialize<JsonElement>(operationsJson);
            if (operationsElement.ValueKind != JsonValueKind.Array)
            {
                return ToolErrors.ValidationError("operationsJson must be a JSON array");
            }

            var request = new Dictionary<string, object?>
            {
                ["operations"] = operationsElement,
                ["reviewOnly"] = reviewOnly ? 1 : 0,
                ["dryRun"] = dryRun ? 1 : 0,
                ["atomic"] = atomic ? 1 : 0,
                ["rollbackOnFail"] = rollbackOnFail ? 1 : 0,
                ["autoCheckpoint"] = autoCheckpoint ? 1 : 0,
                ["requireApproval"] = requireApproval ? 1 : 0,
                ["brief"] = brief ? 1 : 0,
                ["diffOnly"] = diffOnly ? 1 : 0
            };

            if (!string.IsNullOrWhiteSpace(approvedReviewHash))
            {
                request["approvedReviewHash"] = approvedReviewHash;
            }

            if (!string.IsNullOrWhiteSpace(checkpointName))
            {
                request["checkpointName"] = checkpointName;
            }

            return await client.ApplyScenePatchBatchAsync(request, timeoutMs > 0 ? timeoutMs : null);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = $"Invalid operationsJson: {ex.Message}" });
        }
    }

    [McpServerTool(Name = "unity_review_scene_patch_batch")]
    [Description(@"Review a scene patch batch without mutating and return:
- validation results
- riskScore/riskLevel/riskReasons
- reviewHash for explicit approval in unity_apply_scene_patch_batch

Use this before apply for safer autonomous runs.")]
    public static async Task<string> ReviewScenePatchBatch(
        UnityClient client,
        [Description("JSON array of operations. Each item: {\"op\":\"...\",\"payload\":{...}}.")] string operationsJson,
        [Description("Stop at first failure during validation (default true).")] bool atomic = true,
        [Description("Assume rollback-on-fail apply posture for risk modeling (default true).")] bool rollbackOnFail = true,
        [Description("Assume auto-checkpoint apply posture for risk modeling (default true).")] bool autoCheckpoint = true,
        [Description("Return compact per-op validation results (default true).")] bool brief = true,
        [Description("Include changed target summary when possible (default true).")] bool diffOnly = true,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        return await ApplyScenePatchBatch(
            client,
            operationsJson: operationsJson,
            reviewOnly: true,
            dryRun: true,
            atomic: atomic,
            rollbackOnFail: rollbackOnFail,
            autoCheckpoint: autoCheckpoint,
            requireApproval: false,
            approvedReviewHash: null,
            brief: brief,
            diffOnly: diffOnly,
            timeoutMs: timeoutMs,
            checkpointName: null);
    }

    [McpServerTool(Name = "unity_compare_images")]
    [Description(@"Compare a reference image against a current screenshot/image. Returns similarity metrics,
a 'composition' block with spatial analysis (horizon position, visual center of mass, vertical luminance
and edge density per third), and 'suggestions' with actionable CAMERA/FRAMING/LAYOUT/LIGHTING/COLOR guidance.

Input images are base64 strings (raw or data URI). If currentImageBase64 is omitted and
captureCurrentScreenshot=true, the tool captures the current Unity Scene/Game view first.

IMPORTANT: Read the suggestions — they tell you exactly what's wrong spatially (e.g., 'horizon too low',
'subject too far left', 'not enough detail in top third'). Fix camera and layout issues before color adjustments.")]
    public static async Task<string> CompareImages(
        UnityClient client,
        [Description("Reference image base64 (raw or data URI). Optional when referenceImageHandle or referenceFilePath is provided.")] string? referenceImageBase64 = null,
        [Description("Reference image handle from unity_screenshot/unity_store_image_handle.")] string? referenceImageHandle = null,
        [Description("Absolute path to a local reference image file (png/jpg). Avoids large base64 in tool params.")] string? referenceFilePath = null,
        [Description("Current image base64 (raw or data URI). Optional when captureCurrentScreenshot=true.")] string? currentImageBase64 = null,
        [Description("Current image handle from unity_screenshot/unity_store_image_handle.")] string? currentImageHandle = null,
        [Description("Capture Unity screenshot when currentImageBase64 is omitted.")] bool captureCurrentScreenshot = true,
        [Description("Screenshot source when capturing current image: scene or game.")] string screenshotView = "scene",
        [Description("Optional screenshot width used when captureCurrentScreenshot=true (64-1920).")] int screenshotWidth = 0,
        [Description("Optional screenshot height used when captureCurrentScreenshot=true (64-1920).")] int screenshotHeight = 0,
        [Description("Maximum analysis dimension (64-1024).")] int downsampleMaxSize = 256,
        [Description("Heatmap grid size (2-32).")] int gridSize = 8,
        [Description("Threshold for changed pixel ratio metric (0-1).")] float changedPixelThreshold = 0.12f,
        [Description("Threshold for hotspot cells in heatmap metadata (0-1).")] float hotThreshold = 0.2f,
        [Description("Include heatmap metadata in response.")] bool includeHeatmap = false,
        [Description("Store reference image as handle in response when only base64 is provided.")] bool storeReferenceHandle = false,
        [Description("Include imageRefs handles in response (default false for token efficiency).")] bool includeImageHandles = false,
        [Description("Aspect correction mode: 'none' (raw comparison), 'crop' (center-crop wider to narrower aspect), 'fit_letterbox' (pad narrower with black bars). Default 'none'.")] string aspectMode = "none",
        [Description("Optional comma-separated response projection fields (for token control).")] string? fields = null,
        [Description("Omit null/empty values from response (default true).")] bool omitEmpty = true,
        [Description("Max items per list field in response (default 256).")] int maxItems = 256,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        // Resolve filePath to base64 if provided
        if (!string.IsNullOrWhiteSpace(referenceFilePath) && string.IsNullOrWhiteSpace(referenceImageBase64) && string.IsNullOrWhiteSpace(referenceImageHandle))
        {
            if (!File.Exists(referenceFilePath))
                return ToolErrors.ValidationError($"File not found: {referenceFilePath}");
            try
            {
                var bytes = await File.ReadAllBytesAsync(referenceFilePath);
                referenceImageBase64 = Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                return ToolErrors.ValidationError($"Failed to read file: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(referenceImageBase64) && string.IsNullOrWhiteSpace(referenceImageHandle))
        {
            return ToolErrors.ValidationError("referenceImageBase64, referenceImageHandle, or referenceFilePath is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["captureCurrentScreenshot"] = captureCurrentScreenshot ? 1 : 0,
            ["screenshotView"] = screenshotView,
            ["screenshotWidth"] = screenshotWidth,
            ["screenshotHeight"] = screenshotHeight,
            ["downsampleMaxSize"] = downsampleMaxSize,
            ["gridSize"] = gridSize,
            ["changedPixelThreshold"] = changedPixelThreshold,
            ["hotThreshold"] = hotThreshold,
            ["includeHeatmap"] = includeHeatmap ? 1 : 0,
            ["storeReferenceHandle"] = storeReferenceHandle ? 1 : 0,
            ["includeImageHandles"] = includeImageHandles ? 1 : 0,
            ["omitEmpty"] = omitEmpty ? 1 : 0,
            ["maxItems"] = maxItems
        };

        if (!string.IsNullOrWhiteSpace(aspectMode) && aspectMode != "none")
        {
            request["aspectMode"] = aspectMode;
        }
        if (!string.IsNullOrWhiteSpace(referenceImageBase64))
        {
            request["referenceImageBase64"] = referenceImageBase64;
        }
        if (!string.IsNullOrWhiteSpace(referenceImageHandle))
        {
            request["referenceImageHandle"] = referenceImageHandle;
        }
        if (!string.IsNullOrWhiteSpace(currentImageBase64))
        {
            request["currentImageBase64"] = currentImageBase64;
        }
        if (!string.IsNullOrWhiteSpace(currentImageHandle))
        {
            request["currentImageHandle"] = currentImageHandle;
        }
        if (!string.IsNullOrWhiteSpace(fields))
        {
            request["fields"] = fields;
        }

        var compareJson = await client.CompareImagesAsync(request, timeoutMs > 0 ? timeoutMs : null);

        // Inject convergence tracking
        var convergenceKey = ConvergenceTracker.DeriveKey(referenceImageHandle, referenceImageBase64);
        try
        {
            using var parseDoc = JsonDocument.Parse(compareJson);
            if (parseDoc.RootElement.TryGetProperty("metrics", out var metricsProp) &&
                metricsProp.TryGetProperty("similarityScore", out var simProp) &&
                simProp.TryGetSingle(out var similarity))
            {
                ConvergenceTracker.Record(convergenceKey, similarity);
            }
            var responseDict = JsonSerializer.Deserialize<Dictionary<string, object>>(compareJson);
            if (responseDict != null)
            {
                responseDict["convergence"] = ConvergenceTracker.GetMetadata(convergenceKey);
                compareJson = JsonSerializer.Serialize(responseDict);
            }
        }
        catch { /* leave compareJson unmodified on parse failure */ }

        return compareJson;
    }

    #endregion

    #region Descriptor Normalization

    /// <summary>
    /// Pre-process a scene descriptor JSON to resolve field aliases and material maps
    /// before sending to Unity's JsonUtility (which doesn't support dictionaries or aliases).
    /// Handles: "primitive"/"type" → "primitiveType", "material" → "materialPath",
    /// top-level "materials" map resolution.
    /// </summary>
    private static JsonElement NormalizeDescriptor(JsonElement descriptor)
    {
        // Extract materials map if present
        Dictionary<string, string>? materialsMap = null;
        if (descriptor.TryGetProperty("materials", out var matsEl) && matsEl.ValueKind == JsonValueKind.Object)
        {
            materialsMap = new Dictionary<string, string>();
            foreach (var prop in matsEl.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    materialsMap[prop.Name] = prop.Value.GetString()!;
            }
        }

        // Rebuild the descriptor without the materials map, normalizing objects
        var result = new Dictionary<string, object?>();
        foreach (var prop in descriptor.EnumerateObject())
        {
            if (prop.Name == "materials")
                continue; // consumed above
            if (prop.Name == "objects" && prop.Value.ValueKind == JsonValueKind.Array)
            {
                var normalized = new List<object?>();
                foreach (var obj in prop.Value.EnumerateArray())
                    normalized.Add(NormalizeObject(obj, materialsMap));
                result[prop.Name] = normalized;
            }
            else
            {
                result[prop.Name] = prop.Value;
            }
        }

        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(result));
    }

    private static Dictionary<string, object?> NormalizeObject(JsonElement obj, Dictionary<string, string>? materialsMap)
    {
        var result = new Dictionary<string, object?>();
        string? resolvedPrimitiveType = null;

        foreach (var prop in obj.EnumerateObject())
        {
            switch (prop.Name)
            {
                // Resolve "primitive" or "type" → "primitiveType"
                case "primitive" or "type":
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        resolvedPrimitiveType ??= prop.Value.GetString();
                    break;

                case "primitiveType":
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        resolvedPrimitiveType = prop.Value.GetString(); // explicit takes priority
                    break;

                // Resolve "material" → "materialPath" via materials map
                case "material":
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var matRef = prop.Value.GetString()!;
                        if (materialsMap != null && materialsMap.TryGetValue(matRef, out var matPath))
                            result["materialPath"] = matPath;
                        else
                            result["materialPath"] = matRef; // pass through as-is (might be a path)
                    }
                    break;

                // Recurse into children
                case "children":
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var children = new List<object?>();
                        foreach (var child in prop.Value.EnumerateArray())
                            children.Add(NormalizeObject(child, materialsMap));
                        result["children"] = children;
                    }
                    break;

                default:
                    result[prop.Name] = prop.Value;
                    break;
            }
        }

        if (resolvedPrimitiveType != null)
            result["primitiveType"] = resolvedPrimitiveType;

        return result;
    }

    #endregion
}
