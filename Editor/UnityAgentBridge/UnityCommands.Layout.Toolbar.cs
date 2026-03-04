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
        private static bool TryReadConstraintId(Dictionary<string, object> map, string key, out int id)
        {
            id = 0;
            if (map == null || !map.TryGetValue(key, out var raw) || raw == null) return false;
            if (raw is int i)
            {
                id = i;
                return id != 0;
            }

            if (int.TryParse(raw.ToString(), out var parsed))
            {
                id = parsed;
                return id != 0;
            }

            return false;
        }

        private static bool TryReadConstraintVector3(Dictionary<string, object> map, string key, out Vector3 value)
        {
            value = Vector3.zero;
            if (map == null || !map.TryGetValue(key, out var raw) || raw == null) return false;

            if (TryReadVector(raw, 3, out var v3))
            {
                value = new Vector3(v3[0], v3[1], v3[2]);
                return true;
            }

            if (raw is Dictionary<string, object> dict
                && dict.TryGetValue("x", out var xObj)
                && dict.TryGetValue("y", out var yObj)
                && dict.TryGetValue("z", out var zObj))
            {
                value = new Vector3(
                    Convert.ToSingle(xObj, CultureInfo.InvariantCulture),
                    Convert.ToSingle(yObj, CultureInfo.InvariantCulture),
                    Convert.ToSingle(zObj, CultureInfo.InvariantCulture));
                return true;
            }

            return false;
        }

        private static List<Dictionary<string, object>> EvaluateLayoutConstraints(IList<object> constraints, Dictionary<int, LayoutNodeState> nodes)
        {
            var evaluations = new List<Dictionary<string, object>>();
            if (constraints == null || nodes == null) return evaluations;

            for (int i = 0; i < constraints.Count; i++)
            {
                var item = constraints[i] as Dictionary<string, object>;
                string type = ReadString(item, "type")?.Trim().ToLowerInvariant() ?? "unknown";
                if (item == null)
                {
                    evaluations.Add(new Dictionary<string, object>
                    {
                        { "index", i },
                        { "type", "invalid" },
                        { "valid", false },
                        { "violation", 1.0 },
                        { "satisfied", false },
                        { "error", "Constraint entry must be an object" }
                    });
                    continue;
                }

                var eval = EvaluateConstraintViolation(item, i, type, nodes);
                evaluations.Add(eval);
            }

            return evaluations;
        }

        private static Dictionary<string, object> EvaluateConstraintViolation(
            Dictionary<string, object> constraint,
            int index,
            string type,
            Dictionary<int, LayoutNodeState> nodes)
        {
            var baseResult = new Dictionary<string, object>
            {
                { "index", index },
                { "type", type },
                { "valid", true },
                { "violation", 0.0 },
                { "satisfied", true }
            };

            bool TryGetNode(int id, out LayoutNodeState node)
            {
                node = null;
                return id != 0 && nodes.TryGetValue(id, out node) && node != null;
            }

            switch (type)
            {
                case "distance":
                {
                    if (!TryReadConstraintId(constraint, "a", out var aId) || !TryReadConstraintId(constraint, "b", out var bId))
                    {
                        baseResult["valid"] = false;
                        baseResult["satisfied"] = false;
                        baseResult["violation"] = 1.0;
                        baseResult["error"] = "distance requires a and b instance IDs";
                        return baseResult;
                    }

                    if (!TryGetNode(aId, out var aNode) || !TryGetNode(bId, out var bNode))
                    {
                        baseResult["valid"] = false;
                        baseResult["satisfied"] = false;
                        baseResult["violation"] = 1.0;
                        baseResult["error"] = "distance references missing objects";
                        return baseResult;
                    }

                    var mask = ParseLayoutAxesMask(ReadString(constraint, "axes"), "xyz");
                    var delta = bNode.position - aNode.position;
                    delta = ProjectVectorByAxes(delta, mask);
                    float current = delta.magnitude;
                    bool hasTarget = TryReadFloatField(constraint, "target", out var target);
                    bool hasMin = TryReadFloatField(constraint, "min", out var min);
                    bool hasMax = TryReadFloatField(constraint, "max", out var max);

                    float violation = 0f;
                    if (hasTarget)
                    {
                        violation = Mathf.Abs(current - target);
                    }
                    else
                    {
                        if (hasMin && current < min) violation += (min - current);
                        if (hasMax && current > max) violation += (current - max);
                    }

                    baseResult["violation"] = (double)Math.Round(violation, 6);
                    baseResult["satisfied"] = violation < 0.0001f;
                    baseResult["a"] = aId;
                    baseResult["b"] = bId;
                    baseResult["currentDistance"] = (double)Math.Round(current, 6);
                    return baseResult;
                }
                case "align":
                {
                    if (!TryReadConstraintId(constraint, "a", out var aId) || !TryReadConstraintId(constraint, "b", out var bId))
                    {
                        baseResult["valid"] = false;
                        baseResult["satisfied"] = false;
                        baseResult["violation"] = 1.0;
                        baseResult["error"] = "align requires a and b instance IDs";
                        return baseResult;
                    }

                    if (!TryGetNode(aId, out var aNode) || !TryGetNode(bId, out var bNode))
                    {
                        baseResult["valid"] = false;
                        baseResult["satisfied"] = false;
                        baseResult["violation"] = 1.0;
                        baseResult["error"] = "align references missing objects";
                        return baseResult;
                    }

                    char axis = ParseLayoutAxis(ReadString(constraint, "axis"), 'x');
                    float offset = TryReadFloatField(constraint, "offset", out var parsedOffset) ? parsedOffset : 0f;
                    string mode = (ReadString(constraint, "mode") ?? "match").Trim().ToLowerInvariant();

                    float targetValue;
                    if (mode == "min")
                    {
                        targetValue = GetAxisValue(bNode.position, axis) - GetAxisValue(bNode.extents, axis) + GetAxisValue(aNode.extents, axis) + offset;
                    }
                    else if (mode == "max")
                    {
                        targetValue = GetAxisValue(bNode.position, axis) + GetAxisValue(bNode.extents, axis) - GetAxisValue(aNode.extents, axis) + offset;
                    }
                    else
                    {
                        targetValue = GetAxisValue(bNode.position, axis) + offset;
                    }

                    float currentValue = GetAxisValue(aNode.position, axis);
                    float violation = Mathf.Abs(targetValue - currentValue);

                    baseResult["violation"] = (double)Math.Round(violation, 6);
                    baseResult["satisfied"] = violation < 0.0001f;
                    baseResult["a"] = aId;
                    baseResult["b"] = bId;
                    baseResult["axis"] = axis.ToString();
                    return baseResult;
                }
                case "offset":
                {
                    if (!TryReadConstraintId(constraint, "a", out var aId) || !TryReadConstraintId(constraint, "b", out var bId))
                    {
                        baseResult["valid"] = false;
                        baseResult["satisfied"] = false;
                        baseResult["violation"] = 1.0;
                        baseResult["error"] = "offset requires a and b instance IDs";
                        return baseResult;
                    }

                    if (!TryGetNode(aId, out var aNode) || !TryGetNode(bId, out var bNode))
                    {
                        baseResult["valid"] = false;
                        baseResult["satisfied"] = false;
                        baseResult["violation"] = 1.0;
                        baseResult["error"] = "offset references missing objects";
                        return baseResult;
                    }

                    var offset = TryReadConstraintVector3(constraint, "offset", out var offsetVec) ? offsetVec : Vector3.zero;
                    var target = bNode.position + offset;
                    float violation = (target - aNode.position).magnitude;

                    baseResult["violation"] = (double)Math.Round(violation, 6);
                    baseResult["satisfied"] = violation < 0.0001f;
                    baseResult["a"] = aId;
                    baseResult["b"] = bId;
                    return baseResult;
                }
                case "inside_bounds":
                {
                    if (!TryReadConstraintId(constraint, "instanceId", out var instanceId))
                    {
                        if (!TryReadConstraintId(constraint, "a", out instanceId))
                        {
                            baseResult["valid"] = false;
                            baseResult["satisfied"] = false;
                            baseResult["violation"] = 1.0;
                            baseResult["error"] = "inside_bounds requires instanceId or a";
                            return baseResult;
                        }
                    }

                    if (!TryGetNode(instanceId, out var node))
                    {
                        baseResult["valid"] = false;
                        baseResult["satisfied"] = false;
                        baseResult["violation"] = 1.0;
                        baseResult["error"] = "inside_bounds references missing object";
                        return baseResult;
                    }

                    Vector3 min;
                    Vector3 max;
                    if (TryReadConstraintId(constraint, "boundsObjectId", out var boundsObjectId) && TryGetNode(boundsObjectId, out var boundsNode))
                    {
                        min = boundsNode.position - boundsNode.extents;
                        max = boundsNode.position + boundsNode.extents;
                    }
                    else if (TryReadConstraintVector3(constraint, "min", out var minVec) && TryReadConstraintVector3(constraint, "max", out var maxVec))
                    {
                        min = minVec;
                        max = maxVec;
                    }
                    else
                    {
                        baseResult["valid"] = false;
                        baseResult["satisfied"] = false;
                        baseResult["violation"] = 1.0;
                        baseResult["error"] = "inside_bounds requires min/max or boundsObjectId";
                        return baseResult;
                    }

                    float padding = Mathf.Max(0f, TryReadFloatField(constraint, "padding", out var paddingValue) ? paddingValue : 0f);
                    var mask = ParseLayoutAxesMask(ReadString(constraint, "axes"), "xyz");
                    var clamped = node.position;
                    if (mask.x) clamped.x = Mathf.Clamp(clamped.x, min.x + padding, max.x - padding);
                    if (mask.y) clamped.y = Mathf.Clamp(clamped.y, min.y + padding, max.y - padding);
                    if (mask.z) clamped.z = Mathf.Clamp(clamped.z, min.z + padding, max.z - padding);

                    float violation = (clamped - node.position).magnitude;
                    baseResult["violation"] = (double)Math.Round(violation, 6);
                    baseResult["satisfied"] = violation < 0.0001f;
                    baseResult["instanceId"] = instanceId;
                    return baseResult;
                }
                case "no_overlap":
                {
                    var mask = ParseLayoutAxesMask(ReadString(constraint, "axes"), "xz");
                    float minGap = Mathf.Max(0f, TryReadFloatField(constraint, "minGap", out var minGapValue)
                        ? minGapValue
                        : (TryReadFloatField(constraint, "padding", out var paddingValue) ? paddingValue : 0f));

                    float maxViolation = 0f;
                    bool hasA = TryReadConstraintId(constraint, "a", out var aId);
                    bool hasB = TryReadConstraintId(constraint, "b", out var bId);
                    bool hasPair = hasA && hasB;
                    if (hasPair)
                    {
                        if (!TryGetNode(aId, out var aNode) || !TryGetNode(bId, out var bNode))
                        {
                            baseResult["valid"] = false;
                            baseResult["satisfied"] = false;
                            baseResult["violation"] = 1.0;
                            baseResult["error"] = "no_overlap references missing objects";
                            return baseResult;
                        }

                        float signed = ComputeSignedAabbDistanceMasked(aNode, bNode, mask, out _);
                        maxViolation = Mathf.Max(0f, minGap - signed);
                    }
                    else
                    {
                        var ids = ExtractIntList(constraint, "ids");
                        if (ids.Count < 2)
                        {
                            baseResult["valid"] = false;
                            baseResult["satisfied"] = false;
                            baseResult["violation"] = 1.0;
                            baseResult["error"] = "no_overlap requires (a,b) or ids[]";
                            return baseResult;
                        }

                        for (int i = 0; i < ids.Count; i++)
                        {
                            if (!TryGetNode(ids[i], out var lhs)) continue;
                            for (int j = i + 1; j < ids.Count; j++)
                            {
                                if (!TryGetNode(ids[j], out var rhs)) continue;
                                float signed = ComputeSignedAabbDistanceMasked(lhs, rhs, mask, out _);
                                float violation = Mathf.Max(0f, minGap - signed);
                                if (violation > maxViolation) maxViolation = violation;
                            }
                        }
                    }

                    baseResult["violation"] = (double)Math.Round(maxViolation, 6);
                    baseResult["satisfied"] = maxViolation < 0.0001f;
                    return baseResult;
                }
                default:
                {
                    baseResult["valid"] = false;
                    baseResult["satisfied"] = false;
                    baseResult["violation"] = 1.0;
                    baseResult["error"] = $"Unsupported constraint type '{type}'";
                    return baseResult;
                }
            }
        }

        private static bool ApplyLayoutConstraintPass(
            IList<object> constraints,
            Dictionary<int, LayoutNodeState> nodes,
            float gain,
            ref int adjustmentCount)
        {
            bool changed = false;
            if (constraints == null || nodes == null || gain <= 0f)
            {
                return false;
            }

            for (int i = 0; i < constraints.Count; i++)
            {
                if (constraints[i] is not Dictionary<string, object> constraint) continue;

                string type = ReadString(constraint, "type")?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(type)) continue;

                bool didChange = false;
                switch (type)
                {
                    case "distance":
                        didChange = ApplyDistanceConstraint(constraint, nodes, gain);
                        break;
                    case "align":
                        didChange = ApplyAlignConstraint(constraint, nodes, gain);
                        break;
                    case "offset":
                        didChange = ApplyOffsetConstraint(constraint, nodes, gain);
                        break;
                    case "inside_bounds":
                        didChange = ApplyInsideBoundsConstraint(constraint, nodes, gain);
                        break;
                    case "no_overlap":
                        didChange = ApplyNoOverlapConstraint(constraint, nodes, gain);
                        break;
                }

                if (didChange)
                {
                    changed = true;
                    adjustmentCount++;
                }
            }

            return changed;
        }

        private static bool ApplyDistanceConstraint(Dictionary<string, object> constraint, Dictionary<int, LayoutNodeState> nodes, float gain)
        {
            if (!TryReadConstraintId(constraint, "a", out var aId) || !TryReadConstraintId(constraint, "b", out var bId))
            {
                return false;
            }

            if (!nodes.TryGetValue(aId, out var aNode) || !nodes.TryGetValue(bId, out var bNode))
            {
                return false;
            }

            var mask = ParseLayoutAxesMask(ReadString(constraint, "axes"), "xyz");
            float weight = Mathf.Clamp(TryReadFloatField(constraint, "weight", out var weightValue) ? weightValue : 1f, 0f, 4f);
            if (weight <= 0f) return false;

            var delta = ProjectVectorByAxes(bNode.position - aNode.position, mask);
            float currentDistance = delta.magnitude;
            bool hasTarget = TryReadFloatField(constraint, "target", out var target);
            bool hasMin = TryReadFloatField(constraint, "min", out var min);
            bool hasMax = TryReadFloatField(constraint, "max", out var max);

            float desiredDistance = currentDistance;
            if (hasTarget)
            {
                desiredDistance = target;
            }
            else
            {
                if (hasMin) desiredDistance = Mathf.Max(desiredDistance, min);
                if (hasMax) desiredDistance = Mathf.Min(desiredDistance, max);
            }

            float error = desiredDistance - currentDistance;
            if (Mathf.Abs(error) < 0.0001f) return false;

            Vector3 dir;
            if (currentDistance > 0.0001f)
            {
                dir = delta / currentDistance;
            }
            else
            {
                dir = ProjectVectorByAxes(new Vector3((aId % 2 == 0) ? 1f : -1f, 0f, (bId % 2 == 0) ? 1f : -1f), mask);
                if (dir.sqrMagnitude < 0.000001f) dir = Vector3.right;
                dir.Normalize();
            }

            float push = error * 0.5f * gain * weight;
            var move = dir * push;
            aNode.position -= move;
            bNode.position += move;
            return true;
        }

        private static bool ApplyAlignConstraint(Dictionary<string, object> constraint, Dictionary<int, LayoutNodeState> nodes, float gain)
        {
            if (!TryReadConstraintId(constraint, "a", out var aId) || !TryReadConstraintId(constraint, "b", out var bId))
            {
                return false;
            }

            if (!nodes.TryGetValue(aId, out var aNode) || !nodes.TryGetValue(bId, out var bNode))
            {
                return false;
            }

            float weight = Mathf.Clamp(TryReadFloatField(constraint, "weight", out var weightValue) ? weightValue : 1f, 0f, 4f);
            if (weight <= 0f) return false;

            char axis = ParseLayoutAxis(ReadString(constraint, "axis"), 'x');
            float offset = TryReadFloatField(constraint, "offset", out var offsetValue) ? offsetValue : 0f;
            string mode = (ReadString(constraint, "mode") ?? "match").Trim().ToLowerInvariant();

            float targetValue;
            if (mode == "min")
            {
                targetValue = GetAxisValue(bNode.position, axis) - GetAxisValue(bNode.extents, axis) + GetAxisValue(aNode.extents, axis) + offset;
            }
            else if (mode == "max")
            {
                targetValue = GetAxisValue(bNode.position, axis) + GetAxisValue(bNode.extents, axis) - GetAxisValue(aNode.extents, axis) + offset;
            }
            else
            {
                targetValue = GetAxisValue(bNode.position, axis) + offset;
            }

            float currentValue = GetAxisValue(aNode.position, axis);
            float delta = targetValue - currentValue;
            if (Mathf.Abs(delta) < 0.0001f) return false;

            float applied = delta * gain * weight;
            aNode.position = SetAxisValue(aNode.position, axis, currentValue + applied);
            return true;
        }

        private static bool ApplyOffsetConstraint(Dictionary<string, object> constraint, Dictionary<int, LayoutNodeState> nodes, float gain)
        {
            if (!TryReadConstraintId(constraint, "a", out var aId) || !TryReadConstraintId(constraint, "b", out var bId))
            {
                return false;
            }

            if (!nodes.TryGetValue(aId, out var aNode) || !nodes.TryGetValue(bId, out var bNode))
            {
                return false;
            }

            float weight = Mathf.Clamp(TryReadFloatField(constraint, "weight", out var weightValue) ? weightValue : 1f, 0f, 4f);
            if (weight <= 0f) return false;

            var offset = TryReadConstraintVector3(constraint, "offset", out var offsetValue) ? offsetValue : Vector3.zero;
            var target = bNode.position + offset;
            var delta = target - aNode.position;
            if (delta.sqrMagnitude < 0.0000001f) return false;

            aNode.position += delta * gain * weight;
            return true;
        }

        private static bool ApplyInsideBoundsConstraint(Dictionary<string, object> constraint, Dictionary<int, LayoutNodeState> nodes, float gain)
        {
            int instanceId;
            if (!TryReadConstraintId(constraint, "instanceId", out instanceId))
            {
                if (!TryReadConstraintId(constraint, "a", out instanceId))
                {
                    return false;
                }
            }

            if (!nodes.TryGetValue(instanceId, out var node))
            {
                return false;
            }

            Vector3 min;
            Vector3 max;
            if (TryReadConstraintId(constraint, "boundsObjectId", out var boundsObjectId) && nodes.TryGetValue(boundsObjectId, out var boundsNode))
            {
                min = boundsNode.position - boundsNode.extents;
                max = boundsNode.position + boundsNode.extents;
            }
            else if (TryReadConstraintVector3(constraint, "min", out var minVec) && TryReadConstraintVector3(constraint, "max", out var maxVec))
            {
                min = minVec;
                max = maxVec;
            }
            else
            {
                return false;
            }

            float weight = Mathf.Clamp(TryReadFloatField(constraint, "weight", out var weightValue) ? weightValue : 1f, 0f, 4f);
            if (weight <= 0f) return false;

            float padding = Mathf.Max(0f, TryReadFloatField(constraint, "padding", out var paddingValue) ? paddingValue : 0f);
            var mask = ParseLayoutAxesMask(ReadString(constraint, "axes"), "xyz");

            var clamped = node.position;
            if (mask.x) clamped.x = Mathf.Clamp(clamped.x, min.x + padding, max.x - padding);
            if (mask.y) clamped.y = Mathf.Clamp(clamped.y, min.y + padding, max.y - padding);
            if (mask.z) clamped.z = Mathf.Clamp(clamped.z, min.z + padding, max.z - padding);

            var delta = clamped - node.position;
            if (delta.sqrMagnitude < 0.0000001f) return false;

            node.position += delta * gain * weight;
            return true;
        }

        private static bool ApplyNoOverlapConstraint(Dictionary<string, object> constraint, Dictionary<int, LayoutNodeState> nodes, float gain)
        {
            float weight = Mathf.Clamp(TryReadFloatField(constraint, "weight", out var weightValue) ? weightValue : 1f, 0f, 4f);
            if (weight <= 0f) return false;

            float minGap = Mathf.Max(0f, TryReadFloatField(constraint, "minGap", out var minGapValue)
                ? minGapValue
                : (TryReadFloatField(constraint, "padding", out var paddingValue) ? paddingValue : 0f));

            var mask = ParseLayoutAxesMask(ReadString(constraint, "axes"), "xz");
            bool changed = false;

            if (TryReadConstraintId(constraint, "a", out var aId) && TryReadConstraintId(constraint, "b", out var bId))
            {
                if (!nodes.TryGetValue(aId, out var aNode) || !nodes.TryGetValue(bId, out var bNode))
                {
                    return false;
                }

                float signed = ComputeSignedAabbDistanceMasked(aNode, bNode, mask, out var pushDir);
                float deficit = minGap - signed;
                if (deficit <= 0f) return false;

                float push = deficit * 0.5f * gain * weight;
                var shift = pushDir * push;
                aNode.position -= shift;
                bNode.position += shift;
                return true;
            }

            var ids = ExtractIntList(constraint, "ids");
            if (ids.Count < 2) return false;

            for (int i = 0; i < ids.Count; i++)
            {
                if (!nodes.TryGetValue(ids[i], out var lhs)) continue;
                for (int j = i + 1; j < ids.Count; j++)
                {
                    if (!nodes.TryGetValue(ids[j], out var rhs)) continue;
                    float signed = ComputeSignedAabbDistanceMasked(lhs, rhs, mask, out var pushDir);
                    float deficit = minGap - signed;
                    if (deficit <= 0f) continue;

                    float push = deficit * 0.5f * gain * weight;
                    var shift = pushDir * push;
                    lhs.position -= shift;
                    rhs.position += shift;
                    changed = true;
                }
            }

            return changed;
        }

        private static Vector3 ProjectVectorByAxes(Vector3 vector, LayoutAxesMask mask)
        {
            return new Vector3(mask.x ? vector.x : 0f, mask.y ? vector.y : 0f, mask.z ? vector.z : 0f);
        }

        private static LayoutAxesMask ParseLayoutAxesMask(string axesRaw, string fallback)
        {
            string source = string.IsNullOrWhiteSpace(axesRaw) ? fallback : axesRaw;
            source = source?.Trim().ToLowerInvariant() ?? fallback;
            var mask = new LayoutAxesMask
            {
                x = source.Contains("x", StringComparison.Ordinal),
                y = source.Contains("y", StringComparison.Ordinal),
                z = source.Contains("z", StringComparison.Ordinal)
            };

            if (!mask.x && !mask.y && !mask.z)
            {
                mask.x = fallback.Contains("x", StringComparison.Ordinal);
                mask.y = fallback.Contains("y", StringComparison.Ordinal);
                mask.z = fallback.Contains("z", StringComparison.Ordinal);
            }

            if (!mask.x && !mask.y && !mask.z)
            {
                mask.x = true;
                mask.z = true;
            }

            return mask;
        }
    }
}
