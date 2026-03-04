using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Profiling;
using Unity.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Audio;
using TMPro;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        [BridgeRoute("PUT", "/physics/rigidbody", Category = "physics", Description = "Configure Rigidbody")]
        public static string ConfigureRigidbody(string jsonData)
        {
            var request = JsonUtility.FromJson<ConfigureRigidbodyRequest>(jsonData);

            var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            var rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = Undo.AddComponent<Rigidbody>(go);
            }
            else
            {
                Undo.RecordObject(rb, "Configure Rigidbody");
            }

            if (request.mass >= 0f) rb.mass = request.mass;
            if (request.drag >= 0f) rb.linearDamping = request.drag;
            if (request.angularDrag >= 0f) rb.angularDamping = request.angularDrag;
            if (request.useGravity >= 0) rb.useGravity = request.useGravity == 1;
            if (request.isKinematic >= 0) rb.isKinematic = request.isKinematic == 1;

            if (!string.IsNullOrEmpty(request.interpolation))
            {
                if (Enum.TryParse<RigidbodyInterpolation>(request.interpolation, true, out var interp))
                {
                    rb.interpolation = interp;
                }
            }

            if (!string.IsNullOrEmpty(request.collisionDetectionMode))
            {
                if (Enum.TryParse<CollisionDetectionMode>(request.collisionDetectionMode, true, out var cdm))
                {
                    rb.collisionDetectionMode = cdm;
                }
            }

            if (!string.IsNullOrEmpty(request.constraints))
            {
                RigidbodyConstraints combined = RigidbodyConstraints.None;
                var parts = request.constraints.Split(',');
                foreach (var part in parts)
                {
                    if (Enum.TryParse<RigidbodyConstraints>(part.Trim(), true, out var constraint))
                    {
                        combined |= constraint;
                    }
                }
                rb.constraints = combined;
            }

            EditorUtility.SetDirty(rb);

            return JsonResult(new Dictionary<string, object> { { "success", true }, { "name", go.name } });
        }

        [BridgeRoute("PUT", "/physics/collider", Category = "physics", Description = "Configure Collider")]
        public static string ConfigureCollider(string jsonData)
        {
            var request = JsonUtility.FromJson<ConfigureColliderRequest>(jsonData);

            var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            Collider collider = null;

            // If colliderType specified, get or add that type
            if (!string.IsNullOrEmpty(request.colliderType))
            {
                switch (request.colliderType.ToLowerInvariant())
                {
                    case "box":
                        collider = go.GetComponent<BoxCollider>();
                        if (collider == null) collider = Undo.AddComponent<BoxCollider>(go);
                        break;
                    case "sphere":
                        collider = go.GetComponent<SphereCollider>();
                        if (collider == null) collider = Undo.AddComponent<SphereCollider>(go);
                        break;
                    case "capsule":
                        collider = go.GetComponent<CapsuleCollider>();
                        if (collider == null) collider = Undo.AddComponent<CapsuleCollider>(go);
                        break;
                    case "mesh":
                        collider = go.GetComponent<MeshCollider>();
                        if (collider == null) collider = Undo.AddComponent<MeshCollider>(go);
                        break;
                    default:
                        return JsonError($"Unknown collider type: {request.colliderType}");
                }
            }
            else
            {
                // Get first existing collider
                collider = go.GetComponent<Collider>();
            }

            if (collider == null)
            {
                return JsonError("No collider found and no colliderType specified");
            }

            Undo.RecordObject(collider, "Configure Collider");

            if (request.isTrigger >= 0) collider.isTrigger = request.isTrigger == 1;

            // Apply type-specific properties
            if (collider is BoxCollider box)
            {
                if (request.center != null && request.center.Length >= 3)
                    box.center = new Vector3(request.center[0], request.center[1], request.center[2]);
                if (request.size != null && request.size.Length >= 3)
                    box.size = new Vector3(request.size[0], request.size[1], request.size[2]);
            }
            else if (collider is SphereCollider sphere)
            {
                if (request.center != null && request.center.Length >= 3)
                    sphere.center = new Vector3(request.center[0], request.center[1], request.center[2]);
                if (request.radius >= 0f)
                    sphere.radius = request.radius;
            }
            else if (collider is CapsuleCollider capsule)
            {
                if (request.center != null && request.center.Length >= 3)
                    capsule.center = new Vector3(request.center[0], request.center[1], request.center[2]);
                if (request.radius >= 0f)
                    capsule.radius = request.radius;
                if (request.height >= 0f)
                    capsule.height = request.height;
                if (request.direction >= 0)
                    capsule.direction = request.direction;
            }

            // Apply PhysicMaterial
            if (request.physicMaterial != null)
            {
                var pm = collider.material;
                if (pm == null)
                {
                    pm = new PhysicsMaterial();
                    collider.material = pm;
                }

                Undo.RecordObject(pm, "Configure PhysicMaterial");

                if (request.physicMaterial.dynamicFriction >= 0f) pm.dynamicFriction = request.physicMaterial.dynamicFriction;
                if (request.physicMaterial.staticFriction >= 0f) pm.staticFriction = request.physicMaterial.staticFriction;
                if (request.physicMaterial.bounciness >= 0f) pm.bounciness = request.physicMaterial.bounciness;

                if (!string.IsNullOrEmpty(request.physicMaterial.frictionCombine))
                {
                    if (Enum.TryParse<PhysicsMaterialCombine>(request.physicMaterial.frictionCombine, true, out var fc))
                        pm.frictionCombine = fc;
                }

                if (!string.IsNullOrEmpty(request.physicMaterial.bounceCombine))
                {
                    if (Enum.TryParse<PhysicsMaterialCombine>(request.physicMaterial.bounceCombine, true, out var bc))
                        pm.bounceCombine = bc;
                }
            }

            EditorUtility.SetDirty(collider);

            return JsonResult(new Dictionary<string, object> { { "success", true }, { "name", go.name }, { "colliderType", collider.GetType().Name } });
        }

        [BridgeRoute("GET", "/physics/settings", Category = "physics", Description = "Get physics settings")]
        public static string GetPhysicsSettings()
        {
            return JsonUtility.ToJson(new PhysicsSettingsResponse
            {
                success = true,
                gravity = new float[] { Physics.gravity.x, Physics.gravity.y, Physics.gravity.z },
                defaultContactOffset = Physics.defaultContactOffset,
                bounceThreshold = Physics.bounceThreshold,
                sleepThreshold = Physics.sleepThreshold,
                defaultSolverIterations = Physics.defaultSolverIterations,
                defaultSolverVelocityIterations = Physics.defaultSolverVelocityIterations
            });
        }

        [BridgeRoute("POST", "/physics/settings", Category = "physics", Description = "Set physics settings")]
        public static string SetPhysicsSettings(string jsonData)
        {
            var request = JsonUtility.FromJson<SetPhysicsSettingsRequest>(jsonData);

            // Note: Physics.* global settings have limited Undo support in Unity.
            // We record the group name for tracking, but individual Physics properties
            // may not be fully undoable.
            Undo.SetCurrentGroupName("Agent Bridge: Set Physics Settings");

            if (request.gravity != null && request.gravity.Length >= 3)
            {
                Physics.gravity = new Vector3(request.gravity[0], request.gravity[1], request.gravity[2]);
            }

            if (request.defaultContactOffset >= 0f) Physics.defaultContactOffset = request.defaultContactOffset;
            if (request.bounceThreshold >= 0f) Physics.bounceThreshold = request.bounceThreshold;
            if (request.sleepThreshold >= 0f) Physics.sleepThreshold = request.sleepThreshold;
            if (request.defaultSolverIterations >= 0) Physics.defaultSolverIterations = request.defaultSolverIterations;
            if (request.defaultSolverVelocityIterations >= 0) Physics.defaultSolverVelocityIterations = request.defaultSolverVelocityIterations;

            if (request.layerCollisions != null)
            {
                foreach (var lc in request.layerCollisions)
                {
                    Physics.IgnoreLayerCollision(lc.layer1, lc.layer2, lc.ignore);
                }
            }

            return JsonSuccess();
        }

    }
}
