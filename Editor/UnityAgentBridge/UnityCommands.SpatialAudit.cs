using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        // ==================== Camera Visibility Audit ====================

        [BridgeRoute("POST", "/spatial/visibility-audit", Category = "spatial", Description = "Camera visibility + occlusion + attachment audit", ReadOnly = true, TimeoutDefault = 15000)]
        public static string CameraVisibilityAudit(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<CameraVisibilityAuditRequest>(
                    string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) ?? new CameraVisibilityAuditRequest();

                var sw = Stopwatch.StartNew();

                string view = string.IsNullOrWhiteSpace(request.view) ? "game" : request.view.Trim().ToLowerInvariant();
                bool includeVisible = request.includeVisible == 1;
                bool checkAttachment = request.checkAttachment == 1;
                float attachMaxDist = request.attachMaxDistance > 0f ? request.attachMaxDistance : 0.5f;
                int raySamples = Mathf.Clamp(request.raySamples <= 0 ? 9 : request.raySamples, 1, 25);
                int maxObjects = Mathf.Clamp(request.maxObjects <= 0 ? 500 : request.maxObjects, 10, 5000);
                int occluderMask = request.occluderLayerMask == -1 ? ~0 : request.occluderLayerMask;
                var triggerInteraction = request.ignoreTriggers != 0
                    ? QueryTriggerInteraction.Ignore
                    : QueryTriggerInteraction.Collide;

                // Resolve camera
                Camera camera = null;
                if (view == "scene")
                {
                    var sceneView = SceneView.lastActiveSceneView;
                    if (sceneView != null)
                        camera = sceneView.camera;
                }
                else
                {
                    camera = Camera.main;
                }

                if (camera == null)
                    return JsonError($"No camera available for view '{view}'. " +
                        (view == "game" ? "Ensure a Camera with tag 'MainCamera' exists." : "Open a Scene View."));

                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded)
                    return JsonError("No active loaded scene");

                // Collect target objects
                var targetRenderers = CollectFilteredRenderers(scene, request.nameContains, request.tag,
                    request.layer, request.rootInstanceId, request.targetInstanceIds, false);

                if (targetRenderers.Count > maxObjects)
                    targetRenderers = targetRenderers.Take(maxObjects).ToList();

                // Compute frustum planes
                var frustumPlanes = GeometryUtility.CalculateFrustumPlanes(camera);

                // Compute collider coverage (fraction of scene objects with colliders)
                int totalSceneColliders = 0;
                int totalSceneRenderers = 0;
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var rend in root.GetComponentsInChildren<Renderer>(false))
                    {
                        if (rend == null || rend.gameObject == null) continue;
                        totalSceneRenderers++;
                        if (rend.gameObject.GetComponent<Collider>() != null)
                            totalSceneColliders++;
                    }
                }
                float colliderCoverage = totalSceneRenderers > 0
                    ? (float)totalSceneColliders / totalSceneRenderers
                    : 0f;

                int visibleCount = 0, partialCount = 0, fullyOccludedCount = 0, outOfFrustumCount = 0, detachedCount = 0;
                int inconclusiveCount = 0;
                var issues = new List<Dictionary<string, object>>();

                foreach (var rend in targetRenderers)
                {
                    var go = rend.gameObject;
                    if (!go.activeInHierarchy || !rend.enabled) continue;

                    var bounds = rend.bounds;
                    var entry = new Dictionary<string, object>
                    {
                        { "instanceId", go.GetInstanceID() },
                        { "name", go.name },
                        { "path", GetHierarchyPath(go.transform) },
                        { "worldPosition", new List<object> {
                            Math.Round(bounds.center.x, 3),
                            Math.Round(bounds.center.y, 3),
                            Math.Round(bounds.center.z, 3) } }
                    };

                    // Frustum test
                    if (!GeometryUtility.TestPlanesAABB(frustumPlanes, bounds))
                    {
                        outOfFrustumCount++;
                        entry["status"] = "out_of_frustum";
                        entry["blockedSamples"] = 0;
                        entry["totalSamples"] = raySamples;
                        issues.Add(entry);
                        continue;
                    }

                    // Sample points on bounds
                    var samplePoints = SampleBoundsPoints(bounds, raySamples);

                    // Occlusion raycasts from camera to each sample point
                    int blockedCount = 0;
                    var occludedBySet = new Dictionary<int, string>(); // instanceId -> name
                    bool targetHasCollider = go.GetComponent<Collider>() != null;
                    var hitBuffer = new RaycastHit[8];

                    foreach (var point in samplePoints)
                    {
                        var dir = point - camera.transform.position;
                        float dist = dir.magnitude;
                        if (dist < 0.001f) continue;

                        int hitCount = Physics.RaycastNonAlloc(
                            camera.transform.position, dir.normalized, hitBuffer,
                            dist, occluderMask, triggerInteraction);

                        bool blocked = false;
                        for (int h = 0; h < hitCount; h++)
                        {
                            var hitGo = hitBuffer[h].collider.gameObject;
                            // Skip the target itself and its children
                            if (hitGo == go || hitGo.transform.IsChildOf(go.transform)) continue;
                            // This hit is between camera and target — blocks visibility
                            if (hitBuffer[h].distance < dist - 0.01f)
                            {
                                blocked = true;
                                int hitId = hitGo.GetInstanceID();
                                if (!occludedBySet.ContainsKey(hitId))
                                    occludedBySet[hitId] = hitGo.name;
                            }
                        }
                        if (blocked) blockedCount++;
                    }

                    entry["blockedSamples"] = blockedCount;
                    entry["totalSamples"] = samplePoints.Length;
                    entry["targetHasNoCollider"] = !targetHasCollider;

                    // Determine status
                    string status;
                    if (blockedCount == 0)
                    {
                        status = "visible";
                        visibleCount++;
                    }
                    else if (blockedCount == samplePoints.Length)
                    {
                        status = "fully_occluded";
                        fullyOccludedCount++;
                    }
                    else
                    {
                        status = "partial";
                        partialCount++;
                    }

                    // Screen rect
                    var screenRect = ComputeScreenRect(camera, bounds);
                    if (screenRect != null)
                        entry["screenRect"] = screenRect;

                    // Occluder list
                    if (occludedBySet.Count > 0)
                    {
                        entry["occludedBy"] = occludedBySet.Select(kv => new Dictionary<string, object>
                        {
                            { "name", kv.Value },
                            { "instanceId", kv.Key }
                        }).Cast<object>().ToList();
                    }

                    // Attachment check
                    if (checkAttachment)
                    {
                        var attachResult = CheckAttachment(bounds, go, attachMaxDist, occluderMask, triggerInteraction);
                        if (attachResult.detached)
                        {
                            detachedCount++;
                            if (status == "visible")
                                status = "detached";
                            else
                                status = status + ",detached";
                        }
                        entry["nearestSurfaceDistance"] = Math.Round(attachResult.nearestDistance, 3);
                        if (attachResult.nearestSurface != null)
                        {
                            entry["nearestSurface"] = new Dictionary<string, object>
                            {
                                { "name", attachResult.nearestSurface.name },
                                { "instanceId", attachResult.nearestSurface.GetInstanceID() }
                            };
                        }
                        else
                        {
                            entry["nearestSurface"] = null;
                        }
                    }

                    entry["status"] = status;

                    // Low collider coverage → inconclusive
                    if (colliderCoverage < 0.5f && status == "visible")
                        inconclusiveCount++;

                    // Only report issues unless includeVisible
                    if (status == "visible" && !includeVisible) continue;
                    issues.Add(entry);
                }

                sw.Stop();

                string confidence = colliderCoverage >= 0.5f ? "high" : "low";
                var verdictIssues = new List<string>();
                if (fullyOccludedCount > 0) verdictIssues.Add($"{fullyOccludedCount} objects fully occluded");
                if (partialCount > 0) verdictIssues.Add($"{partialCount} objects partially occluded");
                if (outOfFrustumCount > 0) verdictIssues.Add($"{outOfFrustumCount} objects out of frustum");
                if (detachedCount > 0) verdictIssues.Add($"{detachedCount} objects detached from support");

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "summary", new Dictionary<string, object>
                        {
                            { "totalChecked", targetRenderers.Count },
                            { "visible", visibleCount },
                            { "partial", partialCount },
                            { "fullyOccluded", fullyOccludedCount },
                            { "outOfFrustum", outOfFrustumCount },
                            { "detached", detachedCount },
                            { "colliderCoverage", Math.Round(colliderCoverage, 2) },
                            { "inconclusiveCount", inconclusiveCount }
                        }
                    },
                    { "verdict", new Dictionary<string, object>
                        {
                            { "healthy", fullyOccludedCount == 0 && detachedCount == 0 },
                            { "confidence", confidence },
                            { "issues", verdictIssues.Cast<object>().ToList() }
                        }
                    },
                    { "issues", issues.Cast<object>().ToList() },
                    { "scanTimeMs", Math.Round(sw.Elapsed.TotalMilliseconds, 1) }
                };

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        // ==================== Raycast Coverage Check ====================

        [BridgeRoute("POST", "/spatial/coverage-check", Category = "spatial", Description = "Raycast grid coverage check for floor/wall gaps", ReadOnly = true, TimeoutDefault = 15000)]
        public static string RaycastCoverageCheck(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<RaycastCoverageRequest>(
                    string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) ?? new RaycastCoverageRequest();

                var sw = Stopwatch.StartNew();

                float spacing = Mathf.Max(request.spacing > 0f ? request.spacing : 0.5f, 0.1f);
                float maxRayDist = request.maxRayDistance > 0f ? request.maxRayDistance : 100f;
                float originOffset = request.originOffset > 0f ? request.originOffset : 10f;
                int maxGaps = Mathf.Clamp(request.maxGaps > 0 ? request.maxGaps : 50, 1, 500);
                int surfaceMask = request.surfaceLayerMask == -1 ? ~0 : request.surfaceLayerMask;
                var triggerInteraction = request.ignoreTriggers != 0
                    ? QueryTriggerInteraction.Ignore
                    : QueryTriggerInteraction.Collide;
                const int maxRays = 100000;

                // Determine scan bounds
                Bounds scanBounds;
                bool hasExplicitBounds = request.boundsMinX != -1f && request.boundsMaxX != -1f;

                if (hasExplicitBounds)
                {
                    var min = new Vector3(request.boundsMinX, request.boundsMinY, request.boundsMinZ);
                    var max = new Vector3(request.boundsMaxX, request.boundsMaxY, request.boundsMaxZ);
                    scanBounds = new Bounds();
                    scanBounds.SetMinMax(min, max);
                }
                else if (request.rootInstanceId != 0)
                {
                    var rootGo = EditorUtility.EntityIdToObject(request.rootInstanceId) as GameObject;
                    if (rootGo == null)
                        return JsonError("Root GameObject not found for instanceId " + request.rootInstanceId);

                    var renderers = rootGo.GetComponentsInChildren<Renderer>(false);
                    if (renderers.Length == 0)
                        return JsonError("No renderers found under root GameObject to compute bounds");

                    scanBounds = renderers[0].bounds;
                    for (int i = 1; i < renderers.Length; i++)
                        scanBounds.Encapsulate(renderers[i].bounds);
                }
                else
                {
                    return JsonError("Must provide rootInstanceId or explicit bounds (boundsMinX/Y/Z, boundsMaxX/Y/Z)");
                }

                // Compute ray direction
                Vector3 rayDir = ParseDirection(request.direction);
                // Compute the two axes perpendicular to the ray direction for grid layout
                Vector3 gridAxisU, gridAxisV;
                float gridMinU, gridMaxU, gridMinV, gridMaxV;
                float originBase;
                ComputeGridAxes(rayDir, scanBounds, originOffset,
                    out gridAxisU, out gridAxisV,
                    out gridMinU, out gridMaxU, out gridMinV, out gridMaxV,
                    out originBase);

                int gridCountU = Mathf.CeilToInt((gridMaxU - gridMinU) / spacing) + 1;
                int gridCountV = Mathf.CeilToInt((gridMaxV - gridMinV) / spacing) + 1;
                int totalRays = gridCountU * gridCountV;

                if (totalRays > maxRays)
                {
                    float areaW = gridMaxU - gridMinU;
                    float areaD = gridMaxV - gridMinV;
                    return JsonError($"Reduce area or increase spacing. Current grid would cast {totalRays} rays (max {maxRays}). " +
                        $"Area: {areaW:F1}x{areaD:F1}m, spacing: {spacing:F2}m.");
                }

                // Surface filter
                string surfaceName = request.surfaceNameContains ?? "";
                string surfaceTag = request.surfaceTag ?? "";
                string surfaceLayerName = request.surfaceLayer ?? "";
                int surfaceLayerIndex = -1;
                if (!string.IsNullOrWhiteSpace(surfaceLayerName))
                    surfaceLayerIndex = LayerMask.NameToLayer(surfaceLayerName);

                // Collider coverage: count surface-matching objects with colliders
                int surfaceObjectsWithCollider = 0;
                int surfaceObjectsTotal = 0;

                // Cast rays
                int hitSurface = 0, hitOther = 0, miss = 0;
                var gridStatus = new int[gridCountU, gridCountV]; // 0=miss, 1=hitSurface, 2=hitOther
                var hitBuffer = new RaycastHit[4];

                for (int u = 0; u < gridCountU; u++)
                {
                    for (int v = 0; v < gridCountV; v++)
                    {
                        float uPos = gridMinU + u * spacing;
                        float vPos = gridMinV + v * spacing;

                        Vector3 origin = gridAxisU * uPos + gridAxisV * vPos;
                        // Add the offset component along the ray direction (opposite to ray)
                        origin -= rayDir * originBase;

                        int hitCount = Physics.RaycastNonAlloc(origin, rayDir, hitBuffer,
                            maxRayDist, surfaceMask, triggerInteraction);

                        if (hitCount == 0)
                        {
                            miss++;
                            gridStatus[u, v] = 0;
                            continue;
                        }

                        // Find closest hit
                        int closestIdx = 0;
                        for (int h = 1; h < hitCount; h++)
                        {
                            if (hitBuffer[h].distance < hitBuffer[closestIdx].distance)
                                closestIdx = h;
                        }

                        var hitGo = hitBuffer[closestIdx].collider.gameObject;
                        if (MatchesSurfaceFilter(hitGo, surfaceName, surfaceTag, surfaceLayerIndex))
                        {
                            hitSurface++;
                            gridStatus[u, v] = 1;
                        }
                        else
                        {
                            hitOther++;
                            gridStatus[u, v] = 2;
                        }
                    }
                }

                // Compute collider coverage for surface-matching objects
                var scene = SceneManager.GetActiveScene();
                if (scene.IsValid() && scene.isLoaded)
                {
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        foreach (var rend in root.GetComponentsInChildren<Renderer>(false))
                        {
                            if (rend == null || rend.gameObject == null) continue;
                            if (!MatchesSurfaceFilter(rend.gameObject, surfaceName, surfaceTag, surfaceLayerIndex))
                                continue;
                            surfaceObjectsTotal++;
                            if (rend.gameObject.GetComponent<Collider>() != null)
                                surfaceObjectsWithCollider++;
                        }
                    }
                }
                float surfaceColliderCoverage = surfaceObjectsTotal > 0
                    ? (float)surfaceObjectsWithCollider / surfaceObjectsTotal
                    : 0f;

                // Cluster contiguous miss points into gap regions
                var gaps = ClusterGaps(gridStatus, gridCountU, gridCountV, gridMinU, gridMinV,
                    spacing, gridAxisU, gridAxisV, rayDir, originBase, maxGaps,
                    surfaceName, surfaceTag, surfaceLayerIndex, surfaceMask, maxRayDist, triggerInteraction);

                sw.Stop();

                float coveragePercent = totalRays > 0
                    ? (float)(hitSurface + hitOther) / totalRays * 100f
                    : 0f;
                float surfaceCoveragePercent = totalRays > 0
                    ? (float)hitSurface / totalRays * 100f
                    : 0f;

                string confidence = surfaceColliderCoverage >= 0.5f ? "high" : "low";
                int inconclusiveCount = surfaceColliderCoverage < 0.5f ? miss : 0;
                float totalGapArea = gaps.Sum(g => (float)(double)g["areaSqM"]);

                string verdictSummary;
                bool healthy;
                if (gaps.Count == 0)
                {
                    healthy = true;
                    verdictSummary = $"Full coverage ({surfaceCoveragePercent:F1}% surface hits)";
                }
                else
                {
                    healthy = false;
                    verdictSummary = $"{gaps.Count} gap(s) found totaling {totalGapArea:F2} sq meters";
                }

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "coverage", new Dictionary<string, object>
                        {
                            { "coveragePercent", Math.Round(coveragePercent, 1) },
                            { "surfaceCoveragePercent", Math.Round(surfaceCoveragePercent, 1) },
                            { "hitSurface", hitSurface },
                            { "hitOther", hitOther },
                            { "miss", miss },
                            { "totalRays", totalRays }
                        }
                    },
                    { "colliderCoverage", Math.Round(surfaceColliderCoverage, 2) },
                    { "inconclusiveCount", inconclusiveCount },
                    { "gaps", gaps.Cast<object>().ToList() },
                    { "verdict", new Dictionary<string, object>
                        {
                            { "healthy", healthy },
                            { "confidence", confidence },
                            { "summary", verdictSummary }
                        }
                    },
                    { "scanTimeMs", Math.Round(sw.Elapsed.TotalMilliseconds, 1) }
                };

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        // ==================== Helpers ====================

        private static List<Renderer> CollectFilteredRenderers(
            Scene scene, string nameContains, string tag, string layer,
            int rootInstanceId, string targetInstanceIds, bool includeInactive)
        {
            var renderers = new List<Renderer>();

            // If specific instance IDs are provided, use those
            if (!string.IsNullOrWhiteSpace(targetInstanceIds))
            {
                foreach (var idStr in targetInstanceIds.Split(','))
                {
                    if (int.TryParse(idStr.Trim(), out int id))
                    {
                        var go = EditorUtility.EntityIdToObject(id) as GameObject;
                        if (go == null) continue;
                        var rend = go.GetComponent<Renderer>();
                        if (rend == null) rend = go.GetComponentInChildren<Renderer>(includeInactive);
                        if (rend != null) renderers.Add(rend);
                    }
                }
                return renderers;
            }

            // Collect from root or scene
            if (rootInstanceId != 0)
            {
                var rootGo = EditorUtility.EntityIdToObject(rootInstanceId) as GameObject;
                if (rootGo != null)
                {
                    foreach (var rend in rootGo.GetComponentsInChildren<Renderer>(includeInactive))
                    {
                        if (rend != null && rend.gameObject != null)
                            renderers.Add(rend);
                    }
                }
            }
            else
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var rend in root.GetComponentsInChildren<Renderer>(includeInactive))
                    {
                        if (rend != null && rend.gameObject != null)
                            renderers.Add(rend);
                    }
                }
            }

            // Apply filters
            if (!string.IsNullOrWhiteSpace(nameContains))
            {
                renderers = renderers.Where(r =>
                    r.gameObject.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            if (!string.IsNullOrWhiteSpace(tag))
            {
                renderers = renderers.Where(r =>
                {
                    try { return string.Equals(r.gameObject.tag, tag, StringComparison.OrdinalIgnoreCase); }
                    catch { return false; }
                }).ToList();
            }

            if (!string.IsNullOrWhiteSpace(layer))
            {
                int layerIndex = LayerMask.NameToLayer(layer);
                if (layerIndex >= 0)
                    renderers = renderers.Where(r => r.gameObject.layer == layerIndex).ToList();
            }

            return renderers;
        }

        private static Vector3[] SampleBoundsPoints(Bounds bounds, int raySamples)
        {
            if (raySamples <= 1)
                return new[] { bounds.center };

            // Center + 8 corners of bounds (9 default), clamped to raySamples
            var points = new List<Vector3> { bounds.center };

            if (raySamples >= 2)
            {
                var min = bounds.min;
                var max = bounds.max;
                var corners = new[]
                {
                    new Vector3(min.x, min.y, min.z),
                    new Vector3(min.x, min.y, max.z),
                    new Vector3(min.x, max.y, min.z),
                    new Vector3(min.x, max.y, max.z),
                    new Vector3(max.x, min.y, min.z),
                    new Vector3(max.x, min.y, max.z),
                    new Vector3(max.x, max.y, min.z),
                    new Vector3(max.x, max.y, max.z),
                };

                // Pull corners slightly inward to avoid edge cases
                for (int i = 0; i < corners.Length && points.Count < raySamples; i++)
                {
                    var pulled = Vector3.Lerp(corners[i], bounds.center, 0.05f);
                    points.Add(pulled);
                }
            }

            return points.ToArray();
        }

        private struct AttachmentResult
        {
            public bool detached;
            public float nearestDistance;
            public GameObject nearestSurface;
        }

        private static AttachmentResult CheckAttachment(Bounds bounds, GameObject self,
            float maxDistance, int layerMask, QueryTriggerInteraction triggerInteraction)
        {
            var result = new AttachmentResult
            {
                detached = true,
                nearestDistance = float.MaxValue,
                nearestSurface = null
            };

            // Cast 6 directional rays from bounds surfaces
            var directions = new[]
            {
                Vector3.right, Vector3.left,
                Vector3.up, Vector3.down,
                Vector3.forward, Vector3.back
            };

            var extents = bounds.extents;
            foreach (var dir in directions)
            {
                // Start from the surface of the bounds in this direction
                Vector3 surfacePoint = bounds.center + Vector3.Scale(dir, extents);
                if (Physics.Raycast(surfacePoint, dir, out RaycastHit hit, maxDistance, layerMask, triggerInteraction))
                {
                    if (hit.collider.gameObject == self || hit.collider.gameObject.transform.IsChildOf(self.transform))
                        continue;

                    if (hit.distance < result.nearestDistance)
                    {
                        result.nearestDistance = hit.distance;
                        result.nearestSurface = hit.collider.gameObject;
                        result.detached = false;
                    }
                }
            }

            return result;
        }

        private static Dictionary<string, object> ComputeScreenRect(Camera camera, Bounds bounds)
        {
            try
            {
                var corners = new Vector3[8];
                var min = bounds.min;
                var max = bounds.max;
                corners[0] = new Vector3(min.x, min.y, min.z);
                corners[1] = new Vector3(min.x, min.y, max.z);
                corners[2] = new Vector3(min.x, max.y, min.z);
                corners[3] = new Vector3(min.x, max.y, max.z);
                corners[4] = new Vector3(max.x, min.y, min.z);
                corners[5] = new Vector3(max.x, min.y, max.z);
                corners[6] = new Vector3(max.x, max.y, min.z);
                corners[7] = new Vector3(max.x, max.y, max.z);

                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                bool anyBehindCamera = false;

                foreach (var corner in corners)
                {
                    var screenPt = camera.WorldToScreenPoint(corner);
                    if (screenPt.z < 0) { anyBehindCamera = true; continue; }
                    float nx = screenPt.x / camera.pixelWidth;
                    float ny = screenPt.y / camera.pixelHeight;
                    minX = Mathf.Min(minX, nx);
                    minY = Mathf.Min(minY, ny);
                    maxX = Mathf.Max(maxX, nx);
                    maxY = Mathf.Max(maxY, ny);
                }

                if (anyBehindCamera && minX == float.MaxValue) return null;

                float coverage = (maxX - minX) * (maxY - minY);
                return new Dictionary<string, object>
                {
                    { "minX", Math.Round(Mathf.Clamp01(minX), 3) },
                    { "minY", Math.Round(Mathf.Clamp01(minY), 3) },
                    { "maxX", Math.Round(Mathf.Clamp01(maxX), 3) },
                    { "maxY", Math.Round(Mathf.Clamp01(maxY), 3) },
                    { "coveragePercent", Math.Round(coverage * 100f, 2) }
                };
            }
            catch
            {
                return null;
            }
        }

        private static Vector3 ParseDirection(string direction)
        {
            switch ((direction ?? "down").Trim().ToLowerInvariant())
            {
                case "up": return Vector3.up;
                case "forward": return Vector3.forward;
                case "back": return Vector3.back;
                case "left": return Vector3.left;
                case "right": return Vector3.right;
                default: return Vector3.down;
            }
        }

        private static void ComputeGridAxes(Vector3 rayDir, Bounds scanBounds, float originOffset,
            out Vector3 gridAxisU, out Vector3 gridAxisV,
            out float gridMinU, out float gridMaxU, out float gridMinV, out float gridMaxV,
            out float originBase)
        {
            var min = scanBounds.min;
            var max = scanBounds.max;

            if (rayDir == Vector3.down || rayDir == Vector3.up)
            {
                gridAxisU = Vector3.right;  // X
                gridAxisV = Vector3.forward; // Z
                gridMinU = min.x; gridMaxU = max.x;
                gridMinV = min.z; gridMaxV = max.z;
                originBase = rayDir == Vector3.down ? -(max.y + originOffset) : (min.y - originOffset);
            }
            else if (rayDir == Vector3.forward || rayDir == Vector3.back)
            {
                gridAxisU = Vector3.right; // X
                gridAxisV = Vector3.up;    // Y
                gridMinU = min.x; gridMaxU = max.x;
                gridMinV = min.y; gridMaxV = max.y;
                originBase = rayDir == Vector3.forward ? -(min.z - originOffset) : (max.z + originOffset);
            }
            else // left or right
            {
                gridAxisU = Vector3.forward; // Z
                gridAxisV = Vector3.up;      // Y
                gridMinU = min.z; gridMaxU = max.z;
                gridMinV = min.y; gridMaxV = max.y;
                originBase = rayDir == Vector3.right ? -(min.x - originOffset) : (max.x + originOffset);
            }
        }

        private static bool MatchesSurfaceFilter(GameObject go, string nameContains, string tag, int layerIndex)
        {
            bool matchesName = string.IsNullOrWhiteSpace(nameContains) ||
                go.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0;
            bool matchesTag = string.IsNullOrWhiteSpace(tag);
            if (!matchesTag)
            {
                try { matchesTag = string.Equals(go.tag, tag, StringComparison.OrdinalIgnoreCase); }
                catch { }
            }
            bool matchesLayer = layerIndex < 0 || go.layer == layerIndex;

            // If no filter is specified, everything matches
            if (string.IsNullOrWhiteSpace(nameContains) && string.IsNullOrWhiteSpace(tag) && layerIndex < 0)
                return true;

            return matchesName && matchesTag && matchesLayer;
        }

        private static List<Dictionary<string, object>> ClusterGaps(
            int[,] grid, int countU, int countV,
            float gridMinU, float gridMinV, float spacing,
            Vector3 gridAxisU, Vector3 gridAxisV, Vector3 rayDir, float originBase,
            int maxGaps,
            string surfaceName, string surfaceTag, int surfaceLayerIndex,
            int surfaceMask, float maxRayDist, QueryTriggerInteraction triggerInteraction)
        {
            var visited = new bool[countU, countV];
            var gaps = new List<Dictionary<string, object>>();

            for (int u = 0; u < countU && gaps.Count < maxGaps; u++)
            {
                for (int v = 0; v < countV && gaps.Count < maxGaps; v++)
                {
                    if (grid[u, v] != 0 || visited[u, v]) continue;

                    // Flood-fill to find contiguous gap cluster
                    var cluster = new List<(int u, int v)>();
                    var queue = new Queue<(int, int)>();
                    queue.Enqueue((u, v));
                    visited[u, v] = true;

                    while (queue.Count > 0)
                    {
                        var (cu, cv) = queue.Dequeue();
                        cluster.Add((cu, cv));

                        // 4-connected neighbors
                        foreach (var (du, dv) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        {
                            int nu = cu + du, nv = cv + dv;
                            if (nu < 0 || nu >= countU || nv < 0 || nv >= countV) continue;
                            if (visited[nu, nv] || grid[nu, nv] != 0) continue;
                            visited[nu, nv] = true;
                            queue.Enqueue((nu, nv));
                        }
                    }

                    if (cluster.Count == 0) continue;

                    // Compute gap center and approximate size
                    float sumU = 0, sumV = 0;
                    int minCU = int.MaxValue, maxCU = int.MinValue;
                    int minCV = int.MaxValue, maxCV = int.MinValue;
                    foreach (var (cu, cv) in cluster)
                    {
                        sumU += gridMinU + cu * spacing;
                        sumV += gridMinV + cv * spacing;
                        minCU = Mathf.Min(minCU, cu);
                        maxCU = Mathf.Max(maxCU, cu);
                        minCV = Mathf.Min(minCV, cv);
                        maxCV = Mathf.Max(maxCV, cv);
                    }

                    float centerU = sumU / cluster.Count;
                    float centerV = sumV / cluster.Count;
                    float sizeU = (maxCU - minCU + 1) * spacing;
                    float sizeV = (maxCV - minCV + 1) * spacing;
                    float area = cluster.Count * spacing * spacing;

                    // Convert back to world coordinates
                    Vector3 worldCenter = gridAxisU * centerU + gridAxisV * centerV;

                    // Find nearest surface tile
                    Dictionary<string, object> nearestSurface = null;
                    float nearestDist = float.MaxValue;
                    foreach (var (du, dv) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        for (int ci = 0; ci < cluster.Count; ci++)
                        {
                            int nu = cluster[ci].u + du, nv = cluster[ci].v + dv;
                            if (nu < 0 || nu >= countU || nv < 0 || nv >= countV) continue;
                            if (grid[nu, nv] != 1) continue;

                            float surfU = gridMinU + nu * spacing;
                            float surfV = gridMinV + nv * spacing;
                            float d = Mathf.Sqrt((surfU - centerU) * (surfU - centerU) + (surfV - centerV) * (surfV - centerV));
                            if (d < nearestDist)
                            {
                                nearestDist = d;
                                // Raycast to find the actual surface object
                                Vector3 surfOrigin = gridAxisU * surfU + gridAxisV * surfV - rayDir * originBase;
                                if (Physics.Raycast(surfOrigin, rayDir, out RaycastHit hit, maxRayDist, surfaceMask, triggerInteraction))
                                {
                                    nearestSurface = new Dictionary<string, object>
                                    {
                                        { "name", hit.collider.gameObject.name },
                                        { "instanceId", hit.collider.gameObject.GetInstanceID() }
                                    };
                                }
                            }
                        }
                        if (nearestSurface != null) break; // Found one, stop searching
                    }

                    var gapEntry = new Dictionary<string, object>
                    {
                        { "center", new List<object> {
                            Math.Round(worldCenter.x, 2),
                            Math.Round(worldCenter.y, 2),
                            Math.Round(worldCenter.z, 2) } },
                        { "approximateSize", new List<object> {
                            Math.Round(sizeU, 2), 0,
                            Math.Round(sizeV, 2) } },
                        { "areaSqM", Math.Round(area, 2) },
                        { "raysMissed", cluster.Count }
                    };

                    if (nearestSurface != null)
                        gapEntry["nearestSurface"] = nearestSurface;

                    gaps.Add(gapEntry);
                }
            }

            return gaps;
        }
    }
}
