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
        // Lighting, Render Settings, Reflection Probes, Decals, Snap/Align

        [BridgeRoute("POST", "/light", Category = "rendering", Description = "Create light")]
        public static string CreateLight(string jsonData)
        {
            var request = JsonUtility.FromJson<CreateLightRequest>(jsonData);

            var go = new GameObject(request.name ?? "Light");
            Undo.RegisterCreatedObjectUndo(go, "Create Light");

            var light = go.AddComponent<Light>();

            if (!string.IsNullOrEmpty(request.type) && Enum.TryParse<LightType>(request.type, true, out var lightType))
            {
                light.type = lightType;
            }

            if (request.color != null && request.color.Length >= 3)
            {
                float a = request.color.Length >= 4 ? request.color[3] : 1f;
                light.color = new Color(request.color[0], request.color[1], request.color[2], a);
            }

            if (request.intensity >= 0f) light.intensity = request.intensity;
            if (request.range >= 0f) light.range = request.range;
            if (request.spotAngle >= 0f) light.spotAngle = request.spotAngle;

            if (!string.IsNullOrEmpty(request.shadows) && Enum.TryParse<LightShadows>(request.shadows, true, out var shadowType))
            {
                light.shadows = shadowType;
            }

            if (request.position != null && request.position.Length >= 3)
            {
                go.transform.position = new Vector3(request.position[0], request.position[1], request.position[2]);
            }

            if (request.rotation != null && request.rotation.Length >= 3)
            {
                go.transform.eulerAngles = new Vector3(request.rotation[0], request.rotation[1], request.rotation[2]);
            }

            if (request.parentId != 0)
            {
                var parent = EditorUtility.EntityIdToObject(request.parentId) as GameObject;
                if (parent != null)
                {
                    go.transform.SetParent(parent.transform);
                }
            }

            EditorUtility.SetDirty(go);

            return JsonResult(new Dictionary<string, object> { { "success", true }, { "instanceId", go.GetInstanceID() }, { "name", go.name } });
        }

        [BridgeRoute("PUT", "/light", Category = "rendering", Description = "Modify light")]
        public static string ModifyLight(string jsonData)
        {
            var request = JsonUtility.FromJson<ModifyLightRequest>(jsonData);

            var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            var light = go.GetComponent<Light>();
            if (light == null)
            {
                return JsonError("No Light component found");
            }

            Undo.RecordObject(light, "Modify Light");

            if (!string.IsNullOrEmpty(request.type) && Enum.TryParse<LightType>(request.type, true, out var lightType))
            {
                light.type = lightType;
            }

            if (request.color != null && request.color.Length >= 3)
            {
                float a = request.color.Length >= 4 ? request.color[3] : 1f;
                light.color = new Color(request.color[0], request.color[1], request.color[2], a);
            }

            if (request.intensity >= 0f) light.intensity = request.intensity;
            if (request.range >= 0f) light.range = request.range;
            if (request.spotAngle >= 0f) light.spotAngle = request.spotAngle;

            if (!string.IsNullOrEmpty(request.shadows) && Enum.TryParse<LightShadows>(request.shadows, true, out var shadowType))
            {
                light.shadows = shadowType;
            }

            EditorUtility.SetDirty(light);

            return JsonResult(new Dictionary<string, object> { { "success", true }, { "name", go.name } });
        }

        [BridgeRoute("POST", "/reflection/probe", Category = "rendering", Description = "Create reflection probe", TimeoutDefault = 15000)]
        public static string CreateReflectionProbe(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(jsonData ?? "{}") as Dictionary<string, object>;
                if (request == null) return JsonError("Failed to parse reflection probe request");

                var name = ReadString(request, "name");
                var go = new GameObject(string.IsNullOrWhiteSpace(name) ? "Reflection Probe" : name);
                Undo.RegisterCreatedObjectUndo(go, "Create Reflection Probe");

                var probe = go.AddComponent<ReflectionProbe>();
                Undo.RecordObject(probe, "Create Reflection Probe");

                if (TryReadInt(request, "parentId", out var parentId) && parentId != 0)
                {
                    var parent = EditorUtility.EntityIdToObject(parentId) as GameObject;
                    if (parent != null) go.transform.SetParent(parent.transform, true);
                }

                if (TryReadVectorField(request, "position", out var pos))
                {
                    go.transform.position = new Vector3(pos[0], pos[1], pos[2]);
                }

                if (TryReadVectorField(request, "rotation", out var rot))
                {
                    go.transform.eulerAngles = new Vector3(rot[0], rot[1], rot[2]);
                }

                ApplyReflectionProbePatch(probe, request);
                EditorUtility.SetDirty(probe);
                EditorUtility.SetDirty(go);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", go.GetInstanceID() },
                    { "name", go.name },
                    { "mode", probe.mode.ToString() },
                    { "resolution", probe.resolution }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("PUT", "/reflection/probe", Category = "rendering", Description = "Modify reflection probe", TimeoutDefault = 15000)]
        public static string ModifyReflectionProbe(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(jsonData ?? "{}") as Dictionary<string, object>;
                if (request == null) return JsonError("Failed to parse reflection probe request");
                if (!TryReadInt(request, "instanceId", out var instanceId) || instanceId == 0)
                {
                    return JsonError("instanceId is required");
                }

                var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
                if (go == null) return JsonError($"GameObject not found: {instanceId}");

                var probe = go.GetComponent<ReflectionProbe>();
                if (probe == null) return JsonError("No ReflectionProbe component found");

                Undo.RecordObject(go.transform, "Modify Reflection Probe");
                Undo.RecordObject(probe, "Modify Reflection Probe");

                if (TryReadVectorField(request, "position", out var pos))
                {
                    go.transform.position = new Vector3(pos[0], pos[1], pos[2]);
                }

                if (TryReadVectorField(request, "rotation", out var rot))
                {
                    go.transform.eulerAngles = new Vector3(rot[0], rot[1], rot[2]);
                }

                ApplyReflectionProbePatch(probe, request);
                EditorUtility.SetDirty(probe);
                EditorUtility.SetDirty(go);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", go.GetInstanceID() },
                    { "name", go.name },
                    { "mode", probe.mode.ToString() },
                    { "resolution", probe.resolution }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/reflection/probe/bake", Category = "rendering", Description = "Bake reflection probe cubemap", TimeoutDefault = 60000, TimeoutMin = 2000, TimeoutMax = 300000)]
        public static string BakeReflectionProbe(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(jsonData ?? "{}") as Dictionary<string, object>;
                if (request == null) return JsonError("Failed to parse reflection probe bake request");
                if (!TryReadInt(request, "instanceId", out var instanceId) || instanceId == 0)
                {
                    return JsonError("instanceId is required");
                }

                var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
                if (go == null) return JsonError($"GameObject not found: {instanceId}");

                var probe = go.GetComponent<ReflectionProbe>();
                if (probe == null) return JsonError("No ReflectionProbe component found");

                var outputPath = ReadString(request, "path");
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    outputPath = $"Assets/Generated/ReflectionProbes/{MakeSafeFileName(go.name)}_{instanceId}.exr";
                }

                var parent = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(parent)) EnsureAssetFolder(parent.Replace("\\", "/"));

                bool bakeInvoked = false;
                bool bakeResult = true;

                var bakeMethod = typeof(Lightmapping).GetMethod("BakeReflectionProbe", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ReflectionProbe), typeof(string) }, null);
                if (bakeMethod != null)
                {
                    var raw = bakeMethod.Invoke(null, new object[] { probe, outputPath });
                    bakeInvoked = true;
                    if (raw is bool b) bakeResult = b;
                }

                if (!bakeInvoked)
                {
                    probe.RenderProbe();
                }

                DynamicGI.UpdateEnvironment();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", bakeResult },
                    { "instanceId", instanceId },
                    { "name", go.name },
                    { "path", outputPath },
                    { "invokedLightmappingBake", bakeInvoked }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/decal/projector", Category = "rendering", Description = "Create decal projector", TimeoutDefault = 15000)]
        public static string CreateDecalProjector(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(jsonData ?? "{}") as Dictionary<string, object>;
                if (request == null) return JsonError("Failed to parse decal projector request");

                bool autoEnableFeature = ReadBool(request, "autoEnableFeature", true);
                bool featureEnabled = false;
                string featureWarning = string.Empty;

                if (autoEnableFeature)
                {
                    featureEnabled = EnsureDecalRendererFeature(out featureWarning);
                }
                else
                {
                    featureWarning = "autoEnableFeature=false; ensure DecalRendererFeature is enabled manually.";
                }

                var name = ReadString(request, "name");
                var go = new GameObject(string.IsNullOrWhiteSpace(name) ? "Decal Projector" : name);
                Undo.RegisterCreatedObjectUndo(go, "Create Decal Projector");
                var decal = go.AddComponent<DecalProjector>();
                Undo.RecordObject(decal, "Create Decal Projector");

                if (TryReadInt(request, "parentId", out var parentId) && parentId != 0)
                {
                    var parent = EditorUtility.EntityIdToObject(parentId) as GameObject;
                    if (parent != null) go.transform.SetParent(parent.transform, true);
                }

                if (TryReadVectorField(request, "position", out var pos))
                {
                    go.transform.position = new Vector3(pos[0], pos[1], pos[2]);
                }

                if (TryReadVectorField(request, "rotation", out var rot))
                {
                    go.transform.eulerAngles = new Vector3(rot[0], rot[1], rot[2]);
                }

                ApplyDecalProjectorPatch(decal, request);
                EditorUtility.SetDirty(decal);
                EditorUtility.SetDirty(go);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", go.GetInstanceID() },
                    { "name", go.name },
                    { "decalFeatureEnabled", featureEnabled }
                };
                if (!string.IsNullOrWhiteSpace(featureWarning)) result["warning"] = featureWarning;
                return JsonResult(result);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("PUT", "/decal/projector", Category = "rendering", Description = "Modify decal projector", TimeoutDefault = 15000)]
        public static string ModifyDecalProjector(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(jsonData ?? "{}") as Dictionary<string, object>;
                if (request == null) return JsonError("Failed to parse decal projector request");
                if (!TryReadInt(request, "instanceId", out var instanceId) || instanceId == 0)
                {
                    return JsonError("instanceId is required");
                }

                var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
                if (go == null) return JsonError($"GameObject not found: {instanceId}");

                var decal = go.GetComponent<DecalProjector>();
                if (decal == null) return JsonError("No DecalProjector component found");

                Undo.RecordObject(go.transform, "Modify Decal Projector");
                Undo.RecordObject(decal, "Modify Decal Projector");

                if (TryReadVectorField(request, "position", out var pos))
                {
                    go.transform.position = new Vector3(pos[0], pos[1], pos[2]);
                }

                if (TryReadVectorField(request, "rotation", out var rot))
                {
                    go.transform.eulerAngles = new Vector3(rot[0], rot[1], rot[2]);
                }

                ApplyDecalProjectorPatch(decal, request);
                EditorUtility.SetDirty(decal);
                EditorUtility.SetDirty(go);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", go.GetInstanceID() },
                    { "name", go.name }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("GET", "/rendersettings", Category = "rendering", Description = "Get lighting/fog/skybox settings", ReadOnly = true)]
        public static string GetRenderSettings()
        {
            var skyboxMat = RenderSettings.skybox;
            string skyboxPath = skyboxMat != null ? AssetDatabase.GetAssetPath(skyboxMat) : null;

            return JsonUtility.ToJson(new RenderSettingsResponse
            {
                success = true,
                ambientMode = RenderSettings.ambientMode.ToString(),
                ambientLight = new float[] { RenderSettings.ambientLight.r, RenderSettings.ambientLight.g, RenderSettings.ambientLight.b, RenderSettings.ambientLight.a },
                ambientSkyColor = new float[] { RenderSettings.ambientSkyColor.r, RenderSettings.ambientSkyColor.g, RenderSettings.ambientSkyColor.b, RenderSettings.ambientSkyColor.a },
                ambientEquatorColor = new float[] { RenderSettings.ambientEquatorColor.r, RenderSettings.ambientEquatorColor.g, RenderSettings.ambientEquatorColor.b, RenderSettings.ambientEquatorColor.a },
                ambientGroundColor = new float[] { RenderSettings.ambientGroundColor.r, RenderSettings.ambientGroundColor.g, RenderSettings.ambientGroundColor.b, RenderSettings.ambientGroundColor.a },
                fog = RenderSettings.fog,
                fogMode = RenderSettings.fogMode.ToString(),
                fogColor = new float[] { RenderSettings.fogColor.r, RenderSettings.fogColor.g, RenderSettings.fogColor.b, RenderSettings.fogColor.a },
                fogDensity = RenderSettings.fogDensity,
                fogStartDistance = RenderSettings.fogStartDistance,
                fogEndDistance = RenderSettings.fogEndDistance,
                skyboxMaterial = skyboxPath,
                reflectionIntensity = RenderSettings.reflectionIntensity
            });
        }

        [BridgeRoute("POST", "/rendersettings", Category = "rendering", Description = "Set lighting/fog/skybox settings")]
        public static string SetRenderSettings(string jsonData)
        {
            var request = JsonUtility.FromJson<SetRenderSettingsRequest>(jsonData);

            Undo.SetCurrentGroupName("Agent Bridge: Set Render Settings");

            if (!string.IsNullOrEmpty(request.ambientMode))
            {
                if (Enum.TryParse<UnityEngine.Rendering.AmbientMode>(request.ambientMode, true, out var mode))
                {
                    RenderSettings.ambientMode = mode;
                }
            }

            if (request.ambientLight != null && request.ambientLight.Length >= 3)
            {
                float a = request.ambientLight.Length >= 4 ? request.ambientLight[3] : 1f;
                RenderSettings.ambientLight = new Color(request.ambientLight[0], request.ambientLight[1], request.ambientLight[2], a);
            }

            if (request.ambientSkyColor != null && request.ambientSkyColor.Length >= 3)
            {
                float a = request.ambientSkyColor.Length >= 4 ? request.ambientSkyColor[3] : 1f;
                RenderSettings.ambientSkyColor = new Color(request.ambientSkyColor[0], request.ambientSkyColor[1], request.ambientSkyColor[2], a);
            }

            if (request.ambientEquatorColor != null && request.ambientEquatorColor.Length >= 3)
            {
                float a = request.ambientEquatorColor.Length >= 4 ? request.ambientEquatorColor[3] : 1f;
                RenderSettings.ambientEquatorColor = new Color(request.ambientEquatorColor[0], request.ambientEquatorColor[1], request.ambientEquatorColor[2], a);
            }

            if (request.ambientGroundColor != null && request.ambientGroundColor.Length >= 3)
            {
                float a = request.ambientGroundColor.Length >= 4 ? request.ambientGroundColor[3] : 1f;
                RenderSettings.ambientGroundColor = new Color(request.ambientGroundColor[0], request.ambientGroundColor[1], request.ambientGroundColor[2], a);
            }

            if (request.fog >= 0)
            {
                RenderSettings.fog = request.fog == 1;
            }

            if (!string.IsNullOrEmpty(request.fogMode))
            {
                if (Enum.TryParse<FogMode>(request.fogMode, true, out var fogMode))
                {
                    RenderSettings.fogMode = fogMode;
                }
            }

            if (request.fogColor != null && request.fogColor.Length >= 3)
            {
                float a = request.fogColor.Length >= 4 ? request.fogColor[3] : 1f;
                RenderSettings.fogColor = new Color(request.fogColor[0], request.fogColor[1], request.fogColor[2], a);
            }

            if (request.fogDensity >= 0f) RenderSettings.fogDensity = request.fogDensity;
            if (request.fogStartDistance >= 0f) RenderSettings.fogStartDistance = request.fogStartDistance;
            if (request.fogEndDistance >= 0f) RenderSettings.fogEndDistance = request.fogEndDistance;

            if (!string.IsNullOrEmpty(request.skyboxMaterialPath))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(request.skyboxMaterialPath);
                if (mat != null)
                {
                    RenderSettings.skybox = mat;
                }
            }

            if (request.reflectionIntensity >= 0f) RenderSettings.reflectionIntensity = request.reflectionIntensity;

            // Mark scene dirty since RenderSettings is not a standard UnityEngine.Object
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            return JsonSuccess();
        }

        [BridgeRoute("POST", "/skybox/procedural", Category = "rendering", Description = "Create a procedural skybox material and optionally assign it")]
        public static string CreateProceduralSkybox(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<ProceduralSkyboxRequest>(NormalizeColorFields(jsonData));

                var shader = Shader.Find("Skybox/Procedural");
                if (shader == null)
                    return JsonError("Skybox/Procedural shader not found");

                var mat = new Material(shader);
                mat.name = request.name ?? "ProceduralSkybox";

                mat.SetFloat("_SunSize", Mathf.Clamp(request.sunSize, 0f, 1f));
                mat.SetFloat("_SunSizeConvergence", Mathf.Clamp(request.sunSizeConvergence, 1, 10));
                mat.SetFloat("_AtmosphereThickness", Mathf.Clamp(request.atmosphereThickness, 0f, 5f));
                mat.SetFloat("_Exposure", Mathf.Clamp(request.exposure, 0f, 8f));

                if (request.skyTint != null && request.skyTint.Length >= 3)
                    mat.SetColor("_SkyTint", new Color(request.skyTint[0], request.skyTint[1], request.skyTint[2]));

                if (request.groundColor != null && request.groundColor.Length >= 3)
                    mat.SetColor("_GroundColor", new Color(request.groundColor[0], request.groundColor[1], request.groundColor[2]));

                var path = request.path;
                if (string.IsNullOrEmpty(path))
                    path = $"Assets/{mat.name}.mat";
                if (!path.StartsWith("Assets/"))
                    path = "Assets/" + path;
                if (!path.EndsWith(".mat"))
                    path += ".mat";

                if (ValidateAssetPath(path) == null)
                    return JsonError("Path is outside the project directory");

                var dir = System.IO.Path.GetDirectoryName(path);
                if (!System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                AssetDatabase.CreateAsset(mat, path);
                AssetDatabase.SaveAssets();

                if (request.applySkybox == 1)
                {
                    RenderSettings.skybox = mat;
                    EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                }

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "path", path },
                    { "applied", request.applySkybox == 1 },
                    { "message", $"Created procedural skybox at {path}" + (request.applySkybox == 1 ? " and applied to scene" : "") }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static bool EnsureDecalRendererFeature(out string warning)
        {
            warning = string.Empty;
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urpAsset))
            {
                warning = "No active UniversalRenderPipelineAsset found";
                return false;
            }

            var urpAssetSO = new SerializedObject(urpAsset);
            var rendererDataListProp = urpAssetSO.FindProperty("m_RendererDataList");
            var defaultRendererIndexProp = urpAssetSO.FindProperty("m_DefaultRendererIndex");
            if (rendererDataListProp == null || rendererDataListProp.arraySize == 0)
            {
                warning = "URP renderer data list is empty";
                return false;
            }

            int index = 0;
            if (defaultRendererIndexProp != null) index = Mathf.Clamp(defaultRendererIndexProp.intValue, 0, rendererDataListProp.arraySize - 1);
            var rendererDataObject = rendererDataListProp.GetArrayElementAtIndex(index).objectReferenceValue as UniversalRendererData;
            if (rendererDataObject == null)
            {
                warning = $"Default renderer data at index {index} is missing or not UniversalRendererData";
                return false;
            }

            if (rendererDataObject.rendererFeatures.Any(f => f is DecalRendererFeature))
            {
                return true;
            }

            var feature = ScriptableObject.CreateInstance<DecalRendererFeature>();
            feature.name = "Decal Renderer Feature";
            feature.SetActive(true);
            Undo.RegisterCreatedObjectUndo(feature, "Add Decal Renderer Feature");

            AssetDatabase.AddObjectToAsset(feature, rendererDataObject);
            rendererDataObject.rendererFeatures.Add(feature);
            EditorUtility.SetDirty(feature);
            EditorUtility.SetDirty(rendererDataObject);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            return true;
        }

        private static void ApplyReflectionProbePatch(ReflectionProbe probe, Dictionary<string, object> request)
        {
            if (request.TryGetValue("mode", out var modeObj) && modeObj != null
                && Enum.TryParse<ReflectionProbeMode>(modeObj.ToString(), true, out var mode))
            {
                probe.mode = mode;
            }

            if (request.TryGetValue("refreshMode", out var refreshObj) && refreshObj != null
                && Enum.TryParse<ReflectionProbeRefreshMode>(refreshObj.ToString(), true, out var refreshMode))
            {
                probe.refreshMode = refreshMode;
            }

            if (request.TryGetValue("timeSlicingMode", out var slicingObj) && slicingObj != null
                && Enum.TryParse<ReflectionProbeTimeSlicingMode>(slicingObj.ToString(), true, out var slicingMode))
            {
                probe.timeSlicingMode = slicingMode;
            }

            if (TryReadInt(request, "resolution", out var resolution))
            {
                resolution = Mathf.Clamp(Mathf.ClosestPowerOfTwo(Mathf.Max(16, resolution)), 16, 2048);
                probe.resolution = resolution;
            }

            if (TryReadBoolField(request, "hdr", out var hdr)) probe.hdr = hdr;
            if (TryReadVectorField(request, "size", out var size)) probe.size = new Vector3(size[0], size[1], size[2]);
            if (TryReadVectorField(request, "center", out var center)) probe.center = new Vector3(center[0], center[1], center[2]);
            if (TryReadFloatField(request, "nearClipPlane", out var nearClip)) probe.nearClipPlane = nearClip;
            if (TryReadFloatField(request, "farClipPlane", out var farClip)) probe.farClipPlane = farClip;
            if (TryReadFloatField(request, "intensity", out var intensity)) probe.intensity = intensity;
            if (TryReadBoolField(request, "boxProjection", out var boxProjection)) probe.boxProjection = boxProjection;
            if (request.TryGetValue("backgroundColor", out var bgObj) && TryReadColor(bgObj, out var bgColor)) probe.backgroundColor = bgColor;
            if (TryReadInt(request, "cullingMask", out var cullingMask)) probe.cullingMask = cullingMask;
            if (TryReadInt(request, "importance", out var importance)) probe.importance = importance;
        }

        private static void ApplyDecalProjectorPatch(DecalProjector decal, Dictionary<string, object> request)
        {
            var materialPath = ReadString(request, "material");
            if (string.IsNullOrWhiteSpace(materialPath)) materialPath = ReadString(request, "materialPath");
            if (!string.IsNullOrWhiteSpace(materialPath))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material != null) decal.material = material;
            }

            if (TryReadVectorField(request, "size", out var size))
            {
                decal.size = new Vector3(size[0], Mathf.Max(0.001f, size[1]), size[2]);
            }

            if (TryReadVectorField(request, "pivot", out var pivot))
            {
                decal.pivot = new Vector3(pivot[0], pivot[1], pivot[2]);
            }

            if (TryReadFloatField(request, "fadeFactor", out var fadeFactor)) decal.fadeFactor = Mathf.Clamp01(fadeFactor);
            if (TryReadFloatField(request, "drawDistance", out var drawDistance)) decal.drawDistance = Mathf.Max(0f, drawDistance);
            if (TryReadFloatField(request, "startAngleFade", out var startAngleFade)) decal.startAngleFade = startAngleFade;
            if (TryReadFloatField(request, "endAngleFade", out var endAngleFade)) decal.endAngleFade = endAngleFade;

            if (TryReadVectorField(request, "uvScale", out var uvScale))
            {
                decal.uvScale = new Vector2(uvScale[0], uvScale[1]);
            }

            if (TryReadVectorField(request, "uvBias", out var uvBias))
            {
                decal.uvBias = new Vector2(uvBias[0], uvBias[1]);
            }

            if (request.TryGetValue("scaleMode", out var scaleObj) && scaleObj != null
                && Enum.TryParse<DecalScaleMode>(scaleObj.ToString(), true, out var scaleMode))
            {
                decal.scaleMode = scaleMode;
            }

            if (TryReadInt(request, "renderingLayerMask", out var renderingLayerMask))
            {
                decal.renderingLayerMask = (uint)Mathf.Max(0, renderingLayerMask);
            }
        }

        private static bool TryReadVectorField(Dictionary<string, object> map, string key, out float[] result, int size = 3)
        {
            result = null;
            if (!map.TryGetValue(key, out var raw) || raw == null) return false;
            return TryReadVector(raw, size, out result);
        }
    }
}
