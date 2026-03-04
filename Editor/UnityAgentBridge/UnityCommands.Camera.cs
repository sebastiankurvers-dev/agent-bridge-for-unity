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
        #region Scene View Camera Methods

        [BridgeRoute("GET", "/sceneview/camera", Category = "camera", Description = "Get scene view camera state", ReadOnly = true)]
        public static string GetSceneViewCamera()
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                return JsonUtility.ToJson(new SceneViewCameraResponse { success = false, error = "No active scene view" });
            }

            var cam = sceneView.camera;
            return JsonUtility.ToJson(new SceneViewCameraResponse
            {
                success = true,
                pivot = new float[] { sceneView.pivot.x, sceneView.pivot.y, sceneView.pivot.z },
                rotation = new float[] { sceneView.rotation.eulerAngles.x, sceneView.rotation.eulerAngles.y, sceneView.rotation.eulerAngles.z },
                size = sceneView.size,
                orthographic = sceneView.orthographic,
                cameraPosition = cam != null ? new float[] { cam.transform.position.x, cam.transform.position.y, cam.transform.position.z } : null,
                cameraDistance = sceneView.cameraDistance
            });
        }

        [BridgeRoute("POST", "/sceneview/camera", Category = "camera", Description = "Set scene view camera")]
        public static string SetSceneViewCamera(string jsonData)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                return JsonError("No active scene view");
            }

            var request = JsonUtility.FromJson<SetSceneViewCameraRequest>(jsonData);

            if (request.pivot != null && request.pivot.Length >= 3)
            {
                sceneView.pivot = new Vector3(request.pivot[0], request.pivot[1], request.pivot[2]);
            }

            if (request.rotation != null && request.rotation.Length >= 3)
            {
                sceneView.rotation = Quaternion.Euler(request.rotation[0], request.rotation[1], request.rotation[2]);
            }

            if (request.size >= 0f)
            {
                sceneView.size = request.size;
            }

            if (request.orthographic >= 0)
            {
                sceneView.orthographic = request.orthographic == 1;
            }

            sceneView.Repaint();

            return JsonSuccess();
        }

        [BridgeRoute("POST", "/sceneview/frame", Category = "camera", Description = "Frame object in scene view")]
        public static string FrameObject(string jsonData)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                return JsonError("No active scene view");
            }

            var request = JsonUtility.FromJson<FrameObjectRequest>(jsonData);
            var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            Selection.activeGameObject = go;
            sceneView.FrameSelected();

            return JsonResult(new Dictionary<string, object> { { "success", true }, { "framed", go.name } });
        }

        [BridgeRoute("POST", "/sceneview/lookat", Category = "camera", Description = "Look at point")]
        public static string LookAtPoint(string jsonData)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                return JsonError("No active scene view");
            }

            var request = JsonUtility.FromJson<LookAtPointRequest>(jsonData);

            if (request.point == null || request.point.Length < 3)
            {
                return JsonError("Point [x,y,z] is required");
            }

            var point = new Vector3(request.point[0], request.point[1], request.point[2]);
            var direction = Quaternion.identity;
            if (request.direction != null && request.direction.Length >= 3)
            {
                direction = Quaternion.Euler(request.direction[0], request.direction[1], request.direction[2]);
            }

            var size = request.size >= 0f ? request.size : sceneView.size;

            sceneView.LookAt(point, direction, size);
            sceneView.Repaint();

            return JsonSuccess();
        }

        [BridgeRoute("POST", "/sceneview/orbit", Category = "camera", Description = "Orbit camera by yaw/pitch")]
        public static string OrbitCamera(string jsonData)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return JsonError("No active scene view");

            var request = JsonUtility.FromJson<OrbitCameraRequest>(jsonData);

            // Optional: orbit around target object
            if (request.targetInstanceId > 0)
            {
                var target = EditorUtility.EntityIdToObject(request.targetInstanceId) as GameObject;
                if (target != null)
                    sceneView.pivot = target.transform.position;
                else
                    return JsonError($"GameObject not found: {request.targetInstanceId}");
            }

            var currentRot = sceneView.rotation;

            // Yaw: rotate around world Y-axis (keeps horizon level)
            var yawQ = Quaternion.AngleAxis(request.yaw, Vector3.up);

            // Pitch: rotate around camera's local right axis (avoids gimbal lock)
            var rightAxis = currentRot * Vector3.right;
            var pitchQ = Quaternion.AngleAxis(-request.pitch, rightAxis);

            var newRot = pitchQ * yawQ * currentRot;

            // Guard: prevent camera from going fully vertical (dot with up > 0.99)
            var newForward = newRot * Vector3.forward;
            if (Mathf.Abs(Vector3.Dot(newForward, Vector3.up)) > 0.99f)
            {
                // Too close to poles — apply only yaw
                newRot = yawQ * currentRot;
            }

            sceneView.rotation = newRot;
            sceneView.Repaint();

            return request.brief == 1 ? JsonSuccess() : GetSceneViewCamera();
        }

        [BridgeRoute("POST", "/sceneview/pan", Category = "camera", Description = "Pan camera in local plane")]
        public static string PanCamera(string jsonData)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return JsonError("No active scene view");

            var request = JsonUtility.FromJson<PanCameraRequest>(jsonData);

            // Optional: snap pivot to target first
            if (request.targetInstanceId > 0)
            {
                var target = EditorUtility.EntityIdToObject(request.targetInstanceId) as GameObject;
                if (target != null)
                    sceneView.pivot = target.transform.position;
                else
                    return JsonError($"GameObject not found: {request.targetInstanceId}");
            }

            // Zoom-scaled: larger view.size = larger pan per unit
            float scale = sceneView.size * 0.1f;
            var right = sceneView.rotation * Vector3.right;
            var up = sceneView.rotation * Vector3.up;
            sceneView.pivot += right * request.deltaRight * scale + up * request.deltaUp * scale;
            sceneView.Repaint();

            return request.brief == 1 ? JsonSuccess() : GetSceneViewCamera();
        }

        [BridgeRoute("POST", "/sceneview/zoom", Category = "camera", Description = "Zoom camera")]
        public static string ZoomCamera(string jsonData)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return JsonError("No active scene view");

            var request = JsonUtility.FromJson<ZoomCameraRequest>(jsonData);
            float factor = Mathf.Clamp(request.factor, 0.01f, 100f);
            sceneView.size = Mathf.Clamp(sceneView.size * factor, 0.01f, 2000f);
            sceneView.Repaint();

            return request.brief == 1 ? JsonSuccess() : GetSceneViewCamera();
        }

        [BridgeRoute("POST", "/sceneview/pick", Category = "camera", Description = "Raycast pick at screen coords")]
        public static string PickAtScreen(string jsonData)
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return JsonError("No active scene view");

            var request = JsonUtility.FromJson<PickAtScreenRequest>(jsonData);
            var viewType = string.IsNullOrEmpty(request.view) ? "scene" : request.view;
            bool isBrief = request.brief == 1;

            Camera cam;
            if (viewType == "game")
            {
                cam = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>();
                if (cam == null) return JsonError("No camera found. Create a Camera in the scene.");
            }
            else
            {
                cam = sceneView.camera;
                if (cam == null) return JsonError("No scene view camera");
            }

            // --- Primary: HandleUtility.PickGameObject (scene view only) ---
            GameObject hitObject = null;
            Vector3 hitPoint = Vector3.zero;
            float hitDistance = 0f;

            if (viewType == "scene")
            {
                try
                {
                    var pixelRect = cam.pixelRect;
                    var guiPoint = new Vector2(
                        pixelRect.x + request.x * pixelRect.width,
                        pixelRect.y + pixelRect.height - request.y * pixelRect.height
                    );

                    hitObject = HandleUtility.PickGameObject(guiPoint, false);
                    if (hitObject != null)
                    {
                        var ray = cam.ScreenPointToRay(new Vector3(
                            request.x * cam.pixelWidth,
                            (1f - request.y) * cam.pixelHeight, 0f));

                        if (hitObject.TryGetComponent<Collider>(out var col))
                        {
                            if (col.Raycast(ray, out RaycastHit colHit, Mathf.Infinity))
                            {
                                hitPoint = colHit.point;
                                hitDistance = colHit.distance;
                            }
                            else
                            {
                                hitPoint = hitObject.transform.position;
                                hitDistance = Vector3.Distance(cam.transform.position, hitPoint);
                            }
                        }
                        else if (hitObject.TryGetComponent<Renderer>(out var rend))
                        {
                            rend.bounds.IntersectRay(ray, out float dist);
                            hitPoint = ray.GetPoint(Mathf.Max(dist, 0f));
                            hitDistance = Mathf.Max(dist, 0f);
                        }
                        else
                        {
                            hitPoint = hitObject.transform.position;
                            hitDistance = Vector3.Distance(cam.transform.position, hitPoint);
                        }
                    }
                }
                catch
                {
                    hitObject = null; // Fall through to fallback
                }
            }

            // --- Fallback: Physics.Raycast + renderer bounds ---
            if (hitObject == null)
            {
                var screenPoint = new Vector3(
                    request.x * cam.pixelWidth,
                    (1f - request.y) * cam.pixelHeight, 0f);
                var ray = cam.ScreenPointToRay(screenPoint);

                if (Physics.Raycast(ray, out RaycastHit physHit, Mathf.Infinity))
                {
                    hitObject = physHit.collider.gameObject;
                    hitPoint = physHit.point;
                    hitDistance = physHit.distance;
                }
                else
                {
                    // Renderer bounds intersection (objects without colliders)
                    float closestDist = float.MaxValue;
                    var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
                    foreach (var renderer in renderers)
                    {
                        if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                            continue;
                        if (renderer.bounds.IntersectRay(ray, out float dist) && dist > 0f && dist < closestDist)
                        {
                            closestDist = dist;
                            hitObject = renderer.gameObject;
                            hitPoint = ray.GetPoint(dist);
                            hitDistance = dist;
                        }
                    }
                }
            }

            // --- No hit ---
            if (hitObject == null)
            {
                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "hit", false }
                });
            }

            // --- Select in editor ---
            Selection.activeGameObject = hitObject;
            sceneView.Repaint();

            // --- Build response ---
            if (isBrief)
            {
                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "hit", true },
                    { "instanceId", hitObject.GetInstanceID() },
                    { "name", hitObject.name }
                });
            }

            // Full response
            var pathParts = new List<string>();
            var current = hitObject.transform;
            while (current != null)
            {
                pathParts.Insert(0, current.name);
                current = current.parent;
            }

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "hit", true },
                { "instanceId", hitObject.GetInstanceID() },
                { "name", hitObject.name },
                { "path", string.Join("/", pathParts) },
                { "tag", hitObject.tag },
                { "layer", LayerMask.LayerToName(hitObject.layer) },
                { "worldPosition", new float[] { hitPoint.x, hitPoint.y, hitPoint.z } },
                { "distance", hitDistance },
                { "hasCollider", hitObject.GetComponent<Collider>() != null },
                { "isActive", hitObject.activeInHierarchy },
                { "components", hitObject.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name).ToArray() }
            });
        }

        #endregion
    }
}
