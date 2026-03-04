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
        // JsonUtility.ToJson has a hard serialization depth limit of 10 for nested classes.
        // Chain: HierarchyResponse(0) > rootObjects[HierarchyNode](1) > children(2..N) > components(N+1)
        // So max children depth = 10 - 2 (wrapper + leaf) = 8, but use 7 for safety margin.
        private const int MaxSerializationSafeDepth = 7;

        public static string GetHierarchy(int maxDepth = 0, bool brief = true, bool pretty = false)
        {
            // Clamp depth to avoid JsonUtility serialization depth limit (10)
            if (maxDepth < 0 || maxDepth > MaxSerializationSafeDepth)
            {
                maxDepth = MaxSerializationSafeDepth;
            }

            var rootObjects = new List<HierarchyNode>();
            var scene = SceneManager.GetActiveScene();

            foreach (var go in scene.GetRootGameObjects())
            {
                rootObjects.Add(BuildHierarchyNode(go, 0, maxDepth, brief));
            }

            var response = new HierarchyResponse
            {
                sceneName = scene.name,
                scenePath = scene.path,
                rootObjects = rootObjects
            };

            return JsonUtility.ToJson(response, pretty);
        }

        private static HierarchyNode BuildHierarchyNode(GameObject go, int currentDepth, int maxDepth, bool brief)
        {
            var node = new HierarchyNode
            {
                name = go.name,
                instanceId = go.GetInstanceID(),
                active = go.activeSelf,
                children = new List<HierarchyNode>()
            };

            if (!brief)
            {
                node.layer = LayerMask.LayerToName(go.layer);
                node.tag = go.tag;
                node.components = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToList();
            }
            else
            {
                node.childCount = go.transform.childCount;
            }

            // Recurse into children unless we've hit the depth limit
            if (maxDepth < 0 || currentDepth < maxDepth)
            {
                foreach (Transform child in go.transform)
                {
                    node.children.Add(BuildHierarchyNode(child.gameObject, currentDepth + 1, maxDepth, brief));
                }
            }

            return node;
        }

        public static string GetGameObject(int instanceId, bool includeComponents = true, bool transformOnly = false)
        {
            const int MaxSerializedPropertiesPerComponent = 180;
            var obj = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (obj == null)
            {
                return null;
            }

            var details = new GameObjectDetails
            {
                name = obj.name,
                instanceId = obj.GetInstanceID(),
                active = obj.activeSelf,
                activeInHierarchy = obj.activeInHierarchy,
                isStatic = obj.isStatic,
                layer = obj.layer,
                layerName = LayerMask.LayerToName(obj.layer),
                tag = obj.tag,
                transform = new TransformData
                {
                    position = new Vector3Data(obj.transform.position),
                    localPosition = new Vector3Data(obj.transform.localPosition),
                    rotation = new Vector3Data(obj.transform.eulerAngles),
                    localRotation = new Vector3Data(obj.transform.localEulerAngles),
                    localScale = new Vector3Data(obj.transform.localScale)
                },
                components = new List<ComponentInfo>()
            };

            if (transformOnly)
            {
                return JsonUtility.ToJson(details, false);
            }

            if (!includeComponents)
            {
                return JsonUtility.ToJson(details, false);
            }

            foreach (var component in obj.GetComponents<Component>())
            {
                if (component == null) continue;

                var componentInfo = new ComponentInfo
                {
                    type = component.GetType().Name,
                    fullType = component.GetType().FullName,
                    enabled = true,
                    properties = new List<PropertyInfo>()
                };

                // Check if component has enabled property
                var enabledProp = component.GetType().GetProperty("enabled");
                if (enabledProp != null && enabledProp.PropertyType == typeof(bool))
                {
                    componentInfo.enabled = (bool)enabledProp.GetValue(component);
                }

                // Get serialized properties
                var serializedObj = new SerializedObject(component);
                var prop = serializedObj.GetIterator();
                int propertyCount = 0;
                if (prop.NextVisible(true))
                {
                    do
                    {
                        if (propertyCount >= MaxSerializedPropertiesPerComponent)
                        {
                            componentInfo.properties.Add(new PropertyInfo
                            {
                                name = "__truncated__",
                                path = "__truncated__",
                                type = "string",
                                value = $"<properties truncated at {MaxSerializedPropertiesPerComponent}>"
                            });
                            break;
                        }

                        if (prop.propertyPath == "m_Script")
                        {
                            continue;
                        }

                        componentInfo.properties.Add(GetPropertyInfo(prop, 0));
                        propertyCount++;
                    } while (prop.NextVisible(false));
                }

                details.components.Add(componentInfo);
            }

            try
            {
                return JsonUtility.ToJson(details, false);
            }
            catch (Exception ex)
            {
                // Unity JsonUtility has a hard depth limit. If a managed reference graph is too deep,
                // drop nested property expansion and return a stable response instead of failing the route.
                foreach (var componentInfo in details.components)
                {
                    if (componentInfo?.properties == null) continue;
                    StripNestedProperties(componentInfo.properties);
                }

                var fallback = JsonUtility.ToJson(details, false);
                var parsed = MiniJSON.Json.Deserialize(fallback) as Dictionary<string, object> ?? new Dictionary<string, object>();
                parsed["warning"] = "Nested serialized properties were truncated due to JsonUtility depth limits.";
                parsed["warningCode"] = "SERIALIZATION_DEPTH_TRUNCATED";
                parsed["warningMessage"] = ex.Message;
                return JsonResult(parsed);
            }
        }

        private static string GetPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return prop.intValue.ToString();
                case SerializedPropertyType.Boolean:
                    return prop.boolValue.ToString();
                case SerializedPropertyType.Float:
                    return prop.floatValue.ToString("F4");
                case SerializedPropertyType.String:
                    return prop.stringValue;
                case SerializedPropertyType.Color:
                    return $"RGBA({prop.colorValue.r:F2}, {prop.colorValue.g:F2}, {prop.colorValue.b:F2}, {prop.colorValue.a:F2})";
                case SerializedPropertyType.ObjectReference:
                    return prop.objectReferenceValue != null ? prop.objectReferenceValue.name : "null";
                case SerializedPropertyType.Enum:
                    return prop.enumNames.Length > prop.enumValueIndex && prop.enumValueIndex >= 0
                        ? prop.enumNames[prop.enumValueIndex]
                        : prop.enumValueIndex.ToString();
                case SerializedPropertyType.Vector2:
                    return $"({prop.vector2Value.x:F2}, {prop.vector2Value.y:F2})";
                case SerializedPropertyType.Vector3:
                    return $"({prop.vector3Value.x:F2}, {prop.vector3Value.y:F2}, {prop.vector3Value.z:F2})";
                case SerializedPropertyType.Vector4:
                    return $"({prop.vector4Value.x:F2}, {prop.vector4Value.y:F2}, {prop.vector4Value.z:F2}, {prop.vector4Value.w:F2})";
                case SerializedPropertyType.Rect:
                    return $"Rect({prop.rectValue.x:F2}, {prop.rectValue.y:F2}, {prop.rectValue.width:F2}, {prop.rectValue.height:F2})";
                case SerializedPropertyType.Bounds:
                    return $"Bounds(center: {prop.boundsValue.center}, size: {prop.boundsValue.size})";
                case SerializedPropertyType.Quaternion:
                    var euler = prop.quaternionValue.eulerAngles;
                    return $"({euler.x:F2}, {euler.y:F2}, {euler.z:F2})";
                case SerializedPropertyType.ManagedReference:
                    return $"[ManagedReference: {prop.managedReferenceFullTypename}]";
                default:
                    return $"[{prop.propertyType}]";
            }
        }

        private static PropertyInfo GetPropertyInfo(SerializedProperty prop, int depth)
        {
            // Keep managed-reference nesting shallow enough for Unity JsonUtility depth limits.
            const int MaxManagedReferenceNestingDepth = 2;
            var info = new PropertyInfo
            {
                name = prop.name,
                path = prop.propertyPath,
                type = prop.propertyType.ToString(),
                isArray = prop.isArray,
                arraySize = prop.isArray ? prop.arraySize : 0
            };

            if (prop.propertyType == SerializedPropertyType.ManagedReference)
            {
                info.isManagedReference = true;

                // Parse "Assembly Type" format
                var fullTypeName = prop.managedReferenceFullTypename;
                if (!string.IsNullOrEmpty(fullTypeName))
                {
                    var spaceIndex = fullTypeName.IndexOf(' ');
                    if (spaceIndex > 0)
                    {
                        info.managedReferenceAssembly = fullTypeName.Substring(0, spaceIndex);
                        info.managedReferenceTypeName = fullTypeName.Substring(spaceIndex + 1);
                    }
                    else
                    {
                        info.managedReferenceTypeName = fullTypeName;
                    }
                }

                // Recursively get nested properties with a conservative depth cap.
                if (depth < MaxManagedReferenceNestingDepth && prop.hasVisibleChildren)
                {
                    info.nestedProperties = new List<PropertyInfo>();
                    var iter = prop.Copy();
                    var end = prop.GetEndProperty();
                    iter.NextVisible(true);
                    int nestedCount = 0;
                    const int MaxNestedProperties = 80;

                    while (!SerializedProperty.EqualContents(iter, end))
                    {
                        if (nestedCount >= MaxNestedProperties)
                        {
                            info.nestedProperties.Add(new PropertyInfo
                            {
                                name = "__truncated__",
                                path = "__truncated__",
                                type = "string",
                                value = $"<nested properties truncated at {MaxNestedProperties}>"
                            });
                            break;
                        }

                        info.nestedProperties.Add(GetPropertyInfo(iter.Copy(), depth + 1));
                        nestedCount++;
                        if (!iter.NextVisible(false)) break;
                    }
                }

                info.value = $"[ManagedReference: {info.managedReferenceTypeName ?? "null"}]";
            }
            else
            {
                info.value = GetPropertyValue(prop);
            }

            return info;
        }

        private static void StripNestedProperties(List<PropertyInfo> properties)
        {
            if (properties == null) return;

            for (int i = 0; i < properties.Count; i++)
            {
                var property = properties[i];
                if (property == null) continue;

                if (property.nestedProperties != null && property.nestedProperties.Count > 0)
                {
                    StripNestedProperties(property.nestedProperties);
                }

                property.nestedProperties = null;
            }
        }

    }
}
