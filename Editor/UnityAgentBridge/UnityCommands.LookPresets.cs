using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        // Look Presets: SaveLookPreset, LoadLookPreset, ListLookPresets, ApplySeparationSafeLook and helpers

        [BridgeRoute("POST", "/look/preset", Category = "look", Description = "Save look preset")]
        public static string SaveLookPreset(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<SaveLookPresetRequest>(jsonData);
                if (request == null || string.IsNullOrWhiteSpace(request.name))
                    return JsonError("Preset name is required");

                var safeName = MakeSafeFileName(request.name);
                var folderPath = "Assets/Editor/LookPresets";
                EnsureAssetFolder(folderPath);

                // Capture volume snapshot
                Dictionary<string, object> volumeSnapshot = null;
                if (TryResolveVolumeProfile(null, 0, false, out var volume, out var profile, out var resolvedPath, out var volumeError))
                {
                    volumeSnapshot = BuildVolumeProfileSnapshot(profile);
                }

                var preset = new Dictionary<string, object>
                {
                    { "presetVersion", 1 },
                    { "name", request.name },
                    { "description", request.description ?? "" },
                    { "createdAt", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") },
                    { "sceneName", SceneManager.GetActiveScene().name },
                    { "lights", CaptureSceneLights() },
                    { "renderSettings", CaptureRenderSettingsDict() }
                };

                if (volumeSnapshot != null) preset["volume"] = volumeSnapshot;
                var cameraDict = CaptureCameraDict();
                if (cameraDict != null) preset["camera"] = cameraDict;

                var json = MiniJSON.Json.Serialize(preset);
                var filePath = $"{folderPath}/{safeName}.json";

                if (ValidateAssetPath(filePath) == null)
                    return JsonError("Invalid preset path");

                var projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..");
                var fullPath = System.IO.Path.Combine(projectRoot, filePath);
                System.IO.File.WriteAllText(fullPath, json);
                AssetDatabase.Refresh();

                var lightCount = preset.ContainsKey("lights") ? ((List<Dictionary<string, object>>)preset["lights"]).Count : 0;

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "path", filePath },
                    { "summary", new Dictionary<string, object>
                        {
                            { "lightCount", lightCount },
                            { "hasVolume", volumeSnapshot != null },
                            { "volumeOverrideCount", volumeSnapshot?.Count ?? 0 },
                            { "hasRenderSettings", true },
                            { "hasCamera", cameraDict != null }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("PUT", "/look/preset", Category = "look", Description = "Load/apply look preset")]
        public static string LoadLookPreset(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<LoadLookPresetRequest>(jsonData);
                if (request == null || string.IsNullOrWhiteSpace(request.name))
                    return JsonError("Preset name is required");

                var safeName = MakeSafeFileName(request.name);
                var filePath = $"Assets/Editor/LookPresets/{safeName}.json";

                if (ValidateAssetPath(filePath) == null)
                    return JsonError("Invalid preset path");

                var projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..");
                var fullPath = System.IO.Path.Combine(projectRoot, filePath);

                if (!System.IO.File.Exists(fullPath))
                    return JsonError($"Preset not found: {filePath}");

                var json = System.IO.File.ReadAllText(fullPath);
                var preset = MiniJSON.Json.Deserialize(json) as Dictionary<string, object>;
                if (preset == null)
                    return JsonError("Failed to parse preset JSON");

                var mode = (request.mode ?? "replace").ToLowerInvariant();
                var matchBy = (request.matchBy ?? "none").ToLowerInvariant();
                int appliedSections = 0;

                // Apply render settings
                if (request.applyRenderSettings == 1 && preset.ContainsKey("renderSettings"))
                {
                    var rs = preset["renderSettings"] as Dictionary<string, object>;
                    if (rs != null)
                    {
                        ApplyRenderSettingsFromDict(rs);
                        appliedSections++;
                    }
                }

                // Apply volume overrides
                if (request.applyVolume == 1 && preset.ContainsKey("volume"))
                {
                    var volumeOverrides = preset["volume"] as Dictionary<string, object>;
                    if (volumeOverrides != null)
                    {
                        if (TryResolveVolumeProfile(null, 0, true, out var vol, out var prof, out var rp, out var ve))
                        {
                            ApplyVolumeOverrides(prof, volumeOverrides);
                            EditorUtility.SetDirty(prof);
                            appliedSections++;
                        }
                    }
                }

                // Apply camera settings
                if (request.applyCamera == 1 && preset.ContainsKey("camera"))
                {
                    var camDict = preset["camera"] as Dictionary<string, object>;
                    if (camDict != null)
                    {
                        ApplyCameraFromDict(camDict);
                        appliedSections++;
                    }
                }

                // Apply lights
                int lightsCreated = 0;
                int lightsUpdated = 0;
                int lightsDeleted = 0;
                if (request.applyLights == 1 && preset.ContainsKey("lights"))
                {
                    var presetLights = preset["lights"] as List<object>;
                    if (presetLights != null)
                    {
                        // Delete existing lights if replaceLights=true and matchBy=none
                        if (request.replaceLights == 1 && matchBy == "none")
                        {
                            var existingLights = Resources.FindObjectsOfTypeAll<Light>()
                                .Where(l => l != null && l.gameObject.scene.IsValid() && l.gameObject.scene.isLoaded)
                                .ToList();
                            foreach (var existing in existingLights)
                            {
                                Undo.DestroyObjectImmediate(existing.gameObject);
                                lightsDeleted++;
                            }
                        }

                        foreach (var lightObj in presetLights)
                        {
                            var lightDict = lightObj as Dictionary<string, object>;
                            if (lightDict == null) continue;

                            Light matchedLight = null;
                            if (matchBy == "name" && lightDict.ContainsKey("name"))
                            {
                                var targetName = lightDict["name"].ToString();
                                matchedLight = Resources.FindObjectsOfTypeAll<Light>()
                                    .FirstOrDefault(l => l != null && l.gameObject.scene.IsValid() && l.gameObject.name == targetName);
                            }
                            else if (matchBy == "type" && lightDict.ContainsKey("type"))
                            {
                                var targetType = lightDict["type"].ToString();
                                if (Enum.TryParse<LightType>(targetType, true, out var lt))
                                {
                                    matchedLight = Resources.FindObjectsOfTypeAll<Light>()
                                        .FirstOrDefault(l => l != null && l.gameObject.scene.IsValid() && l.type == lt);
                                }
                            }

                            if (matchedLight != null)
                            {
                                // Update existing light
                                ApplyLightFromDict(matchedLight, lightDict);
                                lightsUpdated++;
                            }
                            else
                            {
                                // Create new light
                                SpawnLightFromDict(lightDict);
                                lightsCreated++;
                            }
                        }
                        appliedSections++;
                    }
                }

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "presetName", request.name },
                    { "mode", mode },
                    { "appliedSections", appliedSections },
                    { "lightsCreated", lightsCreated },
                    { "lightsUpdated", lightsUpdated },
                    { "lightsDeleted", lightsDeleted }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("GET", "/look/presets", Category = "look", Description = "List look presets", ReadOnly = true)]
        public static string ListLookPresets()
        {
            try
            {
                var folderPath = "Assets/Editor/LookPresets";
                var projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..");
                var fullFolder = System.IO.Path.Combine(projectRoot, folderPath);

                var presets = new List<Dictionary<string, object>>();

                if (System.IO.Directory.Exists(fullFolder))
                {
                    foreach (var file in System.IO.Directory.GetFiles(fullFolder, "*.json"))
                    {
                        try
                        {
                            var content = System.IO.File.ReadAllText(file);
                            var data = MiniJSON.Json.Deserialize(content) as Dictionary<string, object>;
                            if (data != null)
                            {
                                var info = new Dictionary<string, object>
                                {
                                    { "name", data.ContainsKey("name") ? data["name"] : System.IO.Path.GetFileNameWithoutExtension(file) },
                                    { "description", data.ContainsKey("description") ? data["description"] : "" },
                                    { "sceneName", data.ContainsKey("sceneName") ? data["sceneName"] : "" },
                                    { "createdAt", data.ContainsKey("createdAt") ? data["createdAt"] : "" },
                                    { "path", folderPath + "/" + System.IO.Path.GetFileName(file) }
                                };
                                presets.Add(info);
                            }
                        }
                        catch { /* skip malformed files */ }
                    }
                }

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "presets", presets },
                    { "count", presets.Count }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/look/separation-safe", Category = "look", Description = "Apply separation-safe bloom/exposure")]
        public static string ApplySeparationSafeLook(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>
                    ?? new Dictionary<string, object>();

                string profilePath = ReadString(request, "profilePath");
                int volumeInstanceId = TryReadInt(request, "volumeInstanceId", out var vid) ? vid : 0;
                bool createIfMissing = ReadBool(request, "createIfMissing", true);
                bool saveAssets = ReadBool(request, "saveAssets", true);

                float bloomThresholdMin = Mathf.Clamp(TryReadFloatField(request, "bloomThresholdMin", out var thresholdParsed) ? thresholdParsed : 1.05f, 0f, 10f);
                float bloomIntensityScale = Mathf.Clamp(TryReadFloatField(request, "bloomIntensityScale", out var intensityScaleParsed) ? intensityScaleParsed : 0.6f, 0.05f, 2f);
                float bloomScatterMax = Mathf.Clamp(TryReadFloatField(request, "bloomScatterMax", out var scatterParsed) ? scatterParsed : 0.7f, 0f, 1f);
                float postExposureOffset = Mathf.Clamp(TryReadFloatField(request, "postExposureOffset", out var exposureParsed) ? exposureParsed : -0.2f, -4f, 4f);
                float saturationOffset = Mathf.Clamp(TryReadFloatField(request, "saturationOffset", out var saturationParsed) ? saturationParsed : -8f, -100f, 100f);

                if (!TryResolveVolumeProfile(
                    profilePath,
                    volumeInstanceId,
                    createIfMissing,
                    out var volume,
                    out var profile,
                    out var resolvedProfilePath,
                    out var resolveError))
                {
                    return JsonError(resolveError);
                }

                Undo.RecordObject(profile, "Apply Separation-Safe Look");
                if (volume != null)
                {
                    Undo.RecordObject(volume, "Apply Separation-Safe Look");
                }

                var bloom = EnsureVolumeComponent<Bloom>(profile);
                var colorAdjustments = EnsureVolumeComponent<ColorAdjustments>(profile);

                var before = new Dictionary<string, object>
                {
                    { "bloomThreshold", bloom.threshold.value },
                    { "bloomIntensity", bloom.intensity.value },
                    { "bloomScatter", bloom.scatter.value },
                    { "postExposure", colorAdjustments.postExposure.value },
                    { "saturation", colorAdjustments.saturation.value }
                };

                bloom.active = true;
                bloom.threshold.overrideState = true;
                bloom.threshold.value = Mathf.Max(bloom.threshold.value, bloomThresholdMin);
                bloom.intensity.overrideState = true;
                bloom.intensity.value = Mathf.Clamp(bloom.intensity.value * bloomIntensityScale, 0f, 3f);
                bloom.scatter.overrideState = true;
                bloom.scatter.value = Mathf.Min(bloom.scatter.value, bloomScatterMax);
                bloom.highQualityFiltering.overrideState = true;
                bloom.highQualityFiltering.value = true;

                colorAdjustments.active = true;
                colorAdjustments.postExposure.overrideState = true;
                colorAdjustments.postExposure.value = Mathf.Clamp(colorAdjustments.postExposure.value + postExposureOffset, -5f, 5f);
                colorAdjustments.saturation.overrideState = true;
                colorAdjustments.saturation.value = Mathf.Clamp(colorAdjustments.saturation.value + saturationOffset, -100f, 100f);

                EditorUtility.SetDirty(profile);
                if (volume != null) EditorUtility.SetDirty(volume);

                if (saveAssets)
                {
                    AssetDatabase.SaveAssets();
                }

                var after = new Dictionary<string, object>
                {
                    { "bloomThreshold", bloom.threshold.value },
                    { "bloomIntensity", bloom.intensity.value },
                    { "bloomScatter", bloom.scatter.value },
                    { "postExposure", colorAdjustments.postExposure.value },
                    { "saturation", colorAdjustments.saturation.value }
                };

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "profilePath", resolvedProfilePath ?? string.Empty },
                    { "volumeInstanceId", volume != null ? volume.gameObject.GetInstanceID() : 0 },
                    { "volumeName", volume != null ? volume.gameObject.name : string.Empty },
                    { "before", before },
                    { "after", after },
                    { "notes", new List<object>
                        {
                            "This guardrail reduces glow fusion while keeping a neon look.",
                            "If tiles still visually merge, increase bloomThresholdMin and reduce bloomIntensityScale further.",
                            "Material emission intensity is not modified by this operation."
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static void ApplyRenderSettingsFromDict(Dictionary<string, object> rs)
        {
            if (rs.ContainsKey("ambientMode") && Enum.TryParse<UnityEngine.Rendering.AmbientMode>(rs["ambientMode"].ToString(), true, out var mode))
                RenderSettings.ambientMode = mode;

            ApplyColorFromDict(rs, "ambientLight", c => RenderSettings.ambientLight = c);
            ApplyColorFromDict(rs, "ambientSkyColor", c => RenderSettings.ambientSkyColor = c);
            ApplyColorFromDict(rs, "ambientEquatorColor", c => RenderSettings.ambientEquatorColor = c);
            ApplyColorFromDict(rs, "ambientGroundColor", c => RenderSettings.ambientGroundColor = c);

            if (rs.ContainsKey("fog"))
                RenderSettings.fog = System.Convert.ToBoolean(rs["fog"]);

            if (rs.ContainsKey("fogMode") && Enum.TryParse<FogMode>(rs["fogMode"].ToString(), true, out var fogMode))
                RenderSettings.fogMode = fogMode;

            ApplyColorFromDict(rs, "fogColor", c => RenderSettings.fogColor = c);

            if (rs.ContainsKey("fogDensity"))
                RenderSettings.fogDensity = System.Convert.ToSingle(rs["fogDensity"]);
            if (rs.ContainsKey("fogStartDistance"))
                RenderSettings.fogStartDistance = System.Convert.ToSingle(rs["fogStartDistance"]);
            if (rs.ContainsKey("fogEndDistance"))
                RenderSettings.fogEndDistance = System.Convert.ToSingle(rs["fogEndDistance"]);
            if (rs.ContainsKey("reflectionIntensity"))
                RenderSettings.reflectionIntensity = System.Convert.ToSingle(rs["reflectionIntensity"]);

            if (rs.ContainsKey("skyboxMaterial"))
            {
                var skyPath = rs["skyboxMaterial"].ToString();
                if (!string.IsNullOrWhiteSpace(skyPath))
                {
                    var skyMat = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
                    if (skyMat != null) RenderSettings.skybox = skyMat;
                }
            }
        }

        private static void ApplyColorFromDict(Dictionary<string, object> dict, string key, Action<Color> setter)
        {
            if (!dict.ContainsKey(key)) return;
            var colorList = dict[key] as List<object>;
            if (colorList == null || colorList.Count < 3) return;
            float r = System.Convert.ToSingle(colorList[0]);
            float g = System.Convert.ToSingle(colorList[1]);
            float b = System.Convert.ToSingle(colorList[2]);
            float a = colorList.Count >= 4 ? System.Convert.ToSingle(colorList[3]) : 1f;
            setter(new Color(r, g, b, a));
        }

        private static void ApplyCameraFromDict(Dictionary<string, object> camDict)
        {
            var camera = Camera.main;
            if (camera == null)
            {
                var allCams = Resources.FindObjectsOfTypeAll<Camera>()
                    .Where(c => c != null && c.gameObject.scene.IsValid() && c.gameObject.scene.isLoaded)
                    .ToList();
                camera = allCams.FirstOrDefault();
            }
            if (camera == null) return;

            Undo.RecordObject(camera, "Apply Camera Preset");

            if (camDict.ContainsKey("clearFlags") && Enum.TryParse<CameraClearFlags>(camDict["clearFlags"].ToString(), true, out var cf))
                camera.clearFlags = cf;
            if (camDict.ContainsKey("fieldOfView"))
                camera.fieldOfView = System.Convert.ToSingle(camDict["fieldOfView"]);
            if (camDict.ContainsKey("nearClipPlane"))
                camera.nearClipPlane = System.Convert.ToSingle(camDict["nearClipPlane"]);
            if (camDict.ContainsKey("farClipPlane"))
                camera.farClipPlane = System.Convert.ToSingle(camDict["farClipPlane"]);
            if (camDict.ContainsKey("allowHDR"))
                camera.allowHDR = System.Convert.ToBoolean(camDict["allowHDR"]);

            ApplyColorFromDict(camDict, "backgroundColor", c => camera.backgroundColor = c);

            var urpData = camera.GetComponent<UniversalAdditionalCameraData>();
            if (urpData != null)
            {
                Undo.RecordObject(urpData, "Apply Camera Preset URP");
                if (camDict.ContainsKey("renderPostProcessing"))
                    urpData.renderPostProcessing = System.Convert.ToBoolean(camDict["renderPostProcessing"]);
                if (camDict.ContainsKey("antialiasing") && Enum.TryParse<AntialiasingMode>(camDict["antialiasing"].ToString(), true, out var aa))
                    urpData.antialiasing = aa;
                if (camDict.ContainsKey("stopNaN"))
                    urpData.stopNaN = System.Convert.ToBoolean(camDict["stopNaN"]);
                if (camDict.ContainsKey("dithering"))
                    urpData.dithering = System.Convert.ToBoolean(camDict["dithering"]);
                if (camDict.ContainsKey("renderShadows"))
                    urpData.renderShadows = System.Convert.ToBoolean(camDict["renderShadows"]);
                EditorUtility.SetDirty(urpData);
            }

            EditorUtility.SetDirty(camera);
        }

        private static void ApplyLightFromDict(Light light, Dictionary<string, object> dict)
        {
            Undo.RecordObject(light, "Update Light from Preset");
            Undo.RecordObject(light.transform, "Update Light Transform from Preset");

            if (dict.ContainsKey("type") && Enum.TryParse<LightType>(dict["type"].ToString(), true, out var lt))
                light.type = lt;
            ApplyColorFromDict(dict, "color", c => light.color = c);
            if (dict.ContainsKey("intensity"))
                light.intensity = System.Convert.ToSingle(dict["intensity"]);
            if (dict.ContainsKey("range"))
                light.range = System.Convert.ToSingle(dict["range"]);
            if (dict.ContainsKey("spotAngle"))
                light.spotAngle = System.Convert.ToSingle(dict["spotAngle"]);
            if (dict.ContainsKey("shadows") && Enum.TryParse<LightShadows>(dict["shadows"].ToString(), true, out var sh))
                light.shadows = sh;

            if (dict.ContainsKey("position"))
            {
                var pos = dict["position"] as List<object>;
                if (pos != null && pos.Count >= 3)
                    light.transform.position = new Vector3(
                        System.Convert.ToSingle(pos[0]),
                        System.Convert.ToSingle(pos[1]),
                        System.Convert.ToSingle(pos[2]));
            }
            if (dict.ContainsKey("rotation"))
            {
                var rot = dict["rotation"] as List<object>;
                if (rot != null && rot.Count >= 3)
                    light.transform.eulerAngles = new Vector3(
                        System.Convert.ToSingle(rot[0]),
                        System.Convert.ToSingle(rot[1]),
                        System.Convert.ToSingle(rot[2]));
            }

            EditorUtility.SetDirty(light);
        }

        private static void SpawnLightFromDict(Dictionary<string, object> dict)
        {
            var name = dict.ContainsKey("name") ? dict["name"].ToString() : "Light";
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create Light from Preset");
            var light = go.AddComponent<Light>();
            ApplyLightFromDict(light, dict);
        }
    }
}
