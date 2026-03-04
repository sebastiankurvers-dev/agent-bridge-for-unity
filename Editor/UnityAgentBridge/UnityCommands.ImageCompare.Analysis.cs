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
        private static bool TryReadRegionRect(Dictionary<string, object> region, out float x, out float y, out float w, out float h)
        {
            x = y = w = h = 0f;
            if (!TryReadFloatField(region, "x", out x)) return false;
            if (!TryReadFloatField(region, "y", out y)) return false;
            if (!TryReadFloatField(region, "w", out w)) return false;
            if (!TryReadFloatField(region, "h", out h)) return false;
            if (w <= 0f || h <= 0f) return false;
            return true;
        }

        private static RectInt NormalizeRegionToPixelRect(int width, int height, float x, float y, float w, float h)
        {
            float xMin = Mathf.Clamp01(x);
            float yMin = Mathf.Clamp01(y);
            float xMax = Mathf.Clamp01(x + w);
            float yMax = Mathf.Clamp01(y + h);
            if (xMax <= xMin) xMax = Mathf.Min(1f, xMin + 0.001f);
            if (yMax <= yMin) yMax = Mathf.Min(1f, yMin + 0.001f);

            int px = Mathf.Clamp(Mathf.RoundToInt(xMin * width), 0, Mathf.Max(0, width - 1));
            int pyTop = Mathf.Clamp(Mathf.RoundToInt(yMin * height), 0, Mathf.Max(0, height - 1));
            int pw = Mathf.Clamp(Mathf.RoundToInt((xMax - xMin) * width), 1, Mathf.Max(1, width - px));
            int ph = Mathf.Clamp(Mathf.RoundToInt((yMax - yMin) * height), 1, Mathf.Max(1, height - pyTop));
            int py = Mathf.Clamp(height - pyTop - ph, 0, Mathf.Max(0, height - 1));
            return new RectInt(px, py, pw, ph);
        }

        private static float ComputeSemanticLuminance(Color color)
        {
            return color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f;
        }

        private static float ComputeSemanticSaturation(Color color)
        {
            Color.RGBToHSV(color, out _, out float s, out _);
            return s;
        }

        private static int TryReadIntOrDefault(Dictionary<string, object> map, string key, int defaultValue)
        {
            if (TryReadInt(map, key, out var value)) return value;
            return defaultValue;
        }

        private static bool TryAspectCorrectAndAnalyze(
            string refBase64, string curBase64,
            string mode, int downsample, int gridSize, float changedThreshold, float hotspotThreshold,
            out ImageCompareAnalysis analysis, out string error)
        {
            analysis = null;
            error = null;
            Texture2D refTex = null, curTex = null, correctedRef = null, correctedCur = null;
            try
            {
                if (!TryDecodeImage(refBase64, out refTex, out error)) return false;
                if (!TryDecodeImage(curBase64, out curTex, out error)) return false;

                if (refTex.height == 0 || curTex.height == 0 || refTex.width == 0 || curTex.width == 0)
                {
                    error = "Invalid texture dimensions (zero width or height)";
                    return false;
                }

                float refAspect = (float)refTex.width / refTex.height;
                float curAspect = (float)curTex.width / curTex.height;

                if (mode == "crop")
                {
                    // Center-crop the wider-aspect image to match the narrower's aspect
                    if (refAspect > curAspect)
                    {
                        // Ref is wider — crop ref to match cur's aspect
                        int newW = Mathf.Clamp(Mathf.RoundToInt(refTex.height * curAspect), 1, refTex.width);
                        int offsetX = Mathf.Max(0, (refTex.width - newW) / 2);
                        correctedRef = new Texture2D(newW, refTex.height, TextureFormat.RGB24, false);
                        correctedRef.SetPixels(refTex.GetPixels(offsetX, 0, newW, refTex.height));
                        correctedRef.Apply();
                        correctedCur = curTex; curTex = null; // transfer ownership
                    }
                    else
                    {
                        // Cur is wider — crop cur to match ref's aspect
                        int newW = Mathf.Clamp(Mathf.RoundToInt(curTex.height * refAspect), 1, curTex.width);
                        int offsetX = Mathf.Max(0, (curTex.width - newW) / 2);
                        correctedCur = new Texture2D(newW, curTex.height, TextureFormat.RGB24, false);
                        correctedCur.SetPixels(curTex.GetPixels(offsetX, 0, newW, curTex.height));
                        correctedCur.Apply();
                        correctedRef = refTex; refTex = null;
                    }
                }
                else // fit_letterbox
                {
                    // Pad the narrower image with black bars to match the wider's aspect
                    if (refAspect < curAspect)
                    {
                        // Ref is narrower — pad ref to match cur's aspect
                        int newW = Mathf.Max(refTex.width, Mathf.RoundToInt(refTex.height * curAspect));
                        correctedRef = new Texture2D(newW, refTex.height, TextureFormat.RGB24, false);
                        var black = new Color[newW * refTex.height];
                        correctedRef.SetPixels(black);
                        int offsetX = Mathf.Max(0, (newW - refTex.width) / 2);
                        int copyW = Mathf.Min(refTex.width, newW - offsetX);
                        correctedRef.SetPixels(offsetX, 0, copyW, refTex.height, refTex.GetPixels(0, 0, copyW, refTex.height));
                        correctedRef.Apply();
                        correctedCur = curTex; curTex = null;
                    }
                    else
                    {
                        // Cur is narrower — pad cur
                        int newW = Mathf.Max(curTex.width, Mathf.RoundToInt(curTex.height * refAspect));
                        correctedCur = new Texture2D(newW, curTex.height, TextureFormat.RGB24, false);
                        var black = new Color[newW * curTex.height];
                        correctedCur.SetPixels(black);
                        int offsetX = Mathf.Max(0, (newW - curTex.width) / 2);
                        int copyW = Mathf.Min(curTex.width, newW - offsetX);
                        correctedCur.SetPixels(offsetX, 0, copyW, curTex.height, curTex.GetPixels(0, 0, copyW, curTex.height));
                        correctedCur.Apply();
                        correctedRef = refTex; refTex = null;
                    }
                }

                return TryAnalyzeImageDifferenceFromTextures(
                    correctedRef,
                    correctedCur,
                    downsample,
                    gridSize,
                    changedThreshold,
                    hotspotThreshold,
                    out analysis,
                    out error);
            }
            finally
            {
                if (refTex != null) UnityEngine.Object.DestroyImmediate(refTex);
                if (curTex != null) UnityEngine.Object.DestroyImmediate(curTex);
                if (correctedRef != null && correctedRef != refTex) UnityEngine.Object.DestroyImmediate(correctedRef);
                if (correctedCur != null && correctedCur != curTex) UnityEngine.Object.DestroyImmediate(correctedCur);
            }
        }

        private static bool TryAnalyzeImageDifferenceFromTextures(
            Texture2D referenceTexture,
            Texture2D currentTexture,
            int downsampleMaxSize,
            int gridSize,
            float changedPixelThreshold,
            float hotspotThreshold,
            out ImageCompareAnalysis analysis,
            out string error)
        {
            analysis = null;
            error = null;
            Texture2D referenceWork = null;
            Texture2D currentWork = null;

            try
            {
                int targetWidth = Mathf.Min(referenceTexture.width, currentTexture.width);
                int targetHeight = Mathf.Min(referenceTexture.height, currentTexture.height);
                if (targetWidth <= 0 || targetHeight <= 0)
                {
                    error = "Invalid image dimensions";
                    return false;
                }

                if (Mathf.Max(targetWidth, targetHeight) > downsampleMaxSize)
                {
                    float scale = downsampleMaxSize / (float)Mathf.Max(targetWidth, targetHeight);
                    targetWidth = Mathf.Max(1, Mathf.RoundToInt(targetWidth * scale));
                    targetHeight = Mathf.Max(1, Mathf.RoundToInt(targetHeight * scale));
                }

                referenceWork = ResizeTexture(referenceTexture, targetWidth, targetHeight);
                currentWork = ResizeTexture(currentTexture, targetWidth, targetHeight);

                var refPixels = referenceWork.GetPixels32();
                var curPixels = currentWork.GetPixels32();
                int pixelCount = refPixels.Length;
                if (pixelCount == 0)
                {
                    error = "Images contain no pixels";
                    return false;
                }

                double sumAbs = 0;
                double sumSq = 0;
                int changedPixels = 0;

                double sumRefLuma = 0;
                double sumCurLuma = 0;
                double sumRefSat = 0;
                double sumCurSat = 0;
                double sumRedDelta = 0;
                double sumGreenDelta = 0;
                double sumBlueDelta = 0;

                gridSize = Mathf.Clamp(gridSize, 2, 32);
                int cellTotal = gridSize * gridSize;
                var cellErrors = new double[cellTotal];
                var cellLumaDelta = new double[cellTotal];
                var cellCounts = new int[cellTotal];
                var cellRefR = new double[cellTotal];
                var cellRefG = new double[cellTotal];
                var cellRefB = new double[cellTotal];
                var cellCurR = new double[cellTotal];
                var cellCurG = new double[cellTotal];
                var cellCurB = new double[cellTotal];
                var cellRefLumaSq = new double[cellTotal];
                var cellCurLumaSq = new double[cellTotal];
                var cellRefLumaSum = new double[cellTotal];
                var cellCurLumaSum = new double[cellTotal];
                var cellCovarianceSum = new double[cellTotal];

                int edgeMarginX = Mathf.Max(1, targetWidth / 5);
                int edgeMarginY = Mathf.Max(1, targetHeight / 5);
                int centerStartX = Mathf.FloorToInt(targetWidth * 0.3f);
                int centerEndX = Mathf.CeilToInt(targetWidth * 0.7f);
                int centerStartY = Mathf.FloorToInt(targetHeight * 0.3f);
                int centerEndY = Mathf.CeilToInt(targetHeight * 0.7f);

                double edgeErrorSum = 0;
                double centerErrorSum = 0;
                double edgeLumaDeltaSum = 0;
                double centerLumaDeltaSum = 0;
                int edgeCount = 0;
                int centerCount = 0;

                for (int i = 0; i < pixelCount; i++)
                {
                    var r = refPixels[i];
                    var c = curPixels[i];

                    float rr = r.r / 255f;
                    float rg = r.g / 255f;
                    float rb = r.b / 255f;
                    float cr = c.r / 255f;
                    float cg = c.g / 255f;
                    float cb = c.b / 255f;

                    float redDelta = rr - cr;
                    float greenDelta = rg - cg;
                    float blueDelta = rb - cb;
                    float absDiff = (Mathf.Abs(redDelta) + Mathf.Abs(greenDelta) + Mathf.Abs(blueDelta)) / 3f;

                    sumAbs += absDiff;
                    sumSq += absDiff * absDiff;
                    if (absDiff > changedPixelThreshold) changedPixels++;

                    float refLuma = rr * 0.2126f + rg * 0.7152f + rb * 0.0722f;
                    float curLuma = cr * 0.2126f + cg * 0.7152f + cb * 0.0722f;
                    float refSat = Mathf.Max(rr, Mathf.Max(rg, rb)) - Mathf.Min(rr, Mathf.Min(rg, rb));
                    float curSat = Mathf.Max(cr, Mathf.Max(cg, cb)) - Mathf.Min(cr, Mathf.Min(cg, cb));

                    sumRefLuma += refLuma;
                    sumCurLuma += curLuma;
                    sumRefSat += refSat;
                    sumCurSat += curSat;
                    sumRedDelta += redDelta;
                    sumGreenDelta += greenDelta;
                    sumBlueDelta += blueDelta;

                    int x = i % targetWidth;
                    int y = i / targetWidth;

                    int cellX = (x * gridSize) / targetWidth;
                    int cellY = (y * gridSize) / targetHeight;
                    int cellIndex = cellY * gridSize + cellX;

                    cellErrors[cellIndex] += absDiff;
                    cellLumaDelta[cellIndex] += (refLuma - curLuma);
                    cellCounts[cellIndex]++;
                    cellRefR[cellIndex] += rr;
                    cellRefG[cellIndex] += rg;
                    cellRefB[cellIndex] += rb;
                    cellCurR[cellIndex] += cr;
                    cellCurG[cellIndex] += cg;
                    cellCurB[cellIndex] += cb;
                    cellRefLumaSum[cellIndex] += refLuma;
                    cellCurLumaSum[cellIndex] += curLuma;
                    cellRefLumaSq[cellIndex] += refLuma * refLuma;
                    cellCurLumaSq[cellIndex] += curLuma * curLuma;
                    cellCovarianceSum[cellIndex] += refLuma * curLuma;

                    bool isEdge = x < edgeMarginX || x >= (targetWidth - edgeMarginX) || y < edgeMarginY || y >= (targetHeight - edgeMarginY);
                    bool isCenter = x >= centerStartX && x < centerEndX && y >= centerStartY && y < centerEndY;
                    if (isEdge)
                    {
                        edgeErrorSum += absDiff;
                        edgeLumaDeltaSum += (refLuma - curLuma);
                        edgeCount++;
                    }
                    else if (isCenter)
                    {
                        centerErrorSum += absDiff;
                        centerLumaDeltaSum += (refLuma - curLuma);
                        centerCount++;
                    }
                }

                double refGradSum = 0;
                double curGradSum = 0;
                int gradSamples = 0;
                for (int y = 0; y < targetHeight - 1; y++)
                {
                    for (int x = 0; x < targetWidth - 1; x++)
                    {
                        int idx = y * targetWidth + x;
                        int right = idx + 1;
                        int down = idx + targetWidth;

                        float rl = Luminance(refPixels[idx]);
                        float rr = Luminance(refPixels[right]);
                        float rd = Luminance(refPixels[down]);
                        float cl = Luminance(curPixels[idx]);
                        float crv = Luminance(curPixels[right]);
                        float cd = Luminance(curPixels[down]);

                        refGradSum += Mathf.Abs(rr - rl) + Mathf.Abs(rd - rl);
                        curGradSum += Mathf.Abs(crv - cl) + Mathf.Abs(cd - cl);
                        gradSamples += 2;
                    }
                }

                // Compositional analysis
                ComputeComposition(refPixels, targetWidth, targetHeight,
                    out float refTopLuma, out float refMidLuma, out float refBotLuma,
                    out float refTopEdge, out float refMidEdge, out float refBotEdge,
                    out float refHorizonY, out float refCenterX, out float refCenterY);
                ComputeComposition(curPixels, targetWidth, targetHeight,
                    out float curTopLuma, out float curMidLuma, out float curBotLuma,
                    out float curTopEdge, out float curMidEdge, out float curBotEdge,
                    out float curHorizonY, out float curCenterX, out float curCenterY);

                double mae = sumAbs / pixelCount;
                double rmse = Math.Sqrt(sumSq / pixelCount);
                double colorSimilarity = 1d - (0.65d * mae + 0.35d * rmse);
                colorSimilarity = Math.Max(0d, Math.Min(1d, colorSimilarity));

                var hotspots = new List<object>();
                double maxCellError = 0d;
                for (int cellY = 0; cellY < gridSize; cellY++)
                {
                    for (int cellX = 0; cellX < gridSize; cellX++)
                    {
                        int idx = cellY * gridSize + cellX;
                        if (cellCounts[idx] <= 0) continue;

                        double avgError = cellErrors[idx] / cellCounts[idx];
                        double avgLumaDelta = cellLumaDelta[idx] / cellCounts[idx];
                        if (avgError > maxCellError) maxCellError = avgError;

                        if (avgError >= hotspotThreshold)
                        {
                            hotspots.Add(new Dictionary<string, object>
                            {
                                { "cellX", cellX },
                                { "cellY", cellY },
                                { "xNorm", (double)Math.Round(cellX / (double)gridSize, 4) },
                                { "yNorm", (double)Math.Round(cellY / (double)gridSize, 4) },
                                { "widthNorm", (double)Math.Round(1d / gridSize, 4) },
                                { "heightNorm", (double)Math.Round(1d / gridSize, 4) },
                                { "error", (double)Math.Round(avgError, 6) },
                                { "lumaDelta", (double)Math.Round(avgLumaDelta, 6) }
                            });
                        }
                    }
                }

                hotspots = hotspots
                    .Cast<Dictionary<string, object>>()
                    .OrderByDescending(h => Convert.ToDouble(h["error"]))
                    .Take(12)
                    .Cast<object>()
                    .ToList();

                var cellColorsList = new List<CellColorInfo>();
                for (int cellY2 = 0; cellY2 < gridSize; cellY2++)
                {
                    for (int cellX2 = 0; cellX2 < gridSize; cellX2++)
                    {
                        int idx2 = cellY2 * gridSize + cellX2;
                        if (cellCounts[idx2] <= 0) continue;
                        float n = cellCounts[idx2];
                        float refLumaAvg = (float)(cellRefLumaSum[idx2] / n);
                        float curLumaAvg = (float)(cellCurLumaSum[idx2] / n);
                        float refVar = (float)(cellRefLumaSq[idx2] / n - refLumaAvg * refLumaAvg);
                        float curVar = (float)(cellCurLumaSq[idx2] / n - curLumaAvg * curLumaAvg);
                        float cov = (float)(cellCovarianceSum[idx2] / n - refLumaAvg * curLumaAvg);
                        cellColorsList.Add(new CellColorInfo
                        {
                            row = cellY2,
                            col = cellX2,
                            refR = (float)(cellRefR[idx2] / n),
                            refG = (float)(cellRefG[idx2] / n),
                            refB = (float)(cellRefB[idx2] / n),
                            curR = (float)(cellCurR[idx2] / n),
                            curG = (float)(cellCurG[idx2] / n),
                            curB = (float)(cellCurB[idx2] / n),
                            refLuminance = refLumaAvg,
                            curLuminance = curLumaAvg,
                            refVariance = Mathf.Max(0f, refVar),
                            curVariance = Mathf.Max(0f, curVar),
                            covariance = cov,
                            cellError = (float)(cellErrors[idx2] / n)
                        });
                    }
                }

                // Compute per-cell SSIM and average for structural similarity
                const double C1 = 0.0001; // (0.01)^2
                const double C2 = 0.0009; // (0.03)^2
                double ssimSum = 0d;
                int ssimCells = 0;
                foreach (var cell in cellColorsList)
                {
                    double muR = cell.refLuminance;
                    double muC = cell.curLuminance;
                    double varR = cell.refVariance;
                    double varC = cell.curVariance;
                    double cov = cell.covariance;
                    double ssim = ((2d * muR * muC + C1) * (2d * cov + C2))
                                / ((muR * muR + muC * muC + C1) * (varR + varC + C2));
                    ssimSum += Math.Max(-1d, Math.Min(1d, ssim));
                    ssimCells++;
                }
                double structuralSsim = ssimCells > 0 ? ssimSum / ssimCells : 1d;
                structuralSsim = Math.Max(0d, Math.Min(1d, structuralSsim));

                // Gradient correlation: how similar are edge patterns?
                double gradCorrelation = 1d;
                if (gradSamples > 0)
                {
                    double refGradMean = refGradSum / gradSamples;
                    double curGradMean = curGradSum / gradSamples;
                    double maxGrad = Math.Max(refGradMean, curGradMean);
                    if (maxGrad > 0.001)
                        gradCorrelation = Math.Min(refGradMean, curGradMean) / maxGrad;
                }

                // Changed pixel penalty: high changedPixelRatio means very different images
                double cpRatio = changedPixels / (double)pixelCount;
                double changePenalty = 1d - Math.Min(1d, cpRatio * 1.5d);

                // Multi-signal similarity
                double similarity = 0.30d * colorSimilarity
                                  + 0.30d * structuralSsim
                                  + 0.20d * gradCorrelation
                                  + 0.20d * changePenalty;
                similarity = Math.Max(0d, Math.Min(1d, similarity));

                analysis = new ImageCompareAnalysis
                {
                    referenceWidth = referenceTexture.width,
                    referenceHeight = referenceTexture.height,
                    currentWidth = currentTexture.width,
                    currentHeight = currentTexture.height,
                    width = targetWidth,
                    height = targetHeight,
                    similarityScore = (float)similarity,
                    structuralSimilarity = (float)structuralSsim,
                    meanAbsoluteError = (float)mae,
                    rootMeanSquareError = (float)rmse,
                    changedPixelRatio = changedPixels / (float)pixelCount,
                    changedPixelThreshold = changedPixelThreshold,
                    meanReferenceLuminance = (float)(sumRefLuma / pixelCount),
                    meanCurrentLuminance = (float)(sumCurLuma / pixelCount),
                    meanReferenceSaturation = (float)(sumRefSat / pixelCount),
                    meanCurrentSaturation = (float)(sumCurSat / pixelCount),
                    meanRedDelta = (float)(sumRedDelta / pixelCount),
                    meanGreenDelta = (float)(sumGreenDelta / pixelCount),
                    meanBlueDelta = (float)(sumBlueDelta / pixelCount),
                    meanReferenceGradient = gradSamples > 0 ? (float)(refGradSum / gradSamples) : 0f,
                    meanCurrentGradient = gradSamples > 0 ? (float)(curGradSum / gradSamples) : 0f,
                    edgeError = edgeCount > 0 ? (float)(edgeErrorSum / edgeCount) : 0f,
                    centerError = centerCount > 0 ? (float)(centerErrorSum / centerCount) : 0f,
                    edgeLumaDelta = edgeCount > 0 ? (float)(edgeLumaDeltaSum / edgeCount) : 0f,
                    centerLumaDelta = centerCount > 0 ? (float)(centerLumaDeltaSum / centerCount) : 0f,
                    gridSize = gridSize,
                    maxCellError = (float)maxCellError,
                    hotspots = hotspots,
                    cellColors = cellColorsList,
                    // Composition
                    refTopLuminance = refTopLuma, curTopLuminance = curTopLuma,
                    refMidLuminance = refMidLuma, curMidLuminance = curMidLuma,
                    refBotLuminance = refBotLuma, curBotLuminance = curBotLuma,
                    refTopEdgeDensity = refTopEdge, curTopEdgeDensity = curTopEdge,
                    refMidEdgeDensity = refMidEdge, curMidEdgeDensity = curMidEdge,
                    refBotEdgeDensity = refBotEdge, curBotEdgeDensity = curBotEdge,
                    refHorizonY = refHorizonY, curHorizonY = curHorizonY,
                    refCenterX = refCenterX, refCenterY = refCenterY,
                    curCenterX = curCenterX, curCenterY = curCenterY
                };

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (referenceWork != null && referenceWork != referenceTexture)
                    UnityEngine.Object.DestroyImmediate(referenceWork);
                if (currentWork != null && currentWork != currentTexture)
                    UnityEngine.Object.DestroyImmediate(currentWork);
            }
        }

        // ReproStep and algorithm methods moved to UnityCommands.ImageCompare.Algorithms.cs

        private static List<string> GenerateSuggestions(ImageCompareAnalysis analysis)
        {
            var candidates = new List<(float magnitude, string suggestion)>();

            // === COMPOSITIONAL / SPATIAL SUGGESTIONS (highest priority) ===

            // Horizon line mismatch → camera angle is wrong
            float horizonDelta = analysis.curHorizonY - analysis.refHorizonY;
            if (Mathf.Abs(horizonDelta) > 0.08f)
            {
                if (horizonDelta > 0)
                    candidates.Add((Mathf.Abs(horizonDelta) + 0.5f,
                        $"CAMERA: Horizon is too low in frame (at {analysis.curHorizonY:P0} vs reference {analysis.refHorizonY:P0}). "
                        + "Too much ground visible. Tilt camera UP or increase camera pivot Y."));
                else
                    candidates.Add((Mathf.Abs(horizonDelta) + 0.5f,
                        $"CAMERA: Horizon is too high in frame (at {analysis.curHorizonY:P0} vs reference {analysis.refHorizonY:P0}). "
                        + "Too much sky visible. Tilt camera DOWN or decrease camera pivot Y."));
            }

            // Visual center of mass mismatch → subject is in wrong position
            float comDeltaX = analysis.curCenterX - analysis.refCenterX;
            float comDeltaY = analysis.curCenterY - analysis.refCenterY;
            float comDist = Mathf.Sqrt(comDeltaX * comDeltaX + comDeltaY * comDeltaY);
            if (comDist > 0.08f)
            {
                var dirs = new List<string>();
                if (comDeltaY > 0.05f) dirs.Add("too low");
                if (comDeltaY < -0.05f) dirs.Add("too high");
                if (comDeltaX > 0.05f) dirs.Add("too far right");
                if (comDeltaX < -0.05f) dirs.Add("too far left");
                string dirStr = string.Join(" and ", dirs);
                candidates.Add((comDist + 0.5f,
                    $"FRAMING: Main visual subject is {dirStr} compared to reference. "
                    + "Adjust camera pivot position or rotation to re-center the composition."));
            }

            // Sky/ground balance via vertical band luminance
            float refSkyGroundRatio = analysis.refTopLuminance / Mathf.Max(0.01f, analysis.refBotLuminance);
            float curSkyGroundRatio = analysis.curTopLuminance / Mathf.Max(0.01f, analysis.curBotLuminance);
            float sgDelta = curSkyGroundRatio - refSkyGroundRatio;
            if (Mathf.Abs(sgDelta) > 0.5f)
            {
                if (sgDelta > 0)
                    candidates.Add((Mathf.Abs(sgDelta) * 0.3f + 0.3f,
                        "COMPOSITION: Top of image is relatively too bright vs bottom compared to reference. "
                        + "Sky may be overexposed or ground too dark. Check skybox exposure and ground lighting."));
                else
                    candidates.Add((Mathf.Abs(sgDelta) * 0.3f + 0.3f,
                        "COMPOSITION: Bottom of image is relatively too bright vs top compared to reference. "
                        + "Ground may be overlit or sky too dark. Check ground materials and sky brightness."));
            }

            // Edge density distribution mismatch → objects/detail in wrong areas
            float refTotalEdge = analysis.refTopEdgeDensity + analysis.refMidEdgeDensity + analysis.refBotEdgeDensity;
            float curTotalEdge = analysis.curTopEdgeDensity + analysis.curMidEdgeDensity + analysis.curBotEdgeDensity;
            if (refTotalEdge > 0.001f && curTotalEdge > 0.001f)
            {
                float refMidFrac = analysis.refMidEdgeDensity / refTotalEdge;
                float curMidFrac = analysis.curMidEdgeDensity / curTotalEdge;
                float refTopFrac = analysis.refTopEdgeDensity / refTotalEdge;
                float curTopFrac = analysis.curTopEdgeDensity / curTotalEdge;
                float refBotFrac = analysis.refBotEdgeDensity / refTotalEdge;
                float curBotFrac = analysis.curBotEdgeDensity / curTotalEdge;

                // Find the band with the biggest fractional mismatch
                float midDiff = curMidFrac - refMidFrac;
                float topDiff = curTopFrac - refTopFrac;
                float botDiff = curBotFrac - refBotFrac;

                if (Mathf.Abs(topDiff) > 0.15f)
                {
                    string detail = topDiff > 0 ? "too much detail/objects in the top third (sky area)" : "not enough detail in the top third — reference has more objects/texture there (trees, mountains, clouds)";
                    candidates.Add((Mathf.Abs(topDiff) * 0.4f + 0.2f,
                        $"LAYOUT: {detail}. Check background object placement and sky elements."));
                }
                if (Mathf.Abs(midDiff) > 0.15f)
                {
                    string detail = midDiff > 0 ? "too much detail in the middle third" : "not enough detail in the middle third — reference has more objects there (main structure, trees)";
                    candidates.Add((Mathf.Abs(midDiff) * 0.4f + 0.2f,
                        $"LAYOUT: {detail}. Check main subject size and position."));
                }
                if (Mathf.Abs(botDiff) > 0.15f)
                {
                    string detail = botDiff > 0 ? "too much detail in the bottom third (foreground)" : "not enough detail in the bottom third — reference has more foreground objects (rocks, ground texture)";
                    candidates.Add((Mathf.Abs(botDiff) * 0.4f + 0.2f,
                        $"LAYOUT: {detail}. Check foreground object placement and ground coverage."));
                }
            }

            // === COLOR / LIGHTING SUGGESTIONS ===

            float lumaDelta = analysis.meanCurrentLuminance - analysis.meanReferenceLuminance;
            if (Mathf.Abs(lumaDelta) > 0.15f)
            {
                string dir = lumaDelta > 0 ? "brighter" : "darker";
                candidates.Add((Mathf.Abs(lumaDelta), $"LIGHTING: Scene is significantly {dir} than reference. Adjust lighting intensity or add/remove lights."));
            }

            float satDelta = analysis.meanCurrentSaturation - analysis.meanReferenceSaturation;
            if (Mathf.Abs(satDelta) > 0.12f)
            {
                string dir = satDelta > 0 ? "oversaturated" : "undersaturated";
                candidates.Add((Mathf.Abs(satDelta), $"COLOR: Colors are {dir} vs reference. Check material colors and lighting color temperature."));
            }

            if (analysis.structuralSimilarity < 0.6f)
            {
                candidates.Add((1f - analysis.structuralSimilarity,
                    "STRUCTURE: Structural layout differs significantly. Focus on camera framing and major object placement before color adjustments."));
            }

            if (analysis.changedPixelRatio > 0.5f)
            {
                candidates.Add((analysis.changedPixelRatio, "STRUCTURE: Large structural differences detected. Focus on major object placement, scale, and presence before fine-tuning colors."));
            }

            if (analysis.changedPixelRatio < 0.15f && analysis.similarityScore > 0.85f)
            {
                candidates.Add((analysis.similarityScore, "Scene is close to reference. Focus on color/material tweaks rather than structural changes."));
            }

            // Channel deltas (only include if significant and not already covered by spatial)
            if (Mathf.Abs(analysis.meanRedDelta) > 0.1f)
            {
                string dir = analysis.meanRedDelta < -0.1f ? "high" : "low";
                candidates.Add((Mathf.Abs(analysis.meanRedDelta), $"COLOR: Red channel is too {dir}. Adjust material colors or lighting color."));
            }

            if (Mathf.Abs(analysis.meanGreenDelta) > 0.1f)
            {
                string dir = analysis.meanGreenDelta < -0.1f ? "high" : "low";
                candidates.Add((Mathf.Abs(analysis.meanGreenDelta), $"COLOR: Green channel is too {dir}. Adjust material colors or lighting color."));
            }

            if (Mathf.Abs(analysis.meanBlueDelta) > 0.1f)
            {
                string dir = analysis.meanBlueDelta < -0.1f ? "high" : "low";
                candidates.Add((Mathf.Abs(analysis.meanBlueDelta), $"COLOR: Blue channel is too {dir}. Adjust material colors or lighting color."));
            }

            if (analysis.hotspots != null && analysis.hotspots.Count > 0)
            {
                var zones = new Dictionary<string, List<double>>();
                int gs = analysis.gridSize;
                foreach (var obj in analysis.hotspots)
                {
                    if (obj is Dictionary<string, object> h)
                    {
                        int cellX = Convert.ToInt32(h["cellX"]);
                        int cellY = Convert.ToInt32(h["cellY"]);
                        double err = Convert.ToDouble(h["error"]);
                        string compass = GetCompassDirection(cellY, cellX, gs);
                        if (!zones.ContainsKey(compass)) zones[compass] = new List<double>();
                        zones[compass].Add(err);
                    }
                }
                var topZones = zones
                    .OrderByDescending(kv => kv.Value.Average())
                    .Take(3)
                    .Select(kv => kv.Key)
                    .ToList();
                if (topZones.Count > 0)
                {
                    float maxErr = analysis.hotspots.Cast<Dictionary<string, object>>().Max(h => (float)Convert.ToDouble(h["error"]));
                    candidates.Add((maxErr, $"HOTSPOTS: Biggest differences in regions: {string.Join(", ", topZones)}. Check objects and materials in these areas."));
                }
            }

            return candidates
                .OrderByDescending(c => c.magnitude)
                .Take(5)
                .Select(c => c.suggestion)
                .ToList();
        }

        private static string GetCompassDirection(int row, int col, int gridSize)
        {
            string vertical = row < gridSize / 3 ? "top" : row < 2 * gridSize / 3 ? "center" : "bottom";
            string horizontal = col < gridSize / 3 ? "left" : col < 2 * gridSize / 3 ? "center" : "right";
            if (vertical == "center" && horizontal == "center") return "center";
            if (vertical == "center") return $"center-{horizontal}";
            if (horizontal == "center") return $"{vertical}-center";
            return $"{vertical}-{horizontal}";
        }
    }
}
