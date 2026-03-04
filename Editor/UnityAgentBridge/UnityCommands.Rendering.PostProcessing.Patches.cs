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
        // Volume Profile Apply-Patch Helpers & Type Conversion

        private static bool ApplyBloomPatch(Bloom bloom, Dictionary<string, object> patch)
        {
            bool changed = false;

            if (TryReadBoolField(patch, "active", out bool active) || TryReadBoolField(patch, "enabled", out active))
            {
                bloom.active = active;
                changed = true;
            }
            if (TryReadFloatField(patch, "threshold", out float threshold))
            {
                bloom.threshold.overrideState = true;
                bloom.threshold.value = threshold;
                changed = true;
            }
            if (TryReadFloatField(patch, "intensity", out float intensity))
            {
                bloom.intensity.overrideState = true;
                bloom.intensity.value = intensity;
                changed = true;
            }
            if (TryReadFloatField(patch, "scatter", out float scatter))
            {
                bloom.scatter.overrideState = true;
                bloom.scatter.value = scatter;
                changed = true;
            }
            if (TryReadFloatField(patch, "clamp", out float clamp))
            {
                bloom.clamp.overrideState = true;
                bloom.clamp.value = clamp;
                changed = true;
            }
            if (TryReadBoolField(patch, "highQualityFiltering", out bool hqFiltering))
            {
                bloom.highQualityFiltering.overrideState = true;
                bloom.highQualityFiltering.value = hqFiltering;
                changed = true;
            }
            if (patch.TryGetValue("tint", out var tintObj) && TryReadColor(tintObj, out var tint))
            {
                bloom.tint.overrideState = true;
                bloom.tint.value = tint;
                changed = true;
            }

            return changed;
        }

        private static bool ApplyColorAdjustmentsPatch(ColorAdjustments colorAdjustments, Dictionary<string, object> patch)
        {
            bool changed = false;

            if (TryReadBoolField(patch, "active", out bool active) || TryReadBoolField(patch, "enabled", out active))
            {
                colorAdjustments.active = active;
                changed = true;
            }
            if (TryReadFloatField(patch, "postExposure", out float postExposure))
            {
                colorAdjustments.postExposure.overrideState = true;
                colorAdjustments.postExposure.value = postExposure;
                changed = true;
            }
            if (TryReadFloatField(patch, "contrast", out float contrast))
            {
                colorAdjustments.contrast.overrideState = true;
                colorAdjustments.contrast.value = contrast;
                changed = true;
            }
            if (patch.TryGetValue("colorFilter", out var colorFilterObj) && TryReadColor(colorFilterObj, out var colorFilter))
            {
                colorAdjustments.colorFilter.overrideState = true;
                colorAdjustments.colorFilter.value = colorFilter;
                changed = true;
            }
            if (TryReadFloatField(patch, "hueShift", out float hueShift))
            {
                colorAdjustments.hueShift.overrideState = true;
                colorAdjustments.hueShift.value = hueShift;
                changed = true;
            }
            if (TryReadFloatField(patch, "saturation", out float saturation))
            {
                colorAdjustments.saturation.overrideState = true;
                colorAdjustments.saturation.value = saturation;
                changed = true;
            }

            return changed;
        }

        private static bool ApplyTonemappingPatch(Tonemapping tonemapping, Dictionary<string, object> patch)
        {
            bool changed = false;

            if (TryReadBoolField(patch, "active", out bool active) || TryReadBoolField(patch, "enabled", out active))
            {
                tonemapping.active = active;
                changed = true;
            }
            if (patch.TryGetValue("mode", out var modeObj)
                && modeObj != null
                && Enum.TryParse<TonemappingMode>(modeObj.ToString(), true, out var mode))
            {
                tonemapping.mode.overrideState = true;
                tonemapping.mode.value = mode;
                changed = true;
            }

            return changed;
        }

        private static bool ApplyVignettePatch(Vignette vignette, Dictionary<string, object> patch)
        {
            bool changed = false;

            if (TryReadBoolField(patch, "active", out bool active) || TryReadBoolField(patch, "enabled", out active))
            {
                vignette.active = active;
                changed = true;
            }
            if (TryReadFloatField(patch, "intensity", out float intensity))
            {
                vignette.intensity.overrideState = true;
                vignette.intensity.value = intensity;
                changed = true;
            }
            if (TryReadFloatField(patch, "smoothness", out float smoothness))
            {
                vignette.smoothness.overrideState = true;
                vignette.smoothness.value = smoothness;
                changed = true;
            }
            if (TryReadBoolField(patch, "rounded", out bool rounded))
            {
                vignette.rounded.overrideState = true;
                vignette.rounded.value = rounded;
                changed = true;
            }
            if (patch.TryGetValue("color", out var colorObj) && TryReadColor(colorObj, out var color))
            {
                vignette.color.overrideState = true;
                vignette.color.value = color;
                changed = true;
            }
            if (patch.TryGetValue("center", out var centerObj) && TryReadVector(centerObj, 2, out var center))
            {
                vignette.center.overrideState = true;
                vignette.center.value = new Vector2(center[0], center[1]);
                changed = true;
            }

            return changed;
        }

        private static bool ApplyLiftGammaGainPatch(LiftGammaGain liftGammaGain, Dictionary<string, object> patch)
        {
            bool changed = false;

            if (TryReadBoolField(patch, "active", out bool active) || TryReadBoolField(patch, "enabled", out active))
            {
                liftGammaGain.active = active;
                changed = true;
            }

            if (patch.TryGetValue("lift", out var liftObj) && TryReadColor(liftObj, out var lift))
            {
                liftGammaGain.lift.overrideState = true;
                liftGammaGain.lift.value = new Vector4(lift.r, lift.g, lift.b, lift.a);
                changed = true;
            }

            if (patch.TryGetValue("gamma", out var gammaObj) && TryReadColor(gammaObj, out var gamma))
            {
                liftGammaGain.gamma.overrideState = true;
                liftGammaGain.gamma.value = new Vector4(gamma.r, gamma.g, gamma.b, gamma.a);
                changed = true;
            }

            if (patch.TryGetValue("gain", out var gainObj) && TryReadColor(gainObj, out var gain))
            {
                liftGammaGain.gain.overrideState = true;
                liftGammaGain.gain.value = new Vector4(gain.r, gain.g, gain.b, gain.a);
                changed = true;
            }

            return changed;
        }

        private static bool TryApplyScreenSpaceReflectionPatch(VolumeProfile profile, Dictionary<string, object> patch)
        {
            var ssrComponent = profile.components?.FirstOrDefault(c => c != null &&
                string.Equals(c.GetType().Name, "ScreenSpaceReflection", StringComparison.OrdinalIgnoreCase));
            if (ssrComponent == null)
            {
                // SSR is not available in all URP package variants.
                return false;
            }

            bool changed = false;
            if (TryReadBoolField(patch, "active", out bool active) || TryReadBoolField(patch, "enabled", out active))
            {
                ssrComponent.active = active;
                changed = true;
            }

            changed |= TrySetVolumeParameter(ssrComponent, "quality", ReadString(patch, "quality"));
            changed |= TrySetVolumeParameterFromNumber(ssrComponent, patch, "maxRaySteps");
            changed |= TrySetVolumeParameterFromNumber(ssrComponent, patch, "objectThickness");
            changed |= TrySetVolumeParameterFromNumber(ssrComponent, patch, "minSmoothness");
            changed |= TrySetVolumeParameterFromNumber(ssrComponent, patch, "smoothnessFadeStart");
            changed |= TrySetVolumeParameterFromNumber(ssrComponent, patch, "accumulationFactor");
            return changed;
        }

        private static Dictionary<string, object> TryBuildScreenSpaceReflectionSnapshot(VolumeProfile profile)
        {
            var ssrComponent = profile.components?.FirstOrDefault(c => c != null &&
                string.Equals(c.GetType().Name, "ScreenSpaceReflection", StringComparison.OrdinalIgnoreCase));
            if (ssrComponent == null) return null;

            var result = new Dictionary<string, object>
            {
                { "active", ssrComponent.active }
            };

            TryReadVolumeParameterToSnapshot(ssrComponent, "quality", result);
            TryReadVolumeParameterToSnapshot(ssrComponent, "maxRaySteps", result);
            TryReadVolumeParameterToSnapshot(ssrComponent, "objectThickness", result);
            TryReadVolumeParameterToSnapshot(ssrComponent, "minSmoothness", result);
            TryReadVolumeParameterToSnapshot(ssrComponent, "smoothnessFadeStart", result);
            TryReadVolumeParameterToSnapshot(ssrComponent, "accumulationFactor", result);
            return result;
        }

        private static bool TrySetVolumeParameterFromNumber(VolumeComponent component, Dictionary<string, object> patch, string fieldName)
        {
            if (!TryReadFloatField(patch, fieldName, out var value)) return false;
            return TrySetVolumeParameter(component, fieldName, value);
        }

        private static bool TrySetVolumeParameter(VolumeComponent component, string fieldName, object rawValue)
        {
            var field = component.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (field == null) return false;
            var parameter = field.GetValue(component) as VolumeParameter;
            if (parameter == null) return false;

            var valueProperty = parameter.GetType().GetProperty("value", BindingFlags.Instance | BindingFlags.Public);
            if (valueProperty == null || !valueProperty.CanWrite) return false;
            var targetType = valueProperty.PropertyType;
            if (!TryConvertToType(rawValue, targetType, out var converted)) return false;

            var overrideStateProperty = typeof(VolumeParameter).GetProperty("overrideState", BindingFlags.Instance | BindingFlags.Public);
            overrideStateProperty?.SetValue(parameter, true);
            valueProperty.SetValue(parameter, converted);
            return true;
        }

        private static void TryReadVolumeParameterToSnapshot(VolumeComponent component, string fieldName, Dictionary<string, object> result)
        {
            var field = component.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (field == null) return;
            if (!(field.GetValue(component) is VolumeParameter parameter)) return;

            var valueProperty = parameter.GetType().GetProperty("value", BindingFlags.Instance | BindingFlags.Public);
            if (valueProperty == null || !valueProperty.CanRead) return;

            var value = valueProperty.GetValue(parameter);
            if (value is Vector2 v2) result[fieldName] = new List<object> { v2.x, v2.y };
            else if (value is Vector3 v3) result[fieldName] = new List<object> { v3.x, v3.y, v3.z };
            else if (value is Vector4 v4) result[fieldName] = new List<object> { v4.x, v4.y, v4.z, v4.w };
            else if (value is Color c) result[fieldName] = new List<object> { c.r, c.g, c.b, c.a };
            else result[fieldName] = value?.ToString() ?? string.Empty;
        }

        private static bool TryConvertToType(object rawValue, Type targetType, out object converted)
        {
            converted = null;
            if (rawValue == null) return false;

            if (targetType == typeof(float))
            {
                if (float.TryParse(rawValue.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                {
                    converted = f;
                    return true;
                }
                return false;
            }

            if (targetType == typeof(int))
            {
                if (int.TryParse(rawValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    converted = i;
                    return true;
                }
                return false;
            }

            if (targetType == typeof(bool))
            {
                if (bool.TryParse(rawValue.ToString(), out var b))
                {
                    converted = b;
                    return true;
                }

                if (int.TryParse(rawValue.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    converted = i != 0;
                    return true;
                }
                return false;
            }

            if (targetType == typeof(Vector2) && TryReadVector(rawValue, 2, out var v2))
            {
                converted = new Vector2(v2[0], v2[1]);
                return true;
            }

            if (targetType == typeof(Vector3) && TryReadVector(rawValue, 3, out var v3))
            {
                converted = new Vector3(v3[0], v3[1], v3[2]);
                return true;
            }

            if (targetType == typeof(Vector4) && TryReadVector(rawValue, 4, out var v4))
            {
                converted = new Vector4(v4[0], v4[1], v4[2], v4[3]);
                return true;
            }

            if (targetType == typeof(Color) && TryReadColor(rawValue, out var color))
            {
                converted = color;
                return true;
            }

            if (targetType.IsEnum)
            {
                var raw = rawValue.ToString();
                if (Enum.TryParse(targetType, raw, true, out var enumValue))
                {
                    converted = enumValue;
                    return true;
                }
                return false;
            }

            return false;
        }

        private static bool ApplyDepthOfFieldPatch(DepthOfField depthOfField, Dictionary<string, object> patch)
        {
            bool changed = false;

            if (TryReadBoolField(patch, "active", out bool active) || TryReadBoolField(patch, "enabled", out active))
            {
                depthOfField.active = active;
                changed = true;
            }

            if (patch.TryGetValue("mode", out var modeObj)
                && modeObj != null
                && Enum.TryParse<DepthOfFieldMode>(modeObj.ToString(), true, out var mode))
            {
                depthOfField.mode.overrideState = true;
                depthOfField.mode.value = mode;
                changed = true;
            }

            if (TryReadFloatField(patch, "gaussianStart", out float gaussianStart))
            {
                depthOfField.gaussianStart.overrideState = true;
                depthOfField.gaussianStart.value = gaussianStart;
                changed = true;
            }
            if (TryReadFloatField(patch, "gaussianEnd", out float gaussianEnd))
            {
                depthOfField.gaussianEnd.overrideState = true;
                depthOfField.gaussianEnd.value = gaussianEnd;
                changed = true;
            }
            if (TryReadFloatField(patch, "gaussianMaxRadius", out float gaussianMaxRadius))
            {
                depthOfField.gaussianMaxRadius.overrideState = true;
                depthOfField.gaussianMaxRadius.value = gaussianMaxRadius;
                changed = true;
            }
            if (TryReadBoolField(patch, "highQualitySampling", out bool highQualitySampling))
            {
                depthOfField.highQualitySampling.overrideState = true;
                depthOfField.highQualitySampling.value = highQualitySampling;
                changed = true;
            }
            if (TryReadFloatField(patch, "focusDistance", out float focusDistance))
            {
                depthOfField.focusDistance.overrideState = true;
                depthOfField.focusDistance.value = focusDistance;
                changed = true;
            }
            if (TryReadFloatField(patch, "aperture", out float aperture))
            {
                depthOfField.aperture.overrideState = true;
                depthOfField.aperture.value = aperture;
                changed = true;
            }
            if (TryReadFloatField(patch, "focalLength", out float focalLength))
            {
                depthOfField.focalLength.overrideState = true;
                depthOfField.focalLength.value = focalLength;
                changed = true;
            }
            if (TryReadInt(patch, "bladeCount", out int bladeCount))
            {
                depthOfField.bladeCount.overrideState = true;
                depthOfField.bladeCount.value = bladeCount;
                changed = true;
            }
            if (TryReadFloatField(patch, "bladeCurvature", out float bladeCurvature))
            {
                depthOfField.bladeCurvature.overrideState = true;
                depthOfField.bladeCurvature.value = bladeCurvature;
                changed = true;
            }
            if (TryReadFloatField(patch, "bladeRotation", out float bladeRotation))
            {
                depthOfField.bladeRotation.overrideState = true;
                depthOfField.bladeRotation.value = bladeRotation;
                changed = true;
            }

            return changed;
        }

        private static bool ApplyExposurePatch(ColorAdjustments colorAdjustments, Dictionary<string, object> patch)
        {
            if (!TryReadFloatField(patch, "postExposure", out float postExposure))
            {
                if (!TryReadFloatField(patch, "value", out postExposure))
                {
                    return false;
                }
            }

            colorAdjustments.postExposure.overrideState = true;
            colorAdjustments.postExposure.value = postExposure;
            return true;
        }

        private static T EnsureVolumeComponent<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (profile.TryGet<T>(out var existing))
            {
                return existing;
            }

            return profile.Add<T>(false);
        }
    }
}
