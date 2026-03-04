using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        [BridgeRoute("POST", "/screenshot/parameter-sweep", Category = "screenshot", Description = "Sweep a parameter and capture screenshots per step", TimeoutDefault = 60000, TimeoutMin = 2000, TimeoutMax = 300000)]
        public static string ParameterSweep(string jsonData)
        {
            ISweepTarget target = null;
            float originalValue = 0f;

            try
            {
                var request = MiniJSON.Json.Deserialize(jsonData ?? "{}") as Dictionary<string, object>;
                if (request == null) return JsonError("Failed to parse parameter sweep request");

                var targetSpec = ReadString(request, "target");
                if (string.IsNullOrWhiteSpace(targetSpec))
                {
                    return JsonError("target is required (format: <type>:<path>:<property>)");
                }

                if (!TryReadFloatField(request, "min", out var minValue)) return JsonError("min is required");
                if (!TryReadFloatField(request, "max", out var maxValue)) return JsonError("max is required");
                if (!TryReadInt(request, "steps", out var steps)) steps = 5;
                steps = Mathf.Clamp(steps, 2, 24);

                var viewType = ReadString(request, "viewType");
                if (string.IsNullOrWhiteSpace(viewType)) viewType = "scene";
                var format = ReadString(request, "format");
                if (string.IsNullOrWhiteSpace(format)) format = "jpeg";
                var sourcePrefix = ReadString(request, "source");
                if (string.IsNullOrWhiteSpace(sourcePrefix)) sourcePrefix = "parameter-sweep";
                TryReadInt(request, "width", out var width);
                TryReadInt(request, "height", out var height);

                target = CreateSweepTarget(targetSpec, request);
                if (target == null)
                {
                    return JsonError($"Unsupported sweep target: {targetSpec}");
                }

                var initializeError = target.Initialize();
                if (!string.IsNullOrWhiteSpace(initializeError))
                {
                    return JsonError(initializeError);
                }

                originalValue = target.GetCurrentValue();
                var results = new List<object>();

                for (int i = 0; i < steps; i++)
                {
                    float t = steps == 1 ? 0f : (float)i / (steps - 1);
                    float value = Mathf.Lerp(minValue, maxValue, t);

                    target.SetValue(value);
                    target.Commit();

                    var screenshotJson = TakeScreenshot(
                        viewType,
                        includeBase64: false,
                        includeHandle: true,
                        source: $"{sourcePrefix}:{targetSpec}:{value.ToString("0.###", CultureInfo.InvariantCulture)}",
                        requestedWidth: width,
                        requestedHeight: height,
                        imageFormat: format);

                    var screenshotObj = MiniJSON.Json.Deserialize(screenshotJson) as Dictionary<string, object>;
                    if (screenshotObj == null || !ReadBool(screenshotObj, "success", false))
                    {
                        string screenshotError = screenshotObj != null ? ReadString(screenshotObj, "error") : "Unknown screenshot error";
                        return JsonError($"Screenshot failed at step {i}: {screenshotError}");
                    }

                    results.Add(new Dictionary<string, object>
                    {
                        { "step", i },
                        { "value", Math.Round(value, 4) },
                        { "imageHandle", ReadString(screenshotObj, "imageHandle") }
                    });
                }

                target.SetValue(originalValue);
                target.Commit();

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "target", targetSpec },
                    { "min", minValue },
                    { "max", maxValue },
                    { "steps", steps },
                    { "originalValue", Math.Round(originalValue, 4) },
                    { "results", results }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
            finally
            {
                try
                {
                    if (target != null)
                    {
                        target.SetValue(originalValue);
                        target.Commit();
                        target.Cleanup();
                    }
                }
                catch
                {
                    // best-effort restore
                }
            }
        }

        private static ISweepTarget CreateSweepTarget(string targetSpec, Dictionary<string, object> request)
        {
            var parts = targetSpec.Split(':');
            if (parts.Length < 2) return null;

            var targetType = parts[0].Trim().ToLowerInvariant();
            if (targetType == "material")
            {
                if (parts.Length < 3) return null;
                var materialPath = parts[1].Trim();
                var propertyName = string.Join(":", parts.Skip(2)).Trim();
                return new MaterialSweepTarget(materialPath, propertyName);
            }

            if (targetType == "volume")
            {
                if (parts.Length < 3) return null;
                var componentName = parts[1].Trim();
                var propertyName = string.Join(":", parts.Skip(2)).Trim();

                string profilePath = ReadString(request, "profilePath");
                int volumeInstanceId = 0;
                TryReadInt(request, "volumeInstanceId", out volumeInstanceId);
                return new VolumeSweepTarget(profilePath, volumeInstanceId, componentName, propertyName);
            }

            if (targetType == "rendersettings")
            {
                var propertyName = parts.Length == 2 ? parts[1].Trim() : string.Join(":", parts.Skip(2)).Trim();
                return new RenderSettingsSweepTarget(propertyName);
            }

            return null;
        }

        private interface ISweepTarget
        {
            string Initialize();
            float GetCurrentValue();
            void SetValue(float value);
            void Commit();
            void Cleanup();
        }

        private sealed class MaterialSweepTarget : ISweepTarget
        {
            private readonly string _materialPath;
            private readonly string _propertyName;
            private Material _material;

            public MaterialSweepTarget(string materialPath, string propertyName)
            {
                _materialPath = materialPath;
                _propertyName = propertyName;
            }

            public string Initialize()
            {
                if (string.IsNullOrWhiteSpace(_materialPath)) return "material path is required";
                if (string.IsNullOrWhiteSpace(_propertyName)) return "material property is required";

                _material = AssetDatabase.LoadAssetAtPath<Material>(_materialPath);
                if (_material == null) return $"Material not found: {_materialPath}";
                if (!_material.HasProperty(_propertyName)) return $"Material does not have property '{_propertyName}'";

                Undo.RecordObject(_material, "Parameter Sweep Material");
                return string.Empty;
            }

            public float GetCurrentValue() => _material.GetFloat(_propertyName);

            public void SetValue(float value)
            {
                _material.SetFloat(_propertyName, value);
            }

            public void Commit()
            {
                EditorUtility.SetDirty(_material);
                AssetDatabase.SaveAssets();
            }

            public void Cleanup() { }
        }

        private sealed class VolumeSweepTarget : ISweepTarget
        {
            private readonly string _profilePath;
            private readonly int _volumeInstanceId;
            private readonly string _componentName;
            private readonly string _propertyName;

            private VolumeProfile _profile;
            private VolumeComponent _component;
            private VolumeParameter _parameter;
            private System.Reflection.PropertyInfo _valueProperty;

            public VolumeSweepTarget(string profilePath, int volumeInstanceId, string componentName, string propertyName)
            {
                _profilePath = profilePath;
                _volumeInstanceId = volumeInstanceId;
                _componentName = componentName;
                _propertyName = propertyName;
            }

            public string Initialize()
            {
                if (!TryResolveVolumeProfile(
                    _profilePath,
                    _volumeInstanceId,
                    createIfMissing: false,
                    out _,
                    out _profile,
                    out _,
                    out var error))
                {
                    return error;
                }

                _component = _profile.components.FirstOrDefault(c => c != null &&
                    string.Equals(c.GetType().Name, _componentName, StringComparison.OrdinalIgnoreCase));
                if (_component == null)
                {
                    return $"Volume component '{_componentName}' not found in profile";
                }

                var field = _component.GetType().GetField(_propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (field == null) return $"Volume parameter '{_propertyName}' not found on component '{_componentName}'";

                _parameter = field.GetValue(_component) as VolumeParameter;
                if (_parameter == null) return $"Field '{_propertyName}' is not a VolumeParameter";

                _valueProperty = _parameter.GetType().GetProperty("value", BindingFlags.Instance | BindingFlags.Public);
                if (_valueProperty == null || _valueProperty.PropertyType != typeof(float) || !_valueProperty.CanRead || !_valueProperty.CanWrite)
                {
                    return $"Volume parameter '{_propertyName}' is not a float-compatible parameter";
                }

                Undo.RecordObject(_profile, "Parameter Sweep Volume");
                return string.Empty;
            }

            public float GetCurrentValue()
            {
                return Convert.ToSingle(_valueProperty.GetValue(_parameter), CultureInfo.InvariantCulture);
            }

            public void SetValue(float value)
            {
                _parameter.overrideState = true;
                _valueProperty.SetValue(_parameter, value);
            }

            public void Commit()
            {
                EditorUtility.SetDirty(_profile);
                AssetDatabase.SaveAssets();
            }

            public void Cleanup() { }
        }

        private sealed class RenderSettingsSweepTarget : ISweepTarget
        {
            private readonly string _propertyName;
            private System.Reflection.PropertyInfo _property;

            public RenderSettingsSweepTarget(string propertyName)
            {
                _propertyName = propertyName;
            }

            public string Initialize()
            {
                if (string.IsNullOrWhiteSpace(_propertyName)) return "RenderSettings property is required";

                _property = typeof(RenderSettings).GetProperty(_propertyName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (_property == null) return $"RenderSettings property '{_propertyName}' not found";
                if (_property.PropertyType != typeof(float) || !_property.CanRead || !_property.CanWrite)
                {
                    return $"RenderSettings property '{_propertyName}' is not a writable float property";
                }

                return string.Empty;
            }

            public float GetCurrentValue()
            {
                return Convert.ToSingle(_property.GetValue(null), CultureInfo.InvariantCulture);
            }

            public void SetValue(float value)
            {
                _property.SetValue(null, value);
            }

            public void Commit()
            {
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            }

            public void Cleanup() { }
        }
    }
}
