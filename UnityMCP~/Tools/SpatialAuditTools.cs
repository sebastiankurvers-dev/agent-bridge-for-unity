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
}
