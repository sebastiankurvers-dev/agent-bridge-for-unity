using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class SpatialAuditTools
{
    [McpServerTool(Name = "unity_camera_visibility_audit")]
    [Description(@"Audit object visibility from a camera's perspective using frustum culling + occlusion raycasts + attachment proximity.

Detects: objects fully occluded by walls/other geometry, objects partially occluded, objects outside camera frustum,
objects detached from support surfaces (floating corbels, decorations separated from walls).

Returns per-object: status (visible/partial/fully_occluded/out_of_frustum/detached), blockedSamples/totalSamples,
occludedBy list, screenRect, nearestSurface (when checkAttachment=true).

By default only reports non-visible or detached objects. Set includeVisible=true for full report.

Example - Check if knights are visible to player:
  unity_camera_visibility_audit(nameContains=""Knight"", view=""game"")

Example - Find detached decorations:
  unity_camera_visibility_audit(nameContains=""Corbel"", checkAttachment=true, attachMaxDistance=0.5)

Example - Audit all objects in a subtree:
  unity_camera_visibility_audit(rootInstanceId=12345, includeVisible=true)")]
    public static async Task<string> CameraVisibilityAudit(
        UnityClient client,
        [Description("Camera view to use: 'game' (Camera.main) or 'scene' (Scene View camera). Default: game.")] string view = "game",
        [Description("Filter objects by GameObject name substring (case-insensitive). Empty = all.")] string nameContains = "",
        [Description("Filter by tag name (e.g., 'Player'). Empty = all.")] string tag = "",
        [Description("Filter by layer name (e.g., 'Environment'). Empty = all.")] string layer = "",
        [Description("Scope audit to a specific GameObject subtree by instance ID. 0 = entire scene.")] int rootInstanceId = 0,
        [Description("Comma-separated instance IDs of specific objects to check. Overrides name/tag/layer filters.")] string targetInstanceIds = "",
        [Description("Maximum objects to audit (10-5000, default 500).")] int maxObjects = 500,
        [Description("Number of ray sample points per object for occlusion check (1-25, default 9: center + 8 corners).")] int raySamples = 9,
        [Description("Include visible objects in the report (default false — only issues reported).")] bool includeVisible = false,
        [Description("Check if objects are attached to nearby surfaces (default false). Detects floating objects.")] bool checkAttachment = false,
        [Description("Maximum distance (meters) to nearest surface before an object is flagged as 'detached' (default 0.5).")] float attachMaxDistance = 0.5f,
        [Description("Layer mask for occluder raycasts (-1 = all layers). Use to exclude specific layers from occlusion checks.")] int occluderLayerMask = -1,
        [Description("Ignore trigger colliders during raycasts (default true).")] bool ignoreTriggers = true,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        maxObjects = Math.Clamp(maxObjects, 10, 5000);
        raySamples = Math.Clamp(raySamples, 1, 25);

        var request = new Dictionary<string, object>
        {
            ["view"] = view ?? "game",
            ["nameContains"] = nameContains ?? "",
            ["tag"] = tag ?? "",
            ["layer"] = layer ?? "",
            ["rootInstanceId"] = rootInstanceId,
            ["targetInstanceIds"] = targetInstanceIds ?? "",
            ["maxObjects"] = maxObjects,
            ["raySamples"] = raySamples,
            ["includeVisible"] = includeVisible ? 1 : 0,
            ["checkAttachment"] = checkAttachment ? 1 : 0,
            ["attachMaxDistance"] = attachMaxDistance,
            ["occluderLayerMask"] = occluderLayerMask,
            ["ignoreTriggers"] = ignoreTriggers ? 1 : 0
        };

        return await client.CameraVisibilityAuditAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_raycast_coverage_check")]
    [Description(@"Cast a grid of rays across an area to detect gaps in floor, wall, or ceiling coverage.

Casts rays in a specified direction (default: downward) across a grid and reports where they miss expected
surface objects — revealing floor gaps, wall holes, or ceiling gaps after scene modifications.

Clusters contiguous misses into named gap regions with center position, approximate size, and area.

Must provide either rootInstanceId (auto-computes bounds from renderers) or explicit boundsMin/Max coordinates.

Hard limits: minimum spacing 0.1m, maximum 100,000 rays. If grid exceeds max, returns an actionable error.

Example - Check floor coverage under environment root:
  unity_raycast_coverage_check(rootInstanceId=12345, surfaceNameContains=""Floor"", spacing=0.5)

Example - Check wall coverage in explicit bounds:
  unity_raycast_coverage_check(boundsMinX=0, boundsMinY=0, boundsMinZ=0, boundsMaxX=10, boundsMaxY=5, boundsMaxZ=0.5, direction=""forward"")

Example - Fine-grained floor scan:
  unity_raycast_coverage_check(rootInstanceId=12345, surfaceTag=""Ground"", spacing=0.25)")]
    public static async Task<string> RaycastCoverageCheck(
        UnityClient client,
        [Description("Root GameObject instance ID to auto-compute scan bounds from its renderers. 0 = must provide explicit bounds.")] int rootInstanceId = 0,
        [Description("Explicit scan area minimum X coordinate. Use with boundsMax for custom scan area.")] float boundsMinX = -1f,
        [Description("Explicit scan area minimum Y coordinate.")] float boundsMinY = -1f,
        [Description("Explicit scan area minimum Z coordinate.")] float boundsMinZ = -1f,
        [Description("Explicit scan area maximum X coordinate.")] float boundsMaxX = -1f,
        [Description("Explicit scan area maximum Y coordinate.")] float boundsMaxY = -1f,
        [Description("Explicit scan area maximum Z coordinate.")] float boundsMaxZ = -1f,
        [Description("Ray direction: 'down' (floor gaps), 'up' (ceiling), 'forward'/'back'/'left'/'right' (walls). Default: down.")] string direction = "down",
        [Description("Grid spacing in meters (min 0.1, default 0.5). Smaller = more precise but more rays.")] float spacing = 0.5f,
        [Description("Maximum ray distance in meters (default 100).")] float maxRayDistance = 100f,
        [Description("Offset above scan area for ray origins in meters (default 10).")] float originOffset = 10f,
        [Description("Filter surface hits by GameObject name substring. Empty = all hits count as surface.")] string surfaceNameContains = "",
        [Description("Filter surface hits by tag. Empty = no tag filter.")] string surfaceTag = "",
        [Description("Filter surface hits by layer name. Empty = no layer filter.")] string surfaceLayer = "",
        [Description("Layer mask for raycasts (-1 = all layers).")] int surfaceLayerMask = -1,
        [Description("Ignore trigger colliders during raycasts (default true).")] bool ignoreTriggers = true,
        [Description("Maximum gap clusters to report (1-500, default 50).")] int maxGaps = 50,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        maxGaps = Math.Clamp(maxGaps, 1, 500);

        var request = new Dictionary<string, object>
        {
            ["rootInstanceId"] = rootInstanceId,
            ["boundsMinX"] = boundsMinX,
            ["boundsMinY"] = boundsMinY,
            ["boundsMinZ"] = boundsMinZ,
            ["boundsMaxX"] = boundsMaxX,
            ["boundsMaxY"] = boundsMaxY,
            ["boundsMaxZ"] = boundsMaxZ,
            ["direction"] = direction ?? "down",
            ["spacing"] = spacing,
            ["maxRayDistance"] = maxRayDistance,
            ["originOffset"] = originOffset,
            ["surfaceNameContains"] = surfaceNameContains ?? "",
            ["surfaceTag"] = surfaceTag ?? "",
            ["surfaceLayer"] = surfaceLayer ?? "",
            ["surfaceLayerMask"] = surfaceLayerMask,
            ["ignoreTriggers"] = ignoreTriggers ? 1 : 0,
            ["maxGaps"] = maxGaps
        };

        return await client.RaycastCoverageCheckAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_audit_overlaps")]
    [Description(@"Detect bounds/collider intersections between GameObjects and rank by severity.

Broad phase uses Bounds.Intersects (no colliders needed). Narrow phase uses Physics.ComputePenetration
where colliders exist, giving accurate penetration depth and direction.

Severity: critical (depth >= 0.5m), warning (depth >= 0.1m), info (smaller overlaps).

Skips parent-child pairs (compound object internals). Optionally skips sibling groups
(children of the same parent) to reduce false positives on intentionally nested geometry.

Returns: overlap pairs sorted by penetration depth with instanceIds, names, paths,
penetration direction, overlap volume estimate, and detection method (physics or bounds).

Example — Audit all overlaps in scene:
  unity_audit_overlaps()

Example — Audit children of a root object:
  unity_audit_overlaps(rootInstanceId=12345)

Example — Audit specific objects with tight threshold:
  unity_audit_overlaps(instanceIds=[111, 222, 333], minPenetration=0.001)")]
    public static async Task<string> AuditOverlaps(
        UnityClient client,
        [Description("Scope to children of this root GameObject instance ID. 0 = entire scene.")] int rootInstanceId = 0,
        [Description("Specific instance IDs to check. Overrides rootInstanceId.")] int[]? instanceIds = null,
        [Description("Include children of specified objects (default true).")] bool includeChildren = true,
        [Description("Maximum overlap pairs to return (1-500, default 50).")] int maxPairs = 50,
        [Description("Minimum penetration depth in meters to report (default 0.01).")] float minPenetration = 0.01f,
        [Description("Skip pairs where both objects share the same parent (default true).")] bool ignoreSiblingGroups = true,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        maxPairs = Math.Clamp(maxPairs, 1, 500);

        var request = new Dictionary<string, object?>
        {
            ["includeChildren"] = includeChildren ? 1 : 0,
            ["maxPairs"] = maxPairs,
            ["minPenetration"] = minPenetration,
            ["ignoreSiblingGroups"] = ignoreSiblingGroups ? 1 : 0
        };

        if (rootInstanceId != 0) request["rootInstanceId"] = rootInstanceId;
        if (instanceIds != null && instanceIds.Length > 0) request["instanceIds"] = instanceIds;

        return await client.AuditOverlapsAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_identify_objects_at_points")]
    [Description(@"Identify which GameObjects are at given normalized screen positions.

Uses 3-tier picking: HandleUtility.PickGameObject (scene view GPU pick) → Physics.Raycast →
Renderer.bounds.IntersectRay (fallback for objects without colliders).

Points use normalized [0,1] coordinates: (0,0) = top-left, (1,1) = bottom-right.

Typical use: feed hotspot coordinates from unity_compare_images or unity_capture_and_compare
into this tool to identify which scene objects correspond to visual differences.

Example — Identify objects at hotspot locations:
  unity_identify_objects_at_points(points=[{""x"":0.3, ""y"":0.5}, {""x"":0.7, ""y"":0.2}])

Example — Check center of game view:
  unity_identify_objects_at_points(points=[{""x"":0.5, ""y"":0.5}], source=""game"")")]
    public static async Task<string> IdentifyObjectsAtPoints(
        UnityClient client,
        [Description("Array of normalized screen coordinates, each with x and y in [0,1].")] object[] points,
        [Description("Camera source: 'scene' (Scene View) or 'game' (Camera.main). Default: scene.")] string source = "scene",
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        if (points == null || points.Length == 0)
            return """{"success":false,"error":"points array is required"}""";

        var request = new Dictionary<string, object?>
        {
            ["points"] = points,
            ["source"] = source ?? "scene"
        };

        return await client.IdentifyObjectsAtPointsAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_resolve_overlaps")]
    [Description(@"Non-destructively nudge overlapping GameObjects apart. All moves are undoable (Ctrl+Z).

Runs the same overlap detection as unity_audit_overlaps, then iteratively pushes pairs apart
using Physics.ComputePenetration vectors (or AABB center-to-center fallback).

Objects in preserveInstanceIds are anchors — only the other object in each pair moves.
Optional terrain snapping keeps objects grounded after nudging.

All transforms are grouped in a single Undo operation named 'Resolve Overlaps'.

Example — Resolve all overlaps under a root:
  unity_resolve_overlaps(rootInstanceId=12345)

Example — Anchor buildings, move trees:
  unity_resolve_overlaps(instanceIds=[111,222,333], preserveInstanceIds=[111])

Example — Resolve with terrain snap and extra gap:
  unity_resolve_overlaps(rootInstanceId=12345, minimumGap=0.05, keepOnTerrain=true)")]
    public static async Task<string> ResolveOverlaps(
        UnityClient client,
        [Description("Scope to children of this root GameObject instance ID. 0 = entire scene.")] int rootInstanceId = 0,
        [Description("Specific instance IDs to resolve. Overrides rootInstanceId.")] int[]? instanceIds = null,
        [Description("Include children of specified objects (default true).")] bool includeChildren = true,
        [Description("Extra gap in meters to maintain beyond zero-overlap (default 0.0).")] float minimumGap = 0.0f,
        [Description("Raycast down after nudging to snap Y to terrain/ground (default false).")] bool keepOnTerrain = false,
        [Description("Instance IDs of anchor objects that must not move.")] int[]? preserveInstanceIds = null,
        [Description("Maximum solver iterations (1-64, default 10).")] int maxIterations = 10,
        [Description("Minimum nudge step per adjustment in meters (default 0.05).")] float nudgeStep = 0.05f,
        [Description("Skip sibling pairs — objects sharing the same parent (default true).")] bool ignoreSiblingGroups = true,
        [Description("Save scene after successful adjustments (default false).")] bool autoSaveScene = false,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        maxIterations = Math.Clamp(maxIterations, 1, 64);

        var request = new Dictionary<string, object?>
        {
            ["includeChildren"] = includeChildren ? 1 : 0,
            ["minimumGap"] = minimumGap,
            ["keepOnTerrain"] = keepOnTerrain ? 1 : 0,
            ["maxIterations"] = maxIterations,
            ["nudgeStep"] = nudgeStep,
            ["ignoreSiblingGroups"] = ignoreSiblingGroups ? 1 : 0,
            ["autoSaveScene"] = autoSaveScene ? 1 : 0
        };

        if (rootInstanceId != 0) request["rootInstanceId"] = rootInstanceId;
        if (instanceIds != null && instanceIds.Length > 0) request["instanceIds"] = instanceIds;
        if (preserveInstanceIds != null && preserveInstanceIds.Length > 0) request["preserveInstanceIds"] = preserveInstanceIds;

        return await client.ResolveOverlapsAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_audit_grounding")]
    [Description(@"Audit GameObjects for floating, sinking, or inadequate ground support.

For each object: computes aggregate bounds, raycasts downward to find ground/terrain, and reports:
- Gap distance (floating above ground)
- Sink depth (object bottom below ground surface)
- Footprint support fraction (what % of XZ footprint has ground below it)
- Center of mass support (is the object center above support?)
- Suggested snap Y position to fix grounding

Severity: critical (large gap/sink or no ground found), warning (moderate gap/sink or poor support), ok (grounded).

Only reports objects with issues by default. Use for post-build validation alongside unity_audit_overlaps.

Example — Audit all objects in scene:
  unity_audit_grounding()

Example — Audit children of village root with tight threshold:
  unity_audit_grounding(rootInstanceId=12345, maxGap=0.05)

Example — Check specific objects:
  unity_audit_grounding(instanceIds=[111, 222, 333])")]
    public static async Task<string> AuditGrounding(
        UnityClient client,
        [Description("Scope to children of this root GameObject instance ID. 0 = entire scene.")] int rootInstanceId = 0,
        [Description("Specific instance IDs to check. Overrides rootInstanceId.")] int[]? instanceIds = null,
        [Description("Include children of specified objects (default true).")] bool includeChildren = true,
        [Description("Maximum gap in meters before flagging as floating (default 0.1).")] float maxGap = 0.1f,
        [Description("Maximum raycast distance downward to search for ground (default 50).")] float maxRayDistance = 50f,
        [Description("Grid spacing for footprint support sampling in meters (default 0.25).")] float footprintSpacing = 0.25f,
        [Description("Maximum objects to audit (1-1000, default 200).")] int maxObjects = 200,
        [Description("Layer mask for support surfaces (-1 = all layers).")] int supportLayerMask = -1,
        [Description("Depth below ground before flagging as sunk (default 0.05m).")] float sinkThreshold = 0.05f,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        maxObjects = Math.Clamp(maxObjects, 1, 1000);

        var request = new Dictionary<string, object?>
        {
            ["includeChildren"] = includeChildren ? 1 : 0,
            ["maxGap"] = maxGap,
            ["maxRayDistance"] = maxRayDistance,
            ["footprintSpacing"] = footprintSpacing,
            ["maxObjects"] = maxObjects,
            ["supportLayerMask"] = supportLayerMask,
            ["sinkThreshold"] = sinkThreshold
        };

        if (rootInstanceId != 0) request["rootInstanceId"] = rootInstanceId;
        if (instanceIds != null && instanceIds.Length > 0) request["instanceIds"] = instanceIds;

        return await client.AuditGroundingAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_snap_to_ground")]
    [Description(@"Snap GameObjects to the ground/terrain below them. All moves are undoable (Ctrl+Z).

For each object: computes aggregate bounds, raycasts downward to find the nearest ground surface,
then moves the object so its bounds bottom aligns with the ground hit point.

Handles both floating objects (snaps down) and sunk objects (pulls up).
Uses Physics.RaycastAll to skip self-hits on compound shapes.
Falls back to Terrain.activeTerrain.SampleHeight when no collider-based ground is found.

Pair with unity_audit_grounding: audit first to identify issues, then snap to fix them.

Example — Snap all objects under a root to ground:
  unity_snap_to_ground(rootInstanceId=12345)

Example — Snap specific floating objects:
  unity_snap_to_ground(instanceIds=[111, 222, 333])

Example — Snap with limited drop distance:
  unity_snap_to_ground(rootInstanceId=12345, maxDrop=5.0)")]
    public static async Task<string> SnapToGround(
        UnityClient client,
        [Description("Scope to children of this root GameObject instance ID. 0 = entire scene.")] int rootInstanceId = 0,
        [Description("Specific instance IDs to snap. Overrides rootInstanceId.")] int[]? instanceIds = null,
        [Description("Include children of specified objects (default true).")] bool includeChildren = true,
        [Description("Maximum drop distance in meters to search for ground (default 50).")] float maxDrop = 50f,
        [Description("Keep original X/Z position, only adjust Y (default true).")] bool preserveXZ = true,
        [Description("Layer mask for support surfaces (-1 = all layers).")] int supportLayerMask = -1,
        [Description("Save scene after snapping (default false).")] bool autoSaveScene = false,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        var request = new Dictionary<string, object?>
        {
            ["includeChildren"] = includeChildren ? 1 : 0,
            ["maxDrop"] = maxDrop,
            ["preserveXZ"] = preserveXZ ? 1 : 0,
            ["supportLayerMask"] = supportLayerMask,
            ["autoSaveScene"] = autoSaveScene ? 1 : 0
        };

        if (rootInstanceId != 0) request["rootInstanceId"] = rootInstanceId;
        if (instanceIds != null && instanceIds.Length > 0) request["instanceIds"] = instanceIds;

        return await client.SnapToGroundAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }
}
