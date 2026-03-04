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
        private static bool TryAnalyzeImageDifference(
            string referenceImageBase64,
            string currentImageBase64,
            int downsampleMaxSize,
            int gridSize,
            float changedPixelThreshold,
            float hotspotThreshold,
            out ImageCompareAnalysis analysis,
            out string error)
        {
            analysis = null;
            error = null;

            Texture2D referenceTexture = null;
            Texture2D currentTexture = null;
            Texture2D referenceWork = null;
            Texture2D currentWork = null;

            try
            {
                if (!TryDecodeImage(referenceImageBase64, out referenceTexture, out error))
                {
                    return false;
                }
                if (!TryDecodeImage(currentImageBase64, out currentTexture, out error))
                {
                    return false;
                }

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
                var cellRefLumaSq = new double[cellTotal]; // for variance
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

                // Approximate sharpness via luminance gradients.
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

                // Build per-cell color info for region diagnostics
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

                // Compute per-cell SSIM
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
                    double covVal = cell.covariance;
                    double ssim = ((2d * muR * muC + C1) * (2d * covVal + C2))
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
                double changePenalty = 1d - Math.Min(1d, cpRatio * 1.5d); // aggressive: 67% changed = 0 score

                // Multi-signal similarity: weight structural signals heavily
                // colorSimilarity alone is too generous (random images score ~80%)
                // Use geometric-mean-like blending so any bad signal pulls score down
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
                if (referenceTexture != null)
                    UnityEngine.Object.DestroyImmediate(referenceTexture);
                if (currentTexture != null)
                    UnityEngine.Object.DestroyImmediate(currentTexture);
            }
        }

        private static void ComputeComposition(Color32[] pixels, int w, int h,
            out float topLuma, out float midLuma, out float botLuma,
            out float topEdge, out float midEdge, out float botEdge,
            out float horizonY, out float centerX, out float centerY)
        {
            int thirdH = Mathf.Max(1, h / 3);
            // Vertical band luminance
            double topSum = 0, midSum = 0, botSum = 0;
            int topCount = 0, midCount = 0, botCount = 0;
            // Edge density per band
            double topGrad = 0, midGrad = 0, botGrad = 0;
            int topGradN = 0, midGradN = 0, botGradN = 0;
            // Center of mass weighted by edge strength
            double comX = 0, comY = 0, comW = 0;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    float luma = Luminance(pixels[idx]);

                    if (y < thirdH) { topSum += luma; topCount++; }
                    else if (y < thirdH * 2) { midSum += luma; midCount++; }
                    else { botSum += luma; botCount++; }

                    // Edge detection (gradient magnitude)
                    if (x < w - 1 && y < h - 1)
                    {
                        float lumaR = Luminance(pixels[idx + 1]);
                        float lumaD = Luminance(pixels[idx + w]);
                        float grad = Mathf.Abs(lumaR - luma) + Mathf.Abs(lumaD - luma);

                        if (y < thirdH) { topGrad += grad; topGradN++; }
                        else if (y < thirdH * 2) { midGrad += grad; midGradN++; }
                        else { botGrad += grad; botGradN++; }

                        // Weight center of mass by gradient (edges = visual interest)
                        comX += x * grad;
                        comY += y * grad;
                        comW += grad;
                    }
                }
            }

            topLuma = topCount > 0 ? (float)(topSum / topCount) : 0;
            midLuma = midCount > 0 ? (float)(midSum / midCount) : 0;
            botLuma = botCount > 0 ? (float)(botSum / botCount) : 0;
            topEdge = topGradN > 0 ? (float)(topGrad / topGradN) : 0;
            midEdge = midGradN > 0 ? (float)(midGrad / midGradN) : 0;
            botEdge = botGradN > 0 ? (float)(botGrad / botGradN) : 0;

            // Center of mass (normalized 0-1)
            if (comW > 0.001)
            {
                centerX = Mathf.Clamp01((float)(comX / comW) / Mathf.Max(1, w - 1));
                centerY = Mathf.Clamp01((float)(comY / comW) / Mathf.Max(1, h - 1));
            }
            else
            {
                centerX = 0.5f;
                centerY = 0.5f;
            }

            // Horizon detection: find the row with the biggest luminance transition
            // Scan from top to bottom, find where luminance changes most (sky->ground boundary)
            horizonY = 0.5f;
            float maxTransition = 0;
            int scanStep = Mathf.Max(1, h / 64);
            for (int y = scanStep; y < h - scanStep; y += scanStep)
            {
                // Average luminance of row y vs row y+scanStep
                double rowAbove = 0, rowBelow = 0;
                int sampleCount = 0;
                int sampleStep = Mathf.Max(1, w / 32);
                for (int x = 0; x < w; x += sampleStep)
                {
                    rowAbove += Luminance(pixels[y * w + x]);
                    rowBelow += Luminance(pixels[(y + scanStep) * w + x]);
                    sampleCount++;
                }
                if (sampleCount > 0)
                {
                    float diff = Mathf.Abs((float)(rowAbove / sampleCount - rowBelow / sampleCount));
                    if (diff > maxTransition)
                    {
                        maxTransition = diff;
                        horizonY = (float)y / h;
                    }
                }
            }
        }

        private static float Luminance(Color32 c)
        {
            return (c.r / 255f) * 0.2126f + (c.g / 255f) * 0.7152f + (c.b / 255f) * 0.0722f;
        }

        private static bool TryDecodeImage(string rawBase64, out Texture2D texture, out string error)
        {
            texture = null;
            error = null;

            if (string.IsNullOrWhiteSpace(rawBase64))
            {
                error = "Image base64 input is empty";
                return false;
            }

            try
            {
                string normalized = NormalizeBase64Payload(rawBase64);

                var bytes = Convert.FromBase64String(normalized);
                texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!texture.LoadImage(bytes, false))
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                    texture = null;
                    error = "Failed to decode image bytes";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to decode base64 image: {ex.Message}";
                return false;
            }
        }


        private class CellColorInfo
        {
            public int row, col;
            public float refR, refG, refB;
            public float curR, curG, curB;
            public float refLuminance, curLuminance;
            public float refVariance, curVariance;
            public float covariance;
            public float cellError;
        }

        private class ImageCompareAnalysis
        {
            public int referenceWidth;
            public int referenceHeight;
            public int currentWidth;
            public int currentHeight;
            public int width;
            public int height;
            public float similarityScore;
            public float meanAbsoluteError;
            public float rootMeanSquareError;
            public float changedPixelRatio;
            public float changedPixelThreshold;
            public float meanReferenceLuminance;
            public float meanCurrentLuminance;
            public float meanReferenceSaturation;
            public float meanCurrentSaturation;
            public float meanRedDelta;
            public float meanGreenDelta;
            public float meanBlueDelta;
            public float meanReferenceGradient;
            public float meanCurrentGradient;
            public float edgeError;
            public float centerError;
            public float edgeLumaDelta;
            public float centerLumaDelta;
            public int gridSize;
            public float maxCellError;
            public float structuralSimilarity;
            public List<object> hotspots = new List<object>();
            public List<CellColorInfo> cellColors = new List<CellColorInfo>();

            // Compositional analysis: vertical band luminance (top/middle/bottom thirds)
            public float refTopLuminance, curTopLuminance;
            public float refMidLuminance, curMidLuminance;
            public float refBotLuminance, curBotLuminance;
            // Edge density per vertical third (where are detailed/textured areas)
            public float refTopEdgeDensity, curTopEdgeDensity;
            public float refMidEdgeDensity, curMidEdgeDensity;
            public float refBotEdgeDensity, curBotEdgeDensity;
            // Estimated horizon line (normalized 0=top, 1=bottom)
            public float refHorizonY, curHorizonY;
            // Visual center of mass (normalized 0-1)
            public float refCenterX, refCenterY;
            public float curCenterX, curCenterY;
        }

        private class ReproPatchProposal
        {
            public string id;
            public string title;
            public string rationale;
            public float confidence;
            public string operation;
            public string tool;
            public Dictionary<string, object> payload;

            public Dictionary<string, object> ToJson()
            {
                return new Dictionary<string, object>
                {
                    { "id", id ?? string.Empty },
                    { "title", title ?? string.Empty },
                    { "rationale", rationale ?? string.Empty },
                    { "confidence", (double)Math.Round(confidence, 4) },
                    { "operation", operation ?? string.Empty },
                    { "tool", tool ?? string.Empty },
                    { "payload", payload ?? new Dictionary<string, object>() }
                };
            }
        }
    }
}
