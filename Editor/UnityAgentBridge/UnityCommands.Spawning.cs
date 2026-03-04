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
        #region Path Spawning Methods

        [BridgeRoute("POST", "/scene/spawn-along-path", Category = "scene", Description = "Spawn prefabs along a path", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string SpawnAlongPath(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null)
                    return JsonError("Failed to parse spawn-along-path request");

                // Parse control points
                var cpRaw = request.ContainsKey("controlPoints") ? request["controlPoints"] as List<object> : null;
                if (cpRaw == null || cpRaw.Count < 2)
                    return JsonError("controlPoints must have at least 2 points");

                var controlPoints = new Vector3[cpRaw.Count];
                for (int i = 0; i < cpRaw.Count; i++)
                {
                    var pt = cpRaw[i] as List<object>;
                    if (pt == null || pt.Count < 3)
                        return JsonError($"controlPoints[{i}] must have 3 values [x,y,z]");
                    controlPoints[i] = new Vector3(
                        Convert.ToSingle(pt[0]),
                        Convert.ToSingle(pt[1]),
                        Convert.ToSingle(pt[2]));
                }

                // Resolve primitive type (alternative to prefab)
                PrimitiveType? primitiveType = null;
                if (request.ContainsKey("primitiveType") && request["primitiveType"] is string ptStr && !string.IsNullOrWhiteSpace(ptStr))
                {
                    if (Enum.TryParse<PrimitiveType>(ptStr, true, out var pt))
                        primitiveType = pt;
                    else
                        return JsonError($"Unknown primitiveType: '{ptStr}'. Valid types: Cube, Sphere, Cylinder, Capsule, Plane, Quad");
                }

                // Resolve prefab paths
                var prefabPathsList = new List<string>();
                if (primitiveType == null)
                {
                    if (request.ContainsKey("prefabPaths") && request["prefabPaths"] is List<object> ppList)
                    {
                        foreach (var p in ppList)
                            if (p != null) prefabPathsList.Add(p.ToString());
                    }
                    else if (request.ContainsKey("prefabPath") && request["prefabPath"] != null)
                    {
                        prefabPathsList.Add(request["prefabPath"].ToString());
                    }
                    if (prefabPathsList.Count == 0)
                        return JsonError("Either prefabPath, prefabPaths, or primitiveType is required");
                }

                // Load prefabs and validate (skip when using primitiveType)
                var prefabs = new GameObject[Math.Max(1, prefabPathsList.Count)];
                for (int i = 0; i < prefabPathsList.Count; i++)
                {
                    prefabs[i] = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPathsList[i]);
                    if (prefabs[i] == null)
                        return JsonError($"Prefab not found: {prefabPathsList[i]}");
                }

                // Resolve material paths
                var materialPathsList = new List<string>();
                if (request.ContainsKey("materialPaths") && request["materialPaths"] is List<object> mpList)
                {
                    foreach (var m in mpList)
                        if (m != null) materialPathsList.Add(m.ToString());
                }
                else if (request.ContainsKey("materialPath") && request["materialPath"] != null)
                {
                    materialPathsList.Add(request["materialPath"].ToString());
                }

                // Load materials
                var materials = new Material[materialPathsList.Count];
                for (int i = 0; i < materialPathsList.Count; i++)
                {
                    materials[i] = AssetDatabase.LoadAssetAtPath<Material>(materialPathsList[i]);
                    if (materials[i] == null)
                        return JsonError($"Material not found: {materialPathsList[i]}");
                }

                // Parse params
                string interpolation = request.ContainsKey("interpolation") ? (request["interpolation"]?.ToString() ?? "catmull-rom") : "catmull-rom";
                bool alignToPath = !request.ContainsKey("alignToPath") || ReadBool(request, "alignToPath", true);
                float randomYRot = request.ContainsKey("randomYRotation") ? Convert.ToSingle(request["randomYRotation"]) : 0f;
                string parentName = request.ContainsKey("parentName") ? request["parentName"]?.ToString() : null;
                int laneCount = request.ContainsKey("laneCount") ? Convert.ToInt32(request["laneCount"]) : 1;
                laneCount = Mathf.Max(1, laneCount);
                float laneSpacing = request.ContainsKey("laneSpacing") ? Convert.ToSingle(request["laneSpacing"]) : 0f;
                string lanePattern = request.ContainsKey("lanePattern") ? (request["lanePattern"]?.ToString() ?? "aligned") : "aligned";
                float targetMinEdgeGap = request.ContainsKey("targetMinEdgeGap") ? Convert.ToSingle(request["targetMinEdgeGap"]) : -1f;
                float curvatureWidening = request.ContainsKey("curvatureWidening") ? Convert.ToSingle(request["curvatureWidening"]) : 0f;
                bool autoResolveOverlaps = request.ContainsKey("autoResolveOverlaps")
                    ? ReadBool(request, "autoResolveOverlaps", false)
                    : laneCount > 1;
                int resolveMaxIterations = request.ContainsKey("resolveMaxIterations")
                    ? Mathf.Clamp(Convert.ToInt32(request["resolveMaxIterations"]), 1, 64)
                    : 10;
                float resolveNudgeStep = request.ContainsKey("resolveNudgeStep")
                    ? Mathf.Clamp(Convert.ToSingle(request["resolveNudgeStep"]), 0.0001f, 0.2f)
                    : 0.0025f;
                bool autoSaveScene = request.ContainsKey("autoSaveScene") && ReadBool(request, "autoSaveScene", false);

                Vector3 offsetVec = Vector3.zero;
                if (request.ContainsKey("offset") && request["offset"] is List<object> offList && offList.Count >= 3)
                {
                    offsetVec = new Vector3(
                        Convert.ToSingle(offList[0]),
                        Convert.ToSingle(offList[1]),
                        Convert.ToSingle(offList[2]));
                }

                // Auto-compute laneSpacing from prefab bounds if targetMinEdgeGap is set
                if (targetMinEdgeGap >= 0f && laneCount > 1 && laneSpacing <= 0f)
                {
                    var renderer = prefabs[0].GetComponentInChildren<Renderer>();
                    if (renderer != null)
                    {
                        float boundsWidth = renderer.bounds.size.x;
                        laneSpacing = boundsWidth + targetMinEdgeGap;
                    }
                    else
                    {
                        laneSpacing = 1.5f; // fallback
                    }
                }

                // Compute path length and spawn count
                float pathLength = ComputePathLength(controlPoints, interpolation);
                float spacing = 0f;
                int spawnCount = 0;

                if (request.ContainsKey("count"))
                {
                    spawnCount = Convert.ToInt32(request["count"]);
                    if (spawnCount > 0 && pathLength > 0f)
                        spacing = pathLength / Mathf.Max(1, spawnCount - 1);
                }
                else if (request.ContainsKey("spacing"))
                {
                    spacing = Convert.ToSingle(request["spacing"]);
                    if (spacing > 0f)
                        spawnCount = Mathf.Max(1, Mathf.FloorToInt(pathLength / spacing) + 1);
                }
                else if (targetMinEdgeGap >= 0f)
                {
                    // Auto-compute forward spacing from prefab bounds
                    var autoRenderer = prefabs[0].GetComponentInChildren<Renderer>();
                    if (autoRenderer != null)
                    {
                        float boundsDepth = autoRenderer.bounds.size.z;
                        spacing = boundsDepth + targetMinEdgeGap;
                        spawnCount = Mathf.Max(1, Mathf.FloorToInt(pathLength / spacing) + 1);
                    }
                }

                if (spacing <= 0f && spawnCount <= 0)
                {
                    return JsonError("Either spacing, count, or targetMinEdgeGap is required");
                }

                if (spawnCount <= 0)
                    return JsonError("Computed spawn count is 0. Check spacing or count params.");

                // Create parent container
                Undo.SetCurrentGroupName("Spawn Along Path");
                int undoGroup = Undo.GetCurrentGroup();

                GameObject parentGo = null;
                if (!string.IsNullOrEmpty(parentName))
                {
                    parentGo = new GameObject(parentName);
                    Undo.RegisterCreatedObjectUndo(parentGo, "Create Path Container");
                }

                var instanceIds = new List<int>();
                var rng = randomYRot > 0f ? new System.Random(42) : null;

                for (int lane = 0; lane < laneCount; lane++)
                {
                    // Lane offset centered on path
                    float laneOffset = (lane - (laneCount - 1) / 2f) * laneSpacing;

                    // Stagger offset for odd lanes
                    float staggerOffset = 0f;
                    if (lanePattern == "stagger" && lane % 2 == 1 && spacing > 0f)
                        staggerOffset = spacing * 0.5f;

                    for (int i = 0; i < spawnCount; i++)
                    {
                        // Parameter t along path — use actual spacing for consistent tile distances
                        float dist = (spawnCount <= 1) ? 0f : (i * spacing);
                        dist += staggerOffset;
                        float t = Mathf.Clamp01(dist / Mathf.Max(0.001f, pathLength));

                        // Evaluate path position and tangent
                        Vector3 pathPos = EvaluatePathPosition(controlPoints, interpolation, t);
                        Vector3 tangent = EvaluatePathTangent(controlPoints, interpolation, t);
                        if (tangent.sqrMagnitude < 0.0001f) tangent = Vector3.forward;
                        tangent.Normalize();

                        // Cross vector for lane offset
                        Vector3 cross = Vector3.Cross(tangent, Vector3.up).normalized;
                        if (cross.sqrMagnitude < 0.001f)
                            cross = Vector3.Cross(tangent, Vector3.right).normalized;

                        // Apply curvature widening
                        float effectiveLaneOffset = laneOffset;
                        if (curvatureWidening > 0f && Mathf.Abs(laneOffset) > 0.001f)
                        {
                            float curvature = EstimateCurvature(controlPoints, interpolation, t, 0.01f, pathLength);
                            effectiveLaneOffset += laneOffset * curvature * curvatureWidening;
                        }

                        Vector3 finalPos = pathPos + cross * effectiveLaneOffset + offsetVec;

                        // Select prefab (cycle through) or create primitive
                        int globalIndex = lane * spawnCount + i;
                        GameObject go;
                        if (primitiveType != null)
                        {
                            go = GameObject.CreatePrimitive(primitiveType.Value);
                            Undo.RegisterCreatedObjectUndo(go, "Spawn Primitive Along Path");
                        }
                        else
                        {
                            var prefab = prefabs[globalIndex % prefabs.Length];
                            go = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                            if (go == null) continue;
                        }

                        go.transform.position = finalPos;

                        // Rotation
                        if (alignToPath)
                        {
                            go.transform.rotation = Quaternion.LookRotation(tangent, Vector3.up);
                        }
                        if (randomYRot > 0f && rng != null)
                        {
                            float jitter = (float)(rng.NextDouble() * 2.0 - 1.0) * randomYRot;
                            go.transform.Rotate(0f, jitter, 0f, Space.Self);
                        }

                        // Apply material override
                        if (materials.Length > 0)
                        {
                            var mat = materials[globalIndex % materials.Length];
                            var renderer = go.GetComponentInChildren<Renderer>();
                            if (renderer != null && mat != null)
                            {
                                renderer.sharedMaterial = mat;
                            }
                        }

                        // Parent
                        if (parentGo != null)
                        {
                            go.transform.SetParent(parentGo.transform, true);
                        }

                        Undo.RegisterCreatedObjectUndo(go, "Spawn Path Object");
                        instanceIds.Add(go.GetInstanceID());
                    }
                }

                Undo.CollapseUndoOperations(undoGroup);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                Dictionary<string, object> overlapResolution = null;
                if (autoResolveOverlaps && instanceIds.Count > 1)
                {
                    var resolveRequest = new Dictionary<string, object>
                    {
                        { "instanceIds", instanceIds.Cast<object>().ToList() },
                        { "includeInactive", 0 },
                        { "targetMinEdgeGap", targetMinEdgeGap >= 0f ? targetMinEdgeGap : 0.02f },
                        { "maxIterations", resolveMaxIterations },
                        { "nudgeStep", resolveNudgeStep },
                        { "autoSaveScene", 0 }
                    };

                    var resolveJson = ResolveTileOverlaps(MiniJSON.Json.Serialize(resolveRequest));
                    var resolveParsed = MiniJSON.Json.Deserialize(resolveJson) as Dictionary<string, object>;
                    if (resolveParsed != null)
                    {
                        overlapResolution = resolveParsed;
                    }
                }

                bool sceneSaved = false;
                string sceneSaveError = null;
                if (autoSaveScene)
                {
                    sceneSaved = TrySaveActiveScene(out sceneSaveError);
                }

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "count", instanceIds.Count },
                    { "laneCount", laneCount },
                    { "spawnCountPerLane", spawnCount },
                    { "pathLength", (double)Math.Round(pathLength, 4) },
                    { "instanceIds", instanceIds.Cast<object>().ToList() },
                    { "autoResolveOverlaps", autoResolveOverlaps },
                    { "autoSaveScene", autoSaveScene },
                    { "sceneSaved", sceneSaved }
                };
                if (parentGo != null)
                    result["parentInstanceId"] = parentGo.GetInstanceID();
                if (laneSpacing > 0f)
                    result["computedLaneSpacing"] = (double)Math.Round(laneSpacing, 4);
                if (overlapResolution != null)
                    result["overlapResolution"] = overlapResolution;
                if (!string.IsNullOrWhiteSpace(sceneSaveError))
                    result["sceneSaveError"] = sceneSaveError;

                return JsonResult(result);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        // ---- Spline Helpers ----

        private static Vector3 EvaluateCatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        private static Vector3 EvaluateCatmullRomTangent(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            return 0.5f * (
                (-p0 + p2) +
                (4f * p0 - 10f * p1 + 8f * p2 - 2f * p3) * t +
                (-3f * p0 + 9f * p1 - 9f * p2 + 3f * p3) * t2);
        }

        private static float ComputePathLength(Vector3[] points, string interpolation, int samples = 200)
        {
            float totalLength = 0f;
            Vector3 prev = EvaluatePathPosition(points, interpolation, 0f);
            for (int i = 1; i <= samples; i++)
            {
                float t = i / (float)samples;
                Vector3 cur = EvaluatePathPosition(points, interpolation, t);
                totalLength += Vector3.Distance(prev, cur);
                prev = cur;
            }
            return totalLength;
        }

        private static Vector3 EvaluatePathPosition(Vector3[] points, string interpolation, float t)
        {
            int n = points.Length;
            if (n < 2) return points[0];

            if (interpolation == "linear")
            {
                float scaledT = t * (n - 1);
                int segIndex = Mathf.Clamp(Mathf.FloorToInt(scaledT), 0, n - 2);
                float segT = scaledT - segIndex;
                return Vector3.Lerp(points[segIndex], points[segIndex + 1], segT);
            }

            // Catmull-Rom with endpoint duplication
            int segments = n - 1;
            float totalT = t * segments;
            int seg = Mathf.Clamp(Mathf.FloorToInt(totalT), 0, segments - 1);
            float localT = totalT - seg;

            Vector3 p0 = points[Mathf.Max(0, seg - 1)];
            Vector3 p1 = points[seg];
            Vector3 p2 = points[Mathf.Min(n - 1, seg + 1)];
            Vector3 p3 = points[Mathf.Min(n - 1, seg + 2)];

            return EvaluateCatmullRom(p0, p1, p2, p3, localT);
        }

        private static Vector3 EvaluatePathTangent(Vector3[] points, string interpolation, float t)
        {
            int n = points.Length;
            if (n < 2) return Vector3.forward;

            if (interpolation == "linear")
            {
                float scaledT = t * (n - 1);
                int segIndex = Mathf.Clamp(Mathf.FloorToInt(scaledT), 0, n - 2);
                return (points[segIndex + 1] - points[segIndex]).normalized;
            }

            // Catmull-Rom tangent
            int segments = n - 1;
            float totalT = t * segments;
            int seg = Mathf.Clamp(Mathf.FloorToInt(totalT), 0, segments - 1);
            float localT = totalT - seg;

            Vector3 p0 = points[Mathf.Max(0, seg - 1)];
            Vector3 p1 = points[seg];
            Vector3 p2 = points[Mathf.Min(n - 1, seg + 1)];
            Vector3 p3 = points[Mathf.Min(n - 1, seg + 2)];

            return EvaluateCatmullRomTangent(p0, p1, p2, p3, localT);
        }

        private static float EstimateCurvature(Vector3[] points, string interpolation, float t, float dt = 0.01f, float precomputedPathLength = -1f)
        {
            float t0 = Mathf.Max(0f, t - dt);
            float t1 = Mathf.Min(1f, t + dt);
            Vector3 tan0 = EvaluatePathTangent(points, interpolation, t0).normalized;
            Vector3 tan1 = EvaluatePathTangent(points, interpolation, t1).normalized;
            float angle = Vector3.Angle(tan0, tan1);
            float totalPathLength = precomputedPathLength > 0f ? precomputedPathLength : ComputePathLength(points, interpolation, 50);
            float arcLen = (t1 - t0) * totalPathLength;
            return arcLen > 0.001f ? (angle * Mathf.Deg2Rad) / arcLen : 0f;
        }

        #endregion
    }
}
