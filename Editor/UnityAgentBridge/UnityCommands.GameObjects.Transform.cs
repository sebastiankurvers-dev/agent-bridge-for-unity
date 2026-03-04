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
        [BridgeRoute("POST", "/runtime/values", Category = "runtime", Description = "Read runtime field values from component", ReadOnly = true)]
        public static string GetRuntimeValues(string jsonData)
        {
            var request = JsonUtility.FromJson<RuntimeValuesRequest>(jsonData);
            if (request == null)
            {
                return JsonError("Failed to parse runtime values request");
            }

            var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            if (string.IsNullOrEmpty(request.componentType))
            {
                return JsonError("componentType is required");
            }

            var component = FindComponentOnGameObject(go, request.componentType, out var type);
            if (component == null)
            {
                return JsonError($"Component {request.componentType} not found on {go.name}");
            }

            bool includePrivate = request.includePrivate != 0;
            bool includeProperties = request.includeProperties == 1;
            var filterNames = request.fieldNames != null && request.fieldNames.Length > 0
                ? new HashSet<string>(request.fieldNames, StringComparer.OrdinalIgnoreCase)
                : null;
            var matchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var fields = new List<Dictionary<string, object>>();

            // Gather fields, including private fields declared on base types.
            foreach (var field in EnumerateInstanceFields(type, includePrivate))
            {
                // Skip compiler-generated backing fields
                if (field.Name.StartsWith("<")) continue;
                if (field.IsStatic) continue;
                if (!seenFieldNames.Add(field.Name)) continue;

                bool isPrivate = !field.IsPublic;
                bool isSerialized = field.IsDefined(typeof(SerializeField), true);

                // If no filter specified, skip private non-serialized fields
                if (filterNames == null && isPrivate && !isSerialized) continue;

                // If filter specified, only include matching names
                if (filterNames != null && !filterNames.Contains(field.Name)) continue;

                try
                {
                    var value = field.GetValue(component);
                    fields.Add(new Dictionary<string, object>
                    {
                        { "name", field.Name },
                        { "type", field.FieldType.Name },
                        { "valueJson", SerializeValueToJson(value, field.FieldType) },
                        { "isProperty", false },
                        { "isPrivate", isPrivate },
                        { "isSerialized", isSerialized }
                    });
                    matchedNames.Add(field.Name);
                }
                catch
                {
                    // Skip fields that can't be read
                }
            }

            // Gather properties if requested
            if (includeProperties)
            {
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;

                    // If filter specified, only include matching names
                    if (filterNames != null && !filterNames.Contains(prop.Name)) continue;

                    // Skip noisy base class properties when no filter
                    if (filterNames == null && IsNoisyBaseProperty(prop.Name)) continue;

                    try
                    {
                        var value = prop.GetValue(component);
                        fields.Add(new Dictionary<string, object>
                        {
                            { "name", prop.Name },
                            { "type", prop.PropertyType.Name },
                            { "valueJson", SerializeValueToJson(value, prop.PropertyType) },
                            { "isProperty", true },
                            { "isPrivate", false },
                            { "isSerialized", false }
                        });
                        matchedNames.Add(prop.Name);
                    }
                    catch
                    {
                        // Skip properties that can't be read
                    }
                }
            }

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "gameObjectName", go.name },
                { "instanceId", go.GetInstanceID() },
                { "componentType", type.Name },
                { "isPlayMode", EditorApplication.isPlaying },
                { "fields", fields }
            };

            if (filterNames != null)
            {
                var missing = filterNames
                    .Where(name => !matchedNames.Contains(name))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Cast<object>()
                    .ToList();
                result["missingFieldNames"] = missing;
            }

            return JsonResult(result);
        }

        [BridgeRoute("POST", "/runtime/fields/set", Category = "runtime", Description = "Set runtime field/property values on component during play mode")]
        public static string SetRuntimeFields(string jsonData)
        {
            if (!EditorApplication.isPlaying)
            {
                return JsonError("Play mode required for runtime field patching");
            }

            var request = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
            if (request == null)
            {
                return JsonError("Failed to parse runtime field patch request");
            }

            int instanceId = TryReadInt(request, "instanceId", out var parsedId) ? parsedId : 0;
            string gameObjectName = ReadString(request, "gameObjectName") ?? ReadString(request, "name");
            string componentType = ReadString(request, "componentType");
            bool allowPrivate = ReadBool(request, "allowPrivate", true);
            bool allowProperties = ReadBool(request, "allowProperties", true);

            if (instanceId == 0 && string.IsNullOrWhiteSpace(gameObjectName))
            {
                return JsonError("instanceId or gameObjectName is required");
            }

            if (string.IsNullOrWhiteSpace(componentType))
            {
                return JsonError("componentType is required");
            }

            if (!request.TryGetValue("fields", out var fieldsObj) || fieldsObj is not List<object> fieldsList || fieldsList.Count == 0)
            {
                return JsonError("fields array is required");
            }

            GameObject go = null;
            if (instanceId != 0)
            {
                go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            }

            if (go == null && !string.IsNullOrWhiteSpace(gameObjectName))
            {
                go = GameObject.Find(gameObjectName);
            }

            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            var component = FindComponentOnGameObject(go, componentType, out var componentResolvedType);
            if (component == null)
            {
                return JsonError($"Component {componentType} not found on {go.name}");
            }

            var applied = new List<object>();
            var failed = new List<object>();
            bool changed = false;

            Undo.RecordObject(component, "Agent Bridge Set Runtime Fields");

            for (int i = 0; i < fieldsList.Count; i++)
            {
                if (fieldsList[i] is not Dictionary<string, object> entry)
                {
                    failed.Add(new Dictionary<string, object>
                    {
                        { "index", i },
                        { "reason", "Entry must be an object with name/value" }
                    });
                    continue;
                }

                string name = ReadString(entry, "name");
                string typeHint = ReadString(entry, "typeHint");
                bool preferProperty = ReadBool(entry, "preferProperty", false);

                if (string.IsNullOrWhiteSpace(name))
                {
                    failed.Add(new Dictionary<string, object>
                    {
                        { "index", i },
                        { "reason", "name is required" }
                    });
                    continue;
                }

                if (!entry.TryGetValue("value", out var rawValue))
                {
                    failed.Add(new Dictionary<string, object>
                    {
                        { "index", i },
                        { "name", name },
                        { "reason", "value is required" }
                    });
                    continue;
                }

                if (!TrySetRuntimeMemberValue(component, componentResolvedType, name, rawValue, typeHint, allowPrivate, allowProperties, preferProperty, out var memberKind, out var memberType, out var error))
                {
                    failed.Add(new Dictionary<string, object>
                    {
                        { "index", i },
                        { "name", name },
                        { "reason", error ?? "Unknown error" }
                    });
                    continue;
                }

                changed = true;
                applied.Add(new Dictionary<string, object>
                {
                    { "index", i },
                    { "name", name },
                    { "memberKind", memberKind },
                    { "memberType", memberType }
                });
            }

            if (changed)
            {
                EditorUtility.SetDirty(component);
            }

            bool success = failed.Count == 0 && applied.Count > 0;
            bool partialSuccess = applied.Count > 0 && failed.Count > 0;

            return JsonResult(new Dictionary<string, object>
            {
                { "success", success },
                { "partialSuccess", partialSuccess },
                { "instanceId", go.GetInstanceID() },
                { "gameObjectName", go.name },
                { "componentType", componentResolvedType.Name },
                { "isPlayMode", EditorApplication.isPlaying },
                { "appliedCount", applied.Count },
                { "failedCount", failed.Count },
                { "applied", applied },
                { "failed", failed }
            });
        }

        private static bool TrySetRuntimeMemberValue(
            Component component,
            Type componentType,
            string memberName,
            object rawValue,
            string typeHint,
            bool allowPrivate,
            bool allowProperties,
            bool preferProperty,
            out string memberKind,
            out string memberTypeName,
            out string error)
        {
            memberKind = string.Empty;
            memberTypeName = string.Empty;
            error = null;

            var fieldFlags = BindingFlags.Instance | BindingFlags.Public;
            if (allowPrivate) fieldFlags |= BindingFlags.NonPublic;

            var propertyFlags = fieldFlags | BindingFlags.IgnoreCase;

            System.Reflection.FieldInfo field = componentType.GetField(memberName, fieldFlags);
            System.Reflection.PropertyInfo prop = allowProperties
                ? componentType.GetProperty(memberName, propertyFlags)
                : null;

            if (preferProperty)
            {
                if (prop != null)
                {
                    if (TryApplyRuntimeProperty(component, prop, memberName, rawValue, typeHint, allowPrivate, out memberTypeName, out error))
                    {
                        memberKind = "property";
                        return true;
                    }
                    return false;
                }

                if (field != null)
                {
                    if (TryApplyRuntimeField(component, field, memberName, rawValue, typeHint, out memberTypeName, out error))
                    {
                        memberKind = "field";
                        return true;
                    }
                    return false;
                }
            }
            else
            {
                if (field != null)
                {
                    if (TryApplyRuntimeField(component, field, memberName, rawValue, typeHint, out memberTypeName, out error))
                    {
                        memberKind = "field";
                        return true;
                    }
                    return false;
                }

                if (prop != null)
                {
                    if (TryApplyRuntimeProperty(component, prop, memberName, rawValue, typeHint, allowPrivate, out memberTypeName, out error))
                    {
                        memberKind = "property";
                        return true;
                    }
                    return false;
                }
            }

            error = $"Member '{memberName}' not found on component '{componentType.Name}'";
            return false;
        }

        private static bool TryApplyRuntimeField(
            Component component,
            System.Reflection.FieldInfo field,
            string memberName,
            object rawValue,
            string typeHint,
            out string memberTypeName,
            out string error)
        {
            memberTypeName = string.Empty;
            error = null;

            if (field.IsStatic)
            {
                error = $"Field '{memberName}' is static";
                return false;
            }

            if (field.IsInitOnly)
            {
                error = $"Field '{memberName}' is readonly";
                return false;
            }

            if (!TryConvertRuntimeValue(rawValue, field.FieldType, typeHint, out var converted, out var conversionError))
            {
                error = conversionError;
                return false;
            }

            field.SetValue(component, converted);
            memberTypeName = field.FieldType.Name;
            return true;
        }

        private static bool TryApplyRuntimeProperty(
            Component component,
            System.Reflection.PropertyInfo property,
            string memberName,
            object rawValue,
            string typeHint,
            bool allowPrivate,
            out string memberTypeName,
            out string error)
        {
            memberTypeName = string.Empty;
            error = null;

            if (!property.CanWrite || property.SetMethod == null)
            {
                error = $"Property '{memberName}' is read-only";
                return false;
            }

            if (!allowPrivate && !property.SetMethod.IsPublic)
            {
                error = $"Property '{memberName}' setter is non-public";
                return false;
            }

            if (!TryConvertRuntimeValue(rawValue, property.PropertyType, typeHint, out var converted, out var conversionError))
            {
                error = conversionError;
                return false;
            }

            property.SetValue(component, converted);
            memberTypeName = property.PropertyType.Name;
            return true;
        }

        private static bool TryConvertRuntimeValue(object rawValue, Type targetType, string typeHint, out object converted, out string error)
        {
            converted = null;
            error = null;

            var nullableType = Nullable.GetUnderlyingType(targetType);
            var effectiveType = nullableType ?? targetType;

            if (rawValue == null)
            {
                if (nullableType != null || !effectiveType.IsValueType)
                {
                    converted = null;
                }
                else
                {
                    converted = Activator.CreateInstance(effectiveType);
                }
                return true;
            }

            try
            {
                if (effectiveType == typeof(string))
                {
                    converted = rawValue.ToString();
                    return true;
                }

                if (effectiveType == typeof(Color) && rawValue is string colorStr && ColorUtility.TryParseHtmlString(colorStr, out var parsedColor))
                {
                    converted = parsedColor;
                    return true;
                }

                if (TryConvertUnityObjectReference(rawValue, effectiveType, out var objectValue, out var objectError))
                {
                    converted = objectValue;
                    return true;
                }
                if (objectError != null && typeHint != null && typeHint.Equals("object", StringComparison.OrdinalIgnoreCase))
                {
                    error = objectError;
                    return false;
                }

                string argument;
                if (rawValue is string rawString)
                {
                    argument = rawString;
                }
                else
                {
                    argument = MiniJSON.Json.Serialize(rawValue);
                }

                converted = ConvertArgument(argument, effectiveType);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to convert value to {effectiveType.Name}: {ex.Message}";
                return false;
            }
        }

        private static bool TryConvertUnityObjectReference(object rawValue, Type targetType, out object converted, out string error)
        {
            converted = null;
            error = null;

            if (!typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                return false;
            }

            UnityEngine.Object resolved = null;

            if (rawValue is Dictionary<string, object> map)
            {
                if (TryReadInt(map, "instanceId", out var instanceId) && instanceId != 0)
                    resolved = EditorUtility.EntityIdToObject(instanceId);

                if (resolved == null)
                {
                    var path = ReadString(map, "path");
                    if (!string.IsNullOrWhiteSpace(path))
                        resolved = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                }

                if (resolved == null)
                {
                    var name = ReadString(map, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var byName = GameObject.Find(name);
                        resolved = byName;
                    }
                }
            }
            else if (rawValue is string s)
            {
                if (int.TryParse(s, out var id))
                {
                    resolved = EditorUtility.EntityIdToObject(id);
                }
                else if (s.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    resolved = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(s);
                }
                else
                {
                    resolved = GameObject.Find(s);
                }
            }
            else if (rawValue is double d)
            {
                resolved = EditorUtility.EntityIdToObject(Convert.ToInt32(d));
            }
            else if (rawValue is long l)
            {
                resolved = EditorUtility.EntityIdToObject(Convert.ToInt32(l));
            }

            if (resolved == null)
            {
                error = $"Could not resolve Unity object reference for type {targetType.Name}";
                return false;
            }

            if (targetType == typeof(GameObject))
            {
                if (resolved is GameObject go)
                {
                    converted = go;
                    return true;
                }

                if (resolved is Component compForGo)
                {
                    converted = compForGo.gameObject;
                    return true;
                }

                error = $"Resolved object is not a GameObject ({resolved.GetType().Name})";
                return false;
            }

            if (typeof(Component).IsAssignableFrom(targetType))
            {
                if (resolved is GameObject goRef)
                {
                    var resolvedComponent = goRef.GetComponent(targetType);
                    if (resolvedComponent != null)
                    {
                        converted = resolvedComponent;
                        return true;
                    }
                }

                if (resolved is Component comp && targetType.IsAssignableFrom(comp.GetType()))
                {
                    converted = comp;
                    return true;
                }

                error = $"Resolved object is not a {targetType.Name}";
                return false;
            }

            if (targetType.IsAssignableFrom(resolved.GetType()))
            {
                converted = resolved;
                return true;
            }

            error = $"Resolved object type {resolved.GetType().Name} is not assignable to {targetType.Name}";
            return false;
        }

        private static IEnumerable<FieldInfo> EnumerateInstanceFields(Type type, bool includePrivate)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
            if (includePrivate)
            {
                flags |= BindingFlags.NonPublic;
            }

            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                foreach (var field in current.GetFields(flags))
                {
                    yield return field;
                }
            }
        }

        private static string SerializeValueToJson(object value, Type declaredType)
        {
            if (value == null) return "null";

            // Primitives
            if (value is bool b) return b ? "true" : "false";
            if (value is int i) return i.ToString();
            if (value is float f) return f.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (value is double d) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (value is long l) return l.ToString();
            if (value is string s) return MiniJSON.Json.Serialize(s);

            // Enums
            if (declaredType.IsEnum) return MiniJSON.Json.Serialize(value.ToString());

            // Vector types
            if (value is Vector2 v2)
                return MiniJSON.Json.Serialize(new Dictionary<string, object> { { "x", v2.x }, { "y", v2.y } });
            if (value is Vector3 v3)
                return MiniJSON.Json.Serialize(new Dictionary<string, object> { { "x", v3.x }, { "y", v3.y }, { "z", v3.z } });
            if (value is Vector4 v4)
                return MiniJSON.Json.Serialize(new Dictionary<string, object> { { "x", v4.x }, { "y", v4.y }, { "z", v4.z }, { "w", v4.w } });
            if (value is Quaternion q)
                return MiniJSON.Json.Serialize(new Dictionary<string, object> { { "x", q.x }, { "y", q.y }, { "z", q.z }, { "w", q.w } });

            // Color types
            if (value is Color c)
                return MiniJSON.Json.Serialize(new Dictionary<string, object> { { "r", c.r }, { "g", c.g }, { "b", c.b }, { "a", c.a } });
            if (value is Color32 c32)
                return MiniJSON.Json.Serialize(new Dictionary<string, object> { { "r", (int)c32.r }, { "g", (int)c32.g }, { "b", (int)c32.b }, { "a", (int)c32.a } });

            // Bounds
            if (value is Bounds bounds)
                return MiniJSON.Json.Serialize(new Dictionary<string, object>
                {
                    { "center", new Dictionary<string, object> { { "x", bounds.center.x }, { "y", bounds.center.y }, { "z", bounds.center.z } } },
                    { "size", new Dictionary<string, object> { { "x", bounds.size.x }, { "y", bounds.size.y }, { "z", bounds.size.z } } }
                });

            // Rect
            if (value is Rect rect)
                return MiniJSON.Json.Serialize(new Dictionary<string, object> { { "x", rect.x }, { "y", rect.y }, { "width", rect.width }, { "height", rect.height } });

            // UnityEngine.Object references
            if (value is UnityEngine.Object uObj)
            {
                if (uObj == null) return "null";
                return MiniJSON.Json.Serialize(new Dictionary<string, object>
                {
                    { "name", uObj.name },
                    { "instanceId", uObj.GetInstanceID() },
                    { "type", uObj.GetType().Name }
                });
            }

            // IList collections (arrays, List<T>)
            if (value is System.Collections.IList list)
            {
                var items = new List<object>();
                int cap = Math.Min(list.Count, 50);
                for (int idx = 0; idx < cap; idx++)
                {
                    var item = list[idx];
                    var itemType = item != null ? item.GetType() : typeof(object);
                    items.Add(SerializeValueToJson(item, itemType));
                }
                if (list.Count > 50)
                {
                    items.Add($"... ({list.Count - 50} more)");
                }
                return MiniJSON.Json.Serialize(items);
            }

            // Fallback
            return MiniJSON.Json.Serialize(value.ToString());
        }

        private static bool IsNoisyBaseProperty(string name)
        {
            switch (name)
            {
                case "transform":
                case "gameObject":
                case "tag":
                case "name":
                case "hideFlags":
                case "rigidbody":
                case "collider":
                case "renderer":
                case "animation":
                case "networkView":
                case "enabled":
                case "isActiveAndEnabled":
                case "useGUILayout":
                case "runInEditMode":
                case "destroyCancellationToken":
                    return true;
                default:
                    return false;
            }
        }
    }
}
