using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        // ==================== Grounding Audit ====================

        [BridgeRoute("POST", "/spatial/audit-grounding", Category = "spatial", Description = "Audit objects for floating, sinking, or inadequate ground support", ReadOnly = true, TimeoutDefault = 15000, TimeoutMin = 500, TimeoutMax = 60000)]
        public static string AuditGrounding(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null) return JsonError("Failed to parse audit grounding request");

                var sw = Stopwatch.StartNew();

                int rootInstanceId = TryReadInt(request, "rootInstanceId", out var rootParsed) ? rootParsed : 0;
                var instanceIds = ExtractIntList(request, "instanceIds");
                bool includeChildren = ReadBool(request, "includeChildren", true);
                float maxGap = TryReadFloatField(request, "maxGap", out var gapParsed) ? gapParsed : 0.1f;
                float maxRayDistance = Mathf.Max(1f, TryReadFloatField(request, "maxRayDistance", out var rayParsed) ? rayParsed : 50f);
                float footprintSpacing = Mathf.Clamp(TryReadFloatField(request, "footprintSpacing", out var spaceParsed) ? spaceParsed : 0.25f, 0.05f, 2f);
                int maxObjects = Mathf.Clamp(TryReadInt(request, "maxObjects", out var maxObjParsed) ? maxObjParsed : 200, 1, 1000);
                int supportLayerMask = TryReadInt(request, "supportLayerMask", out var maskParsed) ? maskParsed : -1;
                float sinkThreshold = TryReadFloatField(request, "sinkThreshold", out var sinkParsed) ? sinkParsed : 0.05f;

                var objects = CollectGameObjectsForOverlapAudit(rootInstanceId, instanceIds, includeChildren);
                if (objects.Count > maxObjects)
                    objects = objects.Take(maxObjects).ToList();

                Physics.SyncTransforms();

                int floatingCount = 0, sunkCount = 0, unsupportedCount = 0, groundedCount = 0;
                var issues = new List<object>();

                foreach (var go in objects)
                {
                    if (go == null) continue;
                    if (!TryGetAggregateBounds(go, false, out var bounds, out _)) continue;

                    var result = AnalyzeGrounding(go, bounds, maxRayDistance, footprintSpacing, supportLayerMask, sinkThreshold);

                    string severity;
                    string status;

                    if (result.noGroundFound)
                    {
                        status = "no_ground";
                        severity = "critical";
                        floatingCount++;
                    }
                    else if (result.gapDistance > maxGap)
                    {
                        status = "floating";
                        severity = result.gapDistance > maxGap * 3f ? "critical" : "warning";
                        floatingCount++;
                    }
                    else if (result.sinkDepth > sinkThreshold)
                    {
                        status = "sunk";
                        severity = result.sinkDepth > sinkThreshold * 3f ? "critical" : "warning";
                        sunkCount++;
                    }
                    else if (result.supportedFraction < 0.15f)
                    {
                        status = "unsupported";
                        severity = "warning";
                        unsupportedCount++;
                    }
                    else
                    {
                        status = "grounded";
                        severity = "ok";
                        groundedCount++;
                    }

                    // Only report issues (skip grounded objects unless they have partial support)
                    if (severity == "ok" && result.supportedFraction >= 0.5f)
                        continue;

                    var entry = new Dictionary<string, object>
                    {
                        { "instanceId", go.GetInstanceID() },
                        { "name", go.name },
                        { "path", GetHierarchyPath(go.transform) },
                        { "status", status },
                        { "severity", severity },
                        { "gapDistance", Math.Round(result.gapDistance, 4) },
                        { "sinkDepth", Math.Round(result.sinkDepth, 4) },
                        { "supportedFraction", Math.Round(result.supportedFraction, 2) },
                        { "centerOfMassSupported", result.centerSupported },
                        { "suggestedSnapY", Math.Round(result.suggestedSnapY, 4) }
                    };

                    if (result.supportObject != null)
                    {
                        entry["supportObject"] = new Dictionary<string, object>
                        {
                            { "instanceId", result.supportObject.GetInstanceID() },
                            { "name", result.supportObject.name }
                        };
                    }

                    if (result.supportOffset.sqrMagnitude > 0.001f)
                    {
                        entry["supportOffsetXZ"] = new List<object>
                        {
                            Math.Round(result.supportOffset.x, 3),
                            Math.Round(result.supportOffset.z, 3)
                        };
                    }

                    issues.Add(entry);
                }

                sw.Stop();

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "summary", new Dictionary<string, object>
                        {
                            { "totalObjectsChecked", objects.Count },
                            { "groundedCount", groundedCount },
                            { "floatingCount", floatingCount },
                            { "sunkCount", sunkCount },
                            { "unsupportedCount", unsupportedCount },
                            { "issueCount", issues.Count }
                        }
                    },
                    { "verdict", new Dictionary<string, object>
                        {
                            { "healthy", floatingCount == 0 && sunkCount == 0 && unsupportedCount == 0 },
                            { "maxGapUsed", Math.Round(maxGap, 4) },
                            { "sinkThresholdUsed", Math.Round(sinkThreshold, 4) }
                        }
                    },
                    { "issues", issues },
                    { "scanTimeMs", Math.Round(sw.Elapsed.TotalMilliseconds, 1) }
                };

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        // ==================== Snap to Ground ====================

        [BridgeRoute("POST", "/spatial/snap-to-ground", Category = "spatial", Description = "Snap objects to the ground/terrain below them", TimeoutDefault = 15000, TimeoutMin = 500, TimeoutMax = 60000)]
        public static string SnapToGround(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null) return JsonError("Failed to parse snap to ground request");

                int rootInstanceId = TryReadInt(request, "rootInstanceId", out var rootParsed) ? rootParsed : 0;
                var instanceIds = ExtractIntList(request, "instanceIds");
                bool includeChildren = ReadBool(request, "includeChildren", true);
                float maxDrop = Mathf.Max(0.1f, TryReadFloatField(request, "maxDrop", out var dropParsed) ? dropParsed : 50f);
                bool preserveXZ = ReadBool(request, "preserveXZ", true);
                int supportLayerMask = TryReadInt(request, "supportLayerMask", out var maskParsed) ? maskParsed : -1;
                bool autoSaveScene = ReadBool(request, "autoSaveScene", false);

                var objects = CollectGameObjectsForOverlapAudit(rootInstanceId, instanceIds, includeChildren);
                if (objects.Count == 0)
                    return JsonError("No objects found to snap");
                if (objects.Count > 500)
                    objects = objects.Take(500).ToList();

                Physics.SyncTransforms();

                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Snap To Ground");
                int undoGroup = Undo.GetCurrentGroup();

                var snapped = new List<object>();
                int failedCount = 0;

                foreach (var go in objects)
                {
                    if (go == null) continue;
                    if (!TryGetAggregateBounds(go, false, out var bounds, out _)) { failedCount++; continue; }

                    // Raycast from bottom center of bounds downward
                    var bottomCenter = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                    // Also raycast from above to handle sunk objects
                    var aboveCenter = new Vector3(bounds.center.x, bounds.max.y + 1f, bounds.center.z);

                    Vector3? hitPoint = null;
                    GameObject hitObj = null;

                    // Try from above first (catches both floating and sunk objects)
                    if (Physics.Raycast(aboveCenter, Vector3.down, out RaycastHit hitAbove, bounds.size.y + 1f + maxDrop, supportLayerMask, QueryTriggerInteraction.Ignore))
                    {
                        // Only accept hits below or near the bottom of the object, and not on self
                        if (!hitAbove.collider.gameObject.transform.IsChildOf(go.transform) && hitAbove.collider.gameObject != go)
                        {
                            hitPoint = hitAbove.point;
                            hitObj = hitAbove.collider.gameObject;
                        }
                    }

                    // Fallback: raycast from bottom downward
                    if (hitPoint == null)
                    {
                        if (Physics.Raycast(bottomCenter, Vector3.down, out RaycastHit hitBelow, maxDrop, supportLayerMask, QueryTriggerInteraction.Ignore))
                        {
                            if (!hitBelow.collider.gameObject.transform.IsChildOf(go.transform) && hitBelow.collider.gameObject != go)
                            {
                                hitPoint = hitBelow.point;
                                hitObj = hitBelow.collider.gameObject;
                            }
                        }
                    }

                    if (hitPoint == null)
                    {
                        // Try Terrain.activeTerrain as last resort
                        var terrain = Terrain.activeTerrain;
                        if (terrain != null)
                        {
                            float terrainY = terrain.SampleHeight(go.transform.position) + terrain.transform.position.y;
                            hitPoint = new Vector3(bounds.center.x, terrainY, bounds.center.z);
                            hitObj = terrain.gameObject;
                        }
                    }

                    if (hitPoint == null)
                    {
                        failedCount++;
                        continue;
                    }

                    // Compute how much to move: align bounds bottom to hit point
                    float boundsBottomOffset = go.transform.position.y - bounds.min.y;
                    float targetY = hitPoint.Value.y + boundsBottomOffset;
                    var beforePos = go.transform.position;
                    var afterPos = preserveXZ
                        ? new Vector3(beforePos.x, targetY, beforePos.z)
                        : new Vector3(hitPoint.Value.x, targetY, hitPoint.Value.z);

                    if ((afterPos - beforePos).sqrMagnitude < 0.00001f)
                        continue; // Already grounded

                    Undo.RecordObject(go.transform, "Snap To Ground");
                    go.transform.position = afterPos;

                    var delta = afterPos - beforePos;
                    snapped.Add(new Dictionary<string, object>
                    {
                        { "instanceId", go.GetInstanceID() },
                        { "name", go.name },
                        { "before", new List<object> { Math.Round(beforePos.x, 3), Math.Round(beforePos.y, 3), Math.Round(beforePos.z, 3) } },
                        { "after", new List<object> { Math.Round(afterPos.x, 3), Math.Round(afterPos.y, 3), Math.Round(afterPos.z, 3) } },
                        { "delta", new List<object> { Math.Round(delta.x, 3), Math.Round(delta.y, 3), Math.Round(delta.z, 3) } },
                        { "supportObject", hitObj != null ? hitObj.name : "unknown" }
                    });
                }

                Undo.CollapseUndoOperations(undoGroup);

                if (snapped.Count > 0)
                    EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                bool sceneSaved = false;
                string sceneSaveError = null;
                if (autoSaveScene && snapped.Count > 0)
                    sceneSaved = TrySaveActiveScene(out sceneSaveError);

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "snappedCount", snapped.Count },
                    { "failedCount", failedCount },
                    { "snappedObjects", snapped },
                    { "sceneSaved", sceneSaved }
                };

                if (!string.IsNullOrWhiteSpace(sceneSaveError))
                    response["sceneSaveError"] = sceneSaveError;

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        // ==================== Grounding Analysis Kernel ====================

        private struct GroundingResult
        {
            public float gapDistance;       // Distance from bottom of bounds to ground (> 0 = floating)
            public float sinkDepth;         // How far below ground the object bottom is (> 0 = sunk)
            public float supportedFraction; // 0-1: what fraction of footprint has ground below
            public bool centerSupported;    // Is center of mass above support?
            public bool noGroundFound;      // No ground detected at all
            public float suggestedSnapY;    // Suggested world Y to snap to
            public GameObject supportObject;// Primary support object
            public Vector3 supportOffset;   // XZ offset from object center to nearest support
        }

        private static GroundingResult AnalyzeGrounding(
            GameObject go, Bounds bounds, float maxRayDistance, float footprintSpacing,
            int supportLayerMask, float sinkThreshold)
        {
            var result = new GroundingResult
            {
                gapDistance = 0f,
                sinkDepth = 0f,
                supportedFraction = 0f,
                centerSupported = false,
                noGroundFound = true,
                suggestedSnapY = go.transform.position.y,
                supportObject = null,
                supportOffset = Vector3.zero
            };

            // --- Primary ground check: raycast from bottom center ---
            var boundsBottom = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
            // Cast from well above to catch both floating and sunk cases
            var rayOrigin = new Vector3(bounds.center.x, bounds.max.y + 2f, bounds.center.z);
            float totalRayDist = bounds.size.y + 2f + maxRayDistance;

            float groundY = float.NegativeInfinity;
            GameObject primarySupport = null;

            // Raycast from above the object down through it
            if (CastGroundRay(rayOrigin, totalRayDist, go, supportLayerMask, out var hitCenter))
            {
                groundY = hitCenter.point.y;
                primarySupport = hitCenter.collider.gameObject;
                result.noGroundFound = false;
            }
            else
            {
                // Fallback: try Terrain.activeTerrain
                var terrain = Terrain.activeTerrain;
                if (terrain != null)
                {
                    groundY = terrain.SampleHeight(go.transform.position) + terrain.transform.position.y;
                    primarySupport = terrain.gameObject;
                    result.noGroundFound = false;
                }
            }

            if (result.noGroundFound)
                return result;

            result.supportObject = primarySupport;

            // Compute gap or sink
            float bottomY = bounds.min.y;
            float diff = bottomY - groundY;

            if (diff > 0f)
            {
                result.gapDistance = diff;
                result.sinkDepth = 0f;
            }
            else
            {
                result.gapDistance = 0f;
                result.sinkDepth = -diff;
            }

            // Suggested snap Y: move transform so bounds.min.y aligns with groundY
            float boundsBottomOffset = go.transform.position.y - bounds.min.y;
            result.suggestedSnapY = groundY + boundsBottomOffset;

            // --- Footprint support analysis ---
            // Project bounds to XZ, sample grid of points, raycast down at each
            float xMin = bounds.min.x;
            float xMax = bounds.max.x;
            float zMin = bounds.min.z;
            float zMax = bounds.max.z;

            float xSpan = xMax - xMin;
            float zSpan = zMax - zMin;

            // Limit grid to reasonable size
            int xSteps = Mathf.Clamp(Mathf.CeilToInt(xSpan / footprintSpacing), 2, 20);
            int zSteps = Mathf.Clamp(Mathf.CeilToInt(zSpan / footprintSpacing), 2, 20);

            int totalSamples = 0;
            int supportedSamples = 0;
            bool centerHit = false;
            float nearestSupportDist = float.MaxValue;
            Vector3 nearestSupportPos = Vector3.zero;

            float sampleY = bounds.max.y + 2f;
            float sampleRayDist = bounds.size.y + 2f + maxRayDistance;

            for (int xi = 0; xi <= xSteps; xi++)
            {
                float x = Mathf.Lerp(xMin, xMax, (float)xi / xSteps);
                for (int zi = 0; zi <= zSteps; zi++)
                {
                    float z = Mathf.Lerp(zMin, zMax, (float)zi / zSteps);
                    totalSamples++;

                    var sampleOrigin = new Vector3(x, sampleY, z);
                    if (CastGroundRay(sampleOrigin, sampleRayDist, go, supportLayerMask, out var sampleHit))
                    {
                        // Check if support is within a reasonable range of the object bottom
                        float supportGap = Mathf.Abs(sampleHit.point.y - groundY);
                        if (supportGap < bounds.size.y * 0.5f + 0.5f)
                        {
                            supportedSamples++;
                            float distFromCenter = new Vector2(x - bounds.center.x, z - bounds.center.z).magnitude;
                            if (distFromCenter < nearestSupportDist)
                            {
                                nearestSupportDist = distFromCenter;
                                nearestSupportPos = sampleHit.point;
                            }
                        }

                        // Check center sample (closest to bounds center)
                        bool isCenterSample = xi == xSteps / 2 && zi == zSteps / 2;
                        if (isCenterSample)
                            centerHit = true;
                    }
                }
            }

            result.supportedFraction = totalSamples > 0 ? (float)supportedSamples / totalSamples : 0f;
            result.centerSupported = centerHit;
            result.supportOffset = nearestSupportPos - new Vector3(bounds.center.x, nearestSupportPos.y, bounds.center.z);

            return result;
        }

        /// <summary>
        /// Cast a downward ray, ignoring the source object and its children.
        /// </summary>
        private static bool CastGroundRay(Vector3 origin, float maxDistance, GameObject self, int layerMask, out RaycastHit result)
        {
            result = default;

            // Use RaycastAll to skip self-hits
            var hits = Physics.RaycastAll(origin, Vector3.down, maxDistance, layerMask, QueryTriggerInteraction.Ignore);
            if (hits.Length == 0) return false;

            // Sort by distance and find first non-self hit
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                if (hit.collider == null) continue;
                var hitGo = hit.collider.gameObject;
                if (hitGo == self) continue;
                if (hitGo.transform.IsChildOf(self.transform)) continue;
                if (self.transform.IsChildOf(hitGo.transform)) continue;

                result = hit;
                return true;
            }

            return false;
        }
    }
}
