using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class PhysicsTools
{
    [McpServerTool(Name = "unity_configure_rigidbody")]
    [Description(@"Configure a Rigidbody component on a GameObject. Adds a Rigidbody if one doesn't exist.
Only specified properties are changed.

Interpolation modes: None, Interpolate, Extrapolate
Collision detection modes: Discrete, Continuous, ContinuousDynamic, ContinuousSpeculative
Constraints: FreezePositionX, FreezePositionY, FreezePositionZ, FreezeRotationX, FreezeRotationY, FreezeRotationZ, FreezePosition, FreezeRotation, FreezeAll
  (combine with commas: ""FreezeRotationX,FreezeRotationZ"")

Example - Player rigidbody:
  instanceId: 12345, mass: 2, useGravity: true, constraints: ""FreezeRotationX,FreezeRotationZ""")]
    public static async Task<string> ConfigureRigidbody(
        UnityClient client,
        [Description("Instance ID of the GameObject.")] int instanceId,
        [Description("Mass of the rigidbody.")] float? mass = null,
        [Description("Linear drag.")] float? drag = null,
        [Description("Angular drag.")] float? angularDrag = null,
        [Description("Whether gravity affects this rigidbody.")] bool? useGravity = null,
        [Description("Whether this rigidbody is kinematic (not affected by physics forces).")] bool? isKinematic = null,
        [Description("Interpolation mode: None, Interpolate, or Extrapolate.")] string? interpolation = null,
        [Description("Collision detection mode: Discrete, Continuous, ContinuousDynamic, or ContinuousSpeculative.")] string? collisionDetectionMode = null,
        [Description("Comma-separated constraints: FreezePositionX, FreezeRotationY, etc.")] string? constraints = null)
    {
        var data = new Dictionary<string, object?>();
        data["instanceId"] = instanceId;
        if (mass.HasValue) data["mass"] = mass.Value;
        if (drag.HasValue) data["drag"] = drag.Value;
        if (angularDrag.HasValue) data["angularDrag"] = angularDrag.Value;
        if (useGravity.HasValue) data["useGravity"] = useGravity.Value ? 1 : 0;
        if (isKinematic.HasValue) data["isKinematic"] = isKinematic.Value ? 1 : 0;
        if (interpolation != null) data["interpolation"] = interpolation;
        if (collisionDetectionMode != null) data["collisionDetectionMode"] = collisionDetectionMode;
        if (constraints != null) data["constraints"] = constraints;
        return await client.ConfigureRigidbodyAsync(data);
    }

    [McpServerTool(Name = "unity_configure_collider")]
    [Description(@"Configure a collider on a GameObject. Can add a new collider or modify an existing one.
Only specified properties are changed.

Supported collider types: Box, Sphere, Capsule, Mesh
Friction/bounce combine modes: Average, Minimum, Maximum, Multiply

Example - Box trigger collider:
  instanceId: 12345, colliderType: ""Box"", isTrigger: true, size: [2, 1, 2]

Example - Bouncy sphere collider:
  instanceId: 12345, colliderType: ""Sphere"", radius: 0.5, physicMaterial: { bounciness: 0.8, dynamicFriction: 0.2 }")]
    public static async Task<string> ConfigureCollider(
        UnityClient client,
        [Description("Instance ID of the GameObject.")] int instanceId,
        [Description("Collider type to add/configure: Box, Sphere, Capsule, or Mesh.")] string? colliderType = null,
        [Description("Whether this collider is a trigger.")] bool? isTrigger = null,
        [Description("Collider center offset as [x, y, z].")] float[]? center = null,
        [Description("Box collider size as [x, y, z].")] float[]? size = null,
        [Description("Sphere/Capsule collider radius.")] float? radius = null,
        [Description("Capsule collider height.")] float? height = null,
        [Description("Capsule direction axis: 0=X, 1=Y, 2=Z.")] int? direction = null,
        [Description("PhysicMaterial JSON with: dynamicFriction, staticFriction, bounciness, frictionCombine, bounceCombine.")] string? physicMaterialJson = null)
    {
        var data = new Dictionary<string, object?>();
        data["instanceId"] = instanceId;
        if (colliderType != null) data["colliderType"] = colliderType;
        if (isTrigger.HasValue) data["isTrigger"] = isTrigger.Value ? 1 : 0;
        if (center != null) data["center"] = center;
        if (size != null) data["size"] = size;
        if (radius.HasValue) data["radius"] = radius.Value;
        if (height.HasValue) data["height"] = height.Value;
        if (direction.HasValue) data["direction"] = direction.Value;

        if (!string.IsNullOrEmpty(physicMaterialJson))
        {
            var pm = JsonSerializer.Deserialize<JsonElement>(physicMaterialJson);
            data["physicMaterial"] = pm;
        }

        return await client.ConfigureColliderAsync(data);
    }

    [McpServerTool(Name = "unity_get_physics_settings")]
    [Description(@"Get the current physics settings including gravity, solver iterations, thresholds, and contact offset.
Use this to understand the current physics configuration before making changes.")]
    public static async Task<string> GetPhysicsSettings(UnityClient client)
    {
        return await client.GetPhysicsSettingsAsync();
    }

    [McpServerTool(Name = "unity_set_physics_settings")]
    [Description(@"Set global physics settings. Only specified properties are changed.

Example - Increase gravity for faster falling:
  gravity: [0, -15, 0]

Example - Configure for precise physics:
  gravity: [0, -9.81, 0], defaultSolverIterations: 12, defaultSolverVelocityIterations: 6

Layer collision control:
  layerCollisions: [{""layer1"": 8, ""layer2"": 9, ""ignore"": true}]")]
    public static async Task<string> SetPhysicsSettings(
        UnityClient client,
        [Description("Gravity vector as [x, y, z]. Default is [0, -9.81, 0].")] float[]? gravity = null,
        [Description("Default contact offset for colliders.")] float? defaultContactOffset = null,
        [Description("Bounce threshold. Velocities below this won't bounce.")] float? bounceThreshold = null,
        [Description("Sleep threshold. Objects below this energy will sleep.")] float? sleepThreshold = null,
        [Description("Default solver iterations (higher = more accurate, slower).")] int? defaultSolverIterations = null,
        [Description("Default solver velocity iterations.")] int? defaultSolverVelocityIterations = null,
        [Description("Layer collision overrides as JSON array: [{layer1, layer2, ignore}].")] string? layerCollisionsJson = null)
    {
        var data = new Dictionary<string, object?>();
        if (gravity != null) data["gravity"] = gravity;
        if (defaultContactOffset.HasValue) data["defaultContactOffset"] = defaultContactOffset.Value;
        if (bounceThreshold.HasValue) data["bounceThreshold"] = bounceThreshold.Value;
        if (sleepThreshold.HasValue) data["sleepThreshold"] = sleepThreshold.Value;
        if (defaultSolverIterations.HasValue) data["defaultSolverIterations"] = defaultSolverIterations.Value;
        if (defaultSolverVelocityIterations.HasValue) data["defaultSolverVelocityIterations"] = defaultSolverVelocityIterations.Value;

        if (!string.IsNullOrEmpty(layerCollisionsJson))
        {
            var collisions = JsonSerializer.Deserialize<JsonElement>(layerCollisionsJson);
            data["layerCollisions"] = collisions;
        }

        return await client.SetPhysicsSettingsAsync(data);
    }
}
