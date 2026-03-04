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
        [BridgeRoute("POST", "/scene/repro-plan", Category = "scene", Description = "Multi-pass reconstruction plan", ReadOnly = true, TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string PlanSceneReconstruction(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null)
                {
                    return JsonError("Failed to parse reconstruction plan request");
                }

                if (!TryResolveImageInput(
                    "reference",
                    ReadString(request, "referenceImageBase64"),
                    ReadString(request, "referenceImageHandle"),
                    allowEmpty: false,
                    out var referenceImageBase64,
                    out var resolvedReferenceHandle,
                    out var resolveReferenceError))
                {
                    return JsonError(resolveReferenceError);
                }

                string currentImageBase64 = ReadString(request, "currentImageBase64");
                string resolvedCurrentHandle = null;
                bool captureCurrentScreenshot = ReadBool(request, "captureCurrentScreenshot", true);
                string screenshotView = ReadString(request, "screenshotView") ?? "scene";
                int screenshotWidth = TryReadInt(request, "screenshotWidth", out var screenshotWidthParsed) ? screenshotWidthParsed : 0;
                int screenshotHeight = TryReadInt(request, "screenshotHeight", out var screenshotHeightParsed) ? screenshotHeightParsed : 0;

                if (string.IsNullOrWhiteSpace(currentImageBase64) && captureCurrentScreenshot)
                {
                    if (!TryGetScreenshotData(
                        screenshotView,
                        out currentImageBase64,
                        out resolvedCurrentHandle,
                        out var captureError,
                        includeHandle: true,
                        requestedWidth: screenshotWidth,
                        requestedHeight: screenshotHeight))
                    {
                        return JsonError(captureError);
                    }
                }
                else if (!TryResolveImageInput(
                    "current",
                    ReadString(request, "currentImageBase64"),
                    ReadString(request, "currentImageHandle"),
                    allowEmpty: false,
                    out currentImageBase64,
                    out resolvedCurrentHandle,
                    out var resolveCurrentError))
                {
                    return JsonError(resolveCurrentError);
                }

                int downsample = Mathf.Clamp(TryReadInt(request, "downsampleMaxSize", out var downsampleParsed) ? downsampleParsed : 256, 64, 1024);
                int gridSize = Mathf.Clamp(TryReadInt(request, "gridSize", out var gridParsed) ? gridParsed : 8, 2, 32);
                float changedThreshold = Mathf.Clamp(TryReadFloatField(request, "changedPixelThreshold", out var changedParsed) ? changedParsed : 0.12f, 0.01f, 1f);
                float hotspotThreshold = Mathf.Clamp(TryReadFloatField(request, "hotThreshold", out var hotspotParsed) ? hotspotParsed : 0.2f, 0.01f, 1f);
                float minConfidence = Mathf.Clamp(TryReadFloatField(request, "minConfidence", out var minConfidenceParsed) ? minConfidenceParsed : 0.30f, 0.05f, 0.95f);
                int maxProposals = Mathf.Clamp(TryReadInt(request, "maxProposals", out var maxProposalsParsed) ? maxProposalsParsed : 8, 1, 24);
                int maxTiles = Mathf.Clamp(TryReadInt(request, "maxTiles", out var maxTilesParsed) ? maxTilesParsed : 600, 10, 1500);
                string tileRootName = ReadString(request, "tileRootName");
                string aspectMode = (ReadString(request, "aspectMode") ?? "crop").Trim().ToLowerInvariant();
                string sceneIntent = ReadString(request, "sceneIntent") ?? string.Empty;
                string styleHint = ReadString(request, "styleHint") ?? string.Empty;

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

                float referenceAspect = analysis.referenceHeight > 0 ? (float)analysis.referenceWidth / analysis.referenceHeight : 0f;
                float currentAspect = analysis.currentHeight > 0 ? (float)analysis.currentWidth / analysis.currentHeight : 0f;
                bool aspectMismatch = referenceAspect > 0f && currentAspect > 0f && Mathf.Abs(referenceAspect - currentAspect) > 0.05f;
                float mismatchRatio = (referenceAspect > 0f && currentAspect > 0f)
                    ? Mathf.Max(referenceAspect, currentAspect) / Mathf.Max(0.001f, Mathf.Min(referenceAspect, currentAspect))
                    : 0f;

                float similarityAspectMatched = analysis.similarityScore;
                bool usedAspectCorrection = false;
                if (aspectMismatch && (aspectMode == "crop" || aspectMode == "fit_letterbox"))
                {
                    if (TryAspectCorrectAndAnalyze(
                        referenceImageBase64,
                        currentImageBase64,
                        aspectMode,
                        downsample,
                        gridSize,
                        changedThreshold,
                        hotspotThreshold,
                        out var aspectAnalysis,
                        out _))
                    {
                        similarityAspectMatched = aspectAnalysis.similarityScore;
                        usedAspectCorrection = true;
                    }
                }

                var proposalRequest = new ReproStepRequest
                {
                    cameraInstanceId = TryReadInt(request, "cameraInstanceId", out var cameraInstanceId) ? cameraInstanceId : 0,
                    cameraName = ReadString(request, "cameraName"),
                    volumeInstanceId = TryReadInt(request, "volumeInstanceId", out var volumeInstanceId) ? volumeInstanceId : 0,
                    profilePath = ReadString(request, "profilePath"),
                    minConfidence = minConfidence,
                    maxProposals = maxProposals
                };

                var topProposals = BuildReproPatchProposals(proposalRequest, analysis)
                    .Where(p => p.confidence >= minConfidence)
                    .OrderByDescending(p => p.confidence)
                    .Take(maxProposals)
                    .Select(p => (object)p.ToJson())
                    .ToList();

                var structuralDiagnostics = BuildStructuralDiagnostics(analysis);
                var regionDiagnostics = BuildRegionDiagnostics(analysis);
                var allDiagnostics = new List<Dictionary<string, object>>(structuralDiagnostics.Count + regionDiagnostics.Count);
                allDiagnostics.AddRange(structuralDiagnostics);
                allDiagnostics.AddRange(regionDiagnostics);

                int highSeverityDiagnostics = allDiagnostics.Count(d => string.Equals(ReadString(d, "severity"), "high", StringComparison.OrdinalIgnoreCase));
                int mediumSeverityDiagnostics = allDiagnostics.Count(d => string.Equals(ReadString(d, "severity"), "medium", StringComparison.OrdinalIgnoreCase));
                var diagnosticTypeCounts = allDiagnostics
                    .GroupBy(d => ReadString(d, "type") ?? "unknown")
                    .ToDictionary(g => g.Key, g => (object)g.Count());

                var layoutSnapshot = MiniJSON.Json.Deserialize(GetSceneLayoutSnapshot(tileRootName, maxTiles)) as Dictionary<string, object>;
                var layoutSummary = BuildLayoutSnapshotSummary(layoutSnapshot);
                var findings = BuildSceneReconstructionFindings(analysis, aspectMismatch, usedAspectCorrection, similarityAspectMatched);
                var assetSearchPlan = BuildSceneAssetSearchPlan(sceneIntent, styleHint, analysis);
                var passes = BuildSceneReconstructionPasses(
                    analysis,
                    aspectMismatch,
                    usedAspectCorrection,
                    aspectMode,
                    topProposals.Count,
                    highSeverityDiagnostics,
                    sceneIntent,
                    styleHint);

                var objective = new Dictionary<string, object>
                {
                    { "targetSimilarity", (double)Math.Round(Mathf.Clamp01(Mathf.Max(0.85f, similarityAspectMatched + 0.12f)), 4) },
                    { "primaryMetric", usedAspectCorrection ? "similarityAspectMatched" : "similarityRaw" },
                    { "sceneIntent", sceneIntent },
                    { "styleHint", styleHint }
                };

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "planVersion", "1.0" },
                    { "objective", objective },
                    { "metrics", new Dictionary<string, object>
                        {
                            { "similarityRaw", (double)Math.Round(analysis.similarityScore, 6) },
                            { "similarityAspectMatched", (double)Math.Round(similarityAspectMatched, 6) },
                            { "changedPixelRatio", (double)Math.Round(analysis.changedPixelRatio, 6) },
                            { "meanAbsoluteError", (double)Math.Round(analysis.meanAbsoluteError, 6) }
                        }
                    },
                    { "aspectInfo", new Dictionary<string, object>
                        {
                            { "referenceAspect", (double)Math.Round(referenceAspect, 4) },
                            { "currentAspect", (double)Math.Round(currentAspect, 4) },
                            { "mismatch", aspectMismatch },
                            { "mismatchRatio", (double)Math.Round(mismatchRatio, 4) },
                            { "mode", usedAspectCorrection ? aspectMode : "none" }
                        }
                    },
                    { "findings", findings },
                    { "sceneLayout", layoutSummary },
                    { "diagnosticsSummary", new Dictionary<string, object>
                        {
                            { "total", allDiagnostics.Count },
                            { "high", highSeverityDiagnostics },
                            { "medium", mediumSeverityDiagnostics },
                            { "byType", diagnosticTypeCounts },
                            { "topDiagnostics", allDiagnostics.Take(10).Cast<object>().ToList() }
                        }
                    },
                    { "passes", passes },
                    { "assetSearchPlan", assetSearchPlan },
                    { "topProposals", topProposals }
                };

                if (!string.IsNullOrWhiteSpace(resolvedReferenceHandle) || !string.IsNullOrWhiteSpace(resolvedCurrentHandle))
                {
                    response["imageRefs"] = new Dictionary<string, object>
                    {
                        { "referenceImageHandle", resolvedReferenceHandle ?? string.Empty },
                        { "currentImageHandle", resolvedCurrentHandle ?? string.Empty }
                    };
                }

                string fields = ReadString(request, "fields");
                bool omitEmpty = ReadBool(request, "omitEmpty", false);
                int maxItems = TryReadInt(request, "maxItems", out var maxItemsParsed) ? maxItemsParsed : -1;
                response = ApplyResponseProjection(response, fields, omitEmpty, maxItems);
                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }


        private static string GetDominantColorName(float r, float g, float b)
        {
            Color.RGBToHSV(new Color(r, g, b), out float h, out float s, out float v);
            if (v < 0.1f) return "dark";
            if (s < 0.15f) return v > 0.8f ? "white/bright" : "gray";
            float hDeg = h * 360f;
            if (hDeg < 15f || hDeg >= 345f) return "red";
            if (hDeg < 45f) return "orange";
            if (hDeg < 75f) return "yellow";
            if (hDeg < 165f) return "green";
            if (hDeg < 195f) return "cyan";
            if (hDeg < 255f) return "blue";
            if (hDeg < 285f) return "purple";
            return "pink";
        }

        private static Dictionary<string, object> BuildLayoutSnapshotSummary(Dictionary<string, object> layoutSnapshot)
        {
            var summary = new Dictionary<string, object>
            {
                { "available", false }
            };

            if (layoutSnapshot == null)
            {
                return summary;
            }

            if (layoutSnapshot.TryGetValue("camera", out var cameraObj) && cameraObj is Dictionary<string, object> camera)
            {
                summary["camera"] = camera;
            }

            if (layoutSnapshot.TryGetValue("player", out var playerObj) && playerObj is Dictionary<string, object> player)
            {
                summary["player"] = player;
            }

            if (layoutSnapshot.TryGetValue("tileStats", out var tileStatsObj) && tileStatsObj is Dictionary<string, object> tileStats)
            {
                summary["tileStats"] = tileStats;
            }

            if (layoutSnapshot.TryGetValue("renderSummary", out var renderObj) && renderObj is Dictionary<string, object> renderSummary)
            {
                summary["renderSummary"] = renderSummary;
            }

            summary["available"] = true;
            return summary;
        }

        private static List<object> BuildSceneReconstructionFindings(
            ImageCompareAnalysis analysis,
            bool aspectMismatch,
            bool usedAspectCorrection,
            float similarityAspectMatched)
        {
            var findings = new List<object>();

            findings.Add(new Dictionary<string, object>
            {
                { "type", "overall_similarity" },
                { "severity", similarityAspectMatched < 0.6f ? "high" : (similarityAspectMatched < 0.8f ? "medium" : "low") },
                { "message", $"Current similarity is {similarityAspectMatched:F3}. Primary optimization focus should be layout/camera before fine look tuning." }
            });

            if (aspectMismatch && !usedAspectCorrection)
            {
                findings.Add(new Dictionary<string, object>
                {
                    { "type", "aspect_mismatch" },
                    { "severity", "high" },
                    { "message", "Reference and current aspect ratio differ. Use aspectMode 'crop' or 'fit_letterbox' before evaluating final similarity." }
                });
            }

            float luminanceDelta = analysis.meanReferenceLuminance - analysis.meanCurrentLuminance;
            if (Mathf.Abs(luminanceDelta) > 0.03f)
            {
                findings.Add(new Dictionary<string, object>
                {
                    { "type", "luminance_gap" },
                    { "severity", Mathf.Abs(luminanceDelta) > 0.1f ? "high" : "medium" },
                    { "message", luminanceDelta > 0f
                        ? "Reference appears brighter than current scene."
                        : "Reference appears darker than current scene." }
                });
            }

            float saturationDelta = analysis.meanReferenceSaturation - analysis.meanCurrentSaturation;
            if (Mathf.Abs(saturationDelta) > 0.025f)
            {
                findings.Add(new Dictionary<string, object>
                {
                    { "type", "saturation_gap" },
                    { "severity", Mathf.Abs(saturationDelta) > 0.08f ? "high" : "medium" },
                    { "message", saturationDelta > 0f
                        ? "Reference is more saturated than current scene."
                        : "Reference is less saturated than current scene." }
                });
            }

            float detailDelta = analysis.meanReferenceGradient - analysis.meanCurrentGradient;
            if (Mathf.Abs(detailDelta) > 0.02f)
            {
                findings.Add(new Dictionary<string, object>
                {
                    { "type", "detail_density_gap" },
                    { "severity", Mathf.Abs(detailDelta) > 0.06f ? "high" : "medium" },
                    { "message", detailDelta > 0f
                        ? "Reference has more edge/detail density than current scene."
                        : "Current scene appears busier/sharper than reference." }
                });
            }

            return findings;
        }

        private static Dictionary<string, object> BuildSceneAssetSearchPlan(
            string sceneIntent,
            string styleHint,
            ImageCompareAnalysis analysis)
        {
            var tokens = new List<string>();
            void AddToken(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                if (value.Length < 3) return;
                if (tokens.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
                tokens.Add(value);
            }

            string combined = $"{sceneIntent} {styleHint}".Trim();
            var split = combined
                .Split(new[] { ' ', ',', ';', '.', ':', '-', '_', '/', '\\', '|', '(', ')', '[', ']', '{', '}', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToLowerInvariant());

            foreach (var token in split)
            {
                if (token.Length < 3) continue;
                if (token == "the" || token == "and" || token == "with" || token == "for" || token == "from")
                {
                    continue;
                }

                AddToken(token);
                if (tokens.Count >= 8) break;
            }

            if (analysis.meanReferenceSaturation > 0.42f) AddToken("neon");
            if (analysis.meanReferenceLuminance < 0.32f) AddToken("night");
            if (analysis.meanReferenceGradient < 0.08f) AddToken("fog");
            if (analysis.meanReferenceGradient > 0.16f) AddToken("detail");

            var queries = tokens.Take(8).ToList();
            if (queries.Count == 0)
            {
                queries.Add("environment");
                queries.Add("props");
                queries.Add("lighting");
            }

            return new Dictionary<string, object>
            {
                { "prefabQueries", queries.Cast<object>().ToList() },
                { "materialQueries", queries.Take(6).Cast<object>().ToList() },
                { "recommendedScopedRoots", new List<object> { "Assets/Prefabs", "Assets/Materials" } }
            };
        }

        private static List<object> BuildSceneReconstructionPasses(
            ImageCompareAnalysis analysis,
            bool aspectMismatch,
            bool usedAspectCorrection,
            string aspectMode,
            int proposalCount,
            int highSeverityDiagnostics,
            string sceneIntent,
            string styleHint)
        {
            var passes = new List<object>();

            passes.Add(new Dictionary<string, object>
            {
                { "id", "pass_1_capture_and_camera" },
                { "title", "Align Capture And Camera" },
                { "priority", 1 },
                { "why", "Matching camera framing and aspect first prevents false visual diffs in later passes." },
                { "tools", new List<object> { "unity_get_scene_layout_snapshot", "unity_screenshot", "unity_compare_images", "unity_orbit_camera", "unity_pan_camera", "unity_zoom_camera" } },
                { "acceptance", new List<object>
                    {
                        "aspectInfo.mismatch=false or aspectMode applied",
                        "edgeError and centerError trend downward across iterations"
                    }
                },
                { "notes", aspectMismatch && !usedAspectCorrection
                    ? $"Aspect mismatch detected; use aspectMode '{aspectMode}' for meaningful similarity."
                    : "Aspect/camera baseline is usable for next passes." }
            });

            passes.Add(new Dictionary<string, object>
            {
                { "id", "pass_2_layout_and_assets" },
                { "title", "Blockout Layout And Major Assets" },
                { "priority", 2 },
                { "why", "Object placement and scale have the largest effect on similarity for most scenes." },
                { "tools", new List<object> { "unity_find_prefabs_scoped", "unity_get_prefab_geometry", "unity_snap_objects", "unity_spawn_prefab", "unity_apply_scene_patch_batch" } },
                { "acceptance", new List<object>
                    {
                        "high-severity structural diagnostics reduced to <= 1",
                        "similarity metric increases after each layout batch"
                    }
                },
                { "notes", $"Intent='{sceneIntent}', style='{styleHint}'" }
            });

            passes.Add(new Dictionary<string, object>
            {
                { "id", "pass_3_materials_lighting_post" },
                { "title", "Match Materials, Lighting, And Post" },
                { "priority", 3 },
                { "why", "After geometry is stable, color/contrast/emission alignment converges quickly." },
                { "tools", new List<object> { "unity_sample_screenshot_colors", "unity_create_material", "unity_modify_material", "unity_set_render_settings", "unity_set_volume_profile_overrides", "unity_set_camera_rendering" } },
                { "acceptance", new List<object>
                    {
                        "abs(meanReferenceLuminance-meanCurrentLuminance) < 0.03",
                        "abs(meanReferenceSaturation-meanCurrentSaturation) < 0.03"
                    }
                },
                { "notes", $"Auto proposals currently available: {proposalCount}" }
            });

            passes.Add(new Dictionary<string, object>
            {
                { "id", "pass_4_validation_and_lock" },
                { "title", "Validate, Stabilize, And Save" },
                { "priority", 4 },
                { "why", "Lock quality with hard metrics and ensure editor state is clean for future iterations." },
                { "tools", new List<object> { "unity_repro_step", "unity_compare_images", "unity_run_scene_quality_checks", "unity_get_compilation_errors", "unity_save_scene" } },
                { "acceptance", new List<object>
                    {
                        "similarity target reached",
                        "compilation errors = 0",
                        "scene quality checks pass at configured threshold"
                    }
                },
                { "notes", highSeverityDiagnostics > 0
                    ? $"Resolve remaining high-severity diagnostics ({highSeverityDiagnostics}) before sign-off."
                    : "No high-severity diagnostics remain." }
            });

            return passes;
        }

        private static List<int> ExtractConstraintInstanceIds(IList<object> constraints)
        {
            var ids = new HashSet<int>();
            if (constraints == null) return ids.ToList();

            foreach (var raw in constraints)
            {
                if (raw is not Dictionary<string, object> constraint) continue;

                if (TryReadConstraintId(constraint, "instanceId", out var instanceId)) ids.Add(instanceId);
                if (TryReadConstraintId(constraint, "a", out var a)) ids.Add(a);
                if (TryReadConstraintId(constraint, "b", out var b)) ids.Add(b);
                if (TryReadConstraintId(constraint, "sourceId", out var sourceId)) ids.Add(sourceId);
                if (TryReadConstraintId(constraint, "targetId", out var targetId)) ids.Add(targetId);
                if (TryReadConstraintId(constraint, "boundsObjectId", out var boundsObjectId)) ids.Add(boundsObjectId);

                if (constraint.TryGetValue("ids", out var idsObj) && idsObj is IList<object> idsList)
                {
                    foreach (var item in idsList)
                    {
                        if (item == null) continue;
                        if (int.TryParse(item.ToString(), out var parsed) && parsed != 0)
                        {
                            ids.Add(parsed);
                        }
                    }
                }
            }

            return ids.ToList();
        }

    }
}
