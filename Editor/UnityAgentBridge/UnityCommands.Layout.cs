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
        [BridgeRoute("POST", "/scene/layout-solve", Category = "scene", Description = "Solve layout constraints", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string SolveLayoutConstraints(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null)
                {
                    return JsonError("Failed to parse layout solver request");
                }

                if (!(request.TryGetValue("constraints", out var constraintsObj) && constraintsObj is IList<object> constraints && constraints.Count > 0))
                {
                    return JsonError("constraints array is required");
                }

                int iterations = Mathf.Clamp(TryReadInt(request, "iterations", out var parsedIterations) ? parsedIterations : 12, 1, 64);
                float step = Mathf.Clamp(TryReadFloatField(request, "step", out var parsedStep) ? parsedStep : 0.35f, 0.01f, 1f);
                float damping = Mathf.Clamp(TryReadFloatField(request, "damping", out var parsedDamping) ? parsedDamping : 0.85f, 0.1f, 1f);
                bool apply = ReadBool(request, "apply", false);
                bool includeInactive = ReadBool(request, "includeInactive", false);
                bool autoSaveScene = ReadBool(request, "autoSaveScene", false);
                string fields = ReadString(request, "fields");
                bool omitEmpty = ReadBool(request, "omitEmpty", false);
                int maxItems = TryReadInt(request, "maxItems", out var parsedMaxItems) ? parsedMaxItems : -1;

                var scopeIds = ExtractIntList(request, "instanceIds");
                var referencedIds = ExtractConstraintInstanceIds(constraints);
                var allIds = scopeIds
                    .Concat(referencedIds)
                    .Where(id => id != 0)
                    .Distinct()
                    .ToList();

                if (allIds.Count == 0)
                {
                    return JsonError("No object instance IDs found. Provide instanceIds and/or constraints referencing valid IDs.");
                }

                var skippedIds = new List<object>();
                var nodes = new Dictionary<int, LayoutNodeState>();
                foreach (var id in allIds)
                {
                    var go = EditorUtility.EntityIdToObject(id) as GameObject;
                    if (go == null)
                    {
                        skippedIds.Add(id);
                        continue;
                    }

                    if (!includeInactive && !go.activeInHierarchy)
                    {
                        skippedIds.Add(id);
                        continue;
                    }

                    if (!TryGetAggregateBounds(go, includeInactive, out var bounds, out _))
                    {
                        bounds = new Bounds(go.transform.position, Vector3.one * 0.1f);
                    }

                    nodes[id] = new LayoutNodeState
                    {
                        instanceId = id,
                        go = go,
                        transform = go.transform,
                        originalPosition = go.transform.position,
                        position = go.transform.position,
                        extents = bounds.extents,
                        name = go.name,
                        path = GetHierarchyPath(go.transform)
                    };
                }

                if (nodes.Count == 0)
                {
                    return JsonError("No valid objects resolved from instance IDs");
                }

                var beforeEvaluations = EvaluateLayoutConstraints(constraints, nodes);

                int iterationsUsed = 0;
                int adjustmentCount = 0;
                for (int i = 0; i < iterations; i++)
                {
                    float gain = step * Mathf.Pow(damping, i);
                    bool changed = ApplyLayoutConstraintPass(constraints, nodes, gain, ref adjustmentCount);
                    iterationsUsed = i + 1;
                    if (!changed)
                    {
                        break;
                    }
                }

                var afterEvaluations = EvaluateLayoutConstraints(constraints, nodes);
                var beforeSummary = BuildLayoutViolationSummary(beforeEvaluations);
                var afterSummary = BuildLayoutViolationSummary(afterEvaluations);

                var movedNodes = nodes.Values
                    .Where(n => (n.position - n.originalPosition).sqrMagnitude > 0.0000001f)
                    .OrderByDescending(n => (n.position - n.originalPosition).sqrMagnitude)
                    .ToList();

                bool sceneSaved = false;
                string sceneSaveError = null;
                if (apply && movedNodes.Count > 0)
                {
                    Undo.IncrementCurrentGroup();
                    int undoGroup = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName("Solve Layout Constraints");

                    foreach (var node in movedNodes)
                    {
                        Undo.RecordObject(node.transform, "Solve Layout Constraints");
                        node.transform.position = node.position;
                        EditorUtility.SetDirty(node.transform);
                    }

                    Undo.CollapseUndoOperations(undoGroup);
                    EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                    if (autoSaveScene)
                    {
                        sceneSaved = TrySaveActiveScene(out sceneSaveError);
                    }
                }

                var movedPayload = movedNodes
                    .Select(node =>
                    {
                        var delta = node.position - node.originalPosition;
                        return (object)new Dictionary<string, object>
                        {
                            { "instanceId", node.instanceId },
                            { "name", node.name ?? string.Empty },
                            { "path", node.path ?? string.Empty },
                            { "before", new List<object> { (double)Math.Round(node.originalPosition.x, 4), (double)Math.Round(node.originalPosition.y, 4), (double)Math.Round(node.originalPosition.z, 4) } },
                            { "after", new List<object> { (double)Math.Round(node.position.x, 4), (double)Math.Round(node.position.y, 4), (double)Math.Round(node.position.z, 4) } },
                            { "delta", new List<object> { (double)Math.Round(delta.x, 4), (double)Math.Round(delta.y, 4), (double)Math.Round(delta.z, 4) } }
                        };
                    })
                    .ToList();

                var patchOps = movedNodes
                    .Select(node => (object)new Dictionary<string, object>
                    {
                        { "op", "modify_gameobject" },
                        { "payload", new Dictionary<string, object>
                            {
                                { "instanceId", node.instanceId },
                                { "position", new List<object> { node.position.x, node.position.y, node.position.z } }
                            }
                        }
                    })
                    .ToList();

                bool improved = Convert.ToDouble(afterSummary["totalViolation"]) < Convert.ToDouble(beforeSummary["totalViolation"]);
                int beforeUnsatisfied = Convert.ToInt32(beforeSummary["unsatisfiedCount"]);
                int afterUnsatisfied = Convert.ToInt32(afterSummary["unsatisfiedCount"]);

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "applied", apply },
                    { "iterationsRequested", iterations },
                    { "iterationsUsed", iterationsUsed },
                    { "step", step },
                    { "damping", damping },
                    { "constraintCount", constraints.Count },
                    { "objectCount", nodes.Count },
                    { "skippedObjectCount", skippedIds.Count },
                    { "skippedInstanceIds", skippedIds },
                    { "adjustmentCount", adjustmentCount },
                    { "movedObjectCount", movedNodes.Count },
                    { "improved", improved },
                    { "violationsBefore", beforeSummary },
                    { "violationsAfter", afterSummary },
                    { "unsatisfiedBefore", beforeUnsatisfied },
                    { "unsatisfiedAfter", afterUnsatisfied },
                    { "evaluationsBefore", beforeEvaluations.Cast<object>().ToList() },
                    { "evaluationsAfter", afterEvaluations.Cast<object>().ToList() },
                    { "movedObjects", movedPayload },
                    { "recommendedPatch", new Dictionary<string, object>
                        {
                            { "brief", true },
                            { "diffOnly", true },
                            { "operations", patchOps }
                        }
                    },
                    { "autoSaveScene", autoSaveScene },
                    { "sceneSaved", sceneSaved }
                };

                if (!string.IsNullOrWhiteSpace(sceneSaveError))
                {
                    response["sceneSaveError"] = sceneSaveError;
                }

                response = ApplyResponseProjection(response, fields, omitEmpty, maxItems);
                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/scene/tile-separation", Category = "scene", Description = "Measure tile edge gaps/overlaps", ReadOnly = true, TimeoutDefault = 15000)]
        public static string MeasureTileSeparation(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null)
                {
                    return JsonError("Failed to parse tile separation request");
                }

                bool includeInactive = ReadBool(request, "includeInactive", false);
                bool includeClosestPairs = ReadBool(request, "includeClosestPairs", false);
                bool captureScreenshot = ReadBool(request, "captureScreenshot", true);
                string screenshotView = ReadString(request, "screenshotView") ?? "game";
                int maxTiles = Mathf.Clamp(TryReadInt(request, "maxTiles", out var maxTilesParsed) ? maxTilesParsed : 600, 10, 1500);
                float targetMinEdgeGap = Mathf.Max(0f, TryReadFloatField(request, "targetMinEdgeGap", out var gapParsed) ? gapParsed : 0.04f);
                float brightThreshold = Mathf.Clamp(TryReadFloatField(request, "brightThreshold", out var brightParsed) ? brightParsed : 0.92f, 0.05f, 0.99f);
                float glowThreshold = Mathf.Clamp(TryReadFloatField(request, "glowThreshold", out var glowParsed) ? glowParsed : 0.35f, 0.01f, brightThreshold - 0.01f);
                string fields = ReadString(request, "fields");
                bool omitEmpty = ReadBool(request, "omitEmpty", false);
                int maxItems = TryReadInt(request, "maxItems", out var maxItemsParsed) ? maxItemsParsed : -1;

                var explicitInstanceIds = ExtractIntList(request, "instanceIds");
                int rootInstanceId = TryReadInt(request, "rootInstanceId", out var rootParsed) ? rootParsed : 0;
                string rootName = ReadString(request, "rootName");
                if (string.IsNullOrWhiteSpace(rootName))
                {
                    rootName = ReadString(request, "tileRootName");
                }

                if ((explicitInstanceIds == null || explicitInstanceIds.Count == 0)
                    && rootInstanceId == 0
                    && string.IsNullOrWhiteSpace(rootName))
                {
                    return JsonError("Provide rootInstanceId, rootName/tileRootName, or instanceIds");
                }

                if (!TryCollectTileBoundsSamples(
                    rootInstanceId,
                    explicitInstanceIds,
                    includeInactive,
                    maxTiles,
                    rootName,
                    out var samples,
                    out var skippedCount,
                    out var selectionInfo,
                    out var collectError))
                {
                    return JsonError(collectError);
                }

                if (samples.Count < 2)
                {
                    return JsonError("Need at least 2 tile objects with renderer/collider bounds");
                }

                float minEdgeDistance = float.MaxValue;
                float maxOverlapDepth = 0f;
                int overlapPairCount = 0;
                var nearestDistances = new float[samples.Count];
                var nearestOrientation = new int[samples.Count]; // 1 = lane-ish, 2 = forward-ish
                var closestPairs = new List<Dictionary<string, object>>();
                var lateralDistances = new List<float>();
                var forwardDistances = new List<float>();

                for (int i = 0; i < samples.Count; i++)
                {
                    nearestDistances[i] = float.MaxValue;
                    nearestOrientation[i] = 0;
                }

                for (int i = 0; i < samples.Count; i++)
                {
                    var a = samples[i];
                    for (int j = i + 1; j < samples.Count; j++)
                    {
                        var b = samples[j];
                        float dxGap;
                        float dzGap;
                        float signedDistance = ComputeSignedAabbDistance2D(a, b, out dxGap, out dzGap);
                        float absDx = Mathf.Abs(a.centerXZ.x - b.centerXZ.x);
                        float absDz = Mathf.Abs(a.centerXZ.y - b.centerXZ.y);
                        int orientation = absDx >= absDz ? 1 : 2;

                        if (signedDistance < nearestDistances[i])
                        {
                            nearestDistances[i] = signedDistance;
                            nearestOrientation[i] = orientation;
                        }

                        if (signedDistance < nearestDistances[j])
                        {
                            nearestDistances[j] = signedDistance;
                            nearestOrientation[j] = orientation;
                        }

                        if (signedDistance < minEdgeDistance)
                        {
                            minEdgeDistance = signedDistance;
                        }

                        if (signedDistance < 0f)
                        {
                            overlapPairCount++;
                            maxOverlapDepth = Mathf.Max(maxOverlapDepth, -signedDistance);
                        }
                    }
                }

                for (int i = 0; i < nearestDistances.Length; i++)
                {
                    var d = nearestDistances[i];
                    if (float.IsPositiveInfinity(d) || d == float.MaxValue)
                    {
                        continue;
                    }

                    if (nearestOrientation[i] == 1)
                    {
                        lateralDistances.Add(d);
                    }
                    else if (nearestOrientation[i] == 2)
                    {
                        forwardDistances.Add(d);
                    }
                }

                float avgNearestEdgeDistance = nearestDistances.Where(v => !float.IsInfinity(v) && v != float.MaxValue).DefaultIfEmpty(0f).Average();
                float medianNearestEdgeDistance = ComputeMedian(nearestDistances.Where(v => !float.IsInfinity(v) && v != float.MaxValue));
                float estimatedLaneGap = ComputeMedian(lateralDistances.Where(v => v >= 0f));
                float estimatedForwardGap = ComputeMedian(forwardDistances.Where(v => v >= 0f));
                var nonOverlapNearest = nearestDistances.Where(v => !float.IsInfinity(v) && v != float.MaxValue && v >= 0f).ToList();
                var positiveLateral = lateralDistances.Where(v => v >= 0f).ToList();
                var positiveForward = forwardDistances.Where(v => v >= 0f).ToList();
                float p10NearestEdgeDistance = ComputePercentile(nonOverlapNearest, 0.10f);
                float p25NearestEdgeDistance = ComputePercentile(nonOverlapNearest, 0.25f);
                float p75NearestEdgeDistance = ComputePercentile(nonOverlapNearest, 0.75f);
                float minLaneGap = positiveLateral.DefaultIfEmpty(0f).Min();
                float medianLaneGap = ComputeMedian(positiveLateral);
                float minForwardGap = positiveForward.DefaultIfEmpty(0f).Min();
                float medianForwardGap = ComputeMedian(positiveForward);

                float medianTileWidth = ComputeMedian(samples.Select(s => s.sizeXZ.x));
                float medianTileDepth = ComputeMedian(samples.Select(s => s.sizeXZ.y));

                float curvatureMax;
                float curvatureNormalized;
                EstimateCurvature(samples, out curvatureMax, out curvatureNormalized);
                float recommendedCurvatureWidening = Mathf.Clamp(0.05f + curvatureNormalized * 0.35f, 0.03f, 0.45f);

                float recommendedLaneGap = Mathf.Max(targetMinEdgeGap, estimatedLaneGap > 0f ? estimatedLaneGap : targetMinEdgeGap);
                float recommendedForwardGap = Mathf.Max(targetMinEdgeGap, estimatedForwardGap > 0f ? estimatedForwardGap : targetMinEdgeGap);
                float recommendedLaneCenterSpacing = Mathf.Max(0.001f, medianTileWidth + recommendedLaneGap + (medianTileWidth * recommendedCurvatureWidening * 0.25f));
                float recommendedForwardCenterSpacing = Mathf.Max(0.001f, medianTileDepth + recommendedForwardGap + (medianTileDepth * recommendedCurvatureWidening * 0.1f));

                float geometryMergeRisk;
                if (overlapPairCount > 0)
                {
                    geometryMergeRisk = Mathf.Clamp01(0.7f + Mathf.Min(0.3f, maxOverlapDepth * 2.5f));
                }
                else
                {
                    geometryMergeRisk = targetMinEdgeGap <= 0f
                        ? 0f
                        : Mathf.Clamp01((targetMinEdgeGap - Mathf.Max(0f, minEdgeDistance)) / targetMinEdgeGap);
                }

                var screenshotMetrics = new Dictionary<string, object>();
                float screenshotBleedRisk = 0f;
                if (captureScreenshot)
                {
                    AnalyzeScreenshotBleed(
                        screenshotView,
                        brightThreshold,
                        glowThreshold,
                        out var brightRatio,
                        out var glowRatio,
                        out var bleedRatio,
                        out var clippedRatio,
                        out var neonRatio,
                        out var screenshotHandle,
                        out var screenshotError);

                    screenshotMetrics["captured"] = string.IsNullOrWhiteSpace(screenshotError);
                    screenshotMetrics["screenshotView"] = screenshotView;
                    screenshotMetrics["brightThreshold"] = brightThreshold;
                    screenshotMetrics["glowThreshold"] = glowThreshold;

                    if (string.IsNullOrWhiteSpace(screenshotError))
                    {
                        screenshotMetrics["brightRatio"] = brightRatio;
                        screenshotMetrics["glowRatio"] = glowRatio;
                        screenshotMetrics["bleedRatio"] = bleedRatio;
                        screenshotMetrics["clippedRatio"] = clippedRatio;
                        screenshotMetrics["neonRatio"] = neonRatio;
                        if (!string.IsNullOrWhiteSpace(screenshotHandle))
                        {
                            screenshotMetrics["imageHandle"] = screenshotHandle;
                        }
                        screenshotBleedRisk = Mathf.Clamp01(((bleedRatio - 1.2f) / 3.5f) * 0.7f + (clippedRatio * 8f) * 0.3f);
                    }
                    else
                    {
                        screenshotMetrics["error"] = screenshotError;
                    }
                }
                else
                {
                    screenshotMetrics["captured"] = false;
                }

                float visualMergeRisk = Mathf.Clamp01(geometryMergeRisk * 0.72f + screenshotBleedRisk * 0.28f);
                string mergeLevel = visualMergeRisk switch
                {
                    >= 0.75f => "high",
                    >= 0.45f => "medium",
                    >= 0.2f => "low",
                    _ => "minimal"
                };

                if (includeClosestPairs)
                {
                    closestPairs = BuildClosestPairSamples(samples, maxPairs: 16);
                }

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "selection", selectionInfo },
                    { "tileCount", samples.Count },
                    { "skippedCount", skippedCount },
                    { "metrics", new Dictionary<string, object>
                        {
                            { "minEdgeDistance", minEdgeDistance == float.MaxValue ? 0f : minEdgeDistance },
                            { "avgNearestEdgeDistance", avgNearestEdgeDistance },
                            { "medianNearestEdgeDistance", medianNearestEdgeDistance },
                            { "p10NearestEdgeDistance", p10NearestEdgeDistance },
                            { "p25NearestEdgeDistance", p25NearestEdgeDistance },
                            { "p75NearestEdgeDistance", p75NearestEdgeDistance },
                            { "overlapPairCount", overlapPairCount },
                            { "maxOverlapDepth", maxOverlapDepth },
                            { "estimatedLaneGap", estimatedLaneGap },
                            { "estimatedForwardGap", estimatedForwardGap },
                            { "minLaneGap", minLaneGap },
                            { "medianLaneGap", medianLaneGap },
                            { "minForwardGap", minForwardGap },
                            { "medianForwardGap", medianForwardGap },
                            { "medianTileWidth", medianTileWidth },
                            { "medianTileDepth", medianTileDepth },
                            { "curvatureMax", curvatureMax },
                            { "curvatureNormalized", curvatureNormalized }
                        }
                    },
                    { "visualMergeRisk", visualMergeRisk },
                    { "visualMergeLevel", mergeLevel },
                    { "screenshot", screenshotMetrics },
                    { "generationConstraints", new Dictionary<string, object>
                        {
                            { "targetMinEdgeGap", targetMinEdgeGap },
                            { "recommendedLaneGap", recommendedLaneGap },
                            { "recommendedForwardGap", recommendedForwardGap },
                            { "recommendedCurvatureWidening", recommendedCurvatureWidening },
                            { "recommendedLaneCenterSpacing", recommendedLaneCenterSpacing },
                            { "recommendedForwardCenterSpacing", recommendedForwardCenterSpacing },
                            { "recommendedOuterCurveExtraGap", recommendedLaneGap * (1f + recommendedCurvatureWidening) }
                        }
                    },
                    { "closestPairs", closestPairs }
                };

                response = ApplyResponseProjection(response, fields, omitEmpty, maxItems);
                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/scene/resolve-tile-overlaps", Category = "scene", Description = "Nudge tiles apart to fix overlaps", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string ResolveTileOverlaps(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null)
                {
                    return JsonError("Failed to parse overlap resolver request");
                }

                bool includeInactive = ReadBool(request, "includeInactive", false);
                int maxTiles = Mathf.Clamp(TryReadInt(request, "maxTiles", out var maxTilesParsed) ? maxTilesParsed : 1200, 10, 4000);
                float targetMinEdgeGap = Mathf.Max(0f, TryReadFloatField(request, "targetMinEdgeGap", out var gapParsed) ? gapParsed : 0.04f);
                int maxIterations = Mathf.Clamp(TryReadInt(request, "maxIterations", out var maxIterationsParsed) ? maxIterationsParsed : 10, 1, 64);
                float nudgeStep = Mathf.Clamp(TryReadFloatField(request, "nudgeStep", out var nudgeStepParsed) ? nudgeStepParsed : 0.0025f, 0.0001f, 0.2f);
                bool autoSaveScene = ReadBool(request, "autoSaveScene", false);

                var explicitInstanceIds = ExtractIntList(request, "instanceIds");
                int rootInstanceId = TryReadInt(request, "rootInstanceId", out var rootParsed) ? rootParsed : 0;
                string rootName = ReadString(request, "rootName");
                if (string.IsNullOrWhiteSpace(rootName))
                {
                    rootName = ReadString(request, "tileRootName");
                }

                if ((explicitInstanceIds == null || explicitInstanceIds.Count == 0)
                    && rootInstanceId == 0
                    && string.IsNullOrWhiteSpace(rootName))
                {
                    return JsonError("Provide rootInstanceId, rootName/tileRootName, or instanceIds");
                }

                if (!TryCollectTileObjects(
                    rootInstanceId,
                    explicitInstanceIds,
                    includeInactive,
                    maxTiles,
                    rootName,
                    out var tileObjects,
                    out var skippedCount,
                    out var selectionInfo,
                    out var collectError))
                {
                    return JsonError(collectError);
                }

                if (tileObjects.Count < 2)
                {
                    return JsonError("Need at least 2 tile objects to resolve overlaps");
                }

                var movedInstanceIds = new HashSet<int>();
                var currentSamples = CollectSamplesFromObjects(tileObjects, includeInactive, out _);
                ComputeTilePairStats(currentSamples, out var minBefore, out var overlapBefore, out var maxDepthBefore);

                int iterationsUsed = 0;
                int totalAdjustments = 0;
                for (int iter = 0; iter < maxIterations; iter++)
                {
                    bool adjustedThisIteration = false;
                    iterationsUsed = iter + 1;
                    var recordedThisIteration = new HashSet<int>();

                    // Cache bounds/samples once per iteration instead of per pair.
                    var samplesByIndex = new TileBoundsSample[tileObjects.Count];
                    var hasSample = new bool[tileObjects.Count];
                    for (int idx = 0; idx < tileObjects.Count; idx++)
                    {
                        var go = tileObjects[idx];
                        if (go == null) continue;
                        if (!TryGetAggregateBounds(go, includeInactive, out var bounds, out _)) continue;

                        samplesByIndex[idx] = new TileBoundsSample
                        {
                            centerXZ = new Vector2(bounds.center.x, bounds.center.z),
                            extentsXZ = new Vector2(bounds.extents.x, bounds.extents.z)
                        };
                        hasSample[idx] = true;
                    }

                    for (int i = 0; i < tileObjects.Count; i++)
                    {
                        var aObj = tileObjects[i];
                        if (aObj == null || !hasSample[i]) continue;
                        var aSample = samplesByIndex[i];

                        for (int j = i + 1; j < tileObjects.Count; j++)
                        {
                            var bObj = tileObjects[j];
                            if (bObj == null || !hasSample[j]) continue;
                            var bSample = samplesByIndex[j];

                            float signedDistance = ComputeSignedAabbDistance2D(aSample, bSample, out _, out _);
                            float deficit = targetMinEdgeGap - signedDistance;
                            if (deficit <= 0f) continue;

                            var delta = bSample.centerXZ - aSample.centerXZ;
                            if (delta.sqrMagnitude < 0.000001f)
                            {
                                float pseudo = ((i + j) % 2 == 0) ? 1f : -1f;
                                delta = new Vector2(pseudo, 0.15f * pseudo);
                            }
                            delta.Normalize();

                            float push = Mathf.Max(nudgeStep, deficit * 0.5f + nudgeStep);
                            var shift = new Vector3(delta.x, 0f, delta.y) * push;

                            if (recordedThisIteration.Add(aObj.GetInstanceID()))
                            {
                                Undo.RecordObject(aObj.transform, "Resolve Tile Overlaps");
                            }
                            if (recordedThisIteration.Add(bObj.GetInstanceID()))
                            {
                                Undo.RecordObject(bObj.transform, "Resolve Tile Overlaps");
                            }
                            aObj.transform.position -= shift;
                            bObj.transform.position += shift;
                            samplesByIndex[i].centerXZ -= new Vector2(shift.x, shift.z);
                            samplesByIndex[j].centerXZ += new Vector2(shift.x, shift.z);

                            movedInstanceIds.Add(aObj.GetInstanceID());
                            movedInstanceIds.Add(bObj.GetInstanceID());
                            totalAdjustments++;
                            adjustedThisIteration = true;
                        }
                    }

                    if (!adjustedThisIteration)
                    {
                        break;
                    }
                }

                if (movedInstanceIds.Count > 0)
                {
                    EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                }

                currentSamples = CollectSamplesFromObjects(tileObjects, includeInactive, out _);
                ComputeTilePairStats(currentSamples, out var minAfter, out var overlapAfter, out var maxDepthAfter);

                bool sceneSaved = false;
                string sceneSaveError = null;
                if (autoSaveScene && movedInstanceIds.Count > 0)
                {
                    sceneSaved = TrySaveActiveScene(out sceneSaveError);
                }

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "selection", selectionInfo },
                    { "skippedCount", skippedCount },
                    { "targetMinEdgeGap", targetMinEdgeGap },
                    { "maxIterations", maxIterations },
                    { "iterationsUsed", iterationsUsed },
                    { "adjustmentCount", totalAdjustments },
                    { "adjustedObjectCount", movedInstanceIds.Count },
                    { "adjustedInstanceIds", movedInstanceIds.Cast<object>().ToList() },
                    { "metricsBefore", new Dictionary<string, object>
                        {
                            { "minEdgeDistance", minBefore },
                            { "overlapPairCount", overlapBefore },
                            { "maxOverlapDepth", maxDepthBefore }
                        }
                    },
                    { "metricsAfter", new Dictionary<string, object>
                        {
                            { "minEdgeDistance", minAfter },
                            { "overlapPairCount", overlapAfter },
                            { "maxOverlapDepth", maxDepthAfter }
                        }
                    },
                    { "autoSaveScene", autoSaveScene },
                    { "sceneSaved", sceneSaved }
                };

                if (!string.IsNullOrWhiteSpace(sceneSaveError))
                {
                    response["sceneSaveError"] = sceneSaveError;
                }

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }


        private sealed class TileBoundsSample
        {
            public int instanceId;
            public string name;
            public string path;
            public Vector2 centerXZ;
            public Vector2 sizeXZ;
            public Vector2 extentsXZ;
            public string boundsSource;
        }

        private sealed class LayoutNodeState
        {
            public int instanceId;
            public GameObject go;
            public Transform transform;
            public string name;
            public string path;
            public Vector3 originalPosition;
            public Vector3 position;
            public Vector3 extents;
        }

        private struct LayoutAxesMask
        {
            public bool x;
            public bool y;
            public bool z;
        }

        private sealed class Vector2ApproxComparer : IEqualityComparer<Vector2>
        {
            private readonly float _epsilon;

            public Vector2ApproxComparer(float epsilon)
            {
                _epsilon = Mathf.Max(0.0000001f, epsilon);
            }

            public bool Equals(Vector2 a, Vector2 b)
            {
                return Mathf.Abs(a.x - b.x) <= _epsilon
                    && Mathf.Abs(a.y - b.y) <= _epsilon;
            }

            public int GetHashCode(Vector2 obj)
            {
                int qx = Mathf.RoundToInt(obj.x / _epsilon);
                int qy = Mathf.RoundToInt(obj.y / _epsilon);
                unchecked
                {
                    return (qx * 397) ^ qy;
                }
            }
        }

    }
}
