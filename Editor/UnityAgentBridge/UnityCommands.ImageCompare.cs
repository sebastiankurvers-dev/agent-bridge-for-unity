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
        [BridgeRoute("POST", "/image/compare", Category = "screenshot", Description = "Compare two screenshots", ReadOnly = true, TimeoutDefault = 15000)]
        public static string CompareImages(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<CompareImagesRequest>(jsonData ?? "{}");
                if (request == null)
                {
                    request = new CompareImagesRequest();
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

                string currentImageBase64 = request.currentImageBase64;
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

                if (request.storeReferenceHandle != 0 && string.IsNullOrWhiteSpace(resolvedReferenceHandle))
                {
                    resolvedReferenceHandle = StoreImage(referenceImageBase64, analysis.referenceWidth, analysis.referenceHeight, "image/jpeg", "compare:reference");
                }

                var response = BuildCompareImageResponse(analysis, request.includeHeatmap != 0);
                double rawSimilarity = (double)Math.Round(analysis.similarityScore, 6);

                // Top-level compatibility fields so direct route consumers do not need
                // to parse nested metrics for the most common similarity values.
                response["similarity"] = rawSimilarity;
                response["similarityScore"] = rawSimilarity;
                response["similarityRaw"] = rawSimilarity;
                response["similarityAspectMatched"] = rawSimilarity;

                // Aspect info — always report
                float refAspect = analysis.referenceHeight > 0 ? (float)analysis.referenceWidth / analysis.referenceHeight : 0f;
                float curAspect = analysis.currentHeight > 0 ? (float)analysis.currentWidth / analysis.currentHeight : 0f;
                bool aspectMismatch = refAspect > 0f && curAspect > 0f && Mathf.Abs(refAspect - curAspect) > 0.05f;
                float mismatchRatio = (refAspect > 0f && curAspect > 0f) ? Mathf.Max(refAspect, curAspect) / Mathf.Max(0.001f, Mathf.Min(refAspect, curAspect)) : 0f;
                response["aspectInfo"] = new Dictionary<string, object>
                {
                    { "referenceAspect", (double)Math.Round(refAspect, 4) },
                    { "currentAspect", (double)Math.Round(curAspect, 4) },
                    { "mismatch", aspectMismatch },
                    { "mismatchRatio", (double)Math.Round(mismatchRatio, 4) }
                };

                // Aspect-corrected analysis (dual metrics)
                string aspectMode = string.IsNullOrWhiteSpace(request.aspectMode) ? "none" : request.aspectMode.Trim().ToLowerInvariant();
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
                        response["similarityRaw"] = rawSimilarity;
                        response["similarityAspectMatched"] = (double)Math.Round(aspectAnalysis.similarityScore, 6);
                        response["aspectMode"] = aspectMode;
                    }
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

        [BridgeRoute("POST", "/image/compare-semantic", Category = "screenshot", Description = "Compare semantic screenshot regions with diagnostics", ReadOnly = true, TimeoutDefault = 15000)]
        public static string CompareImagesSemantic(string jsonData)
        {
            Texture2D referenceTexture = null;
            Texture2D currentTexture = null;

            try
            {
                var request = MiniJSON.Json.Deserialize(jsonData ?? "{}") as Dictionary<string, object>;
                if (request == null) return JsonError("Failed to parse semantic compare request");

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
                bool captureCurrentScreenshot = ReadBool(request, "captureCurrentScreenshot", false);
                if (string.IsNullOrWhiteSpace(currentImageBase64) && captureCurrentScreenshot)
                {
                    var screenshotView = ReadString(request, "screenshotView");
                    if (string.IsNullOrWhiteSpace(screenshotView)) screenshotView = "scene";
                    TryReadInt(request, "screenshotWidth", out int screenshotWidth);
                    TryReadInt(request, "screenshotHeight", out int screenshotHeight);
                    bool includeHandles = ReadBool(request, "includeImageHandles", false);

                    if (!TryGetScreenshotData(
                        screenshotView,
                        out currentImageBase64,
                        out resolvedCurrentHandle,
                        out var captureError,
                        includeHandle: includeHandles,
                        requestedWidth: screenshotWidth,
                        requestedHeight: screenshotHeight))
                    {
                        return JsonError(captureError);
                    }
                }
                else if (!TryResolveImageInput(
                    "current",
                    currentImageBase64,
                    ReadString(request, "currentImageHandle"),
                    allowEmpty: false,
                    out currentImageBase64,
                    out resolvedCurrentHandle,
                    out var resolveCurrentError))
                {
                    return JsonError(resolveCurrentError);
                }

                if (!TryDecodeImage(referenceImageBase64, out referenceTexture, out var decodeReferenceError))
                {
                    return JsonError(decodeReferenceError);
                }
                if (!TryDecodeImage(currentImageBase64, out currentTexture, out var decodeCurrentError))
                {
                    return JsonError(decodeCurrentError);
                }

                if (!request.TryGetValue("regions", out var regionsObj) || !(regionsObj is List<object> regionsList) || regionsList.Count == 0)
                {
                    return JsonError("regions is required and must contain at least 1 region");
                }

                int maxRegions = Mathf.Clamp(TryReadIntOrDefault(request, "maxRegions", 24), 1, 64);
                if (regionsList.Count > maxRegions)
                {
                    return JsonError($"regions count {regionsList.Count} exceeds maxRegions {maxRegions}");
                }

                int maxTotalPixels = Mathf.Clamp(TryReadIntOrDefault(request, "maxTotalPixels", 2_000_000), 10000, 20_000_000);
                int totalPixelsProcessed = 0;
                var regionResults = new List<object>();
                double similaritySum = 0d;

                int index = 0;
                foreach (var regionObj in regionsList)
                {
                    if (!(regionObj is Dictionary<string, object> region))
                    {
                        return JsonError($"regions[{index}] must be an object");
                    }

                    if (!TryReadRegionRect(region, out float x, out float y, out float w, out float h))
                    {
                        return JsonError($"regions[{index}] must include normalized x,y,w,h");
                    }

                    string regionName = ReadString(region, "name");
                    if (string.IsNullOrWhiteSpace(regionName)) regionName = $"region_{index}";

                    var refRect = NormalizeRegionToPixelRect(referenceTexture.width, referenceTexture.height, x, y, w, h);
                    var curRect = NormalizeRegionToPixelRect(currentTexture.width, currentTexture.height, x, y, w, h);

                    int sampleWidth = Mathf.Min(refRect.width, curRect.width);
                    int sampleHeight = Mathf.Min(refRect.height, curRect.height);
                    if (sampleWidth <= 0 || sampleHeight <= 0)
                    {
                        return JsonError($"regions[{index}] resolves to empty pixel bounds");
                    }

                    int pixelCount = sampleWidth * sampleHeight;
                    totalPixelsProcessed += pixelCount;
                    if (totalPixelsProcessed > maxTotalPixels)
                    {
                        return JsonError($"semantic compare pixel budget exceeded: {totalPixelsProcessed} > {maxTotalPixels}");
                    }

                    var refPixels = referenceTexture.GetPixels(refRect.x, refRect.y, sampleWidth, sampleHeight);
                    var curPixels = currentTexture.GetPixels(curRect.x, curRect.y, sampleWidth, sampleHeight);

                    double sumRefLuma = 0d;
                    double sumCurLuma = 0d;
                    double sumRefSat = 0d;
                    double sumCurSat = 0d;
                    double sumRedDelta = 0d;
                    double sumGreenDelta = 0d;
                    double sumBlueDelta = 0d;
                    double sumMae = 0d;

                    for (int i = 0; i < pixelCount; i++)
                    {
                        var r = refPixels[i];
                        var c = curPixels[i];
                        float refLuma = ComputeSemanticLuminance(r);
                        float curLuma = ComputeSemanticLuminance(c);
                        float refSat = ComputeSemanticSaturation(r);
                        float curSat = ComputeSemanticSaturation(c);

                        sumRefLuma += refLuma;
                        sumCurLuma += curLuma;
                        sumRefSat += refSat;
                        sumCurSat += curSat;

                        float dR = c.r - r.r;
                        float dG = c.g - r.g;
                        float dB = c.b - r.b;
                        sumRedDelta += dR;
                        sumGreenDelta += dG;
                        sumBlueDelta += dB;
                        sumMae += (Math.Abs(dR) + Math.Abs(dG) + Math.Abs(dB)) / 3.0;
                    }

                    double avgRefLuma = sumRefLuma / pixelCount;
                    double avgCurLuma = sumCurLuma / pixelCount;
                    double luminanceDelta = avgCurLuma - avgRefLuma;
                    double avgRefSat = sumRefSat / pixelCount;
                    double avgCurSat = sumCurSat / pixelCount;
                    double saturationDelta = avgCurSat - avgRefSat;
                    double mae = sumMae / pixelCount;
                    double similarity = Math.Max(0d, 1d - mae);
                    similaritySum += similarity;

                    var suggestions = new List<object>();
                    if (luminanceDelta < -0.15d) suggestions.Add("Region too dark, increase emission/exposure");
                    else if (luminanceDelta > 0.15d) suggestions.Add("Region too bright, reduce emission/exposure");
                    if (saturationDelta < -0.10d) suggestions.Add("Desaturated vs reference");
                    else if (saturationDelta > 0.10d) suggestions.Add("Over-saturated vs reference");

                    double redDelta = sumRedDelta / pixelCount;
                    double greenDelta = sumGreenDelta / pixelCount;
                    double blueDelta = sumBlueDelta / pixelCount;
                    double maxChannelShift = Math.Max(Math.Abs(redDelta), Math.Max(Math.Abs(greenDelta), Math.Abs(blueDelta)));
                    if (maxChannelShift > 0.08d)
                    {
                        suggestions.Add("Color cast detected");
                    }

                    regionResults.Add(new Dictionary<string, object>
                    {
                        { "name", regionName },
                        { "bounds", new Dictionary<string, object> { { "x", x }, { "y", y }, { "w", w }, { "h", h } } },
                        { "pixelCount", pixelCount },
                        { "avgLuminanceRef", Math.Round(avgRefLuma, 4) },
                        { "avgLuminanceCur", Math.Round(avgCurLuma, 4) },
                        { "luminanceDelta", Math.Round(luminanceDelta, 4) },
                        { "avgSaturationRef", Math.Round(avgRefSat, 4) },
                        { "avgSaturationCur", Math.Round(avgCurSat, 4) },
                        { "saturationDelta", Math.Round(saturationDelta, 4) },
                        { "colorShift", new Dictionary<string, object>
                            {
                                { "red", Math.Round(redDelta, 4) },
                                { "green", Math.Round(greenDelta, 4) },
                                { "blue", Math.Round(blueDelta, 4) }
                            }
                        },
                        { "mae", Math.Round(mae, 4) },
                        { "similarity", Math.Round(similarity, 4) },
                        { "suggestions", suggestions }
                    });

                    index++;
                }

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "regionCount", regionResults.Count },
                    { "averageSimilarity", Math.Round(similaritySum / Math.Max(1, regionResults.Count), 4) },
                    { "totalPixelsProcessed", totalPixelsProcessed },
                    { "regions", regionResults }
                };

                if (ReadBool(request, "includeImageHandles", false))
                {
                    response["imageRefs"] = new Dictionary<string, object>
                    {
                        { "referenceImageHandle", resolvedReferenceHandle ?? string.Empty },
                        { "currentImageHandle", resolvedCurrentHandle ?? string.Empty }
                    };
                }

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
            finally
            {
                if (referenceTexture != null) UnityEngine.Object.DestroyImmediate(referenceTexture);
                if (currentTexture != null) UnityEngine.Object.DestroyImmediate(currentTexture);
            }
        }

        // Helper methods (TryReadRegionRect, NormalizeRegionToPixelRect, TryAspectCorrectAndAnalyze,
        // TryAnalyzeImageDifferenceFromTextures, etc.) moved to UnityCommands.ImageCompare.Analysis.cs
        // ReproStep and algorithm methods moved to UnityCommands.ImageCompare.Algorithms.cs
    }
}
