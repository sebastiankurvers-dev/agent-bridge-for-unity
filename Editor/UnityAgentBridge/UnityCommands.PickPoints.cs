using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        // ==================== Identify Objects at Screen Points ====================

        [BridgeRoute("POST", "/spatial/identify-at-points", Category = "spatial", Description = "Identify GameObjects at normalized screen positions", ReadOnly = true, TimeoutDefault = 10000)]
        public static string IdentifyObjectsAtPoints(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null) return JsonError("Failed to parse identify-at-points request");

                var sw = Stopwatch.StartNew();

                string source = ReadString(request, "source") ?? "scene";
                source = source.Trim().ToLowerInvariant();

                if (!request.TryGetValue("points", out var pointsObj) || !(pointsObj is IList<object> pointsList) || pointsList.Count == 0)
                    return JsonError("points array is required (e.g. [{\"x\":0.5,\"y\":0.5}])");

                if (pointsList.Count > 100)
                    return JsonError("Maximum 100 points per call");

                // Resolve camera
                Camera cam = null;
                SceneView sceneView = null;
                if (source == "game")
                {
                    cam = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>();
                    if (cam == null) return JsonError("No camera found. Create a Camera in the scene.");
                }
                else
                {
                    sceneView = SceneView.lastActiveSceneView;
                    if (sceneView == null) return JsonError("No active scene view");
                    cam = sceneView.camera;
                    if (cam == null) return JsonError("No scene view camera");
                }

                // Cache renderers for bounds fallback
                Renderer[] allRenderers = null;

                var results = new List<object>();
                int hitCount = 0, missCount = 0;

                foreach (var pointObj in pointsList)
                {
                    var pointDict = pointObj as Dictionary<string, object>;
                    if (pointDict == null) { missCount++; results.Add(BuildMissEntry(0, 0)); continue; }

                    float nx = 0.5f, ny = 0.5f;
                    if (pointDict.TryGetValue("x", out var xVal)) nx = Convert.ToSingle(xVal);
                    if (pointDict.TryGetValue("y", out var yVal)) ny = Convert.ToSingle(yVal);

                    nx = Mathf.Clamp01(nx);
                    ny = Mathf.Clamp01(ny);

                    GameObject hitObject = null;
                    Vector3 hitPoint = Vector3.zero;
                    float hitDistance = 0f;
                    string detectionMethod = "none";

                    // Tier 1: HandleUtility.PickGameObject (scene view only)
                    if (source == "scene" && sceneView != null)
                    {
                        try
                        {
                            var pixelRect = cam.pixelRect;
                            var guiPoint = new Vector2(
                                pixelRect.x + nx * pixelRect.width,
                                pixelRect.y + pixelRect.height - ny * pixelRect.height
                            );

                            hitObject = HandleUtility.PickGameObject(guiPoint, false);
                            if (hitObject != null)
                            {
                                detectionMethod = "handle";
                                var ray = cam.ScreenPointToRay(new Vector3(nx * cam.pixelWidth, (1f - ny) * cam.pixelHeight, 0f));
                                if (hitObject.TryGetComponent<Collider>(out var col) && col.Raycast(ray, out RaycastHit colHit, Mathf.Infinity))
                                {
                                    hitPoint = colHit.point;
                                    hitDistance = colHit.distance;
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
                            hitObject = null;
                        }
                    }

                    // Tier 2: Physics.Raycast
                    if (hitObject == null)
                    {
                        var screenPoint = new Vector3(nx * cam.pixelWidth, (1f - ny) * cam.pixelHeight, 0f);
                        var ray = cam.ScreenPointToRay(screenPoint);

                        if (Physics.Raycast(ray, out RaycastHit physHit, Mathf.Infinity))
                        {
                            hitObject = physHit.collider.gameObject;
                            hitPoint = physHit.point;
                            hitDistance = physHit.distance;
                            detectionMethod = "physics";
                        }
                        else
                        {
                            // Tier 3: Renderer bounds intersection
                            if (allRenderers == null)
                                allRenderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);

                            float closestDist = float.MaxValue;
                            foreach (var renderer in allRenderers)
                            {
                                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                                if (renderer.bounds.IntersectRay(ray, out float dist) && dist > 0f && dist < closestDist)
                                {
                                    closestDist = dist;
                                    hitObject = renderer.gameObject;
                                    hitPoint = ray.GetPoint(dist);
                                    hitDistance = dist;
                                    detectionMethod = "bounds";
                                }
                            }
                        }
                    }

                    if (hitObject == null)
                    {
                        missCount++;
                        results.Add(BuildMissEntry(nx, ny));
                    }
                    else
                    {
                        hitCount++;
                        results.Add(new Dictionary<string, object>
                        {
                            { "x", Math.Round(nx, 3) },
                            { "y", Math.Round(ny, 3) },
                            { "hit", true },
                            { "instanceId", hitObject.GetInstanceID() },
                            { "name", hitObject.name },
                            { "path", GetHierarchyPath(hitObject.transform) },
                            { "worldPosition", new List<object>
                                {
                                    Math.Round(hitPoint.x, 3),
                                    Math.Round(hitPoint.y, 3),
                                    Math.Round(hitPoint.z, 3)
                                }
                            },
                            { "distance", Math.Round(hitDistance, 3) },
                            { "detectionMethod", detectionMethod }
                        });
                    }
                }

                sw.Stop();

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "source", source },
                    { "results", results },
                    { "hitCount", hitCount },
                    { "missCount", missCount },
                    { "scanTimeMs", Math.Round(sw.Elapsed.TotalMilliseconds, 1) }
                };

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static Dictionary<string, object> BuildMissEntry(float x, float y)
        {
            return new Dictionary<string, object>
            {
                { "x", Math.Round(x, 3) },
                { "y", Math.Round(y, 3) },
                { "hit", false }
            };
        }
    }
}
