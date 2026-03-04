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
        private static readonly List<LogEntry> _logBuffer = new List<LogEntry>();
        private const int MaxLogEntries = 500;
        private const int MaxEventStackTraceChars = 1200;
        private static bool _logCallbackRegistered;
        private static readonly Dictionary<string, PerformanceMetricSnapshot> _performanceBaselines = new Dictionary<string, PerformanceMetricSnapshot>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _performanceBaselineLock = new object();
        private static readonly Dictionary<string, StoredImageEntry> _imageStore = new Dictionary<string, StoredImageEntry>(StringComparer.Ordinal);
        private static readonly object _imageStoreLock = new object();
        private const int MaxStoredImages = 96;
        private const int MaxStoredImageBytes = 24 * 1024 * 1024;

        static UnityCommands()
        {
            try
            {
                Application.logMessageReceived += OnLogMessageReceived;
                _logCallbackRegistered = true;
            }
            catch
            {
                // Registration may fail if called from a background thread.
                // EnsureLogCallback() will retry on the main thread.
                _logCallbackRegistered = false;
            }
        }

        internal static void EnsureLogCallback()
        {
            if (!_logCallbackRegistered)
            {
                Application.logMessageReceived += OnLogMessageReceived;
                _logCallbackRegistered = true;
            }
        }

        // ==================== JSON Helper Methods ====================
        // Use these instead of JsonUtility.ToJson(new { ... }) which produces empty {} for anonymous types.

        internal static string JsonSuccess()
        {
            return MiniJSON.Json.Serialize(new Dictionary<string, object> { { "success", true } });
        }

        internal static string JsonError(string error)
        {
            return MiniJSON.Json.Serialize(new Dictionary<string, object> { { "success", false }, { "error", error } });
        }

        internal static string JsonResult(Dictionary<string, object> data)
        {
            return MiniJSON.Json.Serialize(data);
        }

        // ==================== Color JSON Normalization ====================

        /// <summary>
        /// Preprocesses JSON to convert color object fields ({r,g,b,a}) to array format ([r,g,b,a])
        /// for compatibility with JsonUtility.FromJson which expects float[] for color fields.
        /// Handles fields: color, baseColor, emissionColor.
        /// </summary>
        internal static string NormalizeColorFields(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;

            var parsed = MiniJSON.Json.Deserialize(json) as Dictionary<string, object>;
            if (parsed == null) return json;

            bool changed = false;
            foreach (var fieldName in new[] { "color", "baseColor", "emissionColor" })
            {
                if (parsed.ContainsKey(fieldName) && parsed[fieldName] is Dictionary<string, object> colorObj)
                {
                    // Convert {r, g, b, a} to [r, g, b, a]
                    var arr = new List<object>();
                    if (colorObj.ContainsKey("r")) arr.Add(Convert.ToSingle(colorObj["r"]));
                    if (colorObj.ContainsKey("g")) arr.Add(Convert.ToSingle(colorObj["g"]));
                    if (colorObj.ContainsKey("b")) arr.Add(Convert.ToSingle(colorObj["b"]));
                    if (colorObj.ContainsKey("a")) arr.Add(Convert.ToSingle(colorObj["a"]));

                    if (arr.Count >= 3)
                    {
                        parsed[fieldName] = arr;
                        changed = true;
                    }
                }
            }

            return changed ? MiniJSON.Json.Serialize(parsed) : json;
        }

        // ==================== Component Resolution ====================

        /// <summary>
        /// Find a component on a GameObject by type name, with fallback for namespace collisions.
        /// TypeResolver may return the wrong type when multiple types share a simple name
        /// (e.g., global CameraFollow vs MyGame.CameraFollow). This method falls back to
        /// searching the GameObject's actual components by simple or full name.
        /// </summary>
        internal static Component FindComponentOnGameObject(GameObject go, string typeName, out Type resolvedType)
        {
            resolvedType = TypeResolver.FindComponentType(typeName);
            Component component = null;

            if (resolvedType != null)
            {
                component = go.GetComponent(resolvedType);
            }

            // Fallback: search the GameObject's actual components by name
            if (component == null)
            {
                foreach (var comp in go.GetComponents<Component>())
                {
                    if (comp == null) continue;
                    var compType = comp.GetType();
                    if (compType.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                        compType.FullName.Equals(typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        component = comp;
                        resolvedType = compType;
                        break;
                    }
                }
            }

            return component;
        }

        // ==================== Path Validation ====================

        internal static string ValidateAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..");
            var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, path));
            if (!fullPath.StartsWith(projectRoot + System.IO.Path.DirectorySeparatorChar)
                && fullPath != projectRoot)
                return null;
            return fullPath;
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            lock (_logBuffer)
            {
                _logBuffer.Add(new LogEntry
                {
                    message = condition,
                    stackTrace = stackTrace,
                    type = type.ToString(),
                    timestamp = DateTime.Now.ToString("HH:mm:ss.fff")
                });

                while (_logBuffer.Count > MaxLogEntries)
                {
                    _logBuffer.RemoveAt(0);
                }

                // Push error/exception events to the event stream
                if (type == LogType.Error || type == LogType.Exception)
                {
                    var eventStackTrace = TrimForEventPayload(stackTrace, MaxEventStackTraceChars, out bool stackTraceTrimmed);
                    var payload = new Dictionary<string, object>
                    {
                        { "message", condition },
                        { "type", type.ToString() },
                        { "timestamp", DateTime.Now.ToString("HH:mm:ss.fff") }
                    };

                    if (!string.IsNullOrEmpty(eventStackTrace))
                    {
                        payload["stackTrace"] = eventStackTrace;
                    }
                    if (stackTraceTrimmed)
                    {
                        payload["stackTraceTrimmed"] = true;
                    }

                    UnityAgentBridgeServer.PushEvent("log_error", MiniJSON.Json.Serialize(payload));
                }
            }
        }

        private static string TrimForEventPayload(string value, int maxChars, out bool trimmed)
        {
            trimmed = false;
            if (string.IsNullOrEmpty(value) || maxChars <= 0)
                return string.Empty;

            if (value.Length <= maxChars)
                return value;

            trimmed = true;
            return value.Substring(0, maxChars) + "...";
        }

        // ==================== Shared Image Store Class ====================

        private sealed class StoredImageEntry
        {
            public string handle;
            public string base64;
            public int width;
            public int height;
            public string mimeType;
            public string source;
            public DateTime createdAtUtc;
            public DateTime lastAccessUtc;
            public int byteSize;
        }

        // ==================== Shared Parse Utilities ====================

        private static bool ReadBool(Dictionary<string, object> map, string key, bool defaultValue)
        {
            if (map == null || !map.TryGetValue(key, out var value) || value == null) return defaultValue;
            if (value is bool b) return b;
            if (value is string s)
            {
                if (bool.TryParse(s, out var parsedBool)) return parsedBool;
                if (int.TryParse(s, out var parsedInt)) return parsedInt != 0;
            }

            try { return Convert.ToDouble(value) != 0d; }
            catch { return defaultValue; }
        }

        private static bool TryReadBoolField(Dictionary<string, object> map, string key, out bool value)
        {
            value = false;
            if (map == null || !map.TryGetValue(key, out var raw) || raw == null) return false;
            value = ReadBool(new Dictionary<string, object> { { key, raw } }, key, false);
            return true;
        }

        private static string ReadString(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var value) || value == null) return null;
            var str = value.ToString();
            return string.IsNullOrWhiteSpace(str) ? null : str;
        }

        private static bool TryReadInt(Dictionary<string, object> map, string key, out int value)
        {
            value = 0;
            if (map == null || !map.TryGetValue(key, out var raw) || raw == null) return false;

            if (raw is int i)
            {
                value = i;
                return true;
            }

            if (int.TryParse(raw.ToString(), out var parsed))
            {
                value = parsed;
                return true;
            }

            try
            {
                value = Convert.ToInt32(raw);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadFloatField(Dictionary<string, object> map, string key, out float value)
        {
            value = 0f;
            if (map == null || !map.TryGetValue(key, out var raw) || raw == null) return false;

            if (raw is float f)
            {
                value = f;
                return true;
            }
            if (raw is double d)
            {
                value = (float)d;
                return true;
            }
            if (float.TryParse(raw.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
                return true;
            }

            try
            {
                value = Convert.ToSingle(raw);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
