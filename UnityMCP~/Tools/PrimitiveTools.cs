using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class PrimitiveTools
{
    [McpServerTool(Name = "unity_spawn_primitive")]
    [Description("Spawn a Unity primitive (Cube, Sphere, Cylinder, Capsule, Plane, Quad) with optional inline color material. "
        + "Ideal for prototyping and scene reconstruction using only built-in shapes — no assets required.\n\n"
        + "IMPORTANT: For 2D games (platformers, top-down, etc.), set mode2D=true — this automatically swaps the 3D collider for a BoxCollider2D. "
        + "Organize objects under parent GameObjects using parentId.")]
    public static async Task<string> SpawnPrimitive(
        UnityClient client,
        [Description("Primitive shape: Cube, Sphere, Cylinder, Capsule, Plane, or Quad.")] string primitiveType,
        [Description("Display name for the GameObject.")] string? name = null,
        [Description("World position [x, y, z].")] float[]? position = null,
        [Description("Euler rotation [x, y, z] in degrees.")] float[]? rotation = null,
        [Description("Local scale [x, y, z].")] float[]? scale = null,
        [Description("RGBA color [r, g, b, a] with values 0-1. Creates an instanced material.")] float[]? color = null,
        [Description("Metallic value 0-1. Only applied when color is set.")] float metallic = -1f,
        [Description("Smoothness value 0-1. Only applied when color is set.")] float smoothness = -1f,
        [Description("Instance ID of a parent GameObject (-1 for none).")] int parentId = -1,
        [Description("Set true for 2D games — replaces the default 3D collider with BoxCollider2D automatically.")] bool mode2D = false)
    {
        var validTypes = new[] { "Cube", "Sphere", "Cylinder", "Capsule", "Plane", "Quad" };
        if (string.IsNullOrWhiteSpace(primitiveType) ||
            !Array.Exists(validTypes, t => t.Equals(primitiveType, StringComparison.OrdinalIgnoreCase)))
        {
            return ToolErrors.ValidationError(
                $"primitiveType must be one of: {string.Join(", ", validTypes)}");
        }

        var request = new Dictionary<string, object?>
        {
            ["primitiveType"] = primitiveType
        };

        if (!string.IsNullOrEmpty(name)) request["name"] = name;
        if (position != null) request["position"] = new { x = position.ElementAtOrDefault(0), y = position.ElementAtOrDefault(1), z = position.ElementAtOrDefault(2) };
        if (rotation != null) request["rotation"] = new { x = rotation.ElementAtOrDefault(0), y = rotation.ElementAtOrDefault(1), z = rotation.ElementAtOrDefault(2) };
        if (scale != null) request["scale"] = new { x = scale.ElementAtOrDefault(0), y = scale.ElementAtOrDefault(1), z = scale.ElementAtOrDefault(2) };
        if (color != null) request["color"] = color;
        if (metallic >= 0f) request["metallic"] = metallic;
        if (smoothness >= 0f) request["smoothness"] = smoothness;
        if (parentId != -1) request["parentId"] = parentId;
        if (mode2D) request["mode2D"] = true;

        return await client.SpawnPrimitiveAsync(request);
    }

    [McpServerTool(Name = "unity_create_raw_mesh")]
    [Description("Create a mesh from raw vertex data — full control over geometry. "
        + "Pass flat arrays of vertices (xyz triples), triangle indices, and optionally normals and UVs. "
        + "Useful for custom terrain patches, architectural details, or any shape not covered by primitives/procedural presets. "
        + "Can optionally save the mesh as a reusable .asset file.\n\n"
        + "Example - Simple triangle:\n"
        + "  vertices: [0,0,0, 1,0,0, 0.5,1,0], triangles: [0,1,2]\n\n"
        + "Example - Quad with UVs:\n"
        + "  vertices: [0,0,0, 1,0,0, 1,1,0, 0,1,0], triangles: [0,1,2, 0,2,3], uvs: [0,0, 1,0, 1,1, 0,1]")]
    public static async Task<string> CreateRawMesh(
        UnityClient client,
        [Description("Flat vertex array [x,y,z, x,y,z, ...] — 3 floats per vertex, minimum 3 vertices.")] float[] vertices,
        [Description("Triangle index array — groups of 3 indices referencing vertices. Winding order determines face direction.")] int[] triangles,
        [Description("Flat normal array [x,y,z, ...] — same length as vertices. Auto-calculated if omitted.")] float[]? normals = null,
        [Description("Flat UV array [u,v, u,v, ...] — 2 floats per vertex. Optional.")] float[]? uvs = null,
        [Description("GameObject name.")] string? name = null,
        [Description("Position as [x,y,z].")] float[]? position = null,
        [Description("Rotation as [x,y,z] euler angles.")] float[]? rotation = null,
        [Description("Scale as [x,y,z].")] float[]? scale = null,
        [Description("Color as [r,g,b] or [r,g,b,a] (0-1 range).")] float[]? color = null,
        [Description("Parent instance ID.")] int parentId = -1,
        [Description("Material metallic (0-1).")] float metallic = -1f,
        [Description("Material smoothness (0-1).")] float smoothness = -1f,
        [Description("Optional asset path to save mesh for reuse (e.g., 'Assets/Meshes/Custom.asset').")] string? saveMeshPath = null)
    {
        if (vertices == null || vertices.Length < 9)
            return ToolErrors.ValidationError("vertices requires at least 3 vertices (9 floats)");
        if (vertices.Length % 3 != 0)
            return ToolErrors.ValidationError("vertices length must be a multiple of 3");
        if (triangles == null || triangles.Length < 3)
            return ToolErrors.ValidationError("triangles requires at least 3 indices");
        if (triangles.Length % 3 != 0)
            return ToolErrors.ValidationError("triangles length must be a multiple of 3");

        var request = new Dictionary<string, object?>
        {
            ["vertices"] = vertices,
            ["triangles"] = triangles
        };

        if (normals != null) request["normals"] = normals;
        if (uvs != null) request["uvs"] = uvs;
        if (!string.IsNullOrEmpty(name)) request["name"] = name;
        if (position != null) request["position"] = position;
        if (rotation != null) request["rotation"] = rotation;
        if (scale != null) request["scale"] = scale;
        if (color != null) request["color"] = color;
        if (parentId != -1) request["parentId"] = parentId;
        if (metallic >= 0f) request["metallic"] = metallic;
        if (smoothness >= 0f) request["smoothness"] = smoothness;
        if (!string.IsNullOrEmpty(saveMeshPath)) request["saveMeshPath"] = saveMeshPath;

        return await client.CreateRawMeshAsync(request);
    }

    [McpServerTool(Name = "unity_create_compound_shape")]
    [Description("Create a multi-part compound shape from a preset — one call instead of manually composing 3-8 primitives. "
        + "Presets: tree (trunk+canopy), lantern (post+housing+light), steps (staircase), fence (posts+rails), "
        + "rock_cluster (natural grouped rocks), simple_building (walls+roof+floor). "
        + "All presets come with proper materials, proportions, and hierarchy. Use 'scale' to resize uniformly.\n\n"
        + "Example: unity_create_compound_shape(preset=\"tree\", position=[5,0,3], canopyShape=\"sphere\", scale=1.5)\n"
        + "Example: unity_create_compound_shape(preset=\"lantern\", position=[2,3,0], chainLength=0.5, lightColor=[1,0.8,0.5])\n"
        + "Example: unity_create_compound_shape(preset=\"steps\", position=[0,0,2], stepCount=5, stepWidth=3)")]
    public static async Task<string> CreateCompoundShape(
        UnityClient client,
        [Description("Shape preset: 'tree', 'lantern', 'steps', 'fence', 'rock_cluster', 'simple_building'.")] string preset,
        [Description("GameObject name.")] string? name = null,
        [Description("Position as [x,y,z].")] float[]? position = null,
        [Description("Rotation as [x,y,z] euler angles.")] float[]? rotation = null,
        [Description("Uniform scale multiplier (default 1).")] float scale = 1f,
        [Description("Parent instance ID.")] int parentId = -1,
        // Tree params
        [Description("Tree: trunk height (default 1.5).")] float trunkHeight = 1.5f,
        [Description("Tree: trunk radius (default 0.15).")] float trunkRadius = 0.15f,
        [Description("Tree: canopy height (default 2).")] float canopyHeight = 2f,
        [Description("Tree: canopy radius (default 1).")] float canopyRadius = 1f,
        [Description("Tree: trunk color [r,g,b] (default brown).")] float[]? trunkColor = null,
        [Description("Tree: canopy color [r,g,b] (default green).")] float[]? canopyColor = null,
        [Description("Tree: canopy shape 'cone' or 'sphere' (default cone).")] string canopyShape = "cone",
        // Lantern params
        [Description("Lantern: post height (default 0.3). Set 0 for flush mount.")] float postHeight = 0.3f,
        [Description("Lantern: housing size (default 0.25).")] float housingSize = 0.25f,
        [Description("Lantern: light intensity (default 1).")] float lightIntensity = 1f,
        [Description("Lantern: light range (default 8).")] float lightRange = 8f,
        [Description("Lantern: light color [r,g,b] (default warm yellow).")] float[]? lightColor = null,
        [Description("Lantern: chain length >0 for hanging lantern (default 0 = ground).")] float chainLength = 0f,
        // Steps params
        [Description("Steps: number of steps (default 4, max 30).")] int stepCount = 4,
        [Description("Steps: width (default 2).")] float stepWidth = 2f,
        [Description("Steps: tread depth (default 0.4).")] float stepDepth = 0.4f,
        [Description("Steps: rise per step (default 0.2).")] float stepRise = 0.2f,
        [Description("Steps: color [r,g,b] (default stone grey).")] float[]? stepColor = null,
        // Fence params
        [Description("Fence: total length (default 5).")] float fenceLength = 5f,
        [Description("Fence: height (default 1).")] float fenceHeight = 1f,
        [Description("Fence: spacing between posts (default 1.5).")] float postSpacing = 1.5f,
        [Description("Fence: color [r,g,b] (default wood brown).")] float[]? fenceColor = null,
        // Rock cluster params
        [Description("Rock cluster: number of rocks (default 5, max 20).")] int rockCount = 5,
        [Description("Rock cluster: spread radius (default 1.5).")] float clusterRadius = 1.5f,
        [Description("Rock cluster: min rock scale (default 0.3).")] float rockMinScale = 0.3f,
        [Description("Rock cluster: max rock scale (default 1).")] float rockMaxScale = 1f,
        [Description("Rock cluster: color [r,g,b] (default grey).")] float[]? rockColor = null,
        [Description("Rock cluster/scatter seed for reproducibility.")] int seed = 0,
        // Building params
        [Description("Building: width (default 4).")] float buildingWidth = 4f,
        [Description("Building: depth (default 3).")] float buildingDepth = 3f,
        [Description("Building: wall height (default 3).")] float buildingHeight = 3f,
        [Description("Building: roof height (default 1.5).")] float roofHeight = 1.5f,
        [Description("Building: roof type 'gable' or 'flat' (default gable).")] string roofType = "gable",
        [Description("Building: wall color [r,g,b].")] float[]? wallColor = null,
        [Description("Building: roof color [r,g,b].")] float[]? roofColor = null)
    {
        var validPresets = new[] { "tree", "lantern", "steps", "fence", "rock_cluster", "simple_building" };
        if (string.IsNullOrWhiteSpace(preset) ||
            !Array.Exists(validPresets, t => t.Equals(preset, StringComparison.OrdinalIgnoreCase)))
        {
            return ToolErrors.ValidationError(
                $"preset must be one of: {string.Join(", ", validPresets)}");
        }

        var request = new Dictionary<string, object?>
        {
            ["preset"] = preset
        };

        if (!string.IsNullOrEmpty(name)) request["name"] = name;
        if (position != null) request["position"] = position;
        if (rotation != null) request["rotation"] = rotation;
        if (scale != 1f) request["scale"] = scale;
        if (parentId != -1) request["parentId"] = parentId;

        // Tree
        if (trunkHeight != 1.5f) request["trunkHeight"] = trunkHeight;
        if (trunkRadius != 0.15f) request["trunkRadius"] = trunkRadius;
        if (canopyHeight != 2f) request["canopyHeight"] = canopyHeight;
        if (canopyRadius != 1f) request["canopyRadius"] = canopyRadius;
        if (trunkColor != null) request["trunkColor"] = trunkColor;
        if (canopyColor != null) request["canopyColor"] = canopyColor;
        if (canopyShape != "cone") request["canopyShape"] = canopyShape;

        // Lantern
        if (postHeight != 0.3f) request["postHeight"] = postHeight;
        if (housingSize != 0.25f) request["housingSize"] = housingSize;
        if (lightIntensity != 1f) request["lightIntensity"] = lightIntensity;
        if (lightRange != 8f) request["lightRange"] = lightRange;
        if (lightColor != null) request["lightColor"] = lightColor;
        if (chainLength != 0f) request["chainLength"] = chainLength;

        // Steps
        if (stepCount != 4) request["stepCount"] = stepCount;
        if (stepWidth != 2f) request["stepWidth"] = stepWidth;
        if (stepDepth != 0.4f) request["stepDepth"] = stepDepth;
        if (stepRise != 0.2f) request["stepRise"] = stepRise;
        if (stepColor != null) request["stepColor"] = stepColor;

        // Fence
        if (fenceLength != 5f) request["fenceLength"] = fenceLength;
        if (fenceHeight != 1f) request["fenceHeight"] = fenceHeight;
        if (postSpacing != 1.5f) request["postSpacing"] = postSpacing;
        if (fenceColor != null) request["fenceColor"] = fenceColor;

        // Rock cluster
        if (rockCount != 5) request["rockCount"] = rockCount;
        if (clusterRadius != 1.5f) request["clusterRadius"] = clusterRadius;
        if (rockMinScale != 0.3f) request["rockMinScale"] = rockMinScale;
        if (rockMaxScale != 1f) request["rockMaxScale"] = rockMaxScale;
        if (rockColor != null) request["rockColor"] = rockColor;
        if (seed != 0) request["seed"] = seed;

        // Building
        if (buildingWidth != 4f) request["buildingWidth"] = buildingWidth;
        if (buildingDepth != 3f) request["buildingDepth"] = buildingDepth;
        if (buildingHeight != 3f) request["buildingHeight"] = buildingHeight;
        if (roofHeight != 1.5f) request["roofHeight"] = roofHeight;
        if (roofType != "gable") request["roofType"] = roofType;
        if (wallColor != null) request["wallColor"] = wallColor;
        if (roofColor != null) request["roofColor"] = roofColor;

        return await client.CreateCompoundShapeAsync(request);
    }

    [McpServerTool(Name = "unity_create_procedural_mesh")]
    [Description("Create a procedural mesh GameObject. Supports: cone, wedge (ramp), arch, torus, prism. "
        + "Goes beyond built-in primitives (Cube/Sphere/Cylinder) for architectural and organic shapes. "
        + "Each type has specific parameters — unset params use sensible defaults.")]
    public static async Task<string> CreateProceduralMesh(
        UnityClient client,
        [Description("Mesh type: 'cone', 'wedge', 'arch', 'torus', 'prism'.")] string meshType,
        [Description("GameObject name.")] string? name = null,
        [Description("Position as [x,y,z].")] float[]? position = null,
        [Description("Rotation as [x,y,z] euler angles.")] float[]? rotation = null,
        [Description("Scale as [x,y,z].")] float[]? scale = null,
        [Description("Color as [r,g,b] or [r,g,b,a] (0-1 range).")] float[]? color = null,
        [Description("Parent instance ID.")] int parentId = -1,
        [Description("Cone/prism base radius.")] float radius = 0.5f,
        [Description("Cone/wedge height.")] float height = 1f,
        [Description("Wedge/prism depth.")] float depth = 1f,
        [Description("Wedge width.")] float width = 1f,
        [Description("Cone/prism number of sides.")] int sides = 16,
        [Description("Arch inner radius.")] float innerRadius = 0.3f,
        [Description("Arch outer radius.")] float outerRadius = 0.5f,
        [Description("Arch arc angle in degrees.")] float arcAngle = 180f,
        [Description("Arch/general segments.")] int segments = 16,
        [Description("Torus tube radius.")] float minorRadius = 0.15f,
        [Description("Torus ring radius.")] float majorRadius = 0.5f,
        [Description("Torus radial segments.")] int radialSegments = 12,
        [Description("Torus tubular segments.")] int tubularSegments = 24,
        [Description("Material metallic (0-1).")] float metallic = -1f,
        [Description("Material smoothness (0-1).")] float smoothness = -1f)
    {
        var validTypes = new[] { "cone", "wedge", "arch", "torus", "prism" };
        if (string.IsNullOrWhiteSpace(meshType) ||
            !Array.Exists(validTypes, t => t.Equals(meshType, StringComparison.OrdinalIgnoreCase)))
        {
            return ToolErrors.ValidationError(
                $"meshType must be one of: {string.Join(", ", validTypes)}");
        }

        var request = new Dictionary<string, object?>
        {
            ["meshType"] = meshType
        };

        if (!string.IsNullOrEmpty(name)) request["name"] = name;
        if (position != null) request["position"] = position;
        if (rotation != null) request["rotation"] = rotation;
        if (scale != null) request["scale"] = scale;
        if (color != null) request["color"] = color;
        if (parentId != -1) request["parentId"] = parentId;
        if (radius != 0.5f) request["radius"] = radius;
        if (height != 1f) request["height"] = height;
        if (depth != 1f) request["depth"] = depth;
        if (width != 1f) request["width"] = width;
        if (sides != 16) request["sides"] = sides;
        if (innerRadius != 0.3f) request["innerRadius"] = innerRadius;
        if (outerRadius != 0.5f) request["outerRadius"] = outerRadius;
        if (arcAngle != 180f) request["arcAngle"] = arcAngle;
        if (segments != 16) request["segments"] = segments;
        if (minorRadius != 0.15f) request["minorRadius"] = minorRadius;
        if (majorRadius != 0.5f) request["majorRadius"] = majorRadius;
        if (radialSegments != 12) request["radialSegments"] = radialSegments;
        if (tubularSegments != 24) request["tubularSegments"] = tubularSegments;
        if (metallic >= 0f) request["metallic"] = metallic;
        if (smoothness >= 0f) request["smoothness"] = smoothness;

        return await client.CreateProceduralMeshAsync(request);
    }
}
