using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class AlignmentTools
{
    [McpServerTool(Name = "unity_snap_objects")]
    [Description(@"Snap one GameObject to align against another using world-space AABB bounds.
Moves the source object so its bounding box aligns with the target's bounding box edge, plus an optional gap.
Uses Undo.RecordObject on the source transform so the move can be reverted with Ctrl+Z.

Alignment modes:
- right-of: source.min.x = target.max.x + gap
- left-of: source.max.x = target.min.x - gap
- above: source.min.y = target.max.y + gap
- below: source.max.y = target.min.y - gap
- in-front-of: source.min.z = target.max.z + gap
- behind: source.max.z = target.min.z - gap
- on-top-of: same as above (source sits on top of target)

Note: Uses world-space axis-aligned bounding boxes (AABB). For rotated objects the alignment is approximate.
Bounds are derived from Renderer → Collider → unit bounds fallback chain.

Returns old position, new position, and bounds sizes for both objects.")]
    public static async Task<string> SnapObjects(
        UnityClient client,
        [Description("Instance ID of the source object (the one that will be moved).")] int sourceId,
        [Description("Instance ID of the target object (stays fixed, source aligns to it).")] int targetId,
        [Description("Alignment mode: right-of, left-of, above, below, in-front-of, behind, on-top-of.")] string alignment,
        [Description("Gap between the two bounding boxes in world units (default 0).")] float gap = 0f)
    {
        return await client.SnapObjectsAsync(new { sourceId, targetId, alignment, gap });
    }
}
