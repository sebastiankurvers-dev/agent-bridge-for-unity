using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        // ==================== Runtime Contract Checker ====================

        private static readonly List<RegisteredContract> _contracts = new List<RegisteredContract>();
        private static readonly object _contractLock = new object();
        private static bool _contractTickRegistered;
        private static int _contractFrameCounter;
        private const int MaxContracts = 20;

        private class RegisteredContract
        {
            public string name;
            public int instanceId;
            public string componentType;
            public string field;
            public string op;        // >=, <=, ==, !=, >, <, in_range, not_null
            public string expected;
            public string severity;  // info, warning, error
            public int passCount;
            public int failCount;
            public int errorCount;
            public string lastActualValue;
            public string lastError;
        }

        [BridgeRoute("POST", "/contracts/register", Category = "contracts", Description = "Register runtime contracts")]
        public static string RegisterContracts(string jsonData)
        {
            if (!EditorApplication.isPlaying)
                return JsonError("RegisterContracts is only allowed during play mode.");

            List<object> contractDefs;
            try
            {
                var parsed = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
                if (parsed == null || !parsed.ContainsKey("contracts"))
                    return JsonError("Request must contain a 'contracts' array.");
                contractDefs = parsed["contracts"] as List<object>;
                if (contractDefs == null || contractDefs.Count == 0)
                    return JsonError("contracts array is empty.");
            }
            catch (Exception ex)
            {
                return JsonError($"Failed to parse request: {ex.Message}");
            }

            lock (_contractLock)
            {
                if (_contracts.Count + contractDefs.Count > MaxContracts)
                    return JsonError($"Max {MaxContracts} contracts allowed. Currently registered: {_contracts.Count}, requested: {contractDefs.Count}.");

                var registered = new List<string>();
                foreach (var item in contractDefs)
                {
                    var def = item as Dictionary<string, object>;
                    if (def == null) continue;

                    var contract = new RegisteredContract
                    {
                        name = def.TryGetValue("name", out var n) ? n?.ToString() : $"contract_{_contracts.Count}",
                        instanceId = def.TryGetValue("instanceId", out var id) ? Convert.ToInt32(id) : 0,
                        componentType = def.TryGetValue("componentType", out var ct) ? ct?.ToString() : "",
                        field = def.TryGetValue("field", out var f) ? f?.ToString() : "",
                        op = def.TryGetValue("op", out var o) ? o?.ToString() : "not_null",
                        expected = def.TryGetValue("expected", out var e) ? e?.ToString() : "",
                        severity = def.TryGetValue("severity", out var s) ? s?.ToString() : "error"
                    };

                    if (contract.instanceId == 0)
                        return JsonError($"Contract '{contract.name}' requires instanceId.");
                    if (string.IsNullOrEmpty(contract.field))
                        return JsonError($"Contract '{contract.name}' requires field.");

                    _contracts.Add(contract);
                    registered.Add(contract.name);
                }

                EnsureContractTick();

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "registeredCount", registered.Count },
                    { "registeredNames", registered },
                    { "totalContracts", _contracts.Count }
                });
            }
        }

        [BridgeRoute("GET", "/contracts", Category = "contracts", Description = "Query contract state", ReadOnly = true)]
        public static string QueryContracts()
        {
            lock (_contractLock)
            {
                var results = new List<object>();
                foreach (var c in _contracts)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "name", c.name },
                        { "instanceId", c.instanceId },
                        { "componentType", c.componentType },
                        { "field", c.field },
                        { "op", c.op },
                        { "expected", c.expected },
                        { "severity", c.severity },
                        { "passCount", c.passCount },
                        { "failCount", c.failCount },
                        { "errorCount", c.errorCount },
                        { "lastActualValue", c.lastActualValue ?? "" },
                        { "lastError", c.lastError ?? "" }
                    });
                }

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "contracts", results },
                    { "totalContracts", _contracts.Count },
                    { "isPlaying", EditorApplication.isPlaying }
                });
            }
        }

        [BridgeRoute("DELETE", "/contracts", Category = "contracts", Description = "Clear contracts")]
        public static string ClearContracts()
        {
            lock (_contractLock)
            {
                int cleared = _contracts.Count;
                _contracts.Clear();
                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "clearedCount", cleared }
                });
            }
        }

        private static void EnsureContractTick()
        {
            if (_contractTickRegistered) return;
            _contractTickRegistered = true;
            EditorApplication.update += ContractTick;
            EditorApplication.playModeStateChanged += OnContractPlayModeChanged;
        }

        private static void OnContractPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                lock (_contractLock)
                {
                    _contracts.Clear();
                }
                EditorApplication.update -= ContractTick;
                EditorApplication.playModeStateChanged -= OnContractPlayModeChanged;
                _contractTickRegistered = false;
            }
        }

        private static void ContractTick()
        {
            if (!EditorApplication.isPlaying) return;

            // Evaluate every other frame to avoid performance impact
            _contractFrameCounter++;
            if (_contractFrameCounter % 2 != 0) return;

            lock (_contractLock)
            {
                if (_contracts.Count == 0) return;

                foreach (var contract in _contracts)
                {
                    try
                    {
                        EvaluateContract(contract);
                    }
                    catch (Exception ex)
                    {
                        contract.errorCount++;
                        contract.lastError = ex.Message;
                    }
                }
            }
        }

        private static void EvaluateContract(RegisteredContract contract)
        {
            var go = EditorUtility.EntityIdToObject(contract.instanceId) as GameObject;
            if (go == null)
            {
                contract.errorCount++;
                contract.lastError = "GameObject not found";
                return;
            }

            object actualValue = ResolveContractField(go, contract.componentType, contract.field);
            if (actualValue == null && contract.op != "not_null")
            {
                contract.errorCount++;
                contract.lastError = $"Could not resolve field '{contract.field}'";
                return;
            }

            contract.lastActualValue = actualValue?.ToString() ?? "null";

            bool passed = EvaluateCondition(actualValue, contract.op, contract.expected);
            if (passed)
            {
                contract.passCount++;
            }
            else
            {
                contract.failCount++;
                var payload = new Dictionary<string, object>
                {
                    { "contractName", contract.name },
                    { "instanceId", contract.instanceId },
                    { "field", contract.field },
                    { "op", contract.op },
                    { "expected", contract.expected },
                    { "actual", contract.lastActualValue },
                    { "severity", contract.severity }
                };
                UnityAgentBridgeServer.PushEvent("contract_violation", MiniJSON.Json.Serialize(payload));
            }
        }

        private static object ResolveContractField(GameObject go, string componentType, string fieldPath)
        {
            // Special handling for Transform fields (position.x, position.y, etc.)
            if (string.IsNullOrEmpty(componentType) || componentType.Equals("Transform", StringComparison.OrdinalIgnoreCase))
            {
                var transformValue = ResolveTransformField(go.transform, fieldPath);
                if (transformValue != null) return transformValue;
            }

            // Try component field via reflection
            if (!string.IsNullOrEmpty(componentType))
            {
                var component = FindComponentOnGameObject(go, componentType, out var type);
                if (component == null) return null;
                return ResolveFieldPath(component, type, fieldPath);
            }

            return null;
        }

        private static object ResolveTransformField(Transform t, string fieldPath)
        {
            var parts = fieldPath.Split('.');
            if (parts.Length == 0) return null;

            Vector3 vec;
            switch (parts[0].ToLowerInvariant())
            {
                case "position": vec = t.position; break;
                case "localposition": vec = t.localPosition; break;
                case "rotation":
                case "eulerangles": vec = t.eulerAngles; break;
                case "localrotation":
                case "localeulerangles": vec = t.localEulerAngles; break;
                case "localscale":
                case "scale": vec = t.localScale; break;
                default: return null;
            }

            if (parts.Length == 1) return vec;
            switch (parts[1].ToLowerInvariant())
            {
                case "x": return vec.x;
                case "y": return vec.y;
                case "z": return vec.z;
                case "magnitude": return vec.magnitude;
                default: return null;
            }
        }

        private static object ResolveFieldPath(object obj, Type type, string fieldPath)
        {
            var parts = fieldPath.Split('.');
            object current = obj;
            Type currentType = type;

            foreach (var part in parts)
            {
                if (current == null) return null;

                // Try field first
                var field = currentType.GetField(part,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    current = field.GetValue(current);
                    currentType = field.FieldType;
                    continue;
                }

                // Try property
                var prop = currentType.GetProperty(part,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prop != null && prop.CanRead)
                {
                    current = prop.GetValue(current);
                    currentType = prop.PropertyType;
                    continue;
                }

                return null;
            }

            return current;
        }

        private static bool EvaluateCondition(object actual, string op, string expected)
        {
            if (op == "not_null")
                return actual != null;

            if (actual == null)
                return false;

            switch (op)
            {
                case "==":
                    return string.Equals(actual.ToString(), expected, StringComparison.OrdinalIgnoreCase);
                case "!=":
                    return !string.Equals(actual.ToString(), expected, StringComparison.OrdinalIgnoreCase);
                case ">=":
                case "<=":
                case ">":
                case "<":
                    if (TryParseDouble(actual.ToString(), out double actualNum) &&
                        TryParseDouble(expected, out double expectedNum))
                    {
                        return op switch
                        {
                            ">=" => actualNum >= expectedNum,
                            "<=" => actualNum <= expectedNum,
                            ">" => actualNum > expectedNum,
                            "<" => actualNum < expectedNum,
                            _ => false
                        };
                    }
                    return false;
                case "in_range":
                    // expected format: "[min, max]" or "min,max"
                    var rangeParts = expected.Trim('[', ']', '(', ')').Split(',');
                    if (rangeParts.Length == 2 &&
                        TryParseDouble(actual.ToString(), out double rangeActual) &&
                        TryParseDouble(rangeParts[0].Trim(), out double min) &&
                        TryParseDouble(rangeParts[1].Trim(), out double max))
                    {
                        return rangeActual >= min && rangeActual <= max;
                    }
                    return false;
                default:
                    return false;
            }
        }

        private static bool TryParseDouble(string s, out double result)
        {
            return double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result);
        }
    }
}
