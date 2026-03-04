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
        public static string FindGameObjects(string namePattern = null, string componentType = null, string tag = null, string layerName = null, int activeFilter = -1, int maxResults = 100, bool includeComponents = false)
        {
            var results = new List<FoundGameObject>();
            var scene = SceneManager.GetActiveScene();
            int layerIndex = -1;
            string normalizedComponentType = string.IsNullOrWhiteSpace(componentType)
                ? null
                : componentType.Trim();
            Type resolvedComponentType = null;

            if (!string.IsNullOrEmpty(layerName))
            {
                layerIndex = LayerMask.NameToLayer(layerName);
                if (layerIndex == -1)
                {
                    return JsonError($"Layer '{layerName}' not found");
                }
            }

            if (!string.IsNullOrWhiteSpace(normalizedComponentType))
            {
                resolvedComponentType = TypeResolver.FindComponentType(normalizedComponentType);
            }

            foreach (var rootGo in scene.GetRootGameObjects())
            {
                FindGameObjectsRecursive(
                    rootGo,
                    rootGo.name,
                    namePattern,
                    normalizedComponentType,
                    resolvedComponentType,
                    tag,
                    layerIndex,
                    activeFilter,
                    maxResults,
                    includeComponents,
                    results);
                if (results.Count >= maxResults) break;
            }

            bool truncated = results.Count >= maxResults;
            var response = new FindGameObjectsResponse
            {
                totalFound = results.Count,
                truncated = truncated,
                maxResults = maxResults,
                gameObjects = results
            };

            return JsonUtility.ToJson(response, false);
        }

        private static void FindGameObjectsRecursive(
            GameObject go,
            string currentPath,
            string namePattern,
            string componentType,
            Type resolvedComponentType,
            string tag,
            int layerIndex,
            int activeFilter,
            int maxResults,
            bool includeComponents,
            List<FoundGameObject> results)
        {
            if (results.Count >= maxResults) return;

            bool matches = true;

            if (!string.IsNullOrEmpty(namePattern))
            {
                matches = go.name.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (matches && !string.IsNullOrEmpty(componentType))
            {
                matches = MatchesComponentFilter(go, componentType, resolvedComponentType);
            }

            if (matches && !string.IsNullOrEmpty(tag))
            {
                try { matches = go.tag == tag; }
                catch { matches = false; }
            }

            if (matches && layerIndex >= 0)
            {
                matches = go.layer == layerIndex;
            }

            if (matches && activeFilter >= 0)
            {
                bool wantActive = activeFilter == 1;
                matches = go.activeInHierarchy == wantActive;
            }

            if (matches)
            {
                results.Add(new FoundGameObject
                {
                    instanceId = go.GetInstanceID(),
                    name = go.name,
                    path = currentPath,
                    tag = go.tag,
                    layer = go.layer,
                    layerName = LayerMask.LayerToName(go.layer),
                    active = go.activeSelf,
                    activeInHierarchy = go.activeInHierarchy,
                    components = includeComponents
                        ? go.GetComponents<Component>()
                            .Where(c => c != null)
                            .Select(c => c.GetType().Name)
                            .ToList()
                        : null
                });
            }

            foreach (Transform child in go.transform)
            {
                FindGameObjectsRecursive(
                    child.gameObject,
                    currentPath + "/" + child.gameObject.name,
                    namePattern,
                    componentType,
                    resolvedComponentType,
                    tag,
                    layerIndex,
                    activeFilter,
                    maxResults,
                    includeComponents,
                    results);
                if (results.Count >= maxResults) break;
            }
        }

        private static bool MatchesComponentFilter(GameObject go, string componentType, Type resolvedComponentType)
        {
            if (go == null || string.IsNullOrWhiteSpace(componentType))
            {
                return false;
            }

            var filter = componentType.Trim();
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                var componentClass = component.GetType();

                if (resolvedComponentType != null && resolvedComponentType.IsAssignableFrom(componentClass))
                {
                    return true;
                }

                if (componentClass.Name.Equals(filter, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(componentClass.FullName)
                        && componentClass.FullName.Equals(filter, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                if (component is MonoBehaviour monoBehaviour)
                {
                    var monoScript = MonoScript.FromMonoBehaviour(monoBehaviour);
                    if (monoScript == null)
                    {
                        continue;
                    }

                    if (monoScript.name.Equals(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    var scriptClass = monoScript.GetClass();
                    if (scriptClass != null
                        && (scriptClass.Name.Equals(filter, StringComparison.OrdinalIgnoreCase)
                            || (!string.IsNullOrWhiteSpace(scriptClass.FullName)
                                && scriptClass.FullName.Equals(filter, StringComparison.OrdinalIgnoreCase))))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static string ListScripts(string nameFilter = null, int isMonoBehaviour = -1, int isScriptableObject = -1, int offset = 0, int limit = 50)
        {
            var allScripts = ProjectIndexer.IndexScripts(includeMemberDetails: false, cacheSeconds: 30);

            IEnumerable<ProjectIndexer.ScriptIndex> filtered = allScripts;

            if (!string.IsNullOrEmpty(nameFilter))
            {
                filtered = filtered.Where(s =>
                    (s.className != null && s.className.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (s.fileName != null && s.fileName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            if (isMonoBehaviour >= 0)
            {
                bool wantMB = isMonoBehaviour == 1;
                filtered = filtered.Where(s => s.isMonoBehaviour == wantMB);
            }

            if (isScriptableObject >= 0)
            {
                bool wantSO = isScriptableObject == 1;
                filtered = filtered.Where(s => s.isScriptableObject == wantSO);
            }

            var filteredList = filtered.ToList();
            int totalCount = filteredList.Count;

            var page = filteredList.Skip(offset).Take(limit).ToList();

            var entries = page.Select(s => new ScriptListEntry
            {
                path = s.path,
                fileName = s.fileName,
                className = s.className ?? "",
                namespaceName = s.namespaceName ?? "",
                baseClass = s.baseClass ?? "",
                isMonoBehaviour = s.isMonoBehaviour,
                isScriptableObject = s.isScriptableObject,
                isEditor = s.isEditor
            }).ToList();

            var response = new ScriptListResponse
            {
                totalCount = totalCount,
                scripts = entries
            };

            return JsonUtility.ToJson(response, false);
        }

        public static string GetScriptStructure(
            string scriptPath,
            bool includeMethods = true,
            bool includeFields = true,
            bool includeProperties = true,
            bool includeEvents = true,
            int maxMethods = -1,
            int maxFields = -1,
            int maxProperties = -1,
            int maxEvents = -1,
            bool includeAttributes = true,
            bool includeMethodParameters = true)
        {
            var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
            if (monoScript == null)
            {
                return JsonUtility.ToJson(new ScriptStructureResponse { success = false, error = $"Script not found at path: {scriptPath}" });
            }

            var scriptClass = monoScript.GetClass();
            if (scriptClass == null)
            {
                return JsonUtility.ToJson(new ScriptStructureResponse { success = false, error = $"Could not resolve type for script: {scriptPath}", path = scriptPath });
            }

            var response = new ScriptStructureResponse
            {
                success = true,
                path = scriptPath,
                className = scriptClass.Name,
                fullTypeName = scriptClass.FullName,
                namespaceName = scriptClass.Namespace ?? "",
                baseClass = scriptClass.BaseType?.Name ?? "",
                isAbstract = scriptClass.IsAbstract,
                isSealed = scriptClass.IsSealed,
                isGeneric = scriptClass.IsGenericType,
                inheritanceChain = new List<string>(),
                interfaces = new List<string>(),
                attributes = new List<string>(),
                methods = new List<ScriptMethodInfo>(),
                fields = new List<ScriptFieldInfo>(),
                properties = new List<ScriptPropertyInfoData>(),
                events = new List<ScriptEventInfo>(),
                maxMethods = maxMethods,
                maxFields = maxFields,
                maxProperties = maxProperties,
                maxEvents = maxEvents,
                includeMethods = includeMethods,
                includeFields = includeFields,
                includeProperties = includeProperties,
                includeEvents = includeEvents,
                includeAttributes = includeAttributes,
                includeMethodParameters = includeMethodParameters
            };

            // Build inheritance chain
            var current = scriptClass.BaseType;
            while (current != null && current != typeof(object))
            {
                response.inheritanceChain.Add(current.Name);
                current = current.BaseType;
            }

            // Get interfaces
            foreach (var iface in scriptClass.GetInterfaces())
            {
                response.interfaces.Add(GetFriendlyTypeName(iface));
            }

            // Get class-level attributes
            if (includeAttributes)
            {
                foreach (var attr in scriptClass.GetCustomAttributes(false))
                {
                    response.attributes.Add(attr.GetType().Name.Replace("Attribute", ""));
                }
            }

            int methodCap = NormalizeMemberLimit(maxMethods);
            int fieldCap = NormalizeMemberLimit(maxFields);
            int propertyCap = NormalizeMemberLimit(maxProperties);
            int eventCap = NormalizeMemberLimit(maxEvents);

            if (includeMethods)
            {
                // Get methods (declared only)
                var methods = scriptClass.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                foreach (var method in methods)
                {
                    if (method.IsSpecialName) continue; // Skip property getters/setters
                    response.methodsTotal++;

                    if (response.methods.Count >= methodCap)
                    {
                        response.methodsTruncated = true;
                        continue;
                    }

                    var methodInfo = new ScriptMethodInfo
                    {
                        name = method.Name,
                        returnType = GetFriendlyTypeName(method.ReturnType),
                        access = GetAccessModifier(method),
                        isStatic = method.IsStatic,
                        isVirtual = method.IsVirtual && !method.IsFinal,
                        isAbstract = method.IsAbstract,
                        isOverride = method.IsVirtual && method.GetBaseDefinition().DeclaringType != scriptClass,
                        attributes = new List<string>(),
                        parameters = new List<ScriptParameterInfo>()
                    };

                    if (includeAttributes)
                    {
                        foreach (var attr in method.GetCustomAttributes(false))
                        {
                            methodInfo.attributes.Add(attr.GetType().Name.Replace("Attribute", ""));
                        }
                    }

                    if (includeMethodParameters)
                    {
                        foreach (var param in method.GetParameters())
                        {
                            var paramInfo = new ScriptParameterInfo
                            {
                                name = param.Name,
                                type = GetFriendlyTypeName(param.ParameterType),
                                isOut = param.IsOut,
                                isRef = param.ParameterType.IsByRef && !param.IsOut
                            };
                            if (param.HasDefaultValue)
                            {
                                paramInfo.defaultValue = param.DefaultValue?.ToString() ?? "null";
                            }
                            methodInfo.parameters.Add(paramInfo);
                        }
                    }

                    response.methods.Add(methodInfo);
                }
            }

            if (includeFields)
            {
                // Get fields (declared only)
                var fields = scriptClass.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                foreach (var field in fields)
                {
                    // Skip compiler-generated backing fields
                    if (field.Name.Contains("<")) continue;
                    response.fieldsTotal++;

                    if (response.fields.Count >= fieldCap)
                    {
                        response.fieldsTruncated = true;
                        continue;
                    }

                    var fieldInfo = new ScriptFieldInfo
                    {
                        name = field.Name,
                        type = GetFriendlyTypeName(field.FieldType),
                        access = field.IsPublic ? "public" : field.IsFamily ? "protected" : field.IsAssembly ? "internal" : "private",
                        isStatic = field.IsStatic,
                        isReadonly = field.IsInitOnly,
                        isSerialized = field.IsPublic || field.GetCustomAttribute<SerializeField>() != null,
                        attributes = new List<string>()
                    };

                    if (includeAttributes)
                    {
                        foreach (var attr in field.GetCustomAttributes(false))
                        {
                            fieldInfo.attributes.Add(attr.GetType().Name.Replace("Attribute", ""));
                        }
                    }

                    response.fields.Add(fieldInfo);
                }
            }

            if (includeProperties)
            {
                // Get properties (declared only)
                var props = scriptClass.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                foreach (var prop in props)
                {
                    response.propertiesTotal++;
                    if (response.properties.Count >= propertyCap)
                    {
                        response.propertiesTruncated = true;
                        continue;
                    }

                    var getter = prop.GetGetMethod(true);
                    var setter = prop.GetSetMethod(true);
                    var accessMethod = getter ?? setter;

                    response.properties.Add(new ScriptPropertyInfoData
                    {
                        name = prop.Name,
                        type = GetFriendlyTypeName(prop.PropertyType),
                        access = accessMethod != null ? GetAccessModifier(accessMethod) : "private",
                        hasGetter = getter != null,
                        hasSetter = setter != null,
                        isStatic = accessMethod != null && accessMethod.IsStatic
                    });
                }
            }

            if (includeEvents)
            {
                // Get events (declared only)
                var events = scriptClass.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                foreach (var evt in events)
                {
                    response.eventsTotal++;
                    if (response.events.Count >= eventCap)
                    {
                        response.eventsTruncated = true;
                        continue;
                    }

                    var addMethod = evt.GetAddMethod(true);
                    response.events.Add(new ScriptEventInfo
                    {
                        name = evt.Name,
                        handlerType = GetFriendlyTypeName(evt.EventHandlerType),
                        access = addMethod != null ? GetAccessModifier(addMethod) : "private"
                    });
                }
            }

            return JsonUtility.ToJson(response, false);
        }

        private static int NormalizeMemberLimit(int limit)
        {
            if (limit < 0) return int.MaxValue;
            return Mathf.Clamp(limit, 1, 2000);
        }

        private static string GetAccessModifier(MethodBase method)
        {
            if (method.IsPublic) return "public";
            if (method.IsFamily) return "protected";
            if (method.IsAssembly) return "internal";
            if (method.IsFamilyOrAssembly) return "protected internal";
            return "private";
        }

        private static string GetFriendlyTypeName(Type type)
        {
            if (type == null) return "void";
            if (type == typeof(void)) return "void";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(string)) return "string";
            if (type == typeof(long)) return "long";
            if (type == typeof(byte)) return "byte";
            if (type == typeof(char)) return "char";
            if (type == typeof(object)) return "object";

            if (type.IsByRef)
                return GetFriendlyTypeName(type.GetElementType());

            if (type.IsArray)
                return GetFriendlyTypeName(type.GetElementType()) + "[]";

            if (type.IsGenericType)
            {
                var baseName = type.Name;
                var tickIndex = baseName.IndexOf('`');
                if (tickIndex > 0)
                    baseName = baseName.Substring(0, tickIndex);

                var args = type.GetGenericArguments().Select(GetFriendlyTypeName);
                return $"{baseName}<{string.Join(", ", args)}>";
            }

            return type.Name;
        }

    }
}
