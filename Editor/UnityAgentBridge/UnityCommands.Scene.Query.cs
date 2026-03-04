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
        [BridgeRoute("POST", "/scene/transaction", Category = "scene", Description = "Atomic multi-operation scene transaction", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string SceneTransaction(string jsonData)
        {
            string checkpointId = null;
            try
            {
                var request = JsonUtility.FromJson<SceneTransactionRequest>(jsonData);
                if (string.IsNullOrEmpty(request.descriptor))
                {
                    return JsonUtility.ToJson(new SceneTransactionResponse { success = false, error = "Descriptor JSON is required" });
                }

                bool autoRollback = request.autoRollbackOnError != 0; // default true (1 or -1)

                // Create checkpoint before building
                var cpName = string.IsNullOrEmpty(request.checkpointName)
                    ? $"scene-transaction-{DateTime.Now:yyyyMMdd-HHmmss}"
                    : request.checkpointName;
                var cpResultJson = CheckpointManager.CreateCheckpoint(cpName);
                var cpResult = MiniJSON.Json.Deserialize(cpResultJson) as Dictionary<string, object>;

                if (cpResult != null && cpResult.ContainsKey("checkpoint"))
                {
                    var cpData = cpResult["checkpoint"] as Dictionary<string, object>;
                    if (cpData != null && cpData.ContainsKey("id"))
                    {
                        checkpointId = cpData["id"] as string;
                    }
                }

                if (string.IsNullOrEmpty(checkpointId))
                {
                    // Try to parse with JsonUtility fallback
                    var cpResponse = JsonUtility.FromJson<CheckpointManager.CheckpointResponse>(cpResultJson);
                    if (cpResponse != null && cpResponse.checkpoint != null)
                    {
                        checkpointId = cpResponse.checkpoint.id;
                    }
                }

                // Build the inner request for BuildSceneAndScreenshot
                var buildRequest = new BuildAndScreenshotRequest
                {
                    descriptor = request.descriptor,
                    screenshotView = request.screenshotView,
                    cameraPosition = request.cameraPosition,
                    cameraRotation = request.cameraRotation
                };

                var buildJson = JsonUtility.ToJson(buildRequest);
                var buildResultJson = BuildSceneAndScreenshot(buildJson);
                var buildResult = JsonUtility.FromJson<BuildAndScreenshotResponse>(buildResultJson);

                if (buildResult == null || !buildResult.success)
                {
                    var errorMsg = buildResult?.error ?? "Build failed";

                    if (autoRollback && !string.IsNullOrEmpty(checkpointId))
                    {
                        CheckpointManager.RestoreCheckpoint(checkpointId);
                        return JsonUtility.ToJson(new SceneTransactionResponse
                        {
                            success = false,
                            checkpointId = checkpointId,
                            rolledBack = true,
                            error = errorMsg
                        });
                    }

                    return JsonUtility.ToJson(new SceneTransactionResponse
                    {
                        success = false,
                        checkpointId = checkpointId,
                        rolledBack = false,
                        error = errorMsg
                    });
                }

                return JsonUtility.ToJson(new SceneTransactionResponse
                {
                    success = true,
                    checkpointId = checkpointId,
                    createdCount = buildResult.createdCount,
                    instanceIds = buildResult.instanceIds ?? new List<int>(),
                    screenshot = buildResult.screenshot
                }, true);
            }
            catch (Exception ex)
            {
                bool autoRollback = true;
                try
                {
                    var req = JsonUtility.FromJson<SceneTransactionRequest>(jsonData);
                    autoRollback = req.autoRollbackOnError != 0;
                }
                catch { }

                if (autoRollback && !string.IsNullOrEmpty(checkpointId))
                {
                    try { CheckpointManager.RestoreCheckpoint(checkpointId); } catch { }
                    return JsonUtility.ToJson(new SceneTransactionResponse
                    {
                        success = false,
                        checkpointId = checkpointId,
                        rolledBack = true,
                        error = ex.Message
                    });
                }

                return JsonUtility.ToJson(new SceneTransactionResponse
                {
                    success = false,
                    checkpointId = checkpointId,
                    rolledBack = false,
                    error = ex.Message
                });
            }
        }



        private static string ComputeMaterialOverrideHash(Material baseMat, MaterialOverrideDescriptor overrides)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(baseMat.GetInstanceID());
            if (overrides.color != null) sb.Append("|c:").Append(string.Join(",", overrides.color));
            if (overrides.emissionColor != null) sb.Append("|e:").Append(string.Join(",", overrides.emissionColor));
            if (overrides.emissionIntensity >= 0f) sb.Append("|ei:").Append(overrides.emissionIntensity);
            if (overrides.metallic >= 0f) sb.Append("|m:").Append(overrides.metallic);
            if (overrides.smoothness >= 0f) sb.Append("|s:").Append(overrides.smoothness);
            return sb.ToString();
        }

        private static GameObject CreateObjectFromDescriptor(ObjectDescriptor desc, Transform parent, Dictionary<string, Material> materialCache = null, List<string> warnings = null)
        {
            GameObject go = null;

            // Resolve aliases → primitiveType (AI-friendly fallbacks)
            if (string.IsNullOrEmpty(desc.primitiveType))
            {
                if (!string.IsNullOrEmpty(desc.type))
                    desc.primitiveType = desc.type;
                else if (!string.IsNullOrEmpty(desc.primitive))
                    desc.primitiveType = desc.primitive;
            }

            // Create from prefab, primitive, or empty
            if (!string.IsNullOrEmpty(desc.prefab))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(desc.prefab);
                if (prefab == null)
                {
                    // Try to find by name
                    var guids = AssetDatabase.FindAssets($"t:Prefab {System.IO.Path.GetFileNameWithoutExtension(desc.prefab)}");
                    foreach (var guid in guids)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        if (path == desc.prefab || path.EndsWith(desc.prefab))
                        {
                            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                            break;
                        }
                    }
                }

                if (prefab != null)
                {
                    go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                }
                else
                {
                    var warn = $"'{desc.name ?? "GameObject"}': prefab not found '{desc.prefab}' — created empty GameObject with no mesh";
                    Debug.LogWarning($"[AgentBridge] {warn}");
                    warnings?.Add(warn);
                    go = new GameObject(desc.name ?? "GameObject");
                }
            }
            else if (!string.IsNullOrEmpty(desc.primitiveType))
            {
                if (Enum.TryParse<PrimitiveType>(desc.primitiveType, true, out var primitive))
                {
                    go = GameObject.CreatePrimitive(primitive);
                }
                else
                {
                    var warn = $"'{desc.name ?? "GameObject"}': unknown primitiveType '{desc.primitiveType}' — created empty GameObject with no mesh. Valid types: Cube, Sphere, Cylinder, Capsule, Plane, Quad";
                    Debug.LogWarning($"[AgentBridge] {warn}");
                    warnings?.Add(warn);
                    go = new GameObject(desc.name ?? "GameObject");
                }
            }
            else
            {
                go = new GameObject(desc.name ?? "GameObject");
            }

            // Apply transform
            if (desc.position != null && desc.position.Length >= 3)
            {
                go.transform.position = new Vector3(desc.position[0], desc.position[1], desc.position[2]);
            }
            if (desc.rotation != null && desc.rotation.Length >= 3)
            {
                go.transform.eulerAngles = new Vector3(desc.rotation[0], desc.rotation[1], desc.rotation[2]);
            }
            if (desc.scale != null && desc.scale.Length >= 3)
            {
                go.transform.localScale = new Vector3(desc.scale[0], desc.scale[1], desc.scale[2]);
            }

            // Set parent
            if (parent != null)
            {
                go.transform.SetParent(parent);
            }

            // Set name
            if (!string.IsNullOrEmpty(desc.name))
            {
                go.name = desc.name;
            }

            // Apply material if specified
            if (!string.IsNullOrEmpty(desc.materialPath))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(desc.materialPath);
                if (material != null)
                {
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        renderer.sharedMaterial = material;
                    }
                }
            }

            // Apply material overrides (inline property changes)
            if (desc.materialOverrides != null)
            {
                var renderer = go.GetComponent<Renderer>();
                if (renderer != null && renderer.sharedMaterial != null)
                {
                    var mode = desc.materialOverrides.materialOverrideMode ?? "instanced_copy";
                    if (mode == "property_block")
                    {
                        var block = new MaterialPropertyBlock();
                        renderer.GetPropertyBlock(block);
                        if (desc.materialOverrides.color != null && desc.materialOverrides.color.Length >= 3)
                        {
                            float a = desc.materialOverrides.color.Length >= 4 ? desc.materialOverrides.color[3] : 1f;
                            block.SetColor("_BaseColor", new Color(desc.materialOverrides.color[0], desc.materialOverrides.color[1], desc.materialOverrides.color[2], a));
                        }
                        if (desc.materialOverrides.emissionColor != null && desc.materialOverrides.emissionColor.Length >= 3)
                        {
                            float ei = desc.materialOverrides.emissionIntensity > 0f ? desc.materialOverrides.emissionIntensity : 1f;
                            float ae = desc.materialOverrides.emissionColor.Length >= 4 ? desc.materialOverrides.emissionColor[3] : 1f;
                            block.SetColor("_EmissionColor", new Color(
                                desc.materialOverrides.emissionColor[0] * ei,
                                desc.materialOverrides.emissionColor[1] * ei,
                                desc.materialOverrides.emissionColor[2] * ei, ae));
                        }
                        renderer.SetPropertyBlock(block);
                    }
                    else // instanced_copy (default)
                    {
                        var hash = ComputeMaterialOverrideHash(renderer.sharedMaterial, desc.materialOverrides);
                        if (materialCache != null && materialCache.TryGetValue(hash, out var cached))
                        {
                            renderer.sharedMaterial = cached;
                        }
                        else
                        {
                            var matInstance = new Material(renderer.sharedMaterial);
                            var tempRequest = new MaterialRequest
                            {
                                color = desc.materialOverrides.color,
                                emissionColor = desc.materialOverrides.emissionColor,
                                emissionIntensity = desc.materialOverrides.emissionIntensity,
                                metallic = desc.materialOverrides.metallic,
                                smoothness = desc.materialOverrides.smoothness
                            };
                            ApplyMaterialProperties(matInstance, tempRequest);
                            if (materialCache != null)
                                materialCache[hash] = matInstance;
                            renderer.sharedMaterial = matInstance;
                        }
                    }
                }
            }

            // Apply tag
            if (!string.IsNullOrEmpty(desc.tag))
            {
                try { go.tag = desc.tag; }
                catch (Exception) { Debug.LogWarning($"Invalid tag '{desc.tag}' on {go.name}"); }
            }

            // Apply layer
            if (!string.IsNullOrEmpty(desc.layer))
            {
                int layerIndex = LayerMask.NameToLayer(desc.layer);
                if (layerIndex < 0 && int.TryParse(desc.layer, out int parsedLayer))
                {
                    layerIndex = parsedLayer;
                }
                if (layerIndex >= 0)
                {
                    go.layer = layerIndex;
                }
            }

            // Apply active state
            if (!desc.active)
            {
                go.SetActive(false);
            }

            // Apply static flag
            if (desc.isStatic)
            {
                go.isStatic = true;
            }

            // Apply light descriptor
            if (desc.light != null && !string.IsNullOrEmpty(desc.light.type))
            {
                var light = go.GetComponent<Light>();
                if (light == null) light = go.AddComponent<Light>();

                if (Enum.TryParse<LightType>(desc.light.type, true, out var lightType))
                {
                    light.type = lightType;
                }

                if (desc.light.color != null && desc.light.color.Length >= 3)
                {
                    float a = desc.light.color.Length >= 4 ? desc.light.color[3] : 1f;
                    light.color = new Color(desc.light.color[0], desc.light.color[1], desc.light.color[2], a);
                }

                if (desc.light.intensity >= 0f)
                {
                    light.intensity = desc.light.intensity;
                }

                if (desc.light.range >= 0f)
                {
                    light.range = desc.light.range;
                }

                if (desc.light.spotAngle >= 0f)
                {
                    light.spotAngle = desc.light.spotAngle;
                }

                if (!string.IsNullOrEmpty(desc.light.shadows))
                {
                    if (Enum.TryParse<LightShadows>(desc.light.shadows, true, out var shadowType))
                    {
                        light.shadows = shadowType;
                    }
                }
            }

            // Add components
            if (desc.components != null)
            {
                foreach (var comp in desc.components)
                {
                    AddComponentFromDescriptor(go, comp);
                }
            }

            // Create children recursively
            if (desc.children != null)
            {
                foreach (var child in desc.children)
                {
                    CreateObjectFromDescriptor(child, go.transform, materialCache, warnings);
                }
            }

            Undo.RegisterCreatedObjectUndo(go, "Create " + go.name);
            return go;
        }

        private static void AddComponentFromDescriptor(GameObject go, ComponentDescriptor desc)
        {
            if (string.IsNullOrEmpty(desc.type)) return;

            // Find the component type
            Type componentType = null;

            // Check Unity built-in types first
            componentType = Type.GetType($"UnityEngine.{desc.type}, UnityEngine");
            if (componentType == null)
            {
                componentType = Type.GetType($"UnityEngine.{desc.type}, UnityEngine.PhysicsModule");
            }
            if (componentType == null)
            {
                componentType = Type.GetType($"UnityEngine.{desc.type}, UnityEngine.Physics2DModule");
            }

            // Search all assemblies
            if (componentType == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    componentType = assembly.GetTypes().FirstOrDefault(t => t.Name == desc.type && typeof(Component).IsAssignableFrom(t));
                    if (componentType != null) break;
                }
            }

            if (componentType == null)
            {
                Debug.LogWarning($"Component type not found: {desc.type}");
                return;
            }

            // Skip Transform - already exists
            if (componentType == typeof(Transform)) return;

            var component = go.AddComponent(componentType);
            if (component == null) return;

            // Apply properties if provided
            if (!string.IsNullOrEmpty(desc.propertiesJson))
            {
                try
                {
                    ApplyPropertiesToComponent(component, desc.propertiesJson);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to apply properties to {desc.type}: {ex.Message}");
                }
            }
        }

        private static void ApplyPropertiesToComponent(Component component, string propertiesJson)
        {
            // Parse simple key-value pairs from JSON
            var dict = MiniJSON.Json.Deserialize(propertiesJson) as Dictionary<string, object>;
            if (dict == null) return;

            var serializedObj = new SerializedObject(component);

            foreach (var kvp in dict)
            {
                var prop = serializedObj.FindProperty(kvp.Key);
                if (prop == null) continue;

                SetSerializedPropertyValue(prop, kvp.Value);
            }

            serializedObj.ApplyModifiedProperties();
        }

        private static void SetSerializedPropertyValue(SerializedProperty prop, object value)
        {
            if (value == null) return;

            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = Convert.ToInt32(value);
                    break;
                case SerializedPropertyType.Float:
                    prop.floatValue = Convert.ToSingle(value);
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = Convert.ToBoolean(value);
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = value.ToString();
                    break;
                case SerializedPropertyType.Enum:
                    if (value is string enumStr)
                    {
                        var enumIndex = Array.IndexOf(prop.enumNames, enumStr);
                        if (enumIndex >= 0) prop.enumValueIndex = enumIndex;
                    }
                    else
                    {
                        prop.enumValueIndex = Convert.ToInt32(value);
                    }
                    break;
                case SerializedPropertyType.Vector3:
                    if (value is List<object> v3List && v3List.Count >= 3)
                    {
                        prop.vector3Value = new Vector3(
                            Convert.ToSingle(v3List[0]),
                            Convert.ToSingle(v3List[1]),
                            Convert.ToSingle(v3List[2])
                        );
                    }
                    break;
                case SerializedPropertyType.Vector2:
                    if (value is List<object> v2List && v2List.Count >= 2)
                    {
                        prop.vector2Value = new Vector2(
                            Convert.ToSingle(v2List[0]),
                            Convert.ToSingle(v2List[1])
                        );
                    }
                    break;
            }
        }

        public static string ExportSceneDescriptor(string jsonData = null)
        {
            try
            {
                var objects = new List<object>();
                IEnumerable<GameObject> toExport;

                // Check if specific instance IDs provided
                int[] instanceIds = null;
                if (!string.IsNullOrEmpty(jsonData))
                {
                    var request = JsonUtility.FromJson<SceneExportRequest>(jsonData);
                    instanceIds = request?.instanceIds;
                }

                if (instanceIds != null && instanceIds.Length > 0)
                {
                    toExport = instanceIds
                        .Select(id => EditorUtility.EntityIdToObject(id) as GameObject)
                        .Where(go => go != null);
                }
                else
                {
                    // Export root objects from active scene
                    toExport = SceneManager.GetActiveScene().GetRootGameObjects();
                }

                foreach (var go in toExport)
                {
                    objects.Add(ExportObjectDescriptorDict(go, 0));
                }

                var descriptor = new Dictionary<string, object>
                {
                    { "name", SceneManager.GetActiveScene().name },
                    { "objects", objects }
                };

                return MiniJSON.Json.Serialize(descriptor);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private const int MaxExportDepth = 15;

        private static Dictionary<string, object> ExportObjectDescriptorDict(GameObject go, int depth)
        {
            var desc = new Dictionary<string, object>
            {
                { "name", go.name },
                { "position", new List<object> { go.transform.localPosition.x, go.transform.localPosition.y, go.transform.localPosition.z } },
                { "rotation", new List<object> { go.transform.localEulerAngles.x, go.transform.localEulerAngles.y, go.transform.localEulerAngles.z } },
                { "scale", new List<object> { go.transform.localScale.x, go.transform.localScale.y, go.transform.localScale.z } }
            };

            // Check if it's a prefab instance
            string prefabPath = null;
            var prefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(go);
            if (prefabAsset != null)
            {
                prefabPath = AssetDatabase.GetAssetPath(prefabAsset);
                desc["prefab"] = prefabPath;
            }

            // Check if it's a primitive (has MeshFilter with primitive mesh)
            string primitiveType = null;
            var meshFilter = go.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null && string.IsNullOrEmpty(prefabPath))
            {
                var meshName = meshFilter.sharedMesh.name;
                if (meshName == "Cube" || meshName == "Sphere" || meshName == "Cylinder" ||
                    meshName == "Capsule" || meshName == "Plane" || meshName == "Quad")
                {
                    primitiveType = meshName;
                    desc["primitiveType"] = meshName;
                }
            }

            // Export material path if custom
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null && renderer.sharedMaterial != null)
            {
                var matPath = AssetDatabase.GetAssetPath(renderer.sharedMaterial);
                if (!string.IsNullOrEmpty(matPath) && !matPath.StartsWith("Resources/unity_builtin"))
                    desc["materialPath"] = matPath;
            }

            if (!string.IsNullOrEmpty(go.tag) && go.tag != "Untagged")
                desc["tag"] = go.tag;

            if (go.layer != 0)
                desc["layer"] = LayerMask.LayerToName(go.layer);

            if (!go.activeSelf)
                desc["active"] = false;

            if (go.isStatic)
                desc["isStatic"] = true;

            // Export Light component
            bool hasLightDescriptor = false;
            var lightComp = go.GetComponent<Light>();
            if (lightComp != null)
            {
                hasLightDescriptor = true;
                desc["light"] = new Dictionary<string, object>
                {
                    { "type", lightComp.type.ToString() },
                    { "color", new List<object> { lightComp.color.r, lightComp.color.g, lightComp.color.b, lightComp.color.a } },
                    { "intensity", lightComp.intensity },
                    { "range", lightComp.range },
                    { "spotAngle", lightComp.spotAngle },
                    { "shadows", lightComp.shadows.ToString() }
                };
            }

            // Export non-default components
            var components = new List<object>();
            foreach (var component in go.GetComponents<Component>())
            {
                if (component == null) continue;
                var type = component.GetType();

                if (type == typeof(Transform)) continue;
                if (!string.IsNullOrEmpty(primitiveType))
                {
                    if (type == typeof(MeshFilter) || type == typeof(MeshRenderer)) continue;
                }
                if (type.Name.Contains("Collider") && !string.IsNullOrEmpty(primitiveType)) continue;
                if (type == typeof(Light) && hasLightDescriptor) continue;

                components.Add(new Dictionary<string, object> { { "type", type.Name } });
            }
            if (components.Count > 0)
                desc["components"] = components;

            // Export children recursively (with depth cap)
            if (go.transform.childCount > 0)
            {
                if (depth >= MaxExportDepth)
                {
                    desc["childCount"] = go.transform.childCount;
                    desc["truncated"] = true;
                }
                else
                {
                    var children = new List<object>();
                    foreach (Transform child in go.transform)
                    {
                        children.Add(ExportObjectDescriptorDict(child.gameObject, depth + 1));
                    }
                    desc["children"] = children;
                }
            }

            return desc;
        }

    }
}
