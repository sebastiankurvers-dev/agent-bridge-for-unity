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
        private static void PruneImageStoreLocked()
        {
            if (_imageStore.Count <= MaxStoredImages && _imageStore.Values.Sum(v => v.byteSize) <= MaxStoredImageBytes)
            {
                return;
            }

            var ordered = _imageStore.Values
                .OrderBy(v => v.lastAccessUtc)
                .ToList();

            int totalBytes = _imageStore.Values.Sum(v => v.byteSize);
            foreach (var entry in ordered)
            {
                if (_imageStore.Count <= MaxStoredImages && totalBytes <= MaxStoredImageBytes)
                {
                    break;
                }

                // Skip handles protected by active frame sequence captures
                if (IsHandleProtectedByCapture(entry.handle))
                    continue;

                if (_imageStore.Remove(entry.handle))
                {
                    totalBytes -= entry.byteSize;
                }
            }
        }

        private static Dictionary<string, object> BuildCompareImageResponse(ImageCompareAnalysis analysis, bool includeHeatmap)
        {
            var metrics = new Dictionary<string, object>
            {
                { "similarityScore", (double)Math.Round(analysis.similarityScore, 4) },
                { "structuralSimilarity", (double)Math.Round(analysis.structuralSimilarity, 4) },
                { "changedPixelRatio", (double)Math.Round(analysis.changedPixelRatio, 4) },
                { "meanAbsoluteError", (double)Math.Round(analysis.meanAbsoluteError, 4) },
                { "luminanceDelta", (double)Math.Round(analysis.meanCurrentLuminance - analysis.meanReferenceLuminance, 4) },
                { "saturationDelta", (double)Math.Round(analysis.meanCurrentSaturation - analysis.meanReferenceSaturation, 4) },
                { "channelDeltas", new List<object> {
                    (double)Math.Round(analysis.meanRedDelta, 4),
                    (double)Math.Round(analysis.meanGreenDelta, 4),
                    (double)Math.Round(analysis.meanBlueDelta, 4)
                }},
                { "edgeVsCenter", new List<object> {
                    (double)Math.Round(analysis.edgeError, 4),
                    (double)Math.Round(analysis.centerError, 4)
                }},
                { "analysisSize", new List<object> { analysis.width, analysis.height } }
            };

            // Composition data — spatial/positional analysis
            var composition = new Dictionary<string, object>
            {
                { "horizonY", new List<object> { (double)Math.Round(analysis.refHorizonY, 2), (double)Math.Round(analysis.curHorizonY, 2) } },
                { "visualCenter", new List<object> {
                    new List<object> { (double)Math.Round(analysis.refCenterX, 2), (double)Math.Round(analysis.refCenterY, 2) },
                    new List<object> { (double)Math.Round(analysis.curCenterX, 2), (double)Math.Round(analysis.curCenterY, 2) }
                }},
                { "verticalLuminance", new List<object> {
                    new List<object> { (double)Math.Round(analysis.refTopLuminance, 2), (double)Math.Round(analysis.refMidLuminance, 2), (double)Math.Round(analysis.refBotLuminance, 2) },
                    new List<object> { (double)Math.Round(analysis.curTopLuminance, 2), (double)Math.Round(analysis.curMidLuminance, 2), (double)Math.Round(analysis.curBotLuminance, 2) }
                }},
                { "edgeDensity", new List<object> {
                    new List<object> { (double)Math.Round(analysis.refTopEdgeDensity, 3), (double)Math.Round(analysis.refMidEdgeDensity, 3), (double)Math.Round(analysis.refBotEdgeDensity, 3) },
                    new List<object> { (double)Math.Round(analysis.curTopEdgeDensity, 3), (double)Math.Round(analysis.curMidEdgeDensity, 3), (double)Math.Round(analysis.curBotEdgeDensity, 3) }
                }}
            };

            var response = new Dictionary<string, object>
            {
                { "success", true },
                { "metrics", metrics },
                { "composition", composition }
            };

            if (includeHeatmap)
            {
                var heatmapData = new Dictionary<string, object>
                {
                    { "gridSize", analysis.gridSize },
                    { "maxCellError", (double)Math.Round(analysis.maxCellError, 6) },
                    { "hotspots", analysis.hotspots }
                };

                if (analysis.cellColors != null && analysis.cellColors.Count > 0)
                {
                    // Only include the top 8 worst cells to keep response compact
                    heatmapData["cellColors"] = analysis.cellColors
                        .OrderByDescending(c => c.cellError)
                        .Take(8)
                        .Select(c => (object)new Dictionary<string, object>
                        {
                            { "row", c.row },
                            { "col", c.col },
                            { "error", (double)Math.Round(c.cellError, 4) },
                            { "lumaDelta", (double)Math.Round(c.refLuminance - c.curLuminance, 4) }
                        })
                        .ToList();
                }

                response["heatmap"] = heatmapData;
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
                    .Select(kv => new Dictionary<string, object>
                    {
                        { "zone", kv.Key },
                        { "averageError", (double)Math.Round(kv.Value.Average(), 6) }
                    })
                    .Cast<object>()
                    .ToList();
                response["hotspotSummary"] = topZones;
            }

            var suggestions = GenerateSuggestions(analysis);
            if (suggestions.Count > 0)
            {
                response["suggestions"] = suggestions.Cast<object>().ToList();
            }

            return response;
        }

        private static Dictionary<string, object> ApplyResponseProjection(
            Dictionary<string, object> response,
            string fields,
            bool omitEmpty,
            int maxItems)
        {
            int listLimit = maxItems > 0 ? Mathf.Clamp(maxItems, 1, 2000) : int.MaxValue;
            var clipped = ClipResponseValue(response, omitEmpty, listLimit) as Dictionary<string, object> ?? response;

            if (string.IsNullOrWhiteSpace(fields))
            {
                return clipped;
            }

            var selectedFields = fields
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (selectedFields.Count == 0)
            {
                return clipped;
            }

            var projected = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (clipped.TryGetValue("success", out var success))
            {
                projected["success"] = success;
            }

            foreach (var field in selectedFields)
            {
                var parts = field.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                if (!TryGetPathValue(clipped, parts, 0, out var value)) continue;
                if (omitEmpty && IsEmptyProjectionValue(value)) continue;
                SetPathValue(projected, parts, 0, value);
            }

            if (projected.Count == 0)
            {
                return clipped;
            }

            return projected;
        }

        private static object ClipResponseValue(object value, bool omitEmpty, int listLimit)
        {
            if (value == null)
            {
                return null;
            }

            if (value is Dictionary<string, object> dict)
            {
                var result = new Dictionary<string, object>(dict.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var kv in dict)
                {
                    var clipped = ClipResponseValue(kv.Value, omitEmpty, listLimit);
                    if (omitEmpty && IsEmptyProjectionValue(clipped))
                    {
                        continue;
                    }
                    result[kv.Key] = clipped;
                }
                return result;
            }

            if (value is List<object> list)
            {
                int take = Math.Min(list.Count, listLimit);
                var result = new List<object>(take);
                for (int i = 0; i < take; i++)
                {
                    var clipped = ClipResponseValue(list[i], omitEmpty, listLimit);
                    if (omitEmpty && IsEmptyProjectionValue(clipped))
                    {
                        continue;
                    }
                    result.Add(clipped);
                }
                return result;
            }

            return value;
        }

        private static bool IsEmptyProjectionValue(object value)
        {
            if (value == null) return true;
            if (value is string str) return string.IsNullOrWhiteSpace(str);
            if (value is Dictionary<string, object> dict) return dict.Count == 0;
            if (value is List<object> list) return list.Count == 0;
            return false;
        }

        private static bool TryGetPathValue(object node, string[] parts, int depth, out object value)
        {
            value = null;
            if (node == null || parts == null || depth >= parts.Length)
            {
                return false;
            }

            if (node is not Dictionary<string, object> dict)
            {
                return false;
            }

            if (!dict.TryGetValue(parts[depth], out var current))
            {
                return false;
            }

            if (depth == parts.Length - 1)
            {
                value = current;
                return true;
            }

            return TryGetPathValue(current, parts, depth + 1, out value);
        }

        private static void SetPathValue(Dictionary<string, object> target, string[] parts, int depth, object value)
        {
            if (depth == parts.Length - 1)
            {
                target[parts[depth]] = value;
                return;
            }

            if (!target.TryGetValue(parts[depth], out var child) || child is not Dictionary<string, object> childDict)
            {
                childDict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                target[parts[depth]] = childDict;
            }

            SetPathValue(childDict, parts, depth + 1, value);
        }

    }
}
