using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

public static partial class SceneBuilderTools
{
    #region Scene Recreation Pipeline Tools

    [McpServerTool(Name = "unity_measure_tile_separation")]
    [Description(@"Measure geometric and visual tile separation quality for an existing lane/path layout.
Returns min/avg nearest edge distance, overlap counts, optional luminance-bleed metrics from a screenshot,
and generation constraints (lane gap, forward gap, curvature widening) for visual-gap-safe rebuilding.")]
    public static async Task<string> MeasureTileSeparation(
        UnityClient client,
        [Description("Optional root object instance ID containing tile children.")] int? rootInstanceId = null,
        [Description("Optional root object name used when rootInstanceId is stale/unknown (e.g., 'Environment').")] string? rootName = null,
        [Description("Optional explicit tile instance IDs (overrides rootInstanceId selection when provided).")] int[]? instanceIds = null,
        [Description("Include inactive objects when collecting tiles (default false).")] bool includeInactive = false,
        [Description("Target minimum edge gap in world units for pass/fail risk scoring.")] float targetMinEdgeGap = 0.04f,
        [Description("Capture screenshot and compute luminance bleed metrics (default true).")] bool captureScreenshot = true,
        [Description("Screenshot source when captureScreenshot=true: scene or game.")] string screenshotView = "game",
        [Description("Bright-core luminance threshold (0-1).")] float brightThreshold = 0.92f,
        [Description("Glow-band luminance threshold (0-1).")] float glowThreshold = 0.65f,
        [Description("Maximum number of tiles analyzed (10-1500).")] int maxTiles = 600,
        [Description("Include the closest pair samples in the response for debugging (default false).")] bool includeClosestPairs = false,
        [Description("Optional comma-separated response projection fields (for token control).")] string? fields = null,
        [Description("Omit null/empty values from response (default true).")] bool omitEmpty = true,
        [Description("Max items per list field in response (default 256).")] int maxItems = 256,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        var request = new Dictionary<string, object?>
        {
            ["includeInactive"] = includeInactive ? 1 : 0,
            ["targetMinEdgeGap"] = targetMinEdgeGap,
            ["captureScreenshot"] = captureScreenshot ? 1 : 0,
            ["screenshotView"] = screenshotView,
            ["brightThreshold"] = brightThreshold,
            ["glowThreshold"] = glowThreshold,
            ["maxTiles"] = maxTiles,
            ["includeClosestPairs"] = includeClosestPairs ? 1 : 0,
            ["omitEmpty"] = omitEmpty ? 1 : 0,
            ["maxItems"] = maxItems
        };

        if (rootInstanceId.HasValue && rootInstanceId.Value != 0)
        {
            request["rootInstanceId"] = rootInstanceId.Value;
        }
        if (!string.IsNullOrWhiteSpace(rootName))
        {
            request["rootName"] = rootName;
        }

        if (instanceIds != null && instanceIds.Length > 0)
        {
            request["instanceIds"] = instanceIds;
        }
        if (!string.IsNullOrWhiteSpace(fields)) request["fields"] = fields;

        return await client.MeasureTileSeparationAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_resolve_tile_overlaps")]
    [Description(@"Resolve tile overlaps and minimum-gap violations by nudging objects apart in XZ space.
Use this after spawning lanes/tiles when slight overlaps still occur on curves.")]
    public static async Task<string> ResolveTileOverlaps(
        UnityClient client,
        [Description("Optional root object instance ID containing tile children.")] int? rootInstanceId = null,
        [Description("Optional root object name used when rootInstanceId is stale/unknown (e.g., 'Environment').")] string? rootName = null,
        [Description("Optional explicit tile instance IDs (overrides rootInstanceId selection when provided).")] int[]? instanceIds = null,
        [Description("Include inactive objects when collecting tiles (default false).")] bool includeInactive = false,
        [Description("Target minimum edge gap in world units.")] float targetMinEdgeGap = 0.04f,
        [Description("Maximum solver iterations (1-64).")] int maxIterations = 10,
        [Description("Minimum nudge step per adjustment in world units.")] float nudgeStep = 0.0025f,
        [Description("Maximum number of tiles considered.")] int maxTiles = 1200,
        [Description("If true, save scene after successful adjustments.")] bool autoSaveScene = false,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        var request = new Dictionary<string, object?>
        {
            ["includeInactive"] = includeInactive ? 1 : 0,
            ["targetMinEdgeGap"] = targetMinEdgeGap,
            ["maxIterations"] = maxIterations,
            ["nudgeStep"] = nudgeStep,
            ["maxTiles"] = maxTiles,
            ["autoSaveScene"] = autoSaveScene ? 1 : 0
        };

        if (rootInstanceId.HasValue && rootInstanceId.Value != 0) request["rootInstanceId"] = rootInstanceId.Value;
        if (!string.IsNullOrWhiteSpace(rootName)) request["rootName"] = rootName;
        if (instanceIds != null && instanceIds.Length > 0) request["instanceIds"] = instanceIds;

        return await client.ResolveTileOverlapsAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_solve_layout_constraints")]
    [Description(@"Solve generic scene layout constraints (distance, align, offset, bounds clamp, non-overlap) for arbitrary objects.
Works across any scene type (interior/exterior/props/paths) and supports dry-run or apply mode.
Returns violations before/after and suggested patch operations.")]
    public static async Task<string> SolveLayoutConstraints(
        UnityClient client,
        [Description("Constraint set as JSON array. Each item supports type: distance|align|offset|inside_bounds|no_overlap with instance IDs and params.")] string constraintsJson,
        [Description("Optional object instance IDs to include in solver scope.")] int[]? instanceIds = null,
        [Description("Solver iterations (1-64).")] int iterations = 12,
        [Description("Per-iteration step size (0.01-1.0).")] float step = 0.35f,
        [Description("Damping factor per iteration (0.1-1.0).")] float damping = 0.85f,
        [Description("If true, apply solved transforms to scene. If false, dry-run only.")] bool apply = false,
        [Description("If true, include inactive objects when resolving instance IDs.")] bool includeInactive = false,
        [Description("If true and apply=true, save scene after successful solve.")] bool autoSaveScene = false,
        [Description("Optional comma-separated response projection fields (for token control).")] string? fields = null,
        [Description("Omit null/empty values from response (default true).")] bool omitEmpty = true,
        [Description("Max items per list field in response (default 256).")] int maxItems = 256,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        if (string.IsNullOrWhiteSpace(constraintsJson))
        {
            return ToolErrors.ValidationError("constraintsJson is required");
        }

        JsonElement constraintsElement;
        try
        {
            constraintsElement = JsonSerializer.Deserialize<JsonElement>(constraintsJson);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = $"Invalid constraintsJson: {ex.Message}" });
        }

        var request = new Dictionary<string, object?>
        {
            ["constraints"] = constraintsElement,
            ["iterations"] = iterations,
            ["step"] = step,
            ["damping"] = damping,
            ["apply"] = apply ? 1 : 0,
            ["includeInactive"] = includeInactive ? 1 : 0,
            ["autoSaveScene"] = autoSaveScene ? 1 : 0,
            ["omitEmpty"] = omitEmpty ? 1 : 0,
            ["maxItems"] = maxItems
        };

        if (instanceIds != null && instanceIds.Length > 0) request["instanceIds"] = instanceIds;
        if (!string.IsNullOrWhiteSpace(fields)) request["fields"] = fields;

        return await client.SolveLayoutConstraintsAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_plan_scene_reconstruction")]
    [Description(@"Build a multi-pass reconstruction plan from reference/current images and live scene context.
Returns measurable targets, prioritized passes (layout/assets/look/camera/validation), diagnostics summary,
and top applyable proposals so agents can reproduce arbitrary scenes with fewer trial-and-error loops.")]
    public static async Task<string> PlanSceneReconstruction(
        UnityClient client,
        [Description("Reference image base64 (raw or data URI). Optional when referenceImageHandle or referenceFilePath is provided.")] string? referenceImageBase64 = null,
        [Description("Reference image handle from unity_screenshot/unity_store_image_handle.")] string? referenceImageHandle = null,
        [Description("Absolute path to a local reference image file (png/jpg). Avoids large base64 in tool params.")] string? referenceFilePath = null,
        [Description("Current image base64 (raw or data URI). Optional when captureCurrentScreenshot=true.")] string? currentImageBase64 = null,
        [Description("Current image handle from unity_screenshot/unity_store_image_handle.")] string? currentImageHandle = null,
        [Description("Capture Unity screenshot when current image is omitted.")] bool captureCurrentScreenshot = true,
        [Description("Screenshot source when capturing current image: scene or game.")] string screenshotView = "scene",
        [Description("Optional screenshot width used when captureCurrentScreenshot=true (64-1920).")] int screenshotWidth = 0,
        [Description("Optional screenshot height used when captureCurrentScreenshot=true (64-1920).")] int screenshotHeight = 0,
        [Description("Aspect correction mode for planning metrics: none, crop, fit_letterbox.")] string aspectMode = "crop",
        [Description("Optional free-form scene intent (e.g., 'cyberpunk alley', 'cozy interior').")] string? sceneIntent = null,
        [Description("Optional style hint (e.g., 'moody fog, neon rim light').")] string? styleHint = null,
        [Description("Optional camera instance ID for camera-targeted proposal context.")] int? cameraInstanceId = null,
        [Description("Optional camera name for camera-targeted proposal context.")] string? cameraName = null,
        [Description("Optional volume instance ID for look-targeted proposal context.")] int? volumeInstanceId = null,
        [Description("Optional volume profile path for look-targeted proposal context.")] string? profilePath = null,
        [Description("Optional tile/track root name for layout snapshot context.")] string? tileRootName = null,
        [Description("Max tile candidates used for scene layout snapshot (10-1500).")] int maxTiles = 600,
        [Description("Maximum proposals to include in the plan (1-24).")] int maxProposals = 8,
        [Description("Minimum confidence threshold (0.05-0.95).")] float minConfidence = 0.3f,
        [Description("Maximum analysis dimension (64-1024).")] int downsampleMaxSize = 256,
        [Description("Heatmap grid size (2-32).")] int gridSize = 8,
        [Description("Threshold for changed pixel ratio metric (0-1).")] float changedPixelThreshold = 0.12f,
        [Description("Threshold for hotspot cells in heatmap metadata (0-1).")] float hotThreshold = 0.2f,
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
            ["aspectMode"] = aspectMode,
            ["maxTiles"] = maxTiles,
            ["maxProposals"] = maxProposals,
            ["minConfidence"] = minConfidence,
            ["downsampleMaxSize"] = downsampleMaxSize,
            ["gridSize"] = gridSize,
            ["changedPixelThreshold"] = changedPixelThreshold,
            ["hotThreshold"] = hotThreshold,
            ["omitEmpty"] = omitEmpty ? 1 : 0,
            ["maxItems"] = maxItems
        };

        if (!string.IsNullOrWhiteSpace(referenceImageBase64)) request["referenceImageBase64"] = referenceImageBase64;
        if (!string.IsNullOrWhiteSpace(referenceImageHandle)) request["referenceImageHandle"] = referenceImageHandle;
        if (!string.IsNullOrWhiteSpace(currentImageBase64)) request["currentImageBase64"] = currentImageBase64;
        if (!string.IsNullOrWhiteSpace(currentImageHandle)) request["currentImageHandle"] = currentImageHandle;
        if (!string.IsNullOrWhiteSpace(sceneIntent)) request["sceneIntent"] = sceneIntent;
        if (!string.IsNullOrWhiteSpace(styleHint)) request["styleHint"] = styleHint;
        if (!string.IsNullOrWhiteSpace(tileRootName)) request["tileRootName"] = tileRootName;
        if (cameraInstanceId.HasValue && cameraInstanceId.Value != 0) request["cameraInstanceId"] = cameraInstanceId.Value;
        if (!string.IsNullOrWhiteSpace(cameraName)) request["cameraName"] = cameraName;
        if (volumeInstanceId.HasValue && volumeInstanceId.Value != 0) request["volumeInstanceId"] = volumeInstanceId.Value;
        if (!string.IsNullOrWhiteSpace(profilePath)) request["profilePath"] = profilePath;
        if (!string.IsNullOrWhiteSpace(fields)) request["fields"] = fields;

        return await client.PlanSceneReconstructionAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_repro_step")]
    [Description(@"Run one image-guided reproduction step and return confidence-scored patch proposals.
The response includes:
- image analysis metrics + heatmap metadata
- ranked patch proposals with confidence/reason
- recommended batch payload for unity_apply_scene_patch_batch

This is a minimal heuristic loop (exposure/color/saturation/vignette/DoF/camera post-processing).")]
    public static async Task<string> ReproStep(
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
        [Description("Optional camera instance ID for camera-targeted proposals.")] int? cameraInstanceId = null,
        [Description("Optional camera name for camera-targeted proposals.")] string? cameraName = null,
        [Description("Optional volume instance ID for volume-targeted proposals.")] int? volumeInstanceId = null,
        [Description("Optional volume profile path for volume-targeted proposals.")] string? profilePath = null,
        [Description("Maximum proposals to return (1-24).")] int maxProposals = 6,
        [Description("Minimum confidence threshold (0.05-0.95).")] float minConfidence = 0.35f,
        [Description("Maximum analysis dimension (64-1024).")] int downsampleMaxSize = 256,
        [Description("Heatmap grid size (2-32).")] int gridSize = 8,
        [Description("Threshold for changed pixel ratio metric (0-1).")] float changedPixelThreshold = 0.12f,
        [Description("Threshold for hotspot cells in heatmap metadata (0-1).")] float hotThreshold = 0.2f,
        [Description("Include heatmap in analysis payload.")] bool includeHeatmap = false,
        [Description("Store reference image as handle in response when only base64 is provided.")] bool storeReferenceHandle = false,
        [Description("Include imageRefs handles in response (default false for token efficiency).")] bool includeImageHandles = false,
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
            ["maxProposals"] = maxProposals,
            ["minConfidence"] = minConfidence,
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

        if (!string.IsNullOrWhiteSpace(referenceImageBase64)) request["referenceImageBase64"] = referenceImageBase64;
        if (!string.IsNullOrWhiteSpace(referenceImageHandle)) request["referenceImageHandle"] = referenceImageHandle;
        if (!string.IsNullOrWhiteSpace(currentImageBase64)) request["currentImageBase64"] = currentImageBase64;
        if (!string.IsNullOrWhiteSpace(currentImageHandle)) request["currentImageHandle"] = currentImageHandle;
        if (cameraInstanceId.HasValue && cameraInstanceId.Value != 0) request["cameraInstanceId"] = cameraInstanceId.Value;
        if (!string.IsNullOrWhiteSpace(cameraName)) request["cameraName"] = cameraName;
        if (volumeInstanceId.HasValue && volumeInstanceId.Value != 0) request["volumeInstanceId"] = volumeInstanceId.Value;
        if (!string.IsNullOrWhiteSpace(profilePath)) request["profilePath"] = profilePath;
        if (!string.IsNullOrWhiteSpace(fields)) request["fields"] = fields;

        return await client.ReproStepAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_repro_step_contextual")]
    [Description(@"Run one image-guided reproduction step with contextual hydration in a single call.
This tool keeps the agent in control (single iteration), auto-loads compact context from:
- pinned asset-pack context (optional)
- scene profile summary (optional)
- asset catalog summary (optional)

By default it returns a compact context reference (`contextHash` + resolved names).
Set includeContext=true to include hydrated context summaries in the response.")]
    public static async Task<string> ReproStepContextual(
        UnityClient client,
        [Description("Reference image base64 (raw or data URI).")] string referenceImageBase64,
        [Description("Pinned asset-pack context name to auto-resolve profile/catalog/look names.")] string? assetPackPinName = null,
        [Description("Scene profile name override (optional).")] string? sceneProfileName = null,
        [Description("Asset catalog name override (optional).")] string? assetCatalogName = null,
        [Description("Look preset name override (optional).")] string? lookPresetName = null,
        [Description("Current image base64 (raw or data URI). Optional when captureCurrentScreenshot=true.")] string? currentImageBase64 = null,
        [Description("Capture Unity screenshot when currentImageBase64 is omitted.")] bool captureCurrentScreenshot = true,
        [Description("Screenshot source when capturing current image: scene or game.")] string screenshotView = "scene",
        [Description("Optional camera instance ID for camera-targeted proposals.")] int? cameraInstanceId = null,
        [Description("Optional camera name for camera-targeted proposals.")] string? cameraName = null,
        [Description("Optional volume instance ID for volume-targeted proposals.")] int? volumeInstanceId = null,
        [Description("Optional volume profile path for volume-targeted proposals.")] string? profilePath = null,
        [Description("Maximum proposals to return (1-24).")] int maxProposals = 6,
        [Description("Minimum confidence threshold (0.05-0.95).")] float minConfidence = 0.35f,
        [Description("Maximum analysis dimension (64-1024).")] int downsampleMaxSize = 256,
        [Description("Heatmap grid size (2-32).")] int gridSize = 8,
        [Description("Threshold for changed pixel ratio metric (0-1).")] float changedPixelThreshold = 0.12f,
        [Description("Threshold for hotspot cells in heatmap metadata (0-1).")] float hotThreshold = 0.2f,
        [Description("Max summary entries loaded from contextual sources (1-100).")] int contextMaxEntries = 20,
        [Description("Include hydrated pin/profile/catalog summaries in response (default false for token efficiency).")] bool includeContext = false,
        [Description("Optional route timeout override in milliseconds for compare/repro context calls.")] int timeoutMs = 0)
    {
        if (string.IsNullOrWhiteSpace(referenceImageBase64))
        {
            return ToolErrors.ValidationError("referenceImageBase64 is required");
        }

        contextMaxEntries = Math.Clamp(contextMaxEntries, 1, 100);

        JsonElement? pinElement = null;
        JsonElement? sceneProfileElement = null;
        JsonElement? assetCatalogElement = null;
        string? pinJsonRaw = null;
        string? sceneProfileJsonRaw = null;
        string? assetCatalogJsonRaw = null;
        string resolvedLookPresetName = lookPresetName ?? string.Empty;
        string resolvedSceneProfileName = sceneProfileName ?? string.Empty;
        string resolvedAssetCatalogName = assetCatalogName ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(assetPackPinName))
        {
            pinJsonRaw = await client.GetAssetPackContextPinAsync(assetPackPinName, brief: true, maxEntries: contextMaxEntries);
            if (TryParseJsonElement(pinJsonRaw, out var parsedPin))
            {
                pinElement = parsedPin;
                if (string.IsNullOrWhiteSpace(resolvedSceneProfileName)
                    && TryGetNestedString(parsedPin, out var pinSceneProfileName, "sceneProfile", "name"))
                {
                    resolvedSceneProfileName = pinSceneProfileName;
                }

                if (string.IsNullOrWhiteSpace(resolvedAssetCatalogName)
                    && TryGetNestedString(parsedPin, out var pinCatalogName, "catalog", "name"))
                {
                    resolvedAssetCatalogName = pinCatalogName;
                }

                if (string.IsNullOrWhiteSpace(resolvedLookPresetName)
                    && TryGetNestedString(parsedPin, out var pinLookPresetName, "lookPreset", "name"))
                {
                    resolvedLookPresetName = pinLookPresetName;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(resolvedSceneProfileName))
        {
            sceneProfileJsonRaw = await client.GetSavedSceneProfileAsync(resolvedSceneProfileName, brief: true, maxEntries: contextMaxEntries);
            if (TryParseJsonElement(sceneProfileJsonRaw, out var parsedProfile))
            {
                sceneProfileElement = parsedProfile;
            }
        }

        if (!string.IsNullOrWhiteSpace(resolvedAssetCatalogName))
        {
            assetCatalogJsonRaw = await client.GetSavedAssetCatalogAsync(resolvedAssetCatalogName, brief: true, maxEntries: contextMaxEntries);
            if (TryParseJsonElement(assetCatalogJsonRaw, out var parsedCatalog))
            {
                assetCatalogElement = parsedCatalog;
            }
        }

        var reproRequest = new Dictionary<string, object?>
        {
            ["referenceImageBase64"] = referenceImageBase64,
            ["captureCurrentScreenshot"] = captureCurrentScreenshot ? 1 : 0,
            ["screenshotView"] = screenshotView,
            ["maxProposals"] = maxProposals,
            ["minConfidence"] = minConfidence,
            ["downsampleMaxSize"] = downsampleMaxSize,
            ["gridSize"] = gridSize,
            ["changedPixelThreshold"] = changedPixelThreshold,
            ["hotThreshold"] = hotThreshold
        };

        if (!string.IsNullOrWhiteSpace(currentImageBase64)) reproRequest["currentImageBase64"] = currentImageBase64;
        if (cameraInstanceId.HasValue && cameraInstanceId.Value != 0) reproRequest["cameraInstanceId"] = cameraInstanceId.Value;
        if (!string.IsNullOrWhiteSpace(cameraName)) reproRequest["cameraName"] = cameraName;
        if (volumeInstanceId.HasValue && volumeInstanceId.Value != 0) reproRequest["volumeInstanceId"] = volumeInstanceId.Value;
        if (!string.IsNullOrWhiteSpace(profilePath)) reproRequest["profilePath"] = profilePath;

        var reproJson = await client.ReproStepAsync(reproRequest, timeoutMs > 0 ? timeoutMs : null);
        if (!TryParseJsonElement(reproJson, out var reproElement))
        {
            return reproJson;
        }

        var contextRef = new Dictionary<string, object?>
        {
            ["contextHash"] = ComputeContextHash(
                pinJsonRaw,
                sceneProfileJsonRaw,
                assetCatalogJsonRaw,
                resolvedLookPresetName,
                resolvedSceneProfileName,
                resolvedAssetCatalogName,
                contextMaxEntries),
            ["assetPackPinName"] = assetPackPinName ?? "",
            ["sceneProfileName"] = resolvedSceneProfileName,
            ["assetCatalogName"] = resolvedAssetCatalogName,
            ["lookPresetName"] = resolvedLookPresetName,
            ["contextMaxEntries"] = contextMaxEntries
        };

        var response = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["contextual"] = true,
            ["contextRef"] = contextRef,
            ["reproStep"] = reproElement
        };

        if (includeContext)
        {
            var context = new Dictionary<string, object?>();
            if (pinElement.HasValue) context["pin"] = pinElement.Value;
            if (sceneProfileElement.HasValue) context["sceneProfileSummary"] = sceneProfileElement.Value;
            if (assetCatalogElement.HasValue) context["assetCatalogSummary"] = assetCatalogElement.Value;
            response["context"] = context;
        }

        return JsonSerializer.Serialize(response);
    }

    [McpServerTool(Name = "unity_run_scene_quality_checks")]
    [Description(@"Run scene-level validation checks after mutations and return a severity-scored report.
This is designed as a safety gate for autonomous/editor-assisted scene work.

Checks include:
- missing scripts
- invalid tags/layers
- [RequireComponent] integrity
- broken serialized object references
- Rigidbody/Rigidbody2D collider sanity
- UI/EventSystem/GraphicRaycaster sanity
- lifecycle event unsubscribe heuristic (best-effort, source-based)
- rendering health: null materials, invalid shaders, Built-in shaders in URP, camera layer culling, missing meshes

Use failOnSeverity to define pass/fail threshold (info|warning|error).")]
    public static async Task<string> RunSceneQualityChecks(
        UnityClient client,
        [Description("Include inactive objects in checks (default true).")] bool includeInactive = true,
        [Description("Include informational (low-severity) findings in the response (default false).")] bool includeInfo = false,
        [Description("Enable [RequireComponent] dependency checks (default true).")] bool checkRequireComponents = true,
        [Description("Enable broken serialized object-reference checks (default true).")] bool checkSerializedReferences = true,
        [Description("Enable physics setup sanity checks (Rigidbody vs Collider) (default true).")] bool checkPhysicsSanity = true,
        [Description("Enable UI/EventSystem sanity checks (default true).")] bool checkUISanity = true,
        [Description("Enable lifecycle/event-unsubscribe heuristic checks (default true).")] bool checkLifecycleHeuristics = true,
        [Description("Enable rendering health checks: null materials, invalid shaders, camera culling, missing meshes (default true).")] bool checkRenderingHealth = true,
        [Description("Maximum findings to return (10-5000, default 200).")] int maxIssues = 200,
        [Description("Pass/fail threshold: info, warning, or error (default error).")] string failOnSeverity = "error",
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        maxIssues = Math.Clamp(maxIssues, 10, 5000);
        var normalizedFail = (failOnSeverity ?? "error").Trim().ToLowerInvariant();
        if (normalizedFail != "info" && normalizedFail != "warning" && normalizedFail != "error")
        {
            normalizedFail = "error";
        }

        var request = new Dictionary<string, object>
        {
            ["includeInactive"] = includeInactive ? 1 : 0,
            ["includeInfo"] = includeInfo ? 1 : 0,
            ["checkRequireComponents"] = checkRequireComponents ? 1 : 0,
            ["checkSerializedReferences"] = checkSerializedReferences ? 1 : 0,
            ["checkPhysicsSanity"] = checkPhysicsSanity ? 1 : 0,
            ["checkUISanity"] = checkUISanity ? 1 : 0,
            ["checkLifecycleHeuristics"] = checkLifecycleHeuristics ? 1 : 0,
            ["checkRenderingHealth"] = checkRenderingHealth ? 1 : 0,
            ["maxIssues"] = maxIssues,
            ["failOnSeverity"] = normalizedFail
        };

        return await client.RunSceneQualityChecksAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    private static bool TryParseJsonElement(string json, out JsonElement element)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<JsonElement>(json);
            element = parsed.Clone();
            return true;
        }
        catch
        {
            element = default;
            return false;
        }
    }

    private static bool TryGetNestedString(JsonElement root, out string value, params string[] path)
    {
        value = string.Empty;
        var current = root;
        if (path == null || path.Length == 0) return false;

        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        if (current.ValueKind == JsonValueKind.String)
        {
            value = current.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }

        if (current.ValueKind != JsonValueKind.Undefined && current.ValueKind != JsonValueKind.Null)
        {
            value = current.ToString();
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    #endregion

    #region Scene Profile Tools

    [McpServerTool(Name = "unity_extract_scene_profile")]
    [Description(@"Extract a comprehensive profile from the current scene in a single call.
This is the 'learn from demo scene' tool — captures everything about a scene:

1. Full scene descriptor (hierarchy, transforms, prefabs) via ExportSceneDescriptor
2. Embedded look preset (URP volume + render settings + camera + lights)
3. Material usage map: which materials are used on which objects
4. Prefab frequency: which prefabs are used most, with sample positions
5. Scene statistics: totalObjects, lightCount, uniqueMaterialCount, uniquePrefabCount, prefabDiversity

Always saves to file (token-safe). Returns file path + statistics summary, NOT the full profile inline.
Use unity_get_scene_profile to retrieve the full saved data.

Timeout: 30s for complex scenes.")]
    public static async Task<string> ExtractSceneProfile(
        UnityClient client,
        [Description("Name for the profile. Defaults to '{SceneName}_Profile'.")] string? name = null,
        [Description("Folder to save the profile JSON. Defaults to 'Assets/Editor/SceneProfiles'.")] string? savePath = null,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        return await client.ExtractSceneProfileAsync(new
        {
            name = name ?? "",
            savePath = savePath ?? ""
        }, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_get_scene_profile")]
    [Description(@"Retrieve a previously extracted scene profile by name.
Returns the full profile JSON including scene descriptor, look data, material usage,
prefab frequency, and statistics.

Use this after unity_extract_scene_profile to access the full data for analysis.")]
    public static async Task<string> GetSceneProfile(
        UnityClient client,
        [Description("Name of the profile to retrieve (same name used during extraction).")] string name,
        [Description("Return compact token-efficient summary instead of full profile JSON (default true).")] bool brief = true,
        [Description("Maximum list items included when brief=true (1-200).")] int maxEntries = 25)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolErrors.ValidationError("Profile name is required");
        }

        return await client.GetSavedSceneProfileAsync(name, brief, maxEntries);
    }

    private static string ComputeContextHash(
        string? pinJson,
        string? profileJson,
        string? catalogJson,
        string lookPresetName,
        string sceneProfileName,
        string assetCatalogName,
        int contextMaxEntries)
    {
        using var sha = SHA256.Create();
        var payload = string.Join("||",
            pinJson ?? string.Empty,
            profileJson ?? string.Empty,
            catalogJson ?? string.Empty,
            lookPresetName ?? string.Empty,
            sceneProfileName ?? string.Empty,
            assetCatalogName ?? string.Empty,
            contextMaxEntries.ToString());
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    #endregion

    #region Spatial Analysis Tools

    [McpServerTool(Name = "unity_check_spatial_enclosure")]
    [Description("Check if a set of wall, ceiling, and floor objects form a sealed enclosure. Reports gaps with location and size. Use to verify rooms/corridors have no holes.")]
    public static async Task<string> CheckSpatialEnclosure(
        UnityClient client,
        [Description("Instance IDs of wall objects (or parent containers). Each ID can be a single wall or a parent whose children are wall segments.")] int[] wallIds,
        [Description("Instance IDs of ceiling objects (or parent container).")] int[] ceilingIds,
        [Description("Instance IDs of floor objects (or parent container).")] int[] floorIds,
        [Description("Maximum acceptable gap in meters (default 0.1).")] float gapThreshold = 0.1f,
        [Description("If true (default), expand each ID to include children's renderers for bounds calculation.")] bool includeChildren = true)
    {
        if (wallIds == null || wallIds.Length == 0)
            return ToolErrors.ValidationError("wallIds is required");
        if (ceilingIds == null || ceilingIds.Length == 0)
            return ToolErrors.ValidationError("ceilingIds is required");
        if (floorIds == null || floorIds.Length == 0)
            return ToolErrors.ValidationError("floorIds is required");

        var request = new Dictionary<string, object?>
        {
            ["wallIds"] = wallIds,
            ["ceilingIds"] = ceilingIds,
            ["floorIds"] = floorIds,
            ["gapThreshold"] = gapThreshold,
            ["includeChildren"] = includeChildren ? 1 : 0
        };

        return await client.CheckSpatialEnclosureAsync(request);
    }

    #endregion
}
