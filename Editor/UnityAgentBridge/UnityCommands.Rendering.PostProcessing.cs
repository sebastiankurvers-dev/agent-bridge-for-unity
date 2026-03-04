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
        // Volume Profiles, Post-Processing & Effects

        public static string GetVolumeProfile(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<GetVolumeProfileRequest>(jsonData ?? "{}");
                if (request == null) request = new GetVolumeProfileRequest();

                if (!TryResolveVolumeProfile(
                    request.profilePath,
                    request.volumeInstanceId,
                    createIfMissing: false,
                    out var volume,
                    out var profile,
                    out var resolvedProfilePath,
                    out var error))
                {
                    return JsonError(error);
                }

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "profilePath", resolvedProfilePath },
                    { "volumeInstanceId", volume != null ? volume.gameObject.GetInstanceID() : 0 },
                    { "volumeName", volume != null ? volume.gameObject.name : string.Empty },
                    { "isGlobalVolume", volume != null && volume.isGlobal },
                    { "priority", volume != null ? volume.priority : 0f },
                    { "overrides", BuildVolumeProfileSnapshot(profile) }
                };

                if (request.includeRenderHooks != 0)
                {
                    var renderSettings = MiniJSON.Json.Deserialize(GetRenderSettings()) as Dictionary<string, object>;
                    if (renderSettings != null)
                    {
                        result["renderSettings"] = renderSettings;
                    }
                }

                return JsonResult(result);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("PUT", "/volume/profile/overrides", Category = "rendering", Description = "Set URP volume profile overrides")]
        public static string SetVolumeProfileOverrides(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
                if (request == null)
                {
                    return JsonError("Failed to parse volume profile request");
                }

                string profilePath = ReadString(request, "profilePath");
                TryReadInt(request, "volumeInstanceId", out int volumeInstanceId);
                bool createIfMissing = ReadBool(request, "createIfMissing", true);
                bool saveAssets = ReadBool(request, "saveAssets", true);

                if (!TryResolveVolumeProfile(
                    profilePath,
                    volumeInstanceId,
                    createIfMissing,
                    out var volume,
                    out var profile,
                    out var resolvedProfilePath,
                    out var error))
                {
                    return JsonError(error);
                }

                var overrides = ExtractVolumeOverrides(request);
                if (overrides.Count == 0)
                {
                    return JsonError("No volume overrides provided");
                }

                Undo.RecordObject(profile, "Set Volume Profile Overrides");
                if (volume != null)
                {
                    Undo.RecordObject(volume, "Set Volume Profile Overrides");
                }

                int appliedSections = ApplyVolumeOverrides(profile, overrides);
                var warnings = new List<object>();
                bool requestedSsr = overrides.ContainsKey("ssr") || overrides.ContainsKey("screenspacereflection");
                if (requestedSsr && !profile.components.Any(c => c != null && string.Equals(c.GetType().Name, "ScreenSpaceReflection", StringComparison.OrdinalIgnoreCase)))
                {
                    warnings.Add("ScreenSpaceReflection override requested but ScreenSpaceReflection volume component is unavailable in this URP package.");
                }

                if (overrides.TryGetValue("rendersettings", out var rsObj) && rsObj is Dictionary<string, object> renderSettingsPatch)
                {
                    var rsJson = SetRenderSettings(MiniJSON.Json.Serialize(renderSettingsPatch));
                    var rsResult = MiniJSON.Json.Deserialize(rsJson) as Dictionary<string, object>;
                    if (rsResult == null || !ReadBool(rsResult, "success", false))
                    {
                        warnings.Add("Render settings patch failed");
                    }
                }

                EditorUtility.SetDirty(profile);
                if (volume != null) EditorUtility.SetDirty(volume);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                if (saveAssets)
                {
                    AssetDatabase.SaveAssets();
                }

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "profilePath", resolvedProfilePath },
                    { "volumeInstanceId", volume != null ? volume.gameObject.GetInstanceID() : 0 },
                    { "appliedSections", appliedSections },
                    { "warnings", warnings },
                    { "overrides", BuildVolumeProfileSnapshot(profile) }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static bool TryResolveVolumeProfile(
            string profilePath,
            int volumeInstanceId,
            bool createIfMissing,
            out Volume volume,
            out VolumeProfile profile,
            out string resolvedProfilePath,
            out string error)
        {
            volume = null;
            profile = null;
            resolvedProfilePath = null;
            error = null;

            if (!string.IsNullOrWhiteSpace(profilePath))
            {
                if (ValidateAssetPath(profilePath) == null)
                {
                    error = $"Invalid profile path: {profilePath}";
                    return false;
                }

                profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
                if (profile == null)
                {
                    error = $"Volume profile not found: {profilePath}";
                    return false;
                }

                resolvedProfilePath = profilePath;
                return true;
            }

            if (volumeInstanceId != 0)
            {
                var volumeObj = EditorUtility.EntityIdToObject(volumeInstanceId);
                if (volumeObj is GameObject volumeGo)
                {
                    volume = volumeGo.GetComponent<Volume>();
                }
                else
                {
                    volume = volumeObj as Volume;
                }
            }
            else
            {
                var volumes = Resources.FindObjectsOfTypeAll<Volume>()
                    .Where(v => v != null && v.gameObject.scene.IsValid() && v.gameObject.scene.isLoaded)
                    .OrderByDescending(v => v.isGlobal ? 1 : 0)
                    .ThenByDescending(v => v.priority)
                    .ToList();

                volume = volumes.FirstOrDefault();
            }

            if (volume == null)
            {
                error = "No Volume component found in loaded scenes";
                return false;
            }

            profile = volume.sharedProfile;
            if (profile == null && createIfMissing)
            {
                var folderPath = "Assets/Settings/Volumes";
                EnsureAssetFolder(folderPath);

                var safeName = MakeSafeFileName(volume.gameObject.name);
                resolvedProfilePath = $"{folderPath}/{safeName}_Profile.asset";
                int suffix = 1;
                while (AssetDatabase.LoadAssetAtPath<VolumeProfile>(resolvedProfilePath) != null)
                {
                    resolvedProfilePath = $"{folderPath}/{safeName}_Profile_{suffix}.asset";
                    suffix++;
                }

                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, resolvedProfilePath);
                volume.sharedProfile = profile;
                EditorUtility.SetDirty(volume);
            }

            if (profile == null)
            {
                error = "Target Volume has no shared profile";
                return false;
            }

            if (string.IsNullOrWhiteSpace(resolvedProfilePath))
            {
                resolvedProfilePath = AssetDatabase.GetAssetPath(profile);
            }
            return true;
        }

        private static int ApplyVolumeOverrides(VolumeProfile profile, Dictionary<string, object> overrides)
        {
            int appliedSections = 0;

            if (overrides.TryGetValue("bloom", out var bloomObj) && bloomObj is Dictionary<string, object> bloomPatch)
            {
                var bloom = EnsureVolumeComponent<Bloom>(profile);
                if (ApplyBloomPatch(bloom, bloomPatch)) appliedSections++;
            }

            if (overrides.TryGetValue("coloradjustments", out var colorObj) && colorObj is Dictionary<string, object> colorPatch)
            {
                var colorAdjustments = EnsureVolumeComponent<ColorAdjustments>(profile);
                if (ApplyColorAdjustmentsPatch(colorAdjustments, colorPatch)) appliedSections++;
            }

            if (overrides.TryGetValue("tonemapping", out var toneObj) && toneObj is Dictionary<string, object> tonePatch)
            {
                var tonemapping = EnsureVolumeComponent<Tonemapping>(profile);
                if (ApplyTonemappingPatch(tonemapping, tonePatch)) appliedSections++;
            }

            if (overrides.TryGetValue("vignette", out var vignetteObj) && vignetteObj is Dictionary<string, object> vignettePatch)
            {
                var vignette = EnsureVolumeComponent<Vignette>(profile);
                if (ApplyVignettePatch(vignette, vignettePatch)) appliedSections++;
            }

            if (overrides.TryGetValue("depthoffield", out var dofObj) && dofObj is Dictionary<string, object> dofPatch)
            {
                var depthOfField = EnsureVolumeComponent<DepthOfField>(profile);
                if (ApplyDepthOfFieldPatch(depthOfField, dofPatch)) appliedSections++;
            }

            // URP uses ColorAdjustments.postExposure as the practical exposure hook.
            if (overrides.TryGetValue("exposure", out var exposureObj) && exposureObj is Dictionary<string, object> exposurePatch)
            {
                var colorAdjustments = EnsureVolumeComponent<ColorAdjustments>(profile);
                if (ApplyExposurePatch(colorAdjustments, exposurePatch)) appliedSections++;
            }

            if (overrides.TryGetValue("liftgammagain", out var lggObj) && lggObj is Dictionary<string, object> lggPatch)
            {
                var liftGammaGain = EnsureVolumeComponent<LiftGammaGain>(profile);
                if (ApplyLiftGammaGainPatch(liftGammaGain, lggPatch)) appliedSections++;
            }

            if ((overrides.TryGetValue("ssr", out var ssrObj) || overrides.TryGetValue("screenspacereflection", out ssrObj))
                && ssrObj is Dictionary<string, object> ssrPatch)
            {
                if (TryApplyScreenSpaceReflectionPatch(profile, ssrPatch))
                {
                    appliedSections++;
                }
            }

            return appliedSections;
        }

        private static Dictionary<string, object> BuildVolumeProfileSnapshot(VolumeProfile profile)
        {
            var result = new Dictionary<string, object>();

            if (profile.TryGet<Bloom>(out var bloom))
            {
                result["bloom"] = new Dictionary<string, object>
                {
                    { "active", bloom.active },
                    { "threshold", bloom.threshold.value },
                    { "intensity", bloom.intensity.value },
                    { "scatter", bloom.scatter.value },
                    { "clamp", bloom.clamp.value },
                    { "tint", new List<object> { bloom.tint.value.r, bloom.tint.value.g, bloom.tint.value.b, bloom.tint.value.a } },
                    { "highQualityFiltering", bloom.highQualityFiltering.value }
                };
            }

            if (profile.TryGet<ColorAdjustments>(out var colorAdjustments))
            {
                result["colorAdjustments"] = new Dictionary<string, object>
                {
                    { "active", colorAdjustments.active },
                    { "postExposure", colorAdjustments.postExposure.value },
                    { "contrast", colorAdjustments.contrast.value },
                    { "colorFilter", new List<object> { colorAdjustments.colorFilter.value.r, colorAdjustments.colorFilter.value.g, colorAdjustments.colorFilter.value.b, colorAdjustments.colorFilter.value.a } },
                    { "hueShift", colorAdjustments.hueShift.value },
                    { "saturation", colorAdjustments.saturation.value }
                };
            }

            if (profile.TryGet<Tonemapping>(out var tonemapping))
            {
                result["tonemapping"] = new Dictionary<string, object>
                {
                    { "active", tonemapping.active },
                    { "mode", tonemapping.mode.value.ToString() }
                };
            }

            if (profile.TryGet<Vignette>(out var vignette))
            {
                result["vignette"] = new Dictionary<string, object>
                {
                    { "active", vignette.active },
                    { "color", new List<object> { vignette.color.value.r, vignette.color.value.g, vignette.color.value.b, vignette.color.value.a } },
                    { "center", new List<object> { vignette.center.value.x, vignette.center.value.y } },
                    { "intensity", vignette.intensity.value },
                    { "smoothness", vignette.smoothness.value },
                    { "rounded", vignette.rounded.value }
                };
            }

            if (profile.TryGet<DepthOfField>(out var depthOfField))
            {
                result["depthOfField"] = new Dictionary<string, object>
                {
                    { "active", depthOfField.active },
                    { "mode", depthOfField.mode.value.ToString() },
                    { "gaussianStart", depthOfField.gaussianStart.value },
                    { "gaussianEnd", depthOfField.gaussianEnd.value },
                    { "gaussianMaxRadius", depthOfField.gaussianMaxRadius.value },
                    { "highQualitySampling", depthOfField.highQualitySampling.value },
                    { "focusDistance", depthOfField.focusDistance.value },
                    { "aperture", depthOfField.aperture.value },
                    { "focalLength", depthOfField.focalLength.value },
                    { "bladeCount", depthOfField.bladeCount.value },
                    { "bladeCurvature", depthOfField.bladeCurvature.value },
                    { "bladeRotation", depthOfField.bladeRotation.value }
                };
            }

            if (profile.TryGet<LiftGammaGain>(out var liftGammaGain))
            {
                var lift = liftGammaGain.lift.value;
                var gamma = liftGammaGain.gamma.value;
                var gain = liftGammaGain.gain.value;
                result["liftGammaGain"] = new Dictionary<string, object>
                {
                    { "active", liftGammaGain.active },
                    { "lift", new List<object> { lift.x, lift.y, lift.z, lift.w } },
                    { "gamma", new List<object> { gamma.x, gamma.y, gamma.z, gamma.w } },
                    { "gain", new List<object> { gain.x, gain.y, gain.z, gain.w } }
                };
            }

            var ssrSnapshot = TryBuildScreenSpaceReflectionSnapshot(profile);
            if (ssrSnapshot != null)
            {
                result["ssr"] = ssrSnapshot;
            }

            return result;
        }

        private static Dictionary<string, object> ExtractVolumeOverrides(Dictionary<string, object> request)
        {
            if (request.TryGetValue("overrides", out var overridesObj) && overridesObj is Dictionary<string, object> nestedOverrides)
            {
                return NormalizeOverrideKeyMap(nestedOverrides);
            }

            var extracted = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var allowedTopLevelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "bloom",
                "colorAdjustments",
                "tonemapping",
                "vignette",
                "depthOfField",
                "exposure",
                "ssr",
                "screenSpaceReflection",
                "liftGammaGain",
                "renderSettings"
            };

            foreach (var kv in request)
            {
                if (allowedTopLevelKeys.Contains(kv.Key))
                {
                    extracted[kv.Key] = kv.Value;
                }
            }

            return NormalizeOverrideKeyMap(extracted);
        }

        private static Dictionary<string, object> NormalizeOverrideKeyMap(Dictionary<string, object> map)
        {
            var normalized = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in map)
            {
                normalized[kv.Key.Replace("_", string.Empty).ToLowerInvariant()] = kv.Value;
            }
            return normalized;
        }

    }
}
