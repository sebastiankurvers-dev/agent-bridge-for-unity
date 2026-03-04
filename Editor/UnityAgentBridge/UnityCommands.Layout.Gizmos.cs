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
	        private static bool TryGetAggregateBounds(GameObject go, bool includeInactive, out Bounds bounds, out string source)
	        {
	            bounds = default;
	            source = string.Empty;
	            if (!go) return false;

            bool hasBounds = false;
            var renderers = go.GetComponentsInChildren<Renderer>(includeInactive);
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (hasBounds)
            {
                source = "renderer";
                return true;
            }

	            var colliders = go.GetComponentsInChildren<Collider>(includeInactive);
	            foreach (var collider in colliders)
	            {
	                if (!TryGetSafeColliderBounds(collider, out var cb)) continue;
	                if (!hasBounds)
	                {
	                    bounds = cb;
	                    hasBounds = true;
	                }
	                else
	                {
	                    bounds.Encapsulate(cb);
	                }
	            }

            if (hasBounds)
            {
                source = "collider3d";
                return true;
            }

            var colliders2D = go.GetComponentsInChildren<Collider2D>(includeInactive);
            foreach (var collider2D in colliders2D)
            {
                if (collider2D == null) continue;
                if (!hasBounds)
                {
                    bounds = collider2D.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(collider2D.bounds);
                }
            }

            if (hasBounds)
            {
                source = "collider2d";
                return true;
            }

	            return false;
	        }

	        private static bool TryGetSafeColliderBounds(Collider collider, out Bounds bounds)
	        {
	            bounds = default;
	            if (!collider) return false;

	            // Guard known invalid native states before touching collider.bounds.
	            if (collider is MeshCollider meshCollider && meshCollider.sharedMesh == null)
	            {
	                return false;
	            }

	            if (collider is TerrainCollider terrainCollider && terrainCollider.terrainData == null)
	            {
	                return false;
	            }

	            try
	            {
	                bounds = collider.bounds;
	                if (float.IsNaN(bounds.center.x) || float.IsNaN(bounds.center.y) || float.IsNaN(bounds.center.z)) return false;
	                if (float.IsInfinity(bounds.center.x) || float.IsInfinity(bounds.center.y) || float.IsInfinity(bounds.center.z)) return false;
	                if (float.IsNaN(bounds.size.x) || float.IsNaN(bounds.size.y) || float.IsNaN(bounds.size.z)) return false;
	                if (float.IsInfinity(bounds.size.x) || float.IsInfinity(bounds.size.y) || float.IsInfinity(bounds.size.z)) return false;
	                return true;
	            }
	            catch
	            {
	                return false;
	            }
	        }

        private static float ComputeSignedAabbDistance2D(TileBoundsSample a, TileBoundsSample b, out float dxGap, out float dzGap)
        {
            dxGap = Mathf.Abs(a.centerXZ.x - b.centerXZ.x) - (a.extentsXZ.x + b.extentsXZ.x);
            dzGap = Mathf.Abs(a.centerXZ.y - b.centerXZ.y) - (a.extentsXZ.y + b.extentsXZ.y);

            if (dxGap <= 0f && dzGap <= 0f)
            {
                return -Mathf.Min(-dxGap, -dzGap);
            }

            float gx = Mathf.Max(dxGap, 0f);
            float gz = Mathf.Max(dzGap, 0f);
            return Mathf.Sqrt((gx * gx) + (gz * gz));
        }

        private static float ComputeMedian(IEnumerable<float> values)
        {
            if (values == null) return 0f;
            var ordered = values
                .Where(v => !float.IsNaN(v) && !float.IsInfinity(v))
                .OrderBy(v => v)
                .ToList();
            if (ordered.Count == 0) return 0f;
            if (ordered.Count % 2 == 1) return ordered[ordered.Count / 2];
            int hi = ordered.Count / 2;
            return (ordered[hi - 1] + ordered[hi]) * 0.5f;
        }

        private static float ComputePercentile(IEnumerable<float> values, float percentile)
        {
            if (values == null) return 0f;
            var ordered = values
                .Where(v => !float.IsNaN(v) && !float.IsInfinity(v))
                .OrderBy(v => v)
                .ToList();
            if (ordered.Count == 0) return 0f;

            float p = Mathf.Clamp01(percentile);
            if (ordered.Count == 1) return ordered[0];

            float index = (ordered.Count - 1) * p;
            int lo = Mathf.FloorToInt(index);
            int hi = Mathf.Clamp(lo + 1, 0, ordered.Count - 1);
            if (lo == hi) return ordered[lo];
            float t = index - lo;
            return Mathf.Lerp(ordered[lo], ordered[hi], t);
        }

        private static void EstimateCurvature(List<TileBoundsSample> samples, out float curvatureMax, out float curvatureNormalized)
        {
            curvatureMax = 0f;
            curvatureNormalized = 0f;
            if (samples == null || samples.Count < 6)
            {
                return;
            }

            var sorted = samples
                .OrderBy(s => s.centerXZ.y)
                .ToList();

            float minForward = sorted.First().centerXZ.y;
            float maxForward = sorted.Last().centerXZ.y;
            float range = maxForward - minForward;
            if (range <= 0.0001f) return;

            int binCount = Mathf.Clamp(Mathf.RoundToInt(Mathf.Sqrt(sorted.Count)), 8, 40);
            var laneSums = new float[binCount];
            var counts = new int[binCount];

            foreach (var sample in sorted)
            {
                float t = Mathf.Clamp01((sample.centerXZ.y - minForward) / range);
                int bin = Mathf.Clamp(Mathf.FloorToInt(t * (binCount - 1)), 0, binCount - 1);
                laneSums[bin] += sample.centerXZ.x;
                counts[bin]++;
            }

            var laneMeans = new float[binCount];
            for (int i = 0; i < binCount; i++)
            {
                laneMeans[i] = counts[i] > 0 ? laneSums[i] / counts[i] : float.NaN;
            }

            float step = range / Mathf.Max(1, binCount - 1);
            for (int i = 1; i < binCount - 1; i++)
            {
                if (float.IsNaN(laneMeans[i - 1]) || float.IsNaN(laneMeans[i]) || float.IsNaN(laneMeans[i + 1]))
                {
                    continue;
                }

                float secondDerivative = Mathf.Abs((laneMeans[i + 1] - 2f * laneMeans[i] + laneMeans[i - 1]) / Mathf.Max(0.0001f, step * step));
                if (secondDerivative > curvatureMax)
                {
                    curvatureMax = secondDerivative;
                }
            }

            float medianWidth = ComputeMedian(samples.Select(s => s.sizeXZ.x));
            curvatureNormalized = Mathf.Clamp01(curvatureMax * Mathf.Max(0.25f, medianWidth));
        }

        private static void AnalyzeScreenshotBleed(
            string screenshotView,
            float brightThreshold,
            float glowThreshold,
            out float brightRatio,
            out float glowRatio,
            out float bleedRatio,
            out float clippedRatio,
            out float neonRatio,
            out string screenshotHandle,
            out string error)
        {
            brightRatio = 0f;
            glowRatio = 0f;
            bleedRatio = 0f;
            clippedRatio = 0f;
            neonRatio = 0f;
            screenshotHandle = null;
            error = null;

            if (!TryGetScreenshotData(string.IsNullOrWhiteSpace(screenshotView) ? "game" : screenshotView, out var screenshotBase64, out screenshotHandle, out var captureError, includeHandle: true))
            {
                error = captureError ?? "Failed to capture screenshot";
                return;
            }

            if (!TryDecodeImage(screenshotBase64, out var texture, out var decodeError))
            {
                error = decodeError ?? "Failed to decode screenshot";
                return;
            }

            try
            {
                var pixels = texture.GetPixels32();
                if (pixels == null || pixels.Length == 0)
                {
                    error = "Screenshot has no pixels";
                    return;
                }

                int brightCount = 0;
                int glowCount = 0;
                int clippedCount = 0;
                int neonCount = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    float r = p.r / 255f;
                    float g = p.g / 255f;
                    float b = p.b / 255f;
                    float luma = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);

                    if (luma >= brightThreshold)
                    {
                        brightCount++;
                    }
                    else if (luma >= glowThreshold)
                    {
                        glowCount++;
                    }

                    if (luma >= 0.995f)
                    {
                        clippedCount++;
                    }

                    Color.RGBToHSV(new Color(r, g, b), out _, out var sat, out _);
                    if (luma >= glowThreshold && sat >= 0.55f)
                    {
                        neonCount++;
                    }
                }

                float pixelCount = pixels.Length;
                brightRatio = brightCount / pixelCount;
                glowRatio = glowCount / pixelCount;
                clippedRatio = clippedCount / pixelCount;
                neonRatio = neonCount / pixelCount;
                bleedRatio = brightRatio > 0.000001f
                    ? glowRatio / brightRatio
                    : glowRatio * 10f;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static List<Dictionary<string, object>> BuildClosestPairSamples(List<TileBoundsSample> samples, int maxPairs)
        {
            var pairs = new List<(int i, int j, float distance)>();
            if (samples == null || samples.Count < 2) return new List<Dictionary<string, object>>();

            for (int i = 0; i < samples.Count; i++)
            {
                for (int j = i + 1; j < samples.Count; j++)
                {
                    float dx;
                    float dz;
                    float d = ComputeSignedAabbDistance2D(samples[i], samples[j], out dx, out dz);
                    pairs.Add((i, j, d));
                }
            }

            return pairs
                .OrderBy(p => p.distance)
                .Take(Mathf.Clamp(maxPairs, 1, 50))
                .Select(p =>
                {
                    var a = samples[p.i];
                    var b = samples[p.j];
                    return new Dictionary<string, object>
                    {
                        { "distance", p.distance },
                        { "aInstanceId", a.instanceId },
                        { "aName", a.name ?? string.Empty },
                        { "aPath", a.path ?? string.Empty },
                        { "bInstanceId", b.instanceId },
                        { "bName", b.name ?? string.Empty },
                        { "bPath", b.path ?? string.Empty }
                    };
                })
                .ToList();
        }

        // ==================== Spatial Enclosure Check ====================

        [BridgeRoute("POST", "/spatial/check-enclosure", Category = "spatial", Description = "Check wall/ceiling/floor enclosure", ReadOnly = true, TimeoutDefault = 15000)]
        public static string CheckSpatialEnclosure(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<CheckEnclosureRequest>(
                    string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData);
                if (request == null)
                    return JsonError("Failed to parse enclosure request");

                bool includeChildren = request.includeChildren != 0;
                float gapThreshold = Mathf.Max(0.001f, request.gapThreshold);

                // Collect bounds for each group
                if (!TryCollectGroupBounds(request.wallIds, includeChildren, out var wallBounds, out var wallIndividual, out string wallErr))
                    return JsonError("wallIds: " + wallErr);
                if (!TryCollectGroupBounds(request.ceilingIds, includeChildren, out var ceilingBounds, out var ceilingIndividual, out string ceilErr))
                    return JsonError("ceilingIds: " + ceilErr);
                if (!TryCollectGroupBounds(request.floorIds, includeChildren, out var floorBounds, out var floorIndividual, out string floorErr))
                    return JsonError("floorIds: " + floorErr);

                var gaps = new List<object>();

                // Ceiling-to-wall gap: check if ceiling bottom meets wall tops
                float ceilingBottomY = ceilingBounds.min.y;
                float wallTopY = wallBounds.max.y;
                float ceilWallGap = ceilingBottomY - wallTopY;
                if (ceilWallGap > gapThreshold)
                {
                    gaps.Add(new Dictionary<string, object>
                    {
                        { "between", "ceiling-walls" },
                        { "axis", "Y" },
                        { "size", Mathf.Round(ceilWallGap * 1000f) / 1000f },
                        { "location", new Dictionary<string, object>
                            {
                                { "ceilingMinY", Mathf.Round(ceilingBottomY * 1000f) / 1000f },
                                { "wallMaxY", Mathf.Round(wallTopY * 1000f) / 1000f }
                            }
                        }
                    });
                }

                // Floor-to-wall gap: check if floor top meets wall bottoms
                float floorTopY = floorBounds.max.y;
                float wallBottomY = wallBounds.min.y;
                float floorWallGap = wallBottomY - floorTopY;
                if (floorWallGap > gapThreshold)
                {
                    gaps.Add(new Dictionary<string, object>
                    {
                        { "between", "floor-walls" },
                        { "axis", "Y" },
                        { "size", Mathf.Round(floorWallGap * 1000f) / 1000f },
                        { "location", new Dictionary<string, object>
                            {
                                { "floorMaxY", Mathf.Round(floorTopY * 1000f) / 1000f },
                                { "wallMinY", Mathf.Round(wallBottomY * 1000f) / 1000f }
                            }
                        }
                    });
                }

                // Wall-to-wall gap analysis: check for holes between adjacent wall segments in XZ
                // Sort individual wall bounds by position and check for gaps
                var wallsByX = wallIndividual.OrderBy(b => b.center.x).ToList();
                for (int i = 0; i < wallsByX.Count - 1; i++)
                {
                    float gap = wallsByX[i + 1].min.x - wallsByX[i].max.x;
                    if (gap > gapThreshold)
                    {
                        gaps.Add(new Dictionary<string, object>
                        {
                            { "between", "wall-wall" },
                            { "axis", "X" },
                            { "size", Mathf.Round(gap * 1000f) / 1000f },
                            { "location", new Dictionary<string, object>
                                {
                                    { "leftWallMaxX", Mathf.Round(wallsByX[i].max.x * 1000f) / 1000f },
                                    { "rightWallMinX", Mathf.Round(wallsByX[i + 1].min.x * 1000f) / 1000f },
                                    { "atZ", Mathf.Round((wallsByX[i].center.z + wallsByX[i + 1].center.z) * 0.5f * 1000f) / 1000f }
                                }
                            }
                        });
                    }
                }

                var wallsByZ = wallIndividual.OrderBy(b => b.center.z).ToList();
                for (int i = 0; i < wallsByZ.Count - 1; i++)
                {
                    float gap = wallsByZ[i + 1].min.z - wallsByZ[i].max.z;
                    if (gap > gapThreshold)
                    {
                        gaps.Add(new Dictionary<string, object>
                        {
                            { "between", "wall-wall" },
                            { "axis", "Z" },
                            { "size", Mathf.Round(gap * 1000f) / 1000f },
                            { "location", new Dictionary<string, object>
                                {
                                    { "frontWallMaxZ", Mathf.Round(wallsByZ[i].max.z * 1000f) / 1000f },
                                    { "backWallMinZ", Mathf.Round(wallsByZ[i + 1].min.z * 1000f) / 1000f },
                                    { "atX", Mathf.Round((wallsByZ[i].center.x + wallsByZ[i + 1].center.x) * 0.5f * 1000f) / 1000f }
                                }
                            }
                        });
                    }
                }

                // Ceiling coverage: does ceiling XZ footprint cover the wall XZ footprint?
                float wallFootprintAreaXZ = (wallBounds.max.x - wallBounds.min.x) * (wallBounds.max.z - wallBounds.min.z);
                float ceilingFootprintAreaXZ = (ceilingBounds.max.x - ceilingBounds.min.x) * (ceilingBounds.max.z - ceilingBounds.min.z);

                // Intersection area
                float overlapMinX = Mathf.Max(wallBounds.min.x, ceilingBounds.min.x);
                float overlapMaxX = Mathf.Min(wallBounds.max.x, ceilingBounds.max.x);
                float overlapMinZ = Mathf.Max(wallBounds.min.z, ceilingBounds.min.z);
                float overlapMaxZ = Mathf.Min(wallBounds.max.z, ceilingBounds.max.z);
                float overlapAreaXZ = Mathf.Max(0f, overlapMaxX - overlapMinX) * Mathf.Max(0f, overlapMaxZ - overlapMinZ);
                float coveragePercent = wallFootprintAreaXZ > 0.001f ? (overlapAreaXZ / wallFootprintAreaXZ) * 100f : 0f;

                if (coveragePercent < 99f)
                {
                    gaps.Add(new Dictionary<string, object>
                    {
                        { "between", "ceiling-coverage" },
                        { "axis", "XZ" },
                        { "size", Mathf.Round((100f - coveragePercent) * 10f) / 10f },
                        { "location", new Dictionary<string, object>
                            {
                                { "wallFootprintXZ", new float[] { wallBounds.min.x, wallBounds.min.z, wallBounds.max.x, wallBounds.max.z } },
                                { "ceilingFootprintXZ", new float[] { ceilingBounds.min.x, ceilingBounds.min.z, ceilingBounds.max.x, ceilingBounds.max.z } }
                            }
                        }
                    });
                }

                bool sealed_ = gaps.Count == 0;

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "sealed", sealed_ },
                    { "gapThreshold", gapThreshold },
                    { "gapCount", gaps.Count },
                    { "gaps", gaps },
                    { "wallBounds", BoundsToDict(wallBounds) },
                    { "ceilingBounds", BoundsToDict(ceilingBounds) },
                    { "floorBounds", BoundsToDict(floorBounds) },
                    { "wallSegmentCount", wallIndividual.Count },
                    { "coveragePercent", Mathf.Round(coveragePercent * 10f) / 10f }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static bool TryCollectGroupBounds(int[] ids, bool includeChildren, out Bounds aggregate, out List<Bounds> individual, out string error)
        {
            aggregate = default;
            individual = new List<Bounds>();
            error = null;

            if (ids == null || ids.Length == 0)
            {
                error = "No instance IDs provided";
                return false;
            }

            bool hasAny = false;
            foreach (int id in ids)
            {
                var go = EditorUtility.EntityIdToObject(id) as GameObject;
                if (go == null)
                {
                    error = $"GameObject not found for instanceId {id}";
                    return false;
                }

                Renderer[] renderers;
                if (includeChildren)
                    renderers = go.GetComponentsInChildren<Renderer>(true);
                else
                    renderers = go.GetComponents<Renderer>();

                // If no renderers on the object itself and includeChildren is off, try children anyway
                if (renderers.Length == 0 && !includeChildren)
                    renderers = go.GetComponentsInChildren<Renderer>(true);

                foreach (var rend in renderers)
                {
                    if (rend == null) continue;
                    individual.Add(rend.bounds);
                    if (!hasAny)
                    {
                        aggregate = rend.bounds;
                        hasAny = true;
                    }
                    else
                    {
                        aggregate.Encapsulate(rend.bounds);
                    }
                }
            }

            if (!hasAny)
            {
                error = "No renderers found for the provided IDs";
                return false;
            }

            return true;
        }

        private static Dictionary<string, object> BoundsToDict(Bounds b)
        {
            return new Dictionary<string, object>
            {
                { "center", new float[] { Mathf.Round(b.center.x * 1000f) / 1000f, Mathf.Round(b.center.y * 1000f) / 1000f, Mathf.Round(b.center.z * 1000f) / 1000f } },
                { "size", new float[] { Mathf.Round(b.size.x * 1000f) / 1000f, Mathf.Round(b.size.y * 1000f) / 1000f, Mathf.Round(b.size.z * 1000f) / 1000f } },
                { "min", new float[] { Mathf.Round(b.min.x * 1000f) / 1000f, Mathf.Round(b.min.y * 1000f) / 1000f, Mathf.Round(b.min.z * 1000f) / 1000f } },
                { "max", new float[] { Mathf.Round(b.max.x * 1000f) / 1000f, Mathf.Round(b.max.y * 1000f) / 1000f, Mathf.Round(b.max.z * 1000f) / 1000f } }
            };
        }

    }
}
