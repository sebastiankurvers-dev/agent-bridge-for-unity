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
        // ==================== Overlap Audit ====================

        [BridgeRoute("POST", "/spatial/audit-overlaps", Category = "spatial", Description = "Detect bounds/collider intersections between GameObjects", ReadOnly = true, TimeoutDefault = 15000, TimeoutMin = 500, TimeoutMax = 60000)]
        public static string AuditOverlaps(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null) return JsonError("Failed to parse audit overlaps request");

                var sw = Stopwatch.StartNew();

                int rootInstanceId = TryReadInt(request, "rootInstanceId", out var rootParsed) ? rootParsed : 0;
                var instanceIds = ExtractIntList(request, "instanceIds");
                bool includeChildren = ReadBool(request, "includeChildren", true);
                int maxPairs = Mathf.Clamp(TryReadInt(request, "maxPairs", out var maxParsed) ? maxParsed : 50, 1, 500);
                float minPenetration = TryReadFloatField(request, "minPenetration", out var minPenParsed) ? minPenParsed : 0.01f;
                bool ignoreSiblingGroups = ReadBool(request, "ignoreSiblingGroups", true);

                var objects = CollectGameObjectsForOverlapAudit(rootInstanceId, instanceIds, includeChildren);

                if (objects.Count > 500)
                    objects = objects.Take(500).ToList();

                var pairs = CollectOverlapPairs(objects, minPenetration, ignoreSiblingGroups);
                pairs.Sort((a, b) => b.penetrationDepth.CompareTo(a.penetrationDepth));
                if (pairs.Count > maxPairs)
                    pairs = pairs.Take(maxPairs).ToList();

                // Count collider coverage for confidence (check children too for compound shapes)
                int withColliders = 0;
                foreach (var go in objects)
                {
                    if (go.GetComponentInChildren<Collider>() != null)
                        withColliders++;
                }
                float colliderCoverage = objects.Count > 0 ? (float)withColliders / objects.Count : 0f;

                int criticalCount = 0, warningCount = 0, infoCount = 0;
                var overlaps = new List<object>();
                foreach (var pair in pairs)
                {
                    string severity;
                    if (pair.penetrationDepth >= 0.5f) { severity = "critical"; criticalCount++; }
                    else if (pair.penetrationDepth >= 0.1f) { severity = "warning"; warningCount++; }
                    else { severity = "info"; infoCount++; }

                    // Compute AABB overlap volume estimate
                    float volumeEstimate = 0f;
                    if (TryGetAggregateBounds(pair.objectA, false, out var bA, out _) &&
                        TryGetAggregateBounds(pair.objectB, false, out var bB, out _))
                    {
                        float ox = Mathf.Max(0f, Mathf.Min(bA.max.x, bB.max.x) - Mathf.Max(bA.min.x, bB.min.x));
                        float oy = Mathf.Max(0f, Mathf.Min(bA.max.y, bB.max.y) - Mathf.Max(bA.min.y, bB.min.y));
                        float oz = Mathf.Max(0f, Mathf.Min(bA.max.z, bB.max.z) - Mathf.Max(bA.min.z, bB.min.z));
                        volumeEstimate = ox * oy * oz;
                    }

                    overlaps.Add(new Dictionary<string, object>
                    {
                        { "objectA", new Dictionary<string, object>
                            {
                                { "instanceId", pair.objectA.GetInstanceID() },
                                { "name", pair.objectA.name },
                                { "path", GetHierarchyPath(pair.objectA.transform) }
                            }
                        },
                        { "objectB", new Dictionary<string, object>
                            {
                                { "instanceId", pair.objectB.GetInstanceID() },
                                { "name", pair.objectB.name },
                                { "path", GetHierarchyPath(pair.objectB.transform) }
                            }
                        },
                        { "penetrationDepth", Math.Round(pair.penetrationDepth, 4) },
                        { "penetrationDirection", new List<object>
                            {
                                Math.Round(pair.penetrationDirection.x, 3),
                                Math.Round(pair.penetrationDirection.y, 3),
                                Math.Round(pair.penetrationDirection.z, 3)
                            }
                        },
                        { "severity", severity },
                        { "overlapVolumeEstimateM3", Math.Round(volumeEstimate, 4) },
                        { "detectionMethod", pair.detectionMethod }
                    });
                }

                int totalPairsChecked = objects.Count * (objects.Count - 1) / 2;
                sw.Stop();

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "summary", new Dictionary<string, object>
                        {
                            { "totalObjectsChecked", objects.Count },
                            { "totalPairsChecked", totalPairsChecked },
                            { "overlapCount", pairs.Count },
                            { "criticalCount", criticalCount },
                            { "warningCount", warningCount },
                            { "infoCount", infoCount },
                            { "colliderCoverage", Math.Round(colliderCoverage, 2) }
                        }
                    },
                    { "verdict", new Dictionary<string, object>
                        {
                            { "healthy", pairs.Count == 0 },
                            { "confidence", colliderCoverage >= 0.5f ? "high" : "low" }
                        }
                    },
                    { "overlaps", overlaps },
                    { "scanTimeMs", Math.Round(sw.Elapsed.TotalMilliseconds, 1) }
                };

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        // ==================== Resolve Overlaps ====================

        [BridgeRoute("POST", "/spatial/resolve-overlaps", Category = "spatial", Description = "Nudge overlapping objects apart", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string ResolveOverlaps(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null) return JsonError("Failed to parse resolve overlaps request");

                int rootInstanceId = TryReadInt(request, "rootInstanceId", out var rootParsed) ? rootParsed : 0;
                var instanceIds = ExtractIntList(request, "instanceIds");
                bool includeChildren = ReadBool(request, "includeChildren", true);
                float minimumGap = Mathf.Max(0f, TryReadFloatField(request, "minimumGap", out var gapParsed) ? gapParsed : 0f);
                bool keepOnTerrain = ReadBool(request, "keepOnTerrain", false);
                var preserveIds = ExtractIntList(request, "preserveInstanceIds");
                int maxIterations = Mathf.Clamp(TryReadInt(request, "maxIterations", out var iterParsed) ? iterParsed : 10, 1, 64);
                float nudgeStep = Mathf.Clamp(TryReadFloatField(request, "nudgeStep", out var nudgeParsed) ? nudgeParsed : 0.05f, 0.0001f, 1f);
                bool ignoreSiblingGroups = ReadBool(request, "ignoreSiblingGroups", true);
                bool autoSaveScene = ReadBool(request, "autoSaveScene", false);

                var preserveSet = new HashSet<int>(preserveIds);
                var objects = CollectGameObjectsForOverlapAudit(rootInstanceId, instanceIds, includeChildren);

                if (objects.Count < 2)
                    return JsonError("Need at least 2 objects to resolve overlaps");

                if (objects.Count > 500)
                    objects = objects.Take(500).ToList();

                // Effective threshold: objects must be at least minimumGap apart
                float effectiveMinPen = minimumGap > 0f ? -minimumGap : 0.001f;

                // Capture before metrics
                var pairsBefore = CollectOverlapPairs(objects, effectiveMinPen, ignoreSiblingGroups);
                int overlapCountBefore = pairsBefore.Count;
                float maxDepthBefore = pairsBefore.Count > 0 ? pairsBefore.Max(p => p.penetrationDepth) : 0f;

                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Resolve Overlaps");
                int undoGroup = Undo.GetCurrentGroup();

                var movedObjects = new Dictionary<int, (string name, string path, Vector3 before, Vector3 after)>();
                int iterationsUsed = 0;

                for (int iter = 0; iter < maxIterations; iter++)
                {
                    var pairs = CollectOverlapPairs(objects, effectiveMinPen, ignoreSiblingGroups);
                    if (pairs.Count == 0) break;
                    iterationsUsed = iter + 1;

                    var recordedThisIteration = new HashSet<int>();

                    foreach (var pair in pairs)
                    {
                        int idA = pair.objectA.GetInstanceID();
                        int idB = pair.objectB.GetInstanceID();
                        bool preserveA = preserveSet.Contains(idA);
                        bool preserveB = preserveSet.Contains(idB);
                        if (preserveA && preserveB) continue;

                        var dir = pair.penetrationDirection;
                        if (dir.sqrMagnitude < 0.0001f)
                            dir = (pair.objectB.transform.position - pair.objectA.transform.position).normalized;
                        if (dir.sqrMagnitude < 0.0001f)
                            dir = Vector3.right;

                        float push = Mathf.Max(nudgeStep, pair.penetrationDepth * 0.5f + nudgeStep);
                        if (minimumGap > 0f)
                            push += minimumGap * 0.5f;

                        var shift = dir * push;

                        if (!preserveA)
                        {
                            if (recordedThisIteration.Add(idA))
                            {
                                if (!movedObjects.ContainsKey(idA))
                                    movedObjects[idA] = (pair.objectA.name, GetHierarchyPath(pair.objectA.transform), pair.objectA.transform.position, Vector3.zero);
                                Undo.RecordObject(pair.objectA.transform, "Resolve Overlaps");
                            }
                            pair.objectA.transform.position -= preserveB ? shift : shift * 0.5f;
                        }

                        if (!preserveB)
                        {
                            if (recordedThisIteration.Add(idB))
                            {
                                if (!movedObjects.ContainsKey(idB))
                                    movedObjects[idB] = (pair.objectB.name, GetHierarchyPath(pair.objectB.transform), pair.objectB.transform.position, Vector3.zero);
                                Undo.RecordObject(pair.objectB.transform, "Resolve Overlaps");
                            }
                            pair.objectB.transform.position += preserveA ? shift : shift * 0.5f;
                        }
                    }

                    // Terrain snap pass
                    if (keepOnTerrain)
                    {
                        foreach (var pair in pairs)
                        {
                            SnapToTerrain(pair.objectA, preserveSet);
                            SnapToTerrain(pair.objectB, preserveSet);
                        }
                    }
                }

                Undo.CollapseUndoOperations(undoGroup);

                // Update after positions
                var movedList = new List<object>();
                foreach (var kv in movedObjects)
                {
                    var go = EditorUtility.EntityIdToObject(kv.Key) as GameObject;
                    var after = go != null ? go.transform.position : kv.Value.after;
                    var before = kv.Value.before;
                    var delta = after - before;
                    if (delta.sqrMagnitude < 0.00001f) continue;
                    movedList.Add(new Dictionary<string, object>
                    {
                        { "instanceId", kv.Key },
                        { "name", kv.Value.name },
                        { "path", kv.Value.path },
                        { "before", new List<object> { Math.Round(before.x, 3), Math.Round(before.y, 3), Math.Round(before.z, 3) } },
                        { "after", new List<object> { Math.Round(after.x, 3), Math.Round(after.y, 3), Math.Round(after.z, 3) } },
                        { "delta", new List<object> { Math.Round(delta.x, 3), Math.Round(delta.y, 3), Math.Round(delta.z, 3) } }
                    });
                }

                if (movedList.Count > 0)
                    EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                // After metrics
                var pairsAfter = CollectOverlapPairs(objects, effectiveMinPen, ignoreSiblingGroups);
                int overlapCountAfter = pairsAfter.Count;
                float maxDepthAfter = pairsAfter.Count > 0 ? pairsAfter.Max(p => p.penetrationDepth) : 0f;

                bool sceneSaved = false;
                string sceneSaveError = null;
                if (autoSaveScene && movedList.Count > 0)
                    sceneSaved = TrySaveActiveScene(out sceneSaveError);

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "applied", movedList.Count > 0 },
                    { "movedObjectCount", movedList.Count },
                    { "movedObjects", movedList },
                    { "iterationsUsed", iterationsUsed },
                    { "metricsBefore", new Dictionary<string, object>
                        {
                            { "overlapCount", overlapCountBefore },
                            { "maxDepth", Math.Round(maxDepthBefore, 4) }
                        }
                    },
                    { "metricsAfter", new Dictionary<string, object>
                        {
                            { "overlapCount", overlapCountAfter },
                            { "maxDepth", Math.Round(maxDepthAfter, 4) }
                        }
                    },
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

        // ==================== Overlap Detection Kernel ====================

        private struct OverlapPairResult
        {
            public GameObject objectA;
            public GameObject objectB;
            public float penetrationDepth;
            public Vector3 penetrationDirection;
            public string detectionMethod;
        }

        private static List<OverlapPairResult> CollectOverlapPairs(
            List<GameObject> objects, float minPenetration, bool ignoreSiblingGroups)
        {
            var results = new List<OverlapPairResult>();
            if (objects == null || objects.Count < 2) return results;

            Physics.SyncTransforms();

            // Build caches
            var boundsCache = new Dictionary<int, Bounds>();
            var colliderCache = new Dictionary<int, Collider>();
            foreach (var go in objects)
            {
                if (go == null) continue;
                if (!TryGetAggregateBounds(go, false, out var b, out _)) continue;
                int id = go.GetInstanceID();
                boundsCache[id] = b;
                // For compound shapes, find the first enabled collider in hierarchy
                var col = go.GetComponent<Collider>();
                if (col == null || !col.enabled)
                    col = go.GetComponentInChildren<Collider>();
                if (col != null && col.enabled) colliderCache[id] = col;
            }

            for (int i = 0; i < objects.Count; i++)
            {
                var goA = objects[i];
                if (goA == null) continue;
                int idA = goA.GetInstanceID();
                if (!boundsCache.TryGetValue(idA, out var bA)) continue;

                for (int j = i + 1; j < objects.Count; j++)
                {
                    var goB = objects[j];
                    if (goB == null) continue;
                    int idB = goB.GetInstanceID();
                    if (!boundsCache.TryGetValue(idB, out var bB)) continue;

                    // Skip parent-child relationships
                    if (goA.transform.IsChildOf(goB.transform) || goB.transform.IsChildOf(goA.transform))
                        continue;

                    // Skip sibling groups
                    if (ignoreSiblingGroups && goA.transform.parent != null && goA.transform.parent == goB.transform.parent)
                        continue;

                    // Broad phase: AABB intersection
                    if (!bA.Intersects(bB)) continue;

                    float depth = 0f;
                    Vector3 dir = Vector3.zero;
                    string method = "bounds";

                    // Narrow phase: Physics.ComputePenetration
                    if (colliderCache.TryGetValue(idA, out var colA) && colliderCache.TryGetValue(idB, out var colB))
                    {
                        if (Physics.ComputePenetration(
                            colA, goA.transform.position, goA.transform.rotation,
                            colB, goB.transform.position, goB.transform.rotation,
                            out dir, out depth))
                        {
                            method = "physics";
                        }
                        else
                        {
                            depth = EstimateAabbOverlapDepth(bA, bB);
                            dir = (bB.center - bA.center).normalized;
                            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.right;
                        }
                    }
                    else
                    {
                        depth = EstimateAabbOverlapDepth(bA, bB);
                        dir = (bB.center - bA.center).normalized;
                        if (dir.sqrMagnitude < 0.0001f) dir = Vector3.right;
                    }

                    if (depth < minPenetration) continue;

                    results.Add(new OverlapPairResult
                    {
                        objectA = goA,
                        objectB = goB,
                        penetrationDepth = depth,
                        penetrationDirection = dir,
                        detectionMethod = method
                    });
                }
            }

            return results;
        }

        private static float EstimateAabbOverlapDepth(Bounds a, Bounds b)
        {
            float ox = Mathf.Min(a.max.x, b.max.x) - Mathf.Max(a.min.x, b.min.x);
            float oy = Mathf.Min(a.max.y, b.max.y) - Mathf.Max(a.min.y, b.min.y);
            float oz = Mathf.Min(a.max.z, b.max.z) - Mathf.Max(a.min.z, b.min.z);
            return Mathf.Min(Mathf.Max(ox, 0f), Mathf.Min(Mathf.Max(oy, 0f), Mathf.Max(oz, 0f)));
        }

        /// <summary>
        /// Collects GameObjects for overlap auditing at the LOGICAL ROOT level.
        /// For compound shapes (trees, buildings, etc.) with parent-child hierarchies,
        /// this returns the topmost parent rather than individual child parts (Canopy, Trunk, etc.).
        /// This ensures the resolver moves entire compound objects as a unit.
        /// </summary>
        private static List<GameObject> CollectGameObjectsForOverlapAudit(int rootInstanceId, List<int> instanceIds, bool includeChildren)
        {
            var result = new List<GameObject>();
            var seen = new HashSet<int>();

            if (instanceIds != null && instanceIds.Count > 0)
            {
                foreach (var id in instanceIds)
                {
                    var go = EditorUtility.EntityIdToObject(id) as GameObject;
                    if (go == null) continue;
                    // Explicit instance IDs are treated as logical roots directly
                    if (seen.Add(go.GetInstanceID()))
                        result.Add(go);
                }
            }
            else if (rootInstanceId != 0)
            {
                var rootGo = EditorUtility.EntityIdToObject(rootInstanceId) as GameObject;
                if (rootGo != null)
                {
                    CollectLogicalRoots(rootGo.transform, seen, result, includeChildren);
                }
            }
            else
            {
                var scene = SceneManager.GetActiveScene();
                if (scene.IsValid() && scene.isLoaded)
                {
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        CollectLogicalRoots(root.transform, seen, result, includeChildren);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Collects logical root GameObjects under a scope transform.
        /// A "logical root" is a direct child of the scope that has renderers in its hierarchy.
        /// For scene-root scope, each root GameObject with renderers is a logical root.
        /// This prevents compound shape children (Canopy, Trunk) from being treated as separate objects.
        /// </summary>
        private static void CollectLogicalRoots(Transform scope, HashSet<int> seen, List<GameObject> result, bool includeChildren)
        {
            if (!includeChildren)
            {
                // Only check the scope object itself
                if (scope.gameObject.GetComponent<Renderer>() != null && seen.Add(scope.gameObject.GetInstanceID()))
                    result.Add(scope.gameObject);
                return;
            }

            // Each direct child of scope is a potential logical root (compound shape parent).
            // If a child has any renderer in its subtree, add the child (not the individual renderers).
            for (int i = 0; i < scope.childCount; i++)
            {
                var child = scope.GetChild(i);
                var go = child.gameObject;
                if (go == null || !go.activeInHierarchy) continue;

                // Check if this object (or any descendant) has a renderer
                bool hasRenderer = go.GetComponentInChildren<Renderer>(false) != null;
                if (hasRenderer && seen.Add(go.GetInstanceID()))
                    result.Add(go);
            }

            // Also check if the scope itself has a renderer directly (no children scenario)
            if (scope.GetComponent<Renderer>() != null && scope.childCount == 0 && seen.Add(scope.gameObject.GetInstanceID()))
                result.Add(scope.gameObject);
        }

        private static void SnapToTerrain(GameObject go, HashSet<int> preserveSet)
        {
            if (go == null || preserveSet.Contains(go.GetInstanceID())) return;
            var pos = go.transform.position;
            if (Physics.Raycast(new Vector3(pos.x, pos.y + 10f, pos.z), Vector3.down, out RaycastHit hit, 20f))
            {
                go.transform.position = new Vector3(pos.x, hit.point.y, pos.z);
            }
        }
    }
}
