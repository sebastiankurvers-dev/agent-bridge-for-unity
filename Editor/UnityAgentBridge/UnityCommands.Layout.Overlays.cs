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
        private static char ParseLayoutAxis(string axisRaw, char fallback)
        {
            if (string.IsNullOrWhiteSpace(axisRaw))
            {
                return fallback;
            }

            char axis = char.ToLowerInvariant(axisRaw.Trim()[0]);
            return axis == 'x' || axis == 'y' || axis == 'z' ? axis : fallback;
        }

        private static float GetAxisValue(Vector3 vector, char axis)
        {
            return axis switch
            {
                'x' => vector.x,
                'y' => vector.y,
                _ => vector.z
            };
        }

        private static Vector3 SetAxisValue(Vector3 vector, char axis, float value)
        {
            switch (axis)
            {
                case 'x':
                    vector.x = value;
                    break;
                case 'y':
                    vector.y = value;
                    break;
                default:
                    vector.z = value;
                    break;
            }

            return vector;
        }

        private static float ComputeSignedAabbDistanceMasked(LayoutNodeState a, LayoutNodeState b, LayoutAxesMask mask, out Vector3 pushDirection)
        {
            float dxGap = Mathf.Abs(a.position.x - b.position.x) - (a.extents.x + b.extents.x);
            float dyGap = Mathf.Abs(a.position.y - b.position.y) - (a.extents.y + b.extents.y);
            float dzGap = Mathf.Abs(a.position.z - b.position.z) - (a.extents.z + b.extents.z);

            if (!mask.x) dxGap = float.NegativeInfinity;
            if (!mask.y) dyGap = float.NegativeInfinity;
            if (!mask.z) dzGap = float.NegativeInfinity;

            bool overlapX = !mask.x || dxGap <= 0f;
            bool overlapY = !mask.y || dyGap <= 0f;
            bool overlapZ = !mask.z || dzGap <= 0f;

            if (overlapX && overlapY && overlapZ)
            {
                float bestPenetration = float.MaxValue;
                char axis = 'x';
                if (mask.x && -dxGap < bestPenetration) { bestPenetration = -dxGap; axis = 'x'; }
                if (mask.y && -dyGap < bestPenetration) { bestPenetration = -dyGap; axis = 'y'; }
                if (mask.z && -dzGap < bestPenetration) { bestPenetration = -dzGap; axis = 'z'; }

                var delta = b.position - a.position;
                pushDirection = Vector3.zero;
                if (axis == 'x')
                {
                    float sign = Mathf.Abs(delta.x) > 0.0001f ? Mathf.Sign(delta.x) : ((a.instanceId % 2 == 0) ? 1f : -1f);
                    pushDirection.x = sign;
                }
                else if (axis == 'y')
                {
                    float sign = Mathf.Abs(delta.y) > 0.0001f ? Mathf.Sign(delta.y) : 1f;
                    pushDirection.y = sign;
                }
                else
                {
                    float sign = Mathf.Abs(delta.z) > 0.0001f ? Mathf.Sign(delta.z) : ((b.instanceId % 2 == 0) ? 1f : -1f);
                    pushDirection.z = sign;
                }

                pushDirection.Normalize();
                return -Mathf.Max(0f, bestPenetration);
            }

            float gx = mask.x ? Mathf.Max(dxGap, 0f) : 0f;
            float gy = mask.y ? Mathf.Max(dyGap, 0f) : 0f;
            float gz = mask.z ? Mathf.Max(dzGap, 0f) : 0f;
            float distance = Mathf.Sqrt((gx * gx) + (gy * gy) + (gz * gz));

            pushDirection = ProjectVectorByAxes(b.position - a.position, mask);
            if (pushDirection.sqrMagnitude < 0.0000001f)
            {
                pushDirection = new Vector3(mask.x ? 1f : 0f, mask.y ? 1f : 0f, mask.z ? 1f : 0f);
            }
            pushDirection.Normalize();

            return distance;
        }

        private static Dictionary<string, object> BuildLayoutViolationSummary(List<Dictionary<string, object>> evaluations)
        {
            if (evaluations == null || evaluations.Count == 0)
            {
                return new Dictionary<string, object>
                {
                    { "count", 0 },
                    { "validCount", 0 },
                    { "unsatisfiedCount", 0 },
                    { "totalViolation", 0.0 },
                    { "meanViolation", 0.0 },
                    { "maxViolation", 0.0 }
                };
            }

            int validCount = 0;
            int unsatisfiedCount = 0;
            double totalViolation = 0d;
            double maxViolation = 0d;

            foreach (var eval in evaluations)
            {
                bool valid = ReadBool(eval, "valid", true);
                bool satisfied = ReadBool(eval, "satisfied", false);
                double violation = 0d;
                if (eval.TryGetValue("violation", out var violationObj) && violationObj != null)
                {
                    try { violation = Convert.ToDouble(violationObj, CultureInfo.InvariantCulture); }
                    catch { violation = 0d; }
                }

                if (valid) validCount++;
                if (!satisfied) unsatisfiedCount++;
                totalViolation += violation;
                if (violation > maxViolation) maxViolation = violation;
            }

            return new Dictionary<string, object>
            {
                { "count", evaluations.Count },
                { "validCount", validCount },
                { "unsatisfiedCount", unsatisfiedCount },
                { "totalViolation", (double)Math.Round(totalViolation, 6) },
                { "meanViolation", (double)Math.Round(totalViolation / Mathf.Max(1, evaluations.Count), 6) },
                { "maxViolation", (double)Math.Round(maxViolation, 6) }
            };
        }

        private static Dictionary<string, object> BuildVolumeOverridePayload(ReproStepRequest request, Dictionary<string, object> overrides)
        {
            var payload = new Dictionary<string, object>
            {
                { "overrides", overrides },
                { "createIfMissing", 1 },
                { "saveAssets", 1 }
            };

            if (!string.IsNullOrWhiteSpace(request.profilePath))
            {
                payload["profilePath"] = request.profilePath;
            }

            if (request.volumeInstanceId != 0)
            {
                payload["volumeInstanceId"] = request.volumeInstanceId;
            }

            return payload;
        }

        private static Dictionary<string, object> BuildCameraRenderingPayload(ReproStepRequest request, Dictionary<string, object> patch)
        {
            var payload = new Dictionary<string, object>(patch);
            if (request.cameraInstanceId != 0)
            {
                payload["instanceId"] = request.cameraInstanceId;
            }
            else if (!string.IsNullOrWhiteSpace(request.cameraName))
            {
                payload["cameraName"] = request.cameraName;
            }

            return payload;
        }


        private static List<int> ExtractIntList(Dictionary<string, object> request, string key)
        {
            var result = new List<int>();
            if (request == null || !request.TryGetValue(key, out var raw) || raw == null)
            {
                return result;
            }

            if (raw is List<object> list)
            {
                foreach (var item in list)
                {
                    if (item == null) continue;
                    if (item is int i)
                    {
                        result.Add(i);
                        continue;
                    }

                    if (int.TryParse(item.ToString(), out var parsed))
                    {
                        result.Add(parsed);
                    }
                }
            }

            return result
                .Where(id => id != 0)
                .Distinct()
                .ToList();
        }

        private static bool TryCollectTileObjects(
            int rootInstanceId,
            List<int> explicitIds,
            bool includeInactive,
            int maxTiles,
            string rootName,
            out List<GameObject> tileObjects,
            out int skippedCount,
            out Dictionary<string, object> selectionInfo,
            out string error)
        {
            tileObjects = new List<GameObject>();
            skippedCount = 0;
            selectionInfo = new Dictionary<string, object>();
            error = null;

            var candidates = new List<GameObject>();
            if (explicitIds != null && explicitIds.Count > 0)
            {
                selectionInfo["mode"] = "instanceIds";
                selectionInfo["requestedCount"] = explicitIds.Count;

                foreach (var id in explicitIds)
                {
                    var go = EditorUtility.EntityIdToObject(id) as GameObject;
                    if (go == null)
                    {
                        skippedCount++;
                        continue;
                    }

                    if (!includeInactive && !go.activeInHierarchy)
                    {
                        skippedCount++;
                        continue;
                    }

                    candidates.Add(go);
                }
            }
            else
            {
                if (!TryResolveTileRoot(rootInstanceId, rootName, out var root, out var rootResolution, out var resolveError))
                {
                    error = resolveError;
                    return false;
                }

                selectionInfo["mode"] = "root";
                if (rootInstanceId != 0)
                {
                    selectionInfo["requestedRootInstanceId"] = rootInstanceId;
                }
                if (!string.IsNullOrWhiteSpace(rootName))
                {
                    selectionInfo["requestedRootName"] = rootName;
                }
                selectionInfo["rootResolution"] = rootResolution;
                selectionInfo["rootInstanceId"] = root.GetInstanceID();
                selectionInfo["rootName"] = root.name;

                foreach (Transform child in root.transform)
                {
                    if (child == null || child.gameObject == null)
                    {
                        continue;
                    }

                    if (!includeInactive && !child.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    candidates.Add(child.gameObject);
                }
            }

            selectionInfo["candidateCount"] = candidates.Count;
            selectionInfo["maxTiles"] = maxTiles;

            var dedup = new HashSet<int>();
            foreach (var go in candidates)
            {
                if (tileObjects.Count >= maxTiles)
                {
                    break;
                }

                if (go == null) continue;
                if (!dedup.Add(go.GetInstanceID())) continue;
                tileObjects.Add(go);
            }

            selectionInfo["selectedCount"] = tileObjects.Count;
            selectionInfo["truncated"] = candidates.Count > maxTiles;
            return true;
        }

        private static List<TileBoundsSample> CollectSamplesFromObjects(List<GameObject> objects, bool includeInactive, out int skippedCount)
        {
            skippedCount = 0;
            var samples = new List<TileBoundsSample>();
            if (objects == null || objects.Count == 0)
            {
                return samples;
            }

            foreach (var go in objects)
            {
                if (go == null)
                {
                    skippedCount++;
                    continue;
                }

                if (!TryGetAggregateBounds(go, includeInactive, out var bounds, out var source))
                {
                    skippedCount++;
                    continue;
                }

                samples.Add(new TileBoundsSample
                {
                    instanceId = go.GetInstanceID(),
                    name = go.name,
                    path = GetHierarchyPath(go.transform),
                    centerXZ = new Vector2(bounds.center.x, bounds.center.z),
                    sizeXZ = new Vector2(bounds.size.x, bounds.size.z),
                    extentsXZ = new Vector2(bounds.extents.x, bounds.extents.z),
                    boundsSource = source
                });
            }

            return samples;
        }

        private static void ComputeTilePairStats(
            List<TileBoundsSample> samples,
            out float minEdgeDistance,
            out int overlapPairCount,
            out float maxOverlapDepth)
        {
            minEdgeDistance = float.MaxValue;
            overlapPairCount = 0;
            maxOverlapDepth = 0f;

            if (samples == null || samples.Count < 2)
            {
                minEdgeDistance = 0f;
                return;
            }

            for (int i = 0; i < samples.Count; i++)
            {
                for (int j = i + 1; j < samples.Count; j++)
                {
                    float signedDistance = ComputeSignedAabbDistance2D(samples[i], samples[j], out _, out _);
                    minEdgeDistance = Mathf.Min(minEdgeDistance, signedDistance);
                    if (signedDistance < 0f)
                    {
                        overlapPairCount++;
                        maxOverlapDepth = Mathf.Max(maxOverlapDepth, -signedDistance);
                    }
                }
            }

            if (minEdgeDistance == float.MaxValue)
            {
                minEdgeDistance = 0f;
            }
        }

        private static bool TryCollectTileBoundsSamples(
            int rootInstanceId,
            List<int> explicitIds,
            bool includeInactive,
            int maxTiles,
            string rootName,
            out List<TileBoundsSample> samples,
            out int skippedCount,
            out Dictionary<string, object> selectionInfo,
            out string error)
        {
            samples = new List<TileBoundsSample>();
            skippedCount = 0;
            selectionInfo = new Dictionary<string, object>();
            error = null;

            var targets = new List<GameObject>();
            if (explicitIds != null && explicitIds.Count > 0)
            {
                foreach (var id in explicitIds)
                {
                    var go = EditorUtility.EntityIdToObject(id) as GameObject;
                    if (go != null)
                    {
                        targets.Add(go);
                    }
                }

                selectionInfo["mode"] = "instanceIds";
                selectionInfo["requestedCount"] = explicitIds.Count;
            }
            else
            {
                if (!TryResolveTileRoot(rootInstanceId, rootName, out var root, out var rootResolution, out var resolveError))
                {
                    error = resolveError;
                    return false;
                }

                selectionInfo["mode"] = "root";
                if (rootInstanceId != 0)
                {
                    selectionInfo["requestedRootInstanceId"] = rootInstanceId;
                }
                if (!string.IsNullOrWhiteSpace(rootName))
                {
                    selectionInfo["requestedRootName"] = rootName;
                }
                selectionInfo["rootResolution"] = rootResolution;
                selectionInfo["rootInstanceId"] = root.GetInstanceID();
                selectionInfo["rootName"] = root.name;

                foreach (Transform child in root.transform)
                {
                    if (child == null || child.gameObject == null) continue;
                    if (!includeInactive && !child.gameObject.activeInHierarchy) continue;
                    targets.Add(child.gameObject);
                }
            }

            selectionInfo["candidateCount"] = targets.Count;

            var dedup = new HashSet<int>();
            foreach (var go in targets)
            {
                if (samples.Count >= maxTiles) break;
                if (go == null) continue;
                int id = go.GetInstanceID();
                if (!dedup.Add(id)) continue;

                if (!TryGetAggregateBounds(go, includeInactive, out var bounds, out var source))
                {
                    skippedCount++;
                    continue;
                }

                samples.Add(new TileBoundsSample
                {
                    instanceId = id,
                    name = go.name,
                    path = GetHierarchyPath(go.transform),
                    centerXZ = new Vector2(bounds.center.x, bounds.center.z),
                    sizeXZ = new Vector2(bounds.size.x, bounds.size.z),
                    extentsXZ = new Vector2(bounds.extents.x, bounds.extents.z),
                    boundsSource = source
                });
            }

            selectionInfo["selectedCount"] = samples.Count;
            selectionInfo["maxTiles"] = maxTiles;
            selectionInfo["truncated"] = targets.Count > maxTiles;

            return true;
        }

        private static bool TryResolveTileRoot(
            int rootInstanceId,
            string rootName,
            out GameObject root,
            out string resolution,
            out string error)
        {
            root = null;
            resolution = string.Empty;
            error = null;

            if (rootInstanceId != 0)
            {
                root = EditorUtility.EntityIdToObject(rootInstanceId) as GameObject;
                if (root != null)
                {
                    resolution = "instanceId";
                    return true;
                }

                if (string.IsNullOrWhiteSpace(rootName))
                {
                    error = $"Root GameObject not found for rootInstanceId={rootInstanceId}";
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(rootName))
            {
                error = "Provide rootInstanceId, rootName/tileRootName, or instanceIds";
                return false;
            }

            root = FindSceneObjectByName(rootName);
            if (root == null)
            {
                error = $"Root GameObject not found for rootName='{rootName}'";
                return false;
            }

            resolution = rootInstanceId != 0 ? "rootNameFallback" : "rootName";
            return true;
        }

        private static GameObject FindSceneObjectByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return null;
            }

            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                var root = roots[i];
                if (root != null && string.Equals(root.name, name, StringComparison.Ordinal))
                {
                    return root;
                }
            }

            for (int i = 0; i < roots.Length; i++)
            {
                var root = roots[i];
                if (root != null && string.Equals(root.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return root;
                }
            }

            for (int i = 0; i < roots.Length; i++)
            {
                var root = roots[i];
                if (root == null) continue;
                var match = FindChildByName(root.transform, name, StringComparison.Ordinal);
                if (match != null)
                {
                    return match.gameObject;
                }
            }

            for (int i = 0; i < roots.Length; i++)
            {
                var root = roots[i];
                if (root == null) continue;
                var match = FindChildByName(root.transform, name, StringComparison.OrdinalIgnoreCase);
                if (match != null)
                {
                    return match.gameObject;
                }
            }

            return null;
        }

        private static Transform FindChildByName(Transform root, string name, StringComparison comparison)
        {
            if (root == null) return null;

            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == null) continue;
                if (string.Equals(current.name, name, comparison))
                {
                    return current;
                }

                for (int i = current.childCount - 1; i >= 0; i--)
                {
                    var child = current.GetChild(i);
                    if (child != null)
                    {
                        stack.Push(child);
                    }
                }
            }

            return null;
        }
    }
}
