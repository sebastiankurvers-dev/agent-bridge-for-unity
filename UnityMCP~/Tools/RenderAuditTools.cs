using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class RenderAuditTools
{
    [McpServerTool(Name = "unity_get_hierarchy_renderers")]
    [Description(@"List all renderers on a GameObject and its children with material/emission summary.
Returns per-renderer: gameObjectName, instanceId, active, rendererType, materialName, shaderName,
emissionEnabled (bool), emissionColor (raw HDR r/g/b/a), baseColor (raw HDR r/g/b/a).

Use this to diagnose material assignment issues on complex hierarchies (characters with multiple mesh parts,
prefab variants with accessories). Identifies which child renderers have which materials applied.

Example - Inspect player character renderers:
  unity_get_hierarchy_renderers(instanceId=12345)

Example - Include inactive child meshes:
  unity_get_hierarchy_renderers(instanceId=12345, includeInactive=true)")]
    public static async Task<string> GetHierarchyRenderers(
        UnityClient client,
        [Description("Instance ID of the root GameObject to inspect.")] int instanceId,
        [Description("Include inactive child GameObjects (default false).")] bool includeInactive = false,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        var request = new Dictionary<string, object>
        {
            ["instanceId"] = instanceId,
            ["includeInactive"] = includeInactive ? 1 : 0
        };

        return await client.GetHierarchyRenderersAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_audit_renderers")]
    [Description(@"Batch-audit renderers for rendering health issues.
Detects: null/missing materials, invalid/error shaders, Built-in shaders used in URP, camera layer culling,
MeshRenderer without MeshFilter, SkinnedMeshRenderer without mesh.

Returns a summary with counts (healthy/unhealthy) and a list of per-object issues.
Use this as a fast pre-flight or post-mutation check to catch rendering problems (black objects, invisible meshes, magenta materials).

Scope to a specific subtree with rootInstanceId, or audit the entire scene (default).

Example - Audit all renderers:
  unity_audit_renderers()

Example - Audit only player subtree:
  unity_audit_renderers(rootInstanceId=12345)

Example - Audit only player-related renderers by name:
  unity_audit_renderers(nameContains=""Player"")

Example - Audit buildings on a specific layer:
  unity_audit_renderers(layer=""Environment"")")]
    public static async Task<string> AuditRenderers(
        UnityClient client,
        [Description("Filter renderers by GameObject name substring (case-insensitive). Empty = all.")] string nameContains = "",
        [Description("Filter by tag name (e.g., 'Player'). Empty = all.")] string tag = "",
        [Description("Filter by layer name (e.g., 'Environment'). Empty = all.")] string layer = "",
        [Description("Include inactive GameObjects in the audit (default false).")] bool includeInactive = false,
        [Description("Check if renderer layers are in the main camera's culling mask (default true).")] bool checkCameraCulling = true,
        [Description("Maximum objects to audit (10-5000, default 500).")] int maxObjects = 500,
        [Description("Scope audit to a specific GameObject subtree by instance ID. 0 = entire scene (default).")] int rootInstanceId = 0,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        maxObjects = Math.Clamp(maxObjects, 10, 5000);

        var request = new Dictionary<string, object>
        {
            ["nameContains"] = nameContains ?? "",
            ["tag"] = tag ?? "",
            ["layer"] = layer ?? "",
            ["includeInactive"] = includeInactive ? 1 : 0,
            ["checkCameraCulling"] = checkCameraCulling ? 1 : 0,
            ["maxObjects"] = maxObjects,
            ["rootInstanceId"] = rootInstanceId
        };

        return await client.AuditRenderersAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_get_mesh_info")]
    [Description(@"Get mesh topology information for a GameObject: vertex count, triangle count, submesh structure,
and submesh-to-material correlation. Essential for understanding why materials appear swapped or
which submesh corresponds to which material slot.

Returns: meshName, vertexCount, triangleCount, subMeshCount, submeshes[] (index, firstVertex, vertexCount,
indexStart, indexCount, topology), materialCorrelation[] (submeshIndex, materialName, shaderName),
localBounds, hasNormals, hasUVs, hasColors, hasTangents, blendShapeCount.

Example - Inspect hex tunnel mesh:
  unity_get_mesh_info(name=""HexTunnel_0"")

Example - By instance ID:
  unity_get_mesh_info(instanceId=12345)")]
    public static async Task<string> GetMeshInfo(
        UnityClient client,
        [Description("Instance ID of the GameObject with a mesh. Takes priority over name.")] int instanceId = 0,
        [Description("GameObject name fallback (uses GameObject.Find).")] string name = "",
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        var request = new Dictionary<string, object>
        {
            ["instanceId"] = instanceId,
            ["name"] = name ?? ""
        };

        return await client.GetMeshInfoAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_audit_scene_lighting")]
    [Description(@"Audit scene lighting health and adequacy.
Analyzes all lights, ambient settings, post-processing exposure/bloom, and estimates scene luminance.
Detects: no lights, very low ambient, extreme post-exposure, near-zero intensity lights.

Returns a summary with light counts, luminance estimates, issues, and optional recommendations.
Use this when objects appear too dark, as a black silhouette, or when environment is not visible.

Example: unity_audit_scene_lighting()
Example with recommendations: unity_audit_scene_lighting(includeRecommendations=true)")]
    public static async Task<string> AuditSceneLighting(
        UnityClient client,
        [Description("Include actionable recommendations in the response (default true).")] bool includeRecommendations = true,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        var request = new Dictionary<string, object>
        {
            ["includeRecommendations"] = includeRecommendations ? 1 : 0
        };

        return await client.AuditSceneLightingAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }
}
