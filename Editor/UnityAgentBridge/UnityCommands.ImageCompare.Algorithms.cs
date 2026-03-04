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
        [BridgeRoute("POST", "/scene/repro-step", Category = "scene", Description = "Image-guided reproduction step", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string ReproStep(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<ReproStepRequest>(jsonData ?? "{}");
                if (request == null)
                {
                    request = new ReproStepRequest();
                }

                if (!TryResolveImageInput(
                    "reference",
                    request.referenceImageBase64,
                    request.referenceImageHandle,
                    allowEmpty: false,
                    out var referenceImageBase64,
                    out var resolvedReferenceHandle,
                    out var resolveReferenceError))
                {
                    return JsonError(resolveReferenceError);
                }

                var currentImageBase64 = request.currentImageBase64;
                string resolvedCurrentHandle = null;
                if (string.IsNullOrWhiteSpace(currentImageBase64) && request.captureCurrentScreenshot != 0)
                {
                    if (!TryGetScreenshotData(
                        request.screenshotView,
                        out currentImageBase64,
                        out resolvedCurrentHandle,
                        out var captureError,
                        includeHandle: request.includeImageHandles != 0,
                        requestedWidth: request.screenshotWidth,
                        requestedHeight: request.screenshotHeight))
                    {
                        return JsonError(captureError);
                    }
                }
                else if (!TryResolveImageInput(
                    "current",
                    request.currentImageBase64,
                    request.currentImageHandle,
                    allowEmpty: false,
                    out currentImageBase64,
                    out resolvedCurrentHandle,
                    out var resolveCurrentError))
                {
                    return JsonError(resolveCurrentError);
                }

                int downsample = Mathf.Clamp(request.downsampleMaxSize <= 0 ? 256 : request.downsampleMaxSize, 64, 1024);
                int gridSize = Mathf.Clamp(request.gridSize <= 0 ? 8 : request.gridSize, 2, 32);
                float changedThreshold = Mathf.Clamp(request.changedPixelThreshold <= 0f ? 0.12f : request.changedPixelThreshold, 0.01f, 1f);
                float hotspotThreshold = Mathf.Clamp(request.hotThreshold <= 0f ? 0.2f : request.hotThreshold, 0.01f, 1f);
                float minConfidence = Mathf.Clamp(request.minConfidence <= 0f ? 0.35f : request.minConfidence, 0.05f, 0.95f);
                int maxProposals = Mathf.Clamp(request.maxProposals <= 0 ? 6 : request.maxProposals, 1, 24);

                if (!TryAnalyzeImageDifference(
                    referenceImageBase64,
                    currentImageBase64,
                    downsample,
                    gridSize,
                    changedThreshold,
                    hotspotThreshold,
                    out var analysis,
                    out var analysisError))
                {
                    return JsonError(analysisError);
                }

                var proposals = BuildReproPatchProposals(request, analysis);
                var ranked = proposals
                    .Where(p => p.confidence >= minConfidence)
                    .OrderByDescending(p => p.confidence)
                    .Take(maxProposals)
                    .ToList();

                var proposalPayload = ranked.Select(p => p.ToJson()).Cast<object>().ToList();
                var batchOperations = ranked
                    .Select(p => new Dictionary<string, object>
                    {
                        { "op", p.operation },
                        { "payload", p.payload }
                    })
                    .Cast<object>()
                    .ToList();

                // Structural diagnostics (geometry-based) run first
                var structuralDiagnostics = BuildStructuralDiagnostics(analysis);
                var regionDiagnostics = BuildRegionDiagnostics(analysis);

                // Merge: structural first, then pixel-based region diagnostics
                var allDiagnostics = new List<Dictionary<string, object>>(structuralDiagnostics);
                allDiagnostics.AddRange(regionDiagnostics);

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "analysis", BuildCompareImageResponse(analysis, includeHeatmap: request.includeHeatmap != 0) },
                    { "proposalCount", proposalPayload.Count },
                    { "proposals", proposalPayload },
                    { "diagnosticCount", allDiagnostics.Count },
                    { "diagnostics", allDiagnostics.Cast<object>().ToList() },
                    { "recommendedBatch", new Dictionary<string, object>
                        {
                            { "brief", true },
                            { "diffOnly", true },
                            { "operations", batchOperations }
                        }
                    }
                };

                if (request.storeReferenceHandle != 0 && string.IsNullOrWhiteSpace(resolvedReferenceHandle))
                {
                    resolvedReferenceHandle = StoreImage(referenceImageBase64, analysis.referenceWidth, analysis.referenceHeight, "image/jpeg", "repro:reference");
                }

                if (request.includeImageHandles != 0)
                {
                    response["imageRefs"] = new Dictionary<string, object>
                    {
                        { "referenceImageHandle", resolvedReferenceHandle ?? string.Empty },
                        { "currentImageHandle", resolvedCurrentHandle ?? string.Empty }
                    };
                }

                response = ApplyResponseProjection(response, request.fields, request.omitEmpty != 0, request.maxItems);
                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static List<ReproPatchProposal> BuildReproPatchProposals(ReproStepRequest request, ImageCompareAnalysis analysis)
        {
            var proposals = new List<ReproPatchProposal>();
            float mismatch = 1f - analysis.similarityScore;

            var cameraState = MiniJSON.Json.Deserialize(GetCameraRendering(JsonUtility.ToJson(new GetCameraRenderingRequest
            {
                instanceId = request.cameraInstanceId,
                cameraName = request.cameraName
            }))) as Dictionary<string, object>;

            // Exposure compensation (global brightness delta).
            float lumaDelta = analysis.meanReferenceLuminance - analysis.meanCurrentLuminance;
            if (Mathf.Abs(lumaDelta) > 0.025f)
            {
                float postExposure = Mathf.Clamp(lumaDelta * 2.5f, -2f, 2f);
                float confidence = Mathf.Clamp01(Mathf.Abs(lumaDelta) * 6.5f + mismatch * 0.2f);

                var overrides = new Dictionary<string, object>
                {
                    { "exposure", new Dictionary<string, object> { { "postExposure", (double)Math.Round(postExposure, 3) } } }
                };

                proposals.Add(new ReproPatchProposal
                {
                    id = "exposure_compensation",
                    operation = "set_volume_profile_overrides",
                    tool = "unity_set_volume_profile_overrides",
                    confidence = confidence,
                    title = "Compensate overall exposure",
                    rationale = lumaDelta > 0
                        ? "Reference appears brighter than current screenshot."
                        : "Reference appears darker than current screenshot.",
                    payload = BuildVolumeOverridePayload(request, overrides)
                });
            }

            // Color cast correction.
            float maxChannelDelta = Mathf.Max(
                Mathf.Abs(analysis.meanRedDelta),
                Mathf.Max(Mathf.Abs(analysis.meanGreenDelta), Mathf.Abs(analysis.meanBlueDelta)));

            if (maxChannelDelta > 0.02f)
            {
                var filterColor = new List<object>
                {
                    (double)Math.Round(Mathf.Clamp(1f + analysis.meanRedDelta * 1.2f, 0.6f, 1.5f), 3),
                    (double)Math.Round(Mathf.Clamp(1f + analysis.meanGreenDelta * 1.2f, 0.6f, 1.5f), 3),
                    (double)Math.Round(Mathf.Clamp(1f + analysis.meanBlueDelta * 1.2f, 0.6f, 1.5f), 3),
                    1.0
                };

                float confidence = Mathf.Clamp01(maxChannelDelta * 5.5f + mismatch * 0.15f);
                var overrides = new Dictionary<string, object>
                {
                    { "colorAdjustments", new Dictionary<string, object> { { "colorFilter", filterColor } } }
                };

                proposals.Add(new ReproPatchProposal
                {
                    id = "color_cast_correction",
                    operation = "set_volume_profile_overrides",
                    tool = "unity_set_volume_profile_overrides",
                    confidence = confidence,
                    title = "Correct color cast",
                    rationale = "Average RGB channels differ between reference and current image.",
                    payload = BuildVolumeOverridePayload(request, overrides)
                });
            }

            // Saturation correction.
            float saturationDelta = analysis.meanReferenceSaturation - analysis.meanCurrentSaturation;
            if (Mathf.Abs(saturationDelta) > 0.02f)
            {
                float saturationStep = Mathf.Clamp(saturationDelta * 120f, -60f, 60f);
                float confidence = Mathf.Clamp01(Mathf.Abs(saturationDelta) * 7f + mismatch * 0.1f);

                var overrides = new Dictionary<string, object>
                {
                    { "colorAdjustments", new Dictionary<string, object> { { "saturation", (double)Math.Round(saturationStep, 2) } } }
                };

                proposals.Add(new ReproPatchProposal
                {
                    id = "saturation_adjustment",
                    operation = "set_volume_profile_overrides",
                    tool = "unity_set_volume_profile_overrides",
                    confidence = confidence,
                    title = "Adjust saturation",
                    rationale = saturationDelta > 0
                        ? "Reference looks more saturated than current image."
                        : "Reference looks less saturated than current image.",
                    payload = BuildVolumeOverridePayload(request, overrides)
                });
            }

            // Edge treatment (vignette).
            if (analysis.edgeError > analysis.centerError * 1.2f && Mathf.Abs(analysis.edgeLumaDelta) > 0.015f)
            {
                bool referenceDarkerEdges = analysis.edgeLumaDelta < 0f;
                float targetIntensity = referenceDarkerEdges
                    ? Mathf.Clamp(0.2f + Mathf.Abs(analysis.edgeLumaDelta) * 1.5f, 0.1f, 0.6f)
                    : Mathf.Clamp(0.08f + Mathf.Abs(analysis.edgeLumaDelta) * 0.6f, 0.02f, 0.25f);

                float confidence = Mathf.Clamp01((analysis.edgeError - analysis.centerError) * 3f + Mathf.Abs(analysis.edgeLumaDelta) * 3f);
                var overrides = new Dictionary<string, object>
                {
                    { "vignette", new Dictionary<string, object>
                        {
                            { "active", 1 },
                            { "intensity", (double)Math.Round(targetIntensity, 3) },
                            { "smoothness", 0.5 }
                        }
                    }
                };

                proposals.Add(new ReproPatchProposal
                {
                    id = "edge_treatment",
                    operation = "set_volume_profile_overrides",
                    tool = "unity_set_volume_profile_overrides",
                    confidence = confidence,
                    title = "Adjust vignette edge treatment",
                    rationale = "Edge regions diverge from center regions more than expected.",
                    payload = BuildVolumeOverridePayload(request, overrides)
                });
            }

            // Sharpness / DoF heuristic.
            float gradientDelta = analysis.meanCurrentGradient - analysis.meanReferenceGradient;
            if (Mathf.Abs(gradientDelta) > 0.015f)
            {
                Dictionary<string, object> dofPatch;
                string rationale;
                if (gradientDelta > 0f)
                {
                    dofPatch = new Dictionary<string, object>
                    {
                        { "active", 1 },
                        { "mode", "Gaussian" },
                        { "gaussianStart", 8.0 },
                        { "gaussianEnd", 28.0 },
                        { "gaussianMaxRadius", 1.15 }
                    };
                    rationale = "Current image appears sharper/noisier than the reference.";
                }
                else
                {
                    dofPatch = new Dictionary<string, object>
                    {
                        { "active", 1 },
                        { "mode", "Off" }
                    };
                    rationale = "Current image appears blurrier than the reference.";
                }

                float confidence = Mathf.Clamp01(Mathf.Abs(gradientDelta) * 18f + mismatch * 0.08f);
                var overrides = new Dictionary<string, object> { { "depthOfField", dofPatch } };

                proposals.Add(new ReproPatchProposal
                {
                    id = "depth_of_field_hint",
                    operation = "set_volume_profile_overrides",
                    tool = "unity_set_volume_profile_overrides",
                    confidence = confidence,
                    title = "Adjust depth-of-field",
                    rationale = rationale,
                    payload = BuildVolumeOverridePayload(request, overrides)
                });
            }

            // Ensure post-processing is enabled when mismatch is high.
            bool postProcessingEnabled = cameraState != null && ReadBool(cameraState, "renderPostProcessing", false);
            if (!postProcessingEnabled && mismatch > 0.08f)
            {
                float confidence = Mathf.Clamp01(0.35f + mismatch * 0.9f);
                proposals.Add(new ReproPatchProposal
                {
                    id = "enable_post_processing",
                    operation = "set_camera_rendering",
                    tool = "unity_set_camera_rendering",
                    confidence = confidence,
                    title = "Enable camera post-processing",
                    rationale = "Post-processing is currently disabled while visual mismatch remains high.",
                    payload = BuildCameraRenderingPayload(request, new Dictionary<string, object>
                    {
                        { "renderPostProcessing", 1 }
                    })
                });
            }

            return proposals;
        }

        private static List<Dictionary<string, object>> BuildStructuralDiagnostics(ImageCompareAnalysis analysis)
        {
            var diagnostics = new List<Dictionary<string, object>>();

            try
            {
                // 1. Tile spacing anomaly — find the likely tile root and reuse MeasureTileSeparation logic
                GameObject tileRoot = null;
                int maxChildren = 0;
                for (int s = 0; s < SceneManager.sceneCount; s++)
                {
                    var scene = SceneManager.GetSceneAt(s);
                    if (!scene.isLoaded) continue;
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        if (root.transform.childCount > maxChildren)
                        {
                            maxChildren = root.transform.childCount;
                            tileRoot = root;
                        }
                    }
                }

                if (tileRoot != null && tileRoot.transform.childCount >= 4)
                {
                    List<TileBoundsSample> tileSamples;
                    int skippedSamples;
                    Dictionary<string, object> selectionInfo;
                    string collectError;

                    if (TryCollectTileBoundsSamples(
                        rootInstanceId: tileRoot.GetInstanceID(),
                        explicitIds: null,
                        includeInactive: false,
                        maxTiles: 600,
                        rootName: null,
                        samples: out tileSamples,
                        skippedCount: out skippedSamples,
                        selectionInfo: out selectionInfo,
                        error: out collectError) && tileSamples.Count >= 2)
                    {
                        // Compute nearest-neighbor edge distances (reuse existing logic pattern)
                        var nearestDists = new float[tileSamples.Count];
                        for (int i = 0; i < tileSamples.Count; i++) nearestDists[i] = float.MaxValue;

                        for (int i = 0; i < tileSamples.Count; i++)
                        {
                            for (int j = i + 1; j < tileSamples.Count; j++)
                            {
                                float d = ComputeSignedAabbDistance2D(tileSamples[i], tileSamples[j], out _, out _);
                                if (d < nearestDists[i]) nearestDists[i] = d;
                                if (d < nearestDists[j]) nearestDists[j] = d;
                            }
                        }

                        var validDists = nearestDists.Where(v => !float.IsInfinity(v) && v != float.MaxValue).ToList();
                        if (validDists.Count > 0)
                        {
                            float avgSpacing = validDists.Average();
                            // Estimate tile depth from first sample's bounds
                            float tileBoundsDepth = tileSamples[0].sizeXZ.y; // Z extent
                            if (tileBoundsDepth > 0.01f)
                            {
                                float ratio = avgSpacing / tileBoundsDepth;
                                if (ratio > 1.15f)
                                {
                                    diagnostics.Add(new Dictionary<string, object>
                                    {
                                        { "type", "tile_spacing_anomaly" },
                                        { "severity", ratio > 1.4f ? "high" : "medium" },
                                        { "avgSpacing", (double)Math.Round(avgSpacing, 4) },
                                        { "tileBoundsDepth", (double)Math.Round(tileBoundsDepth, 4) },
                                        { "ratio", (double)Math.Round(ratio, 3) },
                                        { "message", $"Tiles are too spread: avg spacing {avgSpacing:F3} vs tile depth {tileBoundsDepth:F3} (ratio {ratio:F2})" },
                                        { "suggestedAction", "reduce_tile_spacing" }
                                    });
                                }
                                else if (ratio < 0.95f)
                                {
                                    int overlapCount = validDists.Count(d => d < 0f);
                                    diagnostics.Add(new Dictionary<string, object>
                                    {
                                        { "type", "tile_spacing_anomaly" },
                                        { "severity", overlapCount > tileSamples.Count / 4 ? "high" : "medium" },
                                        { "avgSpacing", (double)Math.Round(avgSpacing, 4) },
                                        { "tileBoundsDepth", (double)Math.Round(tileBoundsDepth, 4) },
                                        { "ratio", (double)Math.Round(ratio, 3) },
                                        { "overlapCount", overlapCount },
                                        { "message", $"Tiles overlapping: avg spacing {avgSpacing:F3} vs tile depth {tileBoundsDepth:F3} ({overlapCount} overlapping pairs)" },
                                        { "suggestedAction", "increase_tile_spacing" }
                                    });
                                }
                            }
                        }

                        // 3. Object off-bounds — check Player against tile track envelope
                        float xMin = tileSamples.Min(s => s.centerXZ.x - s.extentsXZ.x);
                        float xMax = tileSamples.Max(s => s.centerXZ.x + s.extentsXZ.x);
                        float zMin = tileSamples.Min(s => s.centerXZ.y - s.extentsXZ.y);
                        float zMax = tileSamples.Max(s => s.centerXZ.y + s.extentsXZ.y);

                        var player = GameObject.FindWithTag("Player");
                        if (player == null)
                        {
                            var candidates = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
                            foreach (var c in candidates)
                            {
                                if (c.name.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    player = c.gameObject;
                                    break;
                                }
                            }
                        }

                        if (player != null)
                        {
                            var pp = player.transform.position;
                            bool inBounds = pp.x >= xMin && pp.x <= xMax && pp.z >= zMin && pp.z <= zMax;
                            if (!inBounds)
                            {
                                diagnostics.Add(new Dictionary<string, object>
                                {
                                    { "type", "object_off_bounds" },
                                    { "severity", "high" },
                                    { "objectName", player.name },
                                    { "objectPosition", new List<object> { (double)Math.Round(pp.x, 3), (double)Math.Round(pp.y, 3), (double)Math.Round(pp.z, 3) } },
                                    { "boundsMin", new List<object> { (double)Math.Round(xMin, 2), (double)Math.Round(zMin, 2) } },
                                    { "boundsMax", new List<object> { (double)Math.Round(xMax, 2), (double)Math.Round(zMax, 2) } },
                                    { "message", $"Player '{player.name}' at ({pp.x:F1},{pp.z:F1}) is outside tile track bounds ({xMin:F1},{zMin:F1})-({xMax:F1},{zMax:F1})" },
                                    { "suggestedAction", "reposition_object" }
                                });
                            }
                        }
                    }
                }

                // 2. Camera orientation mismatch — brightness distribution heuristic
                if (analysis.cellColors != null && analysis.cellColors.Count > 0)
                {
                    int gridSize = analysis.gridSize > 0 ? analysis.gridSize : 8;
                    int halfY = gridSize / 2;
                    float topRefBright = 0f, bottomRefBright = 0f;
                    float topCurBright = 0f, bottomCurBright = 0f;
                    int topCount = 0, bottomCount = 0;

                    foreach (var cell in analysis.cellColors)
                    {
                        if (cell.row < halfY)
                        {
                            topRefBright += cell.refLuminance;
                            topCurBright += cell.curLuminance;
                            topCount++;
                        }
                        else
                        {
                            bottomRefBright += cell.refLuminance;
                            bottomCurBright += cell.curLuminance;
                            bottomCount++;
                        }
                    }

                    if (topCount > 0 && bottomCount > 0)
                    {
                        topRefBright /= topCount;
                        bottomRefBright /= bottomCount;
                        topCurBright /= topCount;
                        bottomCurBright /= bottomCount;

                        // Reference has bright top (sky) but current doesn't, or vice versa
                        float refTopBias = topRefBright - bottomRefBright;
                        float curTopBias = topCurBright - bottomCurBright;
                        float biasDelta = Mathf.Abs(refTopBias - curTopBias);

                        if (biasDelta > 0.12f)
                        {
                            var cam = Camera.main;
                            float currentPitch = cam != null ? cam.transform.eulerAngles.x : 0f;
                            if (currentPitch > 180f) currentPitch -= 360f;

                            diagnostics.Add(new Dictionary<string, object>
                            {
                                { "type", "camera_orientation_mismatch" },
                                { "severity", biasDelta > 0.25f ? "high" : "medium" },
                                { "currentPitch", (double)Math.Round(currentPitch, 2) },
                                { "refTopBias", (double)Math.Round(refTopBias, 4) },
                                { "curTopBias", (double)Math.Round(curTopBias, 4) },
                                { "biasDelta", (double)Math.Round(biasDelta, 4) },
                                { "message", $"Brightness distribution differs (ref top-bias={refTopBias:F3}, cur top-bias={curTopBias:F3}) — camera pitch may differ (current={currentPitch:F1}°)" },
                                { "suggestedAction", "adjust_camera_angle" }
                            });
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Structural diagnostics are best-effort; don't fail the entire repro step
            }

            return diagnostics;
        }

        private static List<Dictionary<string, object>> BuildRegionDiagnostics(ImageCompareAnalysis analysis)
        {
            var diagnostics = new List<Dictionary<string, object>>();
            if (analysis.cellColors == null || analysis.cellColors.Count == 0)
                return diagnostics;

            foreach (var cell in analysis.cellColors)
            {
                // 1. Region brightness gap: reference bright, current dark
                if (cell.refLuminance > 0.3f && cell.curLuminance < 0.1f && cell.cellError > 0.3f)
                {
                    // Determine dominant color name from reference
                    string colorHint = GetDominantColorName(cell.refR, cell.refG, cell.refB);
                    diagnostics.Add(new Dictionary<string, object>
                    {
                        { "type", "region_brightness_gap" },
                        { "cellRow", cell.row },
                        { "cellCol", cell.col },
                        { "severity", cell.cellError > 0.5f ? "high" : "medium" },
                        { "referenceColor", new List<object> { (double)Math.Round(cell.refR, 3), (double)Math.Round(cell.refG, 3), (double)Math.Round(cell.refB, 3) } },
                        { "currentColor", new List<object> { (double)Math.Round(cell.curR, 3), (double)Math.Round(cell.curG, 3), (double)Math.Round(cell.curB, 3) } },
                        { "luminanceDelta", (double)Math.Round(cell.refLuminance - cell.curLuminance, 4) },
                        { "message", $"Region ({cell.row},{cell.col}) is dark but reference has bright {colorHint} content — likely missing emissive objects or unlit materials" },
                        { "suggestedAction", "add_emissive_objects" }
                    });
                    continue; // don't double-report
                }

                // 2. Region color shift: significant per-channel delta
                float rDelta = Mathf.Abs(cell.refR - cell.curR);
                float gDelta = Mathf.Abs(cell.refG - cell.curG);
                float bDelta = Mathf.Abs(cell.refB - cell.curB);
                float maxDelta = Mathf.Max(rDelta, Mathf.Max(gDelta, bDelta));
                if (maxDelta > 0.2f && cell.cellError > 0.15f)
                {
                    string refHue = GetDominantColorName(cell.refR, cell.refG, cell.refB);
                    string curHue = GetDominantColorName(cell.curR, cell.curG, cell.curB);
                    diagnostics.Add(new Dictionary<string, object>
                    {
                        { "type", "region_color_shift" },
                        { "cellRow", cell.row },
                        { "cellCol", cell.col },
                        { "severity", maxDelta > 0.4f ? "high" : "medium" },
                        { "referenceColor", new List<object> { (double)Math.Round(cell.refR, 3), (double)Math.Round(cell.refG, 3), (double)Math.Round(cell.refB, 3) } },
                        { "currentColor", new List<object> { (double)Math.Round(cell.curR, 3), (double)Math.Round(cell.curG, 3), (double)Math.Round(cell.curB, 3) } },
                        { "luminanceDelta", (double)Math.Round(cell.refLuminance - cell.curLuminance, 4) },
                        { "message", $"Region ({cell.row},{cell.col}) color differs: reference is {refHue}, current is {curHue}" },
                        { "suggestedAction", "adjust_material_color" }
                    });
                    continue;
                }

                // 3. Region content missing: reference has high variance (detail), current has low variance (empty)
                if (cell.refVariance > 0.01f && cell.curVariance < 0.002f && cell.cellError > 0.15f)
                {
                    diagnostics.Add(new Dictionary<string, object>
                    {
                        { "type", "region_content_missing" },
                        { "cellRow", cell.row },
                        { "cellCol", cell.col },
                        { "severity", cell.cellError > 0.35f ? "high" : "medium" },
                        { "referenceColor", new List<object> { (double)Math.Round(cell.refR, 3), (double)Math.Round(cell.refG, 3), (double)Math.Round(cell.refB, 3) } },
                        { "currentColor", new List<object> { (double)Math.Round(cell.curR, 3), (double)Math.Round(cell.curG, 3), (double)Math.Round(cell.curB, 3) } },
                        { "luminanceDelta", (double)Math.Round(cell.refLuminance - cell.curLuminance, 4) },
                        { "message", $"Region ({cell.row},{cell.col}) appears empty/flat but reference has detail — check for missing objects" },
                        { "suggestedAction", "add_missing_objects" }
                    });
                }
            }

            // Sort by severity (high first) then by cellError descending
            diagnostics.Sort((a, b) =>
            {
                var sevA = a["severity"].ToString() == "high" ? 0 : 1;
                var sevB = b["severity"].ToString() == "high" ? 0 : 1;
                if (sevA != sevB) return sevA.CompareTo(sevB);
                return 0; // preserve insertion order within same severity
            });

            // Limit to 16 diagnostics
            if (diagnostics.Count > 16)
                diagnostics = diagnostics.Take(16).ToList();

            return diagnostics;
        }

    }
}
