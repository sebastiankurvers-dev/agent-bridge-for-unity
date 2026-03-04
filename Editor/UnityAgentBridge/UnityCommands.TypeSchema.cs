using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        [BridgeRoute("GET", "/type/schema", Category = "scripts", Description = "Get JSON schema for any C# type",
            ReadOnly = true)]
        public static string GetTypeSchema(string path, string method, string body, System.Collections.Specialized.NameValueCollection query)
        {
            try
            {
                var typeName = query?["typeName"];
                if (string.IsNullOrWhiteSpace(typeName))
                    return (JsonError("'typeName' query parameter is required"), 400).Item1;

                int maxMembers = 100;
                if (query?["maxMembers"] != null && int.TryParse(query["maxMembers"], out var mm))
                    maxMembers = Math.Clamp(mm, 1, 500);

                // Resolve the type
                Type resolvedType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (asm.IsDynamic) continue;
                        resolvedType = asm.GetType(typeName, false, true);
                        if (resolvedType != null) break;
                    }
                    catch { }
                }

                // Fallback: search by short name
                if (resolvedType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            if (asm.IsDynamic) continue;
                            resolvedType = asm.GetTypes()
                                .FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
                            if (resolvedType != null) break;
                        }
                        catch { }
                    }
                }

                if (resolvedType == null)
                    return (JsonError($"Type '{typeName}' not found in any loaded assembly"), 404).Item1;

                // Build schema
                var properties = new Dictionary<string, object>();
                int memberCount = 0;
                bool truncated = false;

                // Public fields
                var fields = resolvedType.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                foreach (var field in fields)
                {
                    if (memberCount >= maxMembers) { truncated = true; break; }
                    properties[field.Name] = BuildMemberSchema(field.FieldType, field.GetCustomAttribute<TooltipAttribute>()?.tooltip);
                    memberCount++;
                }

                // Serialized private fields (with [SerializeField])
                if (!truncated)
                {
                    var privateFields = resolvedType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    foreach (var field in privateFields)
                    {
                        if (memberCount >= maxMembers) { truncated = true; break; }
                        if (field.GetCustomAttribute<SerializeField>() == null) continue;
                        var schema = BuildMemberSchema(field.FieldType, field.GetCustomAttribute<TooltipAttribute>()?.tooltip);
                        ((Dictionary<string, object>)schema)["serialized"] = true;
                        properties[field.Name] = schema;
                        memberCount++;
                    }
                }

                // Public properties with getters
                if (!truncated)
                {
                    var props = resolvedType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    foreach (var prop in props)
                    {
                        if (memberCount >= maxMembers) { truncated = true; break; }
                        if (!prop.CanRead) continue;
                        if (properties.ContainsKey(prop.Name)) continue; // skip if field already captured
                        var schema = BuildMemberSchema(prop.PropertyType, null);
                        ((Dictionary<string, object>)schema)["readOnly"] = !prop.CanWrite;
                        ((Dictionary<string, object>)schema)["memberType"] = "property";
                        properties[prop.Name] = schema;
                        memberCount++;
                    }
                }

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "typeName", resolvedType.FullName ?? resolvedType.Name },
                    { "assemblyName", resolvedType.Assembly.GetName().Name },
                    { "isMonoBehaviour", typeof(MonoBehaviour).IsAssignableFrom(resolvedType) },
                    { "isScriptableObject", typeof(ScriptableObject).IsAssignableFrom(resolvedType) },
                    { "isComponent", typeof(Component).IsAssignableFrom(resolvedType) },
                    { "memberCount", memberCount },
                    { "truncated", truncated },
                    { "properties", properties }
                };

                if (resolvedType.BaseType != null)
                    result["baseType"] = resolvedType.BaseType.Name;
                if (resolvedType.IsAbstract)
                    result["isAbstract"] = true;
                if (resolvedType.IsEnum)
                {
                    result["isEnum"] = true;
                    result["enumValues"] = Enum.GetNames(resolvedType);
                }

                return JsonResult(result);
            }
            catch (Exception ex)
            {
                return (JsonError(ex.Message), 500).Item1;
            }
        }

        private static object BuildMemberSchema(Type type, string tooltip)
        {
            var schema = new Dictionary<string, object>
            {
                { "type", MapCSharpTypeToSchemaType(type) }
            };

            if (!string.IsNullOrEmpty(tooltip))
                schema["description"] = tooltip;

            // Add csharpType for non-trivial types
            var schemaType = MapCSharpTypeToSchemaType(type);
            if (schemaType == "object" && type != typeof(object))
                schema["csharpType"] = type.Name;

            // Handle enums
            if (type.IsEnum)
            {
                schema["type"] = "string";
                schema["enum"] = Enum.GetNames(type);
            }

            // Handle arrays/lists
            if (type.IsArray)
            {
                schema["type"] = "array";
                schema["items"] = new Dictionary<string, object>
                {
                    { "type", MapCSharpTypeToSchemaType(type.GetElementType()) }
                };
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                schema["type"] = "array";
                schema["items"] = new Dictionary<string, object>
                {
                    { "type", MapCSharpTypeToSchemaType(type.GetGenericArguments()[0]) }
                };
            }

            // Handle Unity struct types with known shapes
            if (type == typeof(Vector2))
                schema["shape"] = new Dictionary<string, object> { { "x", "number" }, { "y", "number" } };
            else if (type == typeof(Vector3))
                schema["shape"] = new Dictionary<string, object> { { "x", "number" }, { "y", "number" }, { "z", "number" } };
            else if (type == typeof(Vector4) || type == typeof(Quaternion))
                schema["shape"] = new Dictionary<string, object> { { "x", "number" }, { "y", "number" }, { "z", "number" }, { "w", "number" } };
            else if (type == typeof(Color))
                schema["shape"] = new Dictionary<string, object> { { "r", "number" }, { "g", "number" }, { "b", "number" }, { "a", "number" } };
            else if (type == typeof(Rect))
                schema["shape"] = new Dictionary<string, object> { { "x", "number" }, { "y", "number" }, { "width", "number" }, { "height", "number" } };
            else if (type == typeof(Bounds))
                schema["shape"] = new Dictionary<string, object> { { "center", "Vector3" }, { "size", "Vector3" } };

            // Handle Range attribute
            // Note: RangeAttribute is on the field, not the type — handled at field level if needed

            return schema;
        }

        private static string MapCSharpTypeToSchemaType(Type type)
        {
            if (type == null) return "any";
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
                || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte))
                return "integer";
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return "number";
            if (type == typeof(string) || type == typeof(char))
                return "string";
            if (type == typeof(bool))
                return "boolean";
            if (type.IsEnum)
                return "string";
            if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
                return "array";
            return "object";
        }
    }
}
