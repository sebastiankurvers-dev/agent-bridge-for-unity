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
        #region Component Operations

        [BridgeRoute("POST", "/component", Category = "gameobjects", Description = "Add component to GameObject")]
        public static string AddComponent(string jsonData)
        {
            var request = JsonUtility.FromJson<ComponentRequest>(jsonData);

            var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            var type = TypeResolver.FindComponentType(request.componentType);
            if (type == null)
            {
                return JsonError($"Component type not found: {request.componentType}");
            }

            try
            {
                var component = Undo.AddComponent(go, type);

                // Apply initial properties if provided
                if (!string.IsNullOrEmpty(request.properties))
                {
                    ApplyComponentProperties(component, request.properties);
                }

                EditorUtility.SetDirty(go);

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "instanceId", go.GetInstanceID() }, { "componentType", type.Name }, { "message", $"Added {type.Name} to {go.name}" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("DELETE", "/component/{id}/{type}", Category = "gameobjects", Description = "Remove component from GameObject")]
        public static string RemoveComponent(int instanceId, string componentType)
        {
            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            var component = FindComponentOnGameObject(go, componentType, out var type);
            if (component == null)
            {
                return JsonError($"Component {componentType} not found on {go.name}");
            }

            try
            {
                Undo.DestroyObjectImmediate(component);
                EditorUtility.SetDirty(go);

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "message", $"Removed {componentType} from {go.name}" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("PUT", "/component", Category = "gameobjects", Description = "Modify component properties")]
        public static string ModifyComponent(string jsonData)
        {
            var request = JsonUtility.FromJson<ComponentRequest>(jsonData);

            var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            var component = FindComponentOnGameObject(go, request.componentType, out var type);
            if (component == null)
            {
                return JsonError($"Component {request.componentType} not found on {go.name}");
            }

            try
            {
                Undo.RecordObject(component, "Agent Bridge Modify Component");
                ApplyComponentProperties(component, request.properties);
                EditorUtility.SetDirty(component);

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "message", $"Modified {request.componentType} on {go.name}" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/component/patch", Category = "gameobjects", Description = "Patch serialized properties by path")]
        public static string PatchSerializedProperties(string jsonData)
        {
            // Pre-process: normalize "value" (any JSON type) → "valueJson" (string)
            // so callers can send {"value": 30.0} instead of {"valueJson": "30.0"}
            jsonData = NormalizePatchValues(jsonData);

            var request = JsonUtility.FromJson<SerializedPropertyPatchRequest>(jsonData);
            if (request == null)
            {
                return JsonError("Failed to parse patch request");
            }

            var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            if (string.IsNullOrWhiteSpace(request.componentType))
            {
                return JsonError("componentType is required");
            }

            if (request.patches == null || request.patches.Length == 0)
            {
                return JsonError("At least one patch is required");
            }

            var component = FindComponentOnGameObject(go, request.componentType, out var type);
            if (component == null)
            {
                return JsonError($"Component {request.componentType} not found on {go.name}");
            }

            var serializedObj = new SerializedObject(component);
            Undo.RecordObject(component, "Agent Bridge Patch Serialized Properties");

            int applied = 0;
            var errors = new List<object>();

            foreach (var patch in request.patches)
            {
                if (patch == null || string.IsNullOrWhiteSpace(patch.propertyPath))
                {
                    errors.Add("Patch missing propertyPath");
                    continue;
                }

                var prop = serializedObj.FindProperty(patch.propertyPath);
                if (prop == null)
                {
                    errors.Add($"Property not found: {patch.propertyPath}");
                    continue;
                }

                try
                {
                    // Explicit object reference assignment by asset path
                    if (!string.IsNullOrWhiteSpace(patch.objectRefAssetPath))
                    {
                        if (ValidateAssetPath(patch.objectRefAssetPath) == null)
                        {
                            errors.Add($"Invalid asset path: {patch.objectRefAssetPath}");
                            continue;
                        }

                        var objRef = AssetDatabase.LoadMainAssetAtPath(patch.objectRefAssetPath);
                        if (objRef == null)
                        {
                            errors.Add($"Asset not found: {patch.objectRefAssetPath}");
                            continue;
                        }

                        if (prop.propertyType != SerializedPropertyType.ObjectReference)
                        {
                            errors.Add($"Property is not ObjectReference: {patch.propertyPath}");
                            continue;
                        }

                        prop.objectReferenceValue = objRef;
                        applied++;
                        continue;
                    }

                    // Explicit object reference assignment by instance id
                    if (patch.objectRefInstanceId != 0)
                    {
                        var objRef = EditorUtility.EntityIdToObject(patch.objectRefInstanceId);
                        if (objRef == null)
                        {
                            errors.Add($"Instance not found: {patch.objectRefInstanceId}");
                            continue;
                        }

                        if (prop.propertyType != SerializedPropertyType.ObjectReference)
                        {
                            errors.Add($"Property is not ObjectReference: {patch.propertyPath}");
                            continue;
                        }

                        prop.objectReferenceValue = objRef;
                        applied++;
                        continue;
                    }

                    // Resolve "value" alias → "valueJson"
                    var resolvedValueJson = patch.valueJson ?? patch.value;
                    if (resolvedValueJson == null)
                    {
                        errors.Add($"Patch has no value for: {patch.propertyPath} (use \"valueJson\" or \"value\" with a JSON-encoded string, e.g. \"30.0\" or \"\\\"hello\\\"\")");
                        continue;
                    }

                    var parsedValue = MiniJSON.Json.Deserialize(resolvedValueJson);
                    if (!TrySetPropertyFromMiniJson(prop, parsedValue))
                    {
                        errors.Add($"Unsupported patch value/type for: {patch.propertyPath}");
                        continue;
                    }

                    applied++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to patch {patch.propertyPath}: {ex.Message}");
                }
            }

            serializedObj.ApplyModifiedProperties();
            EditorUtility.SetDirty(component);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", errors.Count == 0 },
                { "instanceId", request.instanceId },
                { "componentType", request.componentType },
                { "applied", applied },
                { "errors", errors }
            });
        }

        /// <summary>
        /// Pre-process patch JSON: for each patch entry, if "value" is present but "valueJson" is not,
        /// serialize "value" (any JSON type: number, bool, string, array, object) to a JSON string
        /// and set it as "valueJson". This lets callers send intuitive {"value": 30.0} instead of {"valueJson": "30.0"}.
        /// </summary>
        private static string NormalizePatchValues(string jsonData)
        {
            var parsed = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
            if (parsed == null) return jsonData;

            if (!parsed.TryGetValue("patches", out var patchesObj) || patchesObj is not List<object> patchList)
                return jsonData;

            bool modified = false;
            foreach (var item in patchList)
            {
                if (item is not Dictionary<string, object> patch) continue;

                // Skip if valueJson already set
                if (patch.TryGetValue("valueJson", out var vj) && vj is string vjStr && vjStr.Length > 0)
                    continue;

                // Convert "value" (any type) → "valueJson" (serialized string)
                if (patch.TryGetValue("value", out var rawValue) && rawValue != null)
                {
                    patch["valueJson"] = MiniJSON.Json.Serialize(rawValue);
                    patch.Remove("value");
                    modified = true;
                }
            }

            return modified ? MiniJSON.Json.Serialize(parsed) : jsonData;
        }

        [BridgeRoute("PUT", "/renderer/materials", Category = "rendering", Description = "Set renderer material slots")]
        public static string SetRendererMaterials(string jsonData)
        {
            var request = JsonUtility.FromJson<RendererMaterialsRequest>(jsonData);
            if (request == null)
            {
                return JsonError("Failed to parse renderer material request");
            }

            var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            if (request.materialPaths == null || request.materialPaths.Length == 0)
            {
                return JsonError("materialPaths is required");
            }

            Renderer renderer = null;
            if (!string.IsNullOrWhiteSpace(request.componentType))
            {
                var comp = FindComponentOnGameObject(go, request.componentType, out _);
                renderer = comp as Renderer;
            }
            else
            {
                renderer = go.GetComponent<Renderer>();
            }

            if (renderer == null)
            {
                return JsonError("Renderer component not found");
            }

            var loadedMaterials = new List<Material>();
            foreach (var matPath in request.materialPaths)
            {
                if (string.IsNullOrWhiteSpace(matPath))
                {
                    return JsonError("materialPaths contains an empty path");
                }

                if (ValidateAssetPath(matPath) == null)
                {
                    return JsonError($"Invalid material path: {matPath}");
                }

                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null)
                {
                    return JsonError($"Material not found: {matPath}");
                }

                loadedMaterials.Add(mat);
            }

            Undo.RecordObject(renderer, "Agent Bridge Set Renderer Materials");

            var existing = renderer.sharedMaterials?.ToList() ?? new List<Material>();
            bool hasSlotIndices = request.slotIndices != null && request.slotIndices.Length > 0;

            if (hasSlotIndices)
            {
                if (request.slotIndices.Length != loadedMaterials.Count)
                {
                    return JsonError("slotIndices length must match materialPaths length");
                }

                for (int i = 0; i < request.slotIndices.Length; i++)
                {
                    var slot = request.slotIndices[i];
                    if (slot < 0)
                    {
                        return JsonError($"Invalid slot index: {slot}");
                    }

                    while (existing.Count <= slot)
                    {
                        existing.Add(null);
                    }

                    existing[slot] = loadedMaterials[i];
                }

                renderer.sharedMaterials = existing.ToArray();
            }
            else
            {
                renderer.sharedMaterials = loadedMaterials.ToArray();
            }

            EditorUtility.SetDirty(renderer);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "instanceId", request.instanceId },
                { "rendererType", renderer.GetType().Name },
                { "materialCount", renderer.sharedMaterials.Length },
                { "usedSlotIndices", hasSlotIndices }
            });
        }

        private static void ApplyComponentProperties(Component component, string jsonProperties)
        {
            if (string.IsNullOrEmpty(jsonProperties)) return;

            var serializedObj = new SerializedObject(component);
            var parsed = MiniJSON.Json.Deserialize(jsonProperties) as Dictionary<string, object>;

            if (parsed != null)
            {
                int applied = 0;
                var failed = new List<string>();
                foreach (var kvp in parsed)
                {
                    var prop = serializedObj.FindProperty(kvp.Key);
                    if (prop != null && TrySetPropertyFromMiniJson(prop, kvp.Value))
                    {
                        applied++;
                    }
                    else
                    {
                        failed.Add(kvp.Key);
                    }
                }
                serializedObj.ApplyModifiedProperties();

                // Fallback: try JsonUtility for any fields SerializedProperty couldn't handle
                if (failed.Count > 0)
                {
                    try
                    {
                        JsonUtility.FromJsonOverwrite(jsonProperties, component);
                    }
                    catch
                    {
                        // JsonUtility can't overwrite built-in engine types — report which props failed
                        throw new Exception(
                            $"Could not set properties: [{string.Join(", ", failed)}]. " +
                            $"For built-in Unity components, use unity_patch_serialized_properties with serialized property paths " +
                            $"(e.g., 'm_CastShadows', 'm_ReceiveShadows'). Use unity_get_components to discover property paths.");
                    }
                }
            }
            else
            {
                // Fallback for unparseable JSON
                JsonUtility.FromJsonOverwrite(jsonProperties, component);
                serializedObj.ApplyModifiedProperties();
            }
        }

        private static bool TrySetPropertyFromMiniJson(SerializedProperty prop, object value)
        {
            if (prop == null) return false;

            if (prop.isArray && prop.propertyType != SerializedPropertyType.String)
            {
                if (value is IList<object> listValue)
                {
                    prop.arraySize = listValue.Count;
                    for (int i = 0; i < listValue.Count; i++)
                    {
                        var child = prop.GetArrayElementAtIndex(i);
                        TrySetPropertyFromMiniJson(child, listValue[i]);
                    }
                    return true;
                }
            }

            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = Convert.ToInt32(value);
                    return true;

                case SerializedPropertyType.Float:
                    prop.floatValue = Convert.ToSingle(value);
                    return true;

                case SerializedPropertyType.Boolean:
                    prop.boolValue = Convert.ToBoolean(value);
                    return true;

                case SerializedPropertyType.String:
                    prop.stringValue = value?.ToString() ?? string.Empty;
                    return true;

                case SerializedPropertyType.Enum:
                    if (value is string enumName)
                    {
                        int enumIdx = Array.IndexOf(prop.enumNames, enumName);
                        if (enumIdx >= 0)
                        {
                            prop.enumValueIndex = enumIdx;
                            return true;
                        }
                    }

                    prop.enumValueIndex = Convert.ToInt32(value);
                    return true;

                case SerializedPropertyType.Vector2:
                    if (TryReadVector(value, 2, out var v2))
                    {
                        prop.vector2Value = new Vector2(v2[0], v2[1]);
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Vector3:
                    if (TryReadVector(value, 3, out var v3))
                    {
                        prop.vector3Value = new Vector3(v3[0], v3[1], v3[2]);
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Vector4:
                    if (TryReadVector(value, 4, out var v4))
                    {
                        prop.vector4Value = new Vector4(v4[0], v4[1], v4[2], v4[3]);
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Quaternion:
                    if (TryReadQuaternion(value, out var q))
                    {
                        prop.quaternionValue = q;
                        return true;
                    }
                    return false;

                case SerializedPropertyType.Color:
                    if (TryReadColor(value, out var c))
                    {
                        prop.colorValue = c;
                        return true;
                    }
                    return false;

                case SerializedPropertyType.ObjectReference:
                    if (value == null)
                    {
                        prop.objectReferenceValue = null;
                        return true;
                    }

                    if (value is string path)
                    {
                        if (ValidateAssetPath(path) == null) return false;
                        prop.objectReferenceValue = AssetDatabase.LoadMainAssetAtPath(path);
                        return prop.objectReferenceValue != null;
                    }

                    if (value is Dictionary<string, object> dict)
                    {
                        if (dict.TryGetValue("assetPath", out var assetPathObj))
                        {
                            var assetPath = assetPathObj?.ToString();
                            if (string.IsNullOrWhiteSpace(assetPath) || ValidateAssetPath(assetPath) == null) return false;
                            prop.objectReferenceValue = AssetDatabase.LoadMainAssetAtPath(assetPath);
                            return prop.objectReferenceValue != null;
                        }

                        if (dict.TryGetValue("instanceId", out var instanceObj))
                        {
                            int id = Convert.ToInt32(instanceObj);
                            prop.objectReferenceValue = EditorUtility.EntityIdToObject(id);
                            return prop.objectReferenceValue != null;
                        }
                    }

                    return false;

                case SerializedPropertyType.Generic:
                    if (value is Dictionary<string, object> genericDict)
                    {
                        foreach (var kv in genericDict)
                        {
                            var child = prop.FindPropertyRelative(kv.Key);
                            if (child != null)
                            {
                                TrySetPropertyFromMiniJson(child, kv.Value);
                            }
                        }
                        return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        private static bool TryReadVector(object value, int size, out float[] result)
        {
            result = null;

            // Array format: [x, y, z] or [x, y, z, w]
            if (value is IList<object> list && list.Count >= size)
            {
                result = new float[size];
                for (int i = 0; i < size; i++)
                    result[i] = Convert.ToSingle(list[i]);
                return true;
            }

            // Dict format: {"x": ..., "y": ...} / {"x": ..., "y": ..., "z": ...} / {"x": ..., "y": ..., "z": ..., "w": ...}
            if (value is Dictionary<string, object> dict)
            {
                string[] keys = size switch
                {
                    2 => new[] { "x", "y" },
                    3 => new[] { "x", "y", "z" },
                    4 => new[] { "x", "y", "z", "w" },
                    _ => null
                };

                if (keys != null)
                {
                    result = new float[size];
                    for (int i = 0; i < size; i++)
                    {
                        if (!dict.TryGetValue(keys[i], out var val)) return false;
                        result[i] = Convert.ToSingle(val);
                    }
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadQuaternion(object value, out Quaternion q)
        {
            q = Quaternion.identity;
            if (TryReadVector(value, 4, out var vec))
            {
                q = new Quaternion(vec[0], vec[1], vec[2], vec[3]);
                return true;
            }

            if (value is Dictionary<string, object> dict
                && dict.TryGetValue("x", out var xObj)
                && dict.TryGetValue("y", out var yObj)
                && dict.TryGetValue("z", out var zObj)
                && dict.TryGetValue("w", out var wObj))
            {
                q = new Quaternion(
                    Convert.ToSingle(xObj),
                    Convert.ToSingle(yObj),
                    Convert.ToSingle(zObj),
                    Convert.ToSingle(wObj));
                return true;
            }

            return false;
        }

        private static bool TryReadColor(object value, out Color c)
        {
            c = Color.white;
            if (TryReadVector(value, 3, out var vec3))
            {
                c = new Color(vec3[0], vec3[1], vec3[2], 1f);
                return true;
            }

            if (TryReadVector(value, 4, out var vec4))
            {
                c = new Color(vec4[0], vec4[1], vec4[2], vec4[3]);
                return true;
            }

            if (value is Dictionary<string, object> dict
                && dict.TryGetValue("r", out var rObj)
                && dict.TryGetValue("g", out var gObj)
                && dict.TryGetValue("b", out var bObj))
            {
                float a = 1f;
                if (dict.TryGetValue("a", out var aObj))
                {
                    a = Convert.ToSingle(aObj);
                }

                c = new Color(
                    Convert.ToSingle(rObj),
                    Convert.ToSingle(gObj),
                    Convert.ToSingle(bObj),
                    a);
                return true;
            }

            return false;
        }

        private static object ConvertValue(object value, Type targetType)
        {
            if (value == null) return null;
            if (targetType.IsAssignableFrom(value.GetType())) return value;

            if (targetType == typeof(int)) return Convert.ToInt32(value);
            if (targetType == typeof(float)) return Convert.ToSingle(value);
            if (targetType == typeof(double)) return Convert.ToDouble(value);
            if (targetType == typeof(bool)) return Convert.ToBoolean(value);
            if (targetType == typeof(string)) return value.ToString();

            if (targetType == typeof(Vector3) && value is IList<object> v3List && v3List.Count >= 3)
            {
                return new Vector3(
                    Convert.ToSingle(v3List[0]),
                    Convert.ToSingle(v3List[1]),
                    Convert.ToSingle(v3List[2]));
            }

            return value;
        }

        #endregion

        #region AI Bridge Editors

        /// <summary>
        /// Lists all registered AI bridge editors and their target component types.
        /// </summary>
        [BridgeRoute("GET", "/component/ai-editors", Category = "gameobjects", Description = "List all registered AI bridge editors", ReadOnly = true)]
        public static string GetAiBridgeEditors()
        {
            var editors = UnityAgentBridge.AiBridgeEditorRegistry.GetRegisteredEditors();
            return MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "editors", editors },
                { "count", editors.Count },
                { "hint", "Use GET /component/ai-inspect/{instanceId}/{componentType} to inspect a component with its AI editor" }
            });
        }

        /// <summary>
        /// Inspects a component using its registered AI bridge editor.
        /// Returns clean property names, types, descriptions, and current values.
        /// </summary>
        [BridgeRoute("GET", "/component/ai-inspect/{id}/{type}", Category = "gameobjects", Description = "AI-inspect a component via bridge editor", ReadOnly = true, FailCode = 404)]
        public static string AiInspectComponent(int instanceId, string componentType)
        {
            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null)
                return JsonError($"GameObject not found for instanceId {instanceId}");

            var component = FindComponentOnGameObject(go, componentType, out var type);
            if (component == null)
                return JsonError($"Component {componentType} not found on {go.name}");

            var editor = UnityAgentBridge.AiBridgeEditorRegistry.CreateEditor(component);
            if (editor == null)
                return JsonError($"No AI bridge editor registered for {componentType}. Use GET /components/{instanceId} for raw serialized properties.");

            var definition = editor.GetDefinition();
            var values = editor.Dump();

            return MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "gameObject", go.name },
                { "instanceId", instanceId },
                { "componentType", component.GetType().Name },
                { "hasAiEditor", true },
                { "properties", definition },
                { "values", values }
            });
        }

        /// <summary>
        /// Applies property changes to a component via its AI bridge editor.
        /// Request: { "instanceId": 123, "componentType": "Camera", "values": { "fieldOfView": 60 } }
        /// </summary>
        [BridgeRoute("PUT", "/component/ai-apply", Category = "gameobjects", Description = "Apply properties via AI bridge editor")]
        public static string AiApplyComponent(string jsonData)
        {
            var parsed = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
            if (parsed == null)
                return JsonError("Failed to parse request JSON");

            if (!parsed.TryGetValue("instanceId", out var idObj) || idObj == null)
                return JsonError("instanceId is required");
            int instanceId = Convert.ToInt32(idObj);

            if (!parsed.TryGetValue("componentType", out var typeObj) || typeObj == null)
                return JsonError("componentType is required");
            string componentType = typeObj.ToString();

            if (!parsed.TryGetValue("values", out var valuesObj) || !(valuesObj is Dictionary<string, object> values))
                return JsonError("values is required (object with property name → value)");

            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null)
                return JsonError($"GameObject not found for instanceId {instanceId}");

            var component = FindComponentOnGameObject(go, componentType, out var type);
            if (component == null)
                return JsonError($"Component {componentType} not found on {go.name}");

            var editor = UnityAgentBridge.AiBridgeEditorRegistry.CreateEditor(component);
            if (editor == null)
                return JsonError($"No AI bridge editor for {componentType}. Use PUT /component instead.");

            var (applied, errors) = editor.Apply(values);

            return MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "success", errors.Count == 0 },
                { "applied", applied },
                { "errors", errors },
                { "gameObject", go.name },
                { "componentType", componentType }
            });
        }

        #endregion
    }
}
