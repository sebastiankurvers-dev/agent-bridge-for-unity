using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        // ==================== Renderer Audit ====================

        [BridgeRoute("POST", "/renderer/audit", Category = "rendering", Description = "Batch audit renderer health", ReadOnly = true, TimeoutDefault = 10000)]
        public static string AuditRenderers(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<AuditRenderersRequest>(
                    string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) ?? new AuditRenderersRequest();

                bool includeInactive = request.includeInactive != 0;
                bool checkCameraCulling = request.checkCameraCulling != 0;
                int maxObjects = Mathf.Clamp(request.maxObjects <= 0 ? 500 : request.maxObjects, 10, 5000);
                string nameContains = request.nameContains ?? "";
                string tagFilter = request.tag ?? "";
                string layerFilter = request.layer ?? "";

                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded)
                    return JsonError("No active loaded scene");

                Camera mainCam = Camera.main;
                int cameraCullingMask = mainCam != null ? mainCam.cullingMask : ~0;

                bool isURP = GraphicsSettings.currentRenderPipeline != null;

                var allRenderers = new List<Renderer>();
                if (request.rootInstanceId != 0)
                {
                    var rootGo = EditorUtility.EntityIdToObject(request.rootInstanceId) as GameObject;
                    if (rootGo == null)
                        return JsonError("Root GameObject not found for instanceId " + request.rootInstanceId);
                    foreach (var rend in rootGo.GetComponentsInChildren<Renderer>(includeInactive))
                    {
                        if (rend == null || rend.gameObject == null) continue;
                        allRenderers.Add(rend);
                    }
                }
                else
                {
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        foreach (var rend in root.GetComponentsInChildren<Renderer>(includeInactive))
                        {
                            if (rend == null || rend.gameObject == null) continue;
                            allRenderers.Add(rend);
                        }
                    }
                }

                // Apply filters
                if (!string.IsNullOrWhiteSpace(nameContains))
                {
                    allRenderers = allRenderers.Where(r =>
                        r.gameObject.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                }

                if (!string.IsNullOrWhiteSpace(tagFilter))
                {
                    allRenderers = allRenderers.Where(r =>
                    {
                        try { return string.Equals(r.gameObject.tag, tagFilter, StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    }).ToList();
                }

                if (!string.IsNullOrWhiteSpace(layerFilter))
                {
                    int layerIndex = LayerMask.NameToLayer(layerFilter);
                    if (layerIndex >= 0)
                        allRenderers = allRenderers.Where(r => r.gameObject.layer == layerIndex).ToList();
                }

                int totalScanned = allRenderers.Count;
                if (allRenderers.Count > maxObjects)
                    allRenderers = allRenderers.Take(maxObjects).ToList();

                int renderersEnabled = 0;
                int renderersDisabled = 0;
                int renderersWithNullMaterial = 0;
                int renderersWithInvalidShader = 0;
                int renderersWithBuiltinShaderInURP = 0;
                int renderersLayerCulled = 0;
                int renderersNoMesh = 0;

                var issues = new List<Dictionary<string, object>>();

                foreach (var rend in allRenderers)
                {
                    var go = rend.gameObject;
                    if (rend.enabled && go.activeInHierarchy)
                        renderersEnabled++;
                    else
                        renderersDisabled++;

                    var sharedMats = rend.sharedMaterials;
                    bool hasNullMat = false;
                    bool hasInvalidShader = false;
                    bool hasBuiltinShader = false;

                    for (int mi = 0; mi < sharedMats.Length; mi++)
                    {
                        if (sharedMats[mi] == null)
                        {
                            hasNullMat = true;
                            issues.Add(new Dictionary<string, object>
                            {
                                { "severity", "error" },
                                { "instanceId", go.GetInstanceID() },
                                { "name", go.name },
                                { "path", GetHierarchyPath(go.transform) },
                                { "issue", $"Null material at slot {mi}" },
                                { "rendererType", rend.GetType().Name }
                            });
                        }
                        else
                        {
                            var mat = sharedMats[mi];
                            if (mat.shader == null || mat.shader.name == "Hidden/InternalErrorShader")
                            {
                                hasInvalidShader = true;
                                issues.Add(new Dictionary<string, object>
                                {
                                    { "severity", "error" },
                                    { "instanceId", go.GetInstanceID() },
                                    { "name", go.name },
                                    { "path", GetHierarchyPath(go.transform) },
                                    { "issue", $"Invalid/error shader on material '{mat.name}' at slot {mi}" },
                                    { "rendererType", rend.GetType().Name },
                                    { "materialName", mat.name }
                                });
                            }

                            if (isURP && mat.shader != null)
                            {
                                var sn = mat.shader.name;
                                if (sn.StartsWith("Standard", StringComparison.Ordinal)
                                    || sn.StartsWith("Legacy Shaders/", StringComparison.Ordinal)
                                    || sn.StartsWith("Mobile/", StringComparison.Ordinal))
                                {
                                    hasBuiltinShader = true;
                                    issues.Add(new Dictionary<string, object>
                                    {
                                        { "severity", "warning" },
                                        { "instanceId", go.GetInstanceID() },
                                        { "name", go.name },
                                        { "path", GetHierarchyPath(go.transform) },
                                        { "issue", $"Built-in shader '{sn}' on material '{mat.name}' in URP project" },
                                        { "rendererType", rend.GetType().Name },
                                        { "materialName", mat.name },
                                        { "shaderName", sn }
                                    });
                                }
                            }
                        }
                    }

                    if (hasNullMat) renderersWithNullMaterial++;
                    if (hasInvalidShader) renderersWithInvalidShader++;
                    if (hasBuiltinShader) renderersWithBuiltinShaderInURP++;

                    // Layer culling check
                    if (checkCameraCulling && go.activeInHierarchy && rend.enabled && mainCam != null)
                    {
                        int objLayerMask = 1 << go.layer;
                        if ((cameraCullingMask & objLayerMask) == 0)
                        {
                            renderersLayerCulled++;
                            issues.Add(new Dictionary<string, object>
                            {
                                { "severity", "warning" },
                                { "instanceId", go.GetInstanceID() },
                                { "name", go.name },
                                { "path", GetHierarchyPath(go.transform) },
                                { "issue", $"Layer '{LayerMask.LayerToName(go.layer)}' not in camera culling mask" },
                                { "rendererType", rend.GetType().Name }
                            });
                        }
                    }

                    // Mesh check
                    if (rend is MeshRenderer mr)
                    {
                        var mf = go.GetComponent<MeshFilter>();
                        if (mf == null || mf.sharedMesh == null)
                        {
                            renderersNoMesh++;
                            issues.Add(new Dictionary<string, object>
                            {
                                { "severity", "error" },
                                { "instanceId", go.GetInstanceID() },
                                { "name", go.name },
                                { "path", GetHierarchyPath(go.transform) },
                                { "issue", mf == null ? "MeshRenderer without MeshFilter" : "MeshFilter with no mesh assigned" },
                                { "rendererType", "MeshRenderer" }
                            });
                        }
                    }
                    else if (rend is SkinnedMeshRenderer smr)
                    {
                        if (smr.sharedMesh == null)
                        {
                            renderersNoMesh++;
                            issues.Add(new Dictionary<string, object>
                            {
                                { "severity", "error" },
                                { "instanceId", go.GetInstanceID() },
                                { "name", go.name },
                                { "path", GetHierarchyPath(go.transform) },
                                { "issue", "SkinnedMeshRenderer with no mesh" },
                                { "rendererType", "SkinnedMeshRenderer" }
                            });
                        }
                    }
                }

                // Sort by severity (errors first)
                issues = issues
                    .OrderByDescending(i => SeverityRank((string)i["severity"]))
                    .ThenBy(i => (string)i["name"], StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int errorCount = issues.Count(i => (string)i["severity"] == "error");
                int warningCount = issues.Count(i => (string)i["severity"] == "warning");
                bool healthy = errorCount == 0;

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "summary", new Dictionary<string, object>
                        {
                            { "totalRenderersScanned", totalScanned },
                            { "renderersInResult", allRenderers.Count },
                            { "renderersEnabled", renderersEnabled },
                            { "renderersDisabled", renderersDisabled },
                            { "renderersWithNullMaterial", renderersWithNullMaterial },
                            { "renderersWithInvalidShader", renderersWithInvalidShader },
                            { "renderersWithBuiltinShaderInURP", renderersWithBuiltinShaderInURP },
                            { "renderersLayerCulled", renderersLayerCulled },
                            { "renderersNoMesh", renderersNoMesh },
                            { "errorCount", errorCount },
                            { "warningCount", warningCount },
                            { "healthy", healthy },
                            { "isURP", isURP },
                            { "hasCameraReference", mainCam != null },
                            { "truncated", totalScanned > maxObjects }
                        }
                    },
                    { "issues", issues.Cast<object>().ToList() }
                };

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        // ==================== Hierarchy Renderers ====================

        [BridgeRoute("POST", "/renderer/hierarchy", Category = "rendering", Description = "List renderers on GO + children", ReadOnly = true, TimeoutDefault = 10000)]
        public static string GetHierarchyRenderers(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<GetHierarchyRenderersRequest>(jsonData);
                if (request == null || request.instanceId == 0)
                    return JsonError("instanceId is required");

                var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
                if (go == null)
                    return JsonError("GameObject not found for instanceId " + request.instanceId);

                bool includeInactive = request.includeInactive != 0;
                var renderers = go.GetComponentsInChildren<Renderer>(includeInactive);

                var rendererList = new List<object>();
                int activeCount = 0;

                foreach (var rend in renderers)
                {
                    if (rend == null || rend.gameObject == null) continue;

                    bool isActive = rend.gameObject.activeInHierarchy && rend.enabled;
                    if (isActive) activeCount++;

                    var entry = new Dictionary<string, object>
                    {
                        { "gameObjectName", rend.gameObject.name },
                        { "instanceId", rend.gameObject.GetInstanceID() },
                        { "active", rend.gameObject.activeInHierarchy },
                        { "enabled", rend.enabled },
                        { "rendererType", rend.GetType().Name }
                    };

                    // Material info (use sharedMaterial in edit mode, material in play)
                    var mat = Application.isPlaying ? rend.material : rend.sharedMaterial;
                    if (mat == null)
                    {
                        entry["materialName"] = "(null)";
                        entry["shaderName"] = "None";
                        entry["emissionEnabled"] = false;
                    }
                    else
                    {
                        entry["materialName"] = mat.name;
                        entry["shaderName"] = mat.shader != null ? mat.shader.name : "None";

                        bool emissionOn = mat.shaderKeywords != null
                            && Array.Exists(mat.shaderKeywords, k => k == "_EMISSION");
                        entry["emissionEnabled"] = emissionOn;

                        if (mat.HasProperty("_EmissionColor"))
                        {
                            var ec = mat.GetColor("_EmissionColor");
                            entry["emissionColor"] = new Dictionary<string, object>
                            {
                                { "r", Math.Round(ec.r, 4) },
                                { "g", Math.Round(ec.g, 4) },
                                { "b", Math.Round(ec.b, 4) },
                                { "a", Math.Round(ec.a, 4) }
                            };
                        }

                        if (mat.HasProperty("_BaseColor"))
                        {
                            var bc = mat.GetColor("_BaseColor");
                            entry["baseColor"] = new Dictionary<string, object>
                            {
                                { "r", Math.Round(bc.r, 4) },
                                { "g", Math.Round(bc.g, 4) },
                                { "b", Math.Round(bc.b, 4) },
                                { "a", Math.Round(bc.a, 4) }
                            };
                        }
                        else if (mat.HasProperty("_Color"))
                        {
                            var bc = mat.GetColor("_Color");
                            entry["baseColor"] = new Dictionary<string, object>
                            {
                                { "r", Math.Round(bc.r, 4) },
                                { "g", Math.Round(bc.g, 4) },
                                { "b", Math.Round(bc.b, 4) },
                                { "a", Math.Round(bc.a, 4) }
                            };
                        }
                    }

                    rendererList.Add(entry);
                }

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "rootName", go.name },
                    { "rootInstanceId", go.GetInstanceID() },
                    { "totalRenderers", renderers.Length },
                    { "activeRenderers", activeCount },
                    { "renderers", rendererList }
                };

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        // ==================== Scene Lighting Audit ====================

        [BridgeRoute("POST", "/lighting/audit", Category = "rendering", Description = "Audit scene lighting adequacy", ReadOnly = true, TimeoutDefault = 10000)]
        public static string AuditSceneLighting(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<AuditSceneLightingRequest>(
                    string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) ?? new AuditSceneLightingRequest();

                bool includeRecommendations = request.includeRecommendations != 0;

                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded)
                    return JsonError("No active loaded scene");

                // Gather all lights in scene
                var allLights = new List<Light>();
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var light in root.GetComponentsInChildren<Light>(true))
                    {
                        if (light != null)
                            allLights.Add(light);
                    }
                }

                var activeLights = allLights.Where(l => l.enabled && l.gameObject.activeInHierarchy).ToList();

                int directionalCount = activeLights.Count(l => l.type == LightType.Directional);
                int pointCount = activeLights.Count(l => l.type == LightType.Point);
                int spotCount = activeLights.Count(l => l.type == LightType.Spot);
                int areaCount = activeLights.Count(l => l.type == LightType.Rectangle || l.type == LightType.Disc);

                // Light details
                var lightDetails = new List<object>();
                foreach (var light in activeLights)
                {
                    var detail = new Dictionary<string, object>
                    {
                        { "name", light.gameObject.name },
                        { "instanceId", light.gameObject.GetInstanceID() },
                        { "type", light.type.ToString() },
                        { "intensity", light.intensity },
                        { "color", "#" + ColorUtility.ToHtmlStringRGB(light.color) },
                        { "colorLuminance", light.color.grayscale },
                        { "shadows", light.shadows.ToString() },
                        { "enabled", light.enabled },
                        { "objectActive", light.gameObject.activeInHierarchy }
                    };

                    if (light.type == LightType.Point || light.type == LightType.Spot)
                        detail["range"] = light.range;
                    if (light.type == LightType.Spot)
                        detail["spotAngle"] = light.spotAngle;

                    lightDetails.Add(detail);
                }

                // Ambient lighting analysis
                var ambientMode = RenderSettings.ambientMode;
                float ambientLuminance = 0f;
                switch (ambientMode)
                {
                    case AmbientMode.Flat:
                        ambientLuminance = RenderSettings.ambientLight.grayscale;
                        break;
                    case AmbientMode.Trilight:
                        ambientLuminance = (RenderSettings.ambientSkyColor.grayscale
                            + RenderSettings.ambientEquatorColor.grayscale
                            + RenderSettings.ambientGroundColor.grayscale) / 3f;
                        break;
                    case AmbientMode.Skybox:
                        ambientLuminance = RenderSettings.ambientIntensity;
                        break;
                }

                // Estimate overall scene illumination from directional lights + ambient
                float estimatedDirectionalLuminance = 0f;
                foreach (var light in activeLights.Where(l => l.type == LightType.Directional))
                {
                    estimatedDirectionalLuminance += light.intensity * light.color.grayscale;
                }
                float estimatedSceneLuminance = ambientLuminance + estimatedDirectionalLuminance;

                // Volume post-processing analysis
                float postExposure = 0f;
                float bloomIntensity = 0f;
                bool hasVolume = false;
                var volumes = new List<Volume>();
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var vol in root.GetComponentsInChildren<Volume>(true))
                    {
                        if (vol != null && vol.enabled && vol.gameObject.activeInHierarchy)
                            volumes.Add(vol);
                    }
                }

                var volumeDetailsList = new List<object>();

                if (volumes.Count > 0)
                {
                    hasVolume = true;
                    foreach (var vol in volumes)
                    {
                        var volDetail = new Dictionary<string, object>
                        {
                            { "name", vol.gameObject.name },
                            { "instanceId", vol.gameObject.GetInstanceID() },
                            { "isGlobal", vol.isGlobal },
                            { "weight", vol.weight },
                            { "priority", vol.priority },
                            { "hasProfile", vol.profile != null }
                        };

                        if (vol.profile != null)
                        {
                            var profilePath = UnityEditor.AssetDatabase.GetAssetPath(vol.profile);
                            volDetail["profilePath"] = string.IsNullOrEmpty(profilePath) ? "(inline)" : profilePath;
                            volDetail["profileName"] = vol.profile.name;

                            bool hasBloom = vol.profile.TryGet<Bloom>(out var bloomComp);
                            volDetail["hasBloom"] = hasBloom;
                            if (hasBloom)
                            {
                                volDetail["bloomActive"] = bloomComp.active;
                                volDetail["bloomIntensityOverridden"] = bloomComp.intensity.overrideState;
                                volDetail["bloomIntensityValue"] = bloomComp.intensity.value;
                                volDetail["bloomThresholdOverridden"] = bloomComp.threshold.overrideState;
                                volDetail["bloomThresholdValue"] = bloomComp.threshold.value;
                            }

                            bool hasColorAdj = vol.profile.TryGet<ColorAdjustments>(out var caComp);
                            volDetail["hasColorAdjustments"] = hasColorAdj;
                            if (hasColorAdj)
                            {
                                volDetail["colorAdjActive"] = caComp.active;
                                volDetail["postExposureValue"] = caComp.postExposure.value;
                            }
                        }

                        volumeDetailsList.Add(volDetail);

                        if (vol.profile == null) continue;
                        if (vol.profile.TryGet<ColorAdjustments>(out var colorAdj) && colorAdj.active)
                        {
                            postExposure += colorAdj.postExposure.value * vol.weight;
                        }
                        if (vol.profile.TryGet<Bloom>(out var bloom) && bloom.active)
                        {
                            bloomIntensity = Mathf.Max(bloomIntensity, bloom.intensity.value);
                        }
                    }
                }

                // Fallback: also check the VolumeManager global default profile
                // (covers shared profiles assigned in Project Settings that aren't on a scene Volume)
                if (bloomIntensity <= 0f)
                {
                    var defaultProfile = VolumeManager.instance.stack;
                    if (defaultProfile != null)
                    {
                        var globalBloom = defaultProfile.GetComponent<Bloom>();
                        if (globalBloom != null && globalBloom.active && globalBloom.intensity.overrideState)
                        {
                            bloomIntensity = globalBloom.intensity.value;
                        }
                    }
                }

                // Issues analysis
                var issues = new List<Dictionary<string, object>>();

                if (activeLights.Count == 0)
                {
                    issues.Add(new Dictionary<string, object>
                    {
                        { "severity", "warning" },
                        { "message", "No active lights in scene. Scene relies entirely on ambient/emissive lighting." }
                    });
                }

                if (estimatedSceneLuminance < 0.05f && activeLights.Count > 0)
                {
                    issues.Add(new Dictionary<string, object>
                    {
                        { "severity", "warning" },
                        { "message", $"Very low estimated scene luminance ({estimatedSceneLuminance:F3}). Non-emissive objects may appear black." }
                    });
                }

                if (ambientLuminance < 0.01f)
                {
                    issues.Add(new Dictionary<string, object>
                    {
                        { "severity", "warning" },
                        { "message", $"Ambient light luminance is very low ({ambientLuminance:F3}). Unlit areas will be completely black." }
                    });
                }

                if (postExposure < -2f)
                {
                    issues.Add(new Dictionary<string, object>
                    {
                        { "severity", "warning" },
                        { "message", $"Post exposure is very negative ({postExposure:F2}). Scene will appear very dark." }
                    });
                }

                if (bloomIntensity > 2f)
                {
                    issues.Add(new Dictionary<string, object>
                    {
                        { "severity", "info" },
                        { "message", $"Bloom intensity is high ({bloomIntensity:F2}). Emissive objects may bleed into dark regions." }
                    });
                }

                foreach (var light in activeLights)
                {
                    if (light.intensity < 0.01f)
                    {
                        issues.Add(new Dictionary<string, object>
                        {
                            { "severity", "info" },
                            { "message", $"Light '{light.gameObject.name}' has near-zero intensity ({light.intensity:F4})." },
                            { "instanceId", light.gameObject.GetInstanceID() }
                        });
                    }
                }

                // Recommendations
                var recommendations = new List<string>();
                if (includeRecommendations)
                {
                    if (estimatedSceneLuminance < 0.1f && ambientLuminance < 0.05f)
                    {
                        recommendations.Add("Increase ambient light or add a directional light so non-emissive objects are visible.");
                    }

                    if (activeLights.Count > 0 && directionalCount == 0)
                    {
                        recommendations.Add("Consider adding a directional light for consistent base illumination.");
                    }

                    if (renderersWithNullMaterialCount(scene) > 0)
                    {
                        recommendations.Add("Run unity_audit_renderers to find objects with missing materials.");
                    }
                }

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "summary", new Dictionary<string, object>
                        {
                            { "sceneName", scene.name },
                            { "totalLightsInScene", allLights.Count },
                            { "activeLights", activeLights.Count },
                            { "directionalLights", directionalCount },
                            { "pointLights", pointCount },
                            { "spotLights", spotCount },
                            { "areaLights", areaCount },
                            { "ambientMode", ambientMode.ToString() },
                            { "ambientLuminance", Mathf.Round(ambientLuminance * 1000f) / 1000f },
                            { "estimatedDirectionalLuminance", Mathf.Round(estimatedDirectionalLuminance * 1000f) / 1000f },
                            { "estimatedSceneLuminance", Mathf.Round(estimatedSceneLuminance * 1000f) / 1000f },
                            { "postExposure", Mathf.Round(postExposure * 100f) / 100f },
                            { "bloomIntensity", Mathf.Round(bloomIntensity * 100f) / 100f },
                            { "hasActiveVolume", hasVolume },
                            { "issueCount", issues.Count },
                            { "healthy", issues.All(i => (string)i["severity"] != "error") }
                        }
                    },
                    { "lights", lightDetails },
                    { "volumeDetails", volumeDetailsList },
                    { "issues", issues.Cast<object>().ToList() }
                };

                if (includeRecommendations && recommendations.Count > 0)
                {
                    response["recommendations"] = recommendations.Cast<object>().ToList();
                }

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static int renderersWithNullMaterialCount(Scene scene)
        {
            int count = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var rend in root.GetComponentsInChildren<Renderer>(false))
                {
                    if (rend == null) continue;
                    foreach (var mat in rend.sharedMaterials)
                    {
                        if (mat == null) { count++; break; }
                    }
                }
            }
            return count;
        }
    }
}
