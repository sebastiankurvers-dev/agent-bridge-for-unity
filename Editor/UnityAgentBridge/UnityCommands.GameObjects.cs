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
        [BridgeRoute("PUT", "/gameobject/{id}", Category = "gameobjects", Description = "Modify GameObject (name/active/transform/tag/layer)")]
        public static string ModifyGameObject(int instanceId, string jsonData)
        {
            var obj = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (obj == null)
            {
                return JsonError("GameObject not found");
            }

            var modification = JsonUtility.FromJson<ModifyRequest>(jsonData);
            Undo.RecordObject(obj, "Agent Bridge Modify");

            var changesApplied = new List<string>();

            if (modification.name != null)
            {
                obj.name = modification.name;
                changesApplied.Add("name");
            }

            if (modification.active >= 0)
            {
                obj.SetActive(modification.active == 1);
                changesApplied.Add("active");
            }

            if (modification.layer >= 0)
            {
                obj.layer = modification.layer;
                changesApplied.Add("layer");
            }

            if (modification.tag != null)
            {
                obj.tag = modification.tag;
                changesApplied.Add("tag");
            }

            if (modification.transform != null)
            {
                Undo.RecordObject(obj.transform, "Agent Bridge Transform");
                var t = modification.transform;

                // Detect which transform keys were actually present in the raw JSON
                var transformKeys = new HashSet<string>();
                try
                {
                    var parsed = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
                    if (parsed != null && parsed.ContainsKey("transform"))
                    {
                        var tDict = parsed["transform"] as Dictionary<string, object>;
                        if (tDict != null)
                            foreach (var k in tDict.Keys)
                                transformKeys.Add(k);
                    }
                }
                catch { /* fallback: apply all non-null fields */ }

                if (t.position != null && (transformKeys.Count == 0 || transformKeys.Contains("position")))
                {
                    obj.transform.position = t.position.ToVector3();
                    changesApplied.Add("position");
                }
                if (t.localPosition != null && (transformKeys.Count == 0 || transformKeys.Contains("localPosition")))
                {
                    obj.transform.localPosition = t.localPosition.ToVector3();
                    changesApplied.Add("localPosition");
                }
                if (t.rotation != null && (transformKeys.Count == 0 || transformKeys.Contains("rotation")))
                {
                    obj.transform.eulerAngles = t.rotation.ToVector3();
                    changesApplied.Add("rotation");
                }
                if (t.localRotation != null && (transformKeys.Count == 0 || transformKeys.Contains("localRotation")))
                {
                    obj.transform.localEulerAngles = t.localRotation.ToVector3();
                    changesApplied.Add("localRotation");
                }
                if (t.localScale != null && (transformKeys.Count == 0 || transformKeys.Contains("localScale")))
                {
                    obj.transform.localScale = t.localScale.ToVector3();
                    changesApplied.Add("localScale");
                }
            }

            EditorUtility.SetDirty(obj);

            var tr = obj.transform;
            var resultTransform = new Dictionary<string, object>
            {
                { "position", new float[] { tr.position.x, tr.position.y, tr.position.z } },
                { "localPosition", new float[] { tr.localPosition.x, tr.localPosition.y, tr.localPosition.z } },
                { "rotation", new float[] { tr.eulerAngles.x, tr.eulerAngles.y, tr.eulerAngles.z } },
                { "localScale", new float[] { tr.localScale.x, tr.localScale.y, tr.localScale.z } }
            };

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "instanceId", obj.GetInstanceID() },
                { "name", obj.name },
                { "changesApplied", changesApplied },
                { "transform", resultTransform }
            });
        }

        [BridgeRoute("POST", "/spawn", Category = "gameobjects", Description = "Spawn prefab or create empty GameObject")]
        public static string Spawn(string jsonData)
        {
            var request = JsonUtility.FromJson<SpawnRequest>(jsonData);

            // JsonUtility.FromJson creates Vector3Data(0,0,0) for absent fields.
            // Parse raw JSON to detect which keys were actually sent.
            var rawDict = MiniJSON.Json.Deserialize(jsonData ?? "{}") as Dictionary<string, object>;
            bool hasPosition = rawDict != null && rawDict.ContainsKey("position");
            bool hasRotation = rawDict != null && rawDict.ContainsKey("rotation");
            bool hasScale = rawDict != null && rawDict.ContainsKey("scale");

            GameObject newObj;

            if (!string.IsNullOrEmpty(request.prefabPath))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(request.prefabPath);
                if (prefab == null)
                {
                    // Try to find by name
                    var guids = AssetDatabase.FindAssets($"t:Prefab {request.prefabPath}");
                    foreach (var guid in guids)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (prefab != null && prefab.name == request.prefabPath)
                        {
                            break;
                        }
                    }
                }

                if (prefab == null)
                {
                    return JsonError($"Prefab not found: {request.prefabPath}");
                }

                newObj = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            }
            else
            {
                newObj = new GameObject(request.name ?? "New GameObject");
            }

            Undo.RegisterCreatedObjectUndo(newObj, "Agent Bridge Spawn");

            if (hasPosition && request.position != null)
            {
                newObj.transform.position = request.position.ToVector3();
            }

            if (hasRotation && request.rotation != null)
            {
                newObj.transform.eulerAngles = request.rotation.ToVector3();
            }

            if (hasScale && request.scale != null)
            {
                newObj.transform.localScale = request.scale.ToVector3();
            }

            if (request.parentId != -1)
            {
                var parent = EditorUtility.EntityIdToObject(request.parentId) as GameObject;
                if (parent != null)
                {
                    newObj.transform.SetParent(parent.transform);
                }
            }

            if (!string.IsNullOrEmpty(request.name))
            {
                newObj.name = request.name;
            }

            EditorUtility.SetDirty(newObj);

            return JsonResult(new Dictionary<string, object> { { "success", true }, { "instanceId", newObj.GetInstanceID() }, { "name", newObj.name } });
        }

        [BridgeRoute("POST", "/spawn/primitive", Category = "gameobjects", Description = "Spawn a Unity primitive (Cube, Sphere, etc.) with optional inline color material")]
        public static string SpawnPrimitive(string jsonData)
        {
            var request = JsonUtility.FromJson<SpawnPrimitiveRequest>(NormalizeColorFields(jsonData));

            if (string.IsNullOrEmpty(request.primitiveType))
                return JsonError("primitiveType is required (Cube, Sphere, Cylinder, Capsule, Plane, Quad)");

            if (!Enum.TryParse<PrimitiveType>(request.primitiveType, true, out var primitive))
                return JsonError($"Unknown primitiveType: {request.primitiveType}. Valid: Cube, Sphere, Cylinder, Capsule, Plane, Quad");

            var rawDict = MiniJSON.Json.Deserialize(jsonData ?? "{}") as Dictionary<string, object>;
            bool hasPosition = rawDict != null && rawDict.ContainsKey("position");
            bool hasRotation = rawDict != null && rawDict.ContainsKey("rotation");
            bool hasScale    = rawDict != null && rawDict.ContainsKey("scale");

            var go = GameObject.CreatePrimitive(primitive);
            Undo.RegisterCreatedObjectUndo(go, "Agent Bridge Spawn Primitive");

            if (!string.IsNullOrEmpty(request.name))
                go.name = request.name;

            if (hasPosition && request.position != null)
                go.transform.position = request.position.ToVector3();

            if (hasRotation && request.rotation != null)
                go.transform.eulerAngles = request.rotation.ToVector3();

            if (hasScale && request.scale != null)
                go.transform.localScale = request.scale.ToVector3();

            if (request.parentId != -1)
            {
                var parent = EditorUtility.EntityIdToObject(request.parentId) as GameObject;
                if (parent != null)
                    go.transform.SetParent(parent.transform);
            }

            // Apply inline color material if color is provided
            if (request.color != null && request.color.Length >= 3)
            {
                var renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    // Try URP Lit first, fall back to Standard
                    var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                    var mat = new Material(shader);

                    var color = new Color(
                        request.color[0],
                        request.color[1],
                        request.color[2],
                        request.color.Length >= 4 ? request.color[3] : 1f);
                    mat.color = color;

                    // Also set _BaseColor for URP shaders
                    if (mat.HasProperty("_BaseColor"))
                        mat.SetColor("_BaseColor", color);

                    if (request.metallic >= 0f)
                        mat.SetFloat("_Metallic", request.metallic);

                    if (request.smoothness >= 0f)
                    {
                        if (mat.HasProperty("_Smoothness"))
                            mat.SetFloat("_Smoothness", request.smoothness);
                        else if (mat.HasProperty("_Glossiness"))
                            mat.SetFloat("_Glossiness", request.smoothness);
                    }

                    renderer.sharedMaterial = mat;
                }
            }

            // 2D mode: swap 3D collider for 2D equivalent
            if (request.mode2D)
            {
                var collider3D = go.GetComponent<Collider>();
                if (collider3D != null)
                    UnityEngine.Object.DestroyImmediate(collider3D);
                go.AddComponent<BoxCollider2D>();
            }

            EditorUtility.SetDirty(go);

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "instanceId", go.GetInstanceID() },
                { "name", go.name },
                { "primitiveType", request.primitiveType }
            };
            if (request.mode2D)
                result["mode2D"] = true;

            return JsonResult(result);
        }

        [BridgeRoute("POST", "/spawn/batch", Category = "gameobjects", Description = "Spawn multiple prefabs in one call", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string SpawnBatch(string jsonData)
        {
            var request = JsonUtility.FromJson<SpawnBatchRequest>(jsonData);
            if (request.entries == null || request.entries.Length == 0)
            {
                return JsonError("No entries provided");
            }

            // Parse raw JSON to detect which keys each entry actually contains.
            // JsonUtility creates Vector3Data(0,0,0) for absent fields.
            var rawEntryKeys = new List<HashSet<string>>();
            var rawDict = MiniJSON.Json.Deserialize(jsonData ?? "{}") as Dictionary<string, object>;
            var rawEntries = rawDict != null && rawDict.ContainsKey("entries")
                ? rawDict["entries"] as List<object> : null;
            if (rawEntries != null)
            {
                foreach (var re in rawEntries)
                {
                    var entryDict = re as Dictionary<string, object>;
                    rawEntryKeys.Add(entryDict != null ? new HashSet<string>(entryDict.Keys) : new HashSet<string>());
                }
            }

            var results = new List<Dictionary<string, object>>();
            int successCount = 0;
            int errorCount = 0;

            Undo.SetCurrentGroupName("Agent Bridge Spawn Batch");
            int undoGroup = Undo.GetCurrentGroup();

            int entryIndex = 0;
            foreach (var entry in request.entries)
            {
                try
                {
                    GameObject newObj = null;

                    if (!string.IsNullOrEmpty(entry.prefabPath))
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.prefabPath);
                        if (prefab == null)
                        {
                            // Try to find by name
                            var guids = AssetDatabase.FindAssets($"t:Prefab {entry.prefabPath}");
                            foreach (var guid in guids)
                            {
                                var foundPath = AssetDatabase.GUIDToAssetPath(guid);
                                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(foundPath);
                                if (prefab != null && prefab.name == entry.prefabPath)
                                    break;
                            }
                        }

                        if (prefab == null)
                        {
                            results.Add(new Dictionary<string, object> { { "success", false }, { "error", $"Prefab not found: {entry.prefabPath}" } });
                            errorCount++;
                            continue;
                        }

                        newObj = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    }
                    else
                    {
                        newObj = new GameObject(entry.name ?? "New GameObject");
                    }

                    Undo.RegisterCreatedObjectUndo(newObj, "Agent Bridge Spawn Batch Item");

                    var eKeys = entryIndex < rawEntryKeys.Count ? rawEntryKeys[entryIndex] : new HashSet<string>();
                    if (eKeys.Contains("position") && entry.position != null)
                        newObj.transform.position = entry.position.ToVector3();
                    if (eKeys.Contains("rotation") && entry.rotation != null)
                        newObj.transform.eulerAngles = entry.rotation.ToVector3();
                    if (eKeys.Contains("scale") && entry.scale != null)
                        newObj.transform.localScale = entry.scale.ToVector3();

                    if (entry.parentId != -1)
                    {
                        var parent = EditorUtility.EntityIdToObject(entry.parentId) as GameObject;
                        if (parent != null)
                            newObj.transform.SetParent(parent.transform);
                    }

                    if (!string.IsNullOrEmpty(entry.name))
                        newObj.name = entry.name;

                    EditorUtility.SetDirty(newObj);
                    results.Add(new Dictionary<string, object> { { "success", true }, { "instanceId", newObj.GetInstanceID() }, { "name", newObj.name } });
                    successCount++;
                }
                catch (Exception ex)
                {
                    results.Add(new Dictionary<string, object> { { "success", false }, { "error", ex.Message } });
                    errorCount++;
                }
                entryIndex++;
            }

            Undo.CollapseUndoOperations(undoGroup);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", errorCount == 0 },
                { "totalRequested", request.entries.Length },
                { "successCount", successCount },
                { "errorCount", errorCount },
                { "results", results }
            });
        }

        [BridgeRoute("DELETE", "/delete/{id}", Category = "gameobjects", Description = "Delete GameObject by instance ID")]
        public static string DeleteGameObject(int instanceId)
        {
            var obj = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (obj == null)
            {
                return JsonError("GameObject not found");
            }

            Undo.DestroyObjectImmediate(obj);
            return JsonSuccess();
        }


	        public static string GetComponents(int instanceId, bool namesOnly = false)
	        {
	            var obj = EditorUtility.EntityIdToObject(instanceId) as GameObject;
	            if (!obj)
	            {
	                return JsonError($"GameObject not found for instanceId {instanceId}");
	            }

	            // Fast path: just return component type names + AI editor availability
	            if (namesOnly)
	            {
	                var entries = obj.GetComponents<Component>()
	                    .Where(c => c != null)
	                    .Select(c => {
	                        bool hasAi = UnityAgentBridge.AiBridgeEditorRegistry.HasEditor(c.GetType());
	                        return hasAi
	                            ? $"{{\"type\":\"{c.GetType().Name}\",\"aiEditor\":true}}"
	                            : $"\"{c.GetType().Name}\"";
	                    });
	                return "{\"components\":[" + string.Join(",", entries) + "]}";
	            }

	            // IMPORTANT SAFETY NOTE:
	            // Calling arbitrary Component property getters via reflection can hard-crash Unity (native PhysX)
	            // for some components (e.g., Collider.bounds) depending on object lifetime/geometry state.
	            // We therefore avoid reflective property getters here and instead expose serialized properties
	            // (inspector-backed) via SerializedObject, plus public fields as a lightweight supplement.
	            const int MaxSerializedPropertiesPerComponent = 200;

	            var components = new List<ComponentDetails>();

	            foreach (var component in obj.GetComponents<Component>())
	            {
	                if (component == null) continue;

	                var details = new ComponentDetails
	                {
	                    type = component.GetType().Name,
	                    fullType = component.GetType().FullName,
	                    fields = new List<FieldData>()
	                };

	                // Get public fields
	                var fields = component.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
	                foreach (var field in fields)
	                {
	                    try
	                    {
	                        var value = field.GetValue(component);
	                        details.fields.Add(new FieldData
	                        {
	                            name = field.Name,
	                            type = field.FieldType.Name,
	                            value = value?.ToString() ?? "null"
	                        });
	                    }
	                    catch
	                    {
	                        // Skip fields that can't be read
	                    }
	                }

	                // Get serialized properties (safe, inspector-backed)
	                try
	                {
	                    var serializedObj = new SerializedObject(component);
	                    var it = serializedObj.GetIterator();
	                    bool enterChildren = true;
	                    int count = 0;
	                    while (it.NextVisible(enterChildren))
	                    {
	                        enterChildren = false;
	                        if (count >= MaxSerializedPropertiesPerComponent) break;

	                        // Skip very noisy internal bookkeeping unless explicitly needed.
	                        if (it.propertyPath == "m_Script") continue;

	                        // Avoid exploding output for arrays/generics: report summary and don't descend.
	                        if (it.isArray && it.propertyType != SerializedPropertyType.String)
	                        {
	                            details.fields.Add(new FieldData
	                            {
	                                name = it.propertyPath,
	                                type = it.propertyType.ToString(),
	                                value = $"<array size={it.arraySize}>",
	                                isProperty = true
	                            });
	                            count++;
	                            continue;
	                        }

	                        details.fields.Add(new FieldData
	                        {
	                            name = it.propertyPath,
	                            type = it.propertyType.ToString(),
	                            value = FormatSerializedPropertyValue(it),
	                            isProperty = true
	                        });
	                        count++;
	                    }

	                    if (count >= MaxSerializedPropertiesPerComponent)
	                    {
	                        details.fields.Add(new FieldData
	                        {
	                            name = "__truncated__",
	                            type = "string",
	                            value = $"<serialized properties truncated at {MaxSerializedPropertiesPerComponent}>",
	                            isProperty = true
	                        });
	                    }
	                }
	                catch
	                {
	                    // If serialization fails for a component type, fall back to whatever we already collected.
	                }

	                components.Add(details);
	            }

	            var jsonParts = components.Select(c => JsonUtility.ToJson(c));
	            return "{\"components\":[" + string.Join(",", jsonParts) + "]}";
	        }

	        private static string FormatSerializedPropertyValue(SerializedProperty prop)
	        {
	            if (prop == null) return "null";

	            try
	            {
	                switch (prop.propertyType)
	                {
	                    case SerializedPropertyType.Integer:
	                        return prop.intValue.ToString();
	                    case SerializedPropertyType.Boolean:
	                        return prop.boolValue ? "true" : "false";
	                    case SerializedPropertyType.Float:
	                        return prop.floatValue.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
	                    case SerializedPropertyType.String:
	                        return prop.stringValue ?? string.Empty;
	                    case SerializedPropertyType.Enum:
	                        return prop.enumDisplayNames != null && prop.enumValueIndex >= 0 && prop.enumValueIndex < prop.enumDisplayNames.Length
	                            ? prop.enumDisplayNames[prop.enumValueIndex]
	                            : prop.enumValueIndex.ToString();
	                    case SerializedPropertyType.Color:
	                        {
	                            var c = prop.colorValue;
	                            return $"rgba({c.r:F3},{c.g:F3},{c.b:F3},{c.a:F3})";
	                        }
	                    case SerializedPropertyType.Vector2:
	                        {
	                            var v = prop.vector2Value;
	                            return $"({v.x:F4},{v.y:F4})";
	                        }
	                    case SerializedPropertyType.Vector3:
	                        {
	                            var v = prop.vector3Value;
	                            return $"({v.x:F4},{v.y:F4},{v.z:F4})";
	                        }
	                    case SerializedPropertyType.Vector4:
	                        {
	                            var v = prop.vector4Value;
	                            return $"({v.x:F4},{v.y:F4},{v.z:F4},{v.w:F4})";
	                        }
	                    case SerializedPropertyType.Rect:
	                        {
	                            var r = prop.rectValue;
	                            return $"rect({r.x:F3},{r.y:F3},{r.width:F3},{r.height:F3})";
	                        }
	                    case SerializedPropertyType.Bounds:
	                        {
	                            var b = prop.boundsValue;
	                            return $"bounds(center=({b.center.x:F3},{b.center.y:F3},{b.center.z:F3}), size=({b.size.x:F3},{b.size.y:F3},{b.size.z:F3}))";
	                        }
	                    case SerializedPropertyType.ObjectReference:
	                        {
	                            var o = prop.objectReferenceValue;
	                            if (!o) return "null";
	                            var path = AssetDatabase.GetAssetPath(o);
	                            if (!string.IsNullOrWhiteSpace(path))
	                            {
	                                return path;
	                            }
	                            return $"{o.name} (instanceId={o.GetInstanceID()})";
	                        }
	                    case SerializedPropertyType.LayerMask:
	                        return prop.intValue.ToString();
	                    case SerializedPropertyType.AnimationCurve:
	                        return "<animationCurve>";
	                    case SerializedPropertyType.Gradient:
	                        return "<gradient>";
	                    case SerializedPropertyType.Quaternion:
	                        {
	                            var q = prop.quaternionValue;
	                            return $"quat({q.x:F4},{q.y:F4},{q.z:F4},{q.w:F4})";
	                        }
	                    case SerializedPropertyType.ExposedReference:
	                        return "<exposedReference>";
	                    case SerializedPropertyType.FixedBufferSize:
	                        return prop.fixedBufferSize.ToString();
	                    case SerializedPropertyType.ManagedReference:
	                        return prop.managedReferenceFullTypename ?? "<managedReference>";
	                    case SerializedPropertyType.Character:
	                        return ((char)prop.intValue).ToString();
	                    case SerializedPropertyType.Hash128:
	                        return prop.hash128Value.ToString();
	                    default:
	                        return "<unsupported>";
	                }
	            }
	            catch
	            {
	                return "<unreadable>";
	            }
	        }

        // Reparent, BatchModifyChildren, and ScriptableObject operations moved to UnityCommands.GameObjects.Hierarchy.cs
    }
}
