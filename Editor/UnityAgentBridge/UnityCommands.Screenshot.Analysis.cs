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
        #region Multi-POV Snapshot

        // Preset euler rotations: camera looks toward boundsCenter from each direction
        private static readonly Dictionary<string, Vector3> _povPresetRotations = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase)
        {
            { "front",  new Vector3(0, 180, 0) },
            { "back",   new Vector3(0, 0, 0) },
            { "top",    new Vector3(90, 0, 0) },
            { "left",   new Vector3(0, -90, 0) },
            { "right",  new Vector3(0, 90, 0) }
        };

        private static readonly string[] _allCardinalPresets = { "front", "back", "top", "left", "right" };

        [BridgeRoute("POST", "/screenshot/multi-pov", Category = "screenshot", Description = "Multi-angle snapshot with image handles", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string MultiPovSnapshot(string jsonData)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null)
                    return JsonError("Failed to parse multi-pov request");

                // Parse parameters
                int targetInstanceId = TryReadInt(request, "targetInstanceId", out var tid) ? tid : -1;
                int width = TryReadInt(request, "width", out var w) ? Mathf.Clamp(w, 64, 1920) : 800;
                int height = TryReadInt(request, "height", out var h) ? Mathf.Clamp(h, 64, 1920) : 600;
                string format = ReadString(request, "format") ?? "jpeg";
                format = format.Trim().ToLowerInvariant() == "png" ? "png" : "jpeg";
                bool includePlayerView = ReadBool(request, "includePlayerView", true);
                bool brief = ReadBool(request, "brief", false);
                float sizeMultiplier = request.ContainsKey("sizeMultiplier") ? Convert.ToSingle(request["sizeMultiplier"]) : 1.5f;
                sizeMultiplier = Mathf.Clamp(sizeMultiplier, 0.5f, 10f);

                // Build POV list
                var povList = new List<Dictionary<string, object>>();

                // Parse presets shorthand
                string shorthand = ReadString(request, "presetsShorthand");
                if (!string.IsNullOrWhiteSpace(shorthand) && shorthand.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var p in _allCardinalPresets)
                        povList.Add(new Dictionary<string, object> { { "name", p }, { "preset", p } });
                }

                // Parse presets array
                if (request.ContainsKey("presets") && request["presets"] != null)
                {
                    var presetsObj = request["presets"];
                    List<object> presetsList = null;

                    if (presetsObj is List<object> directList)
                        presetsList = directList;
                    else if (presetsObj is string presetsStr && !string.IsNullOrWhiteSpace(presetsStr))
                    {
                        // Could be JSON array string like '["front","top"]' or shorthand "all"
                        if (presetsStr.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
                        {
                            if (povList.Count == 0)
                            {
                                foreach (var p in _allCardinalPresets)
                                    povList.Add(new Dictionary<string, object> { { "name", p }, { "preset", p } });
                            }
                        }
                        else
                        {
                            var parsed = MiniJSON.Json.Deserialize(presetsStr) as List<object>;
                            if (parsed != null)
                                presetsList = parsed;
                            else
                            {
                                // Bare string like "top" — treat as single preset if it's a known name
                                var bare = presetsStr.Trim().ToLowerInvariant();
                                if (_povPresetRotations.ContainsKey(bare))
                                    presetsList = new List<object> { bare };
                            }
                        }
                    }

                    if (presetsList != null)
                    {
                        foreach (var item in presetsList)
                        {
                            string presetName = item?.ToString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(presetName) && _povPresetRotations.ContainsKey(presetName))
                            {
                                // Avoid duplicates
                                if (!povList.Any(p => p.ContainsKey("preset") && presetName.Equals(p["preset"]?.ToString(), StringComparison.OrdinalIgnoreCase)))
                                    povList.Add(new Dictionary<string, object> { { "name", presetName }, { "preset", presetName } });
                            }
                        }
                    }
                }

                // Parse custom POVs
                if (request.ContainsKey("povs") && request["povs"] != null)
                {
                    var povsObj = request["povs"];
                    List<object> customPovs = null;

                    if (povsObj is List<object> directPovs)
                        customPovs = directPovs;
                    else if (povsObj is string povsStr && !string.IsNullOrWhiteSpace(povsStr))
                    {
                        customPovs = MiniJSON.Json.Deserialize(povsStr) as List<object>;
                    }

                    if (customPovs != null)
                    {
                        foreach (var pov in customPovs)
                        {
                            if (pov is Dictionary<string, object> povDict)
                            {
                                string povName = ReadString(povDict, "name") ?? $"custom_{povList.Count}";

                                // If it references a preset
                                string presetRef = ReadString(povDict, "preset");
                                if (!string.IsNullOrWhiteSpace(presetRef) && _povPresetRotations.ContainsKey(presetRef))
                                {
                                    povList.Add(new Dictionary<string, object> { { "name", povName }, { "preset", presetRef } });
                                }
                                else
                                {
                                    // Custom rotation
                                    var customPov = new Dictionary<string, object> { { "name", povName } };
                                    if (povDict.ContainsKey("rotation")) customPov["rotation"] = povDict["rotation"];
                                    if (povDict.ContainsKey("pivot")) customPov["pivot"] = povDict["pivot"];
                                    if (povDict.ContainsKey("size")) customPov["size"] = povDict["size"];
                                    povList.Add(customPov);
                                }
                            }
                        }
                    }
                }

                // Add player view if requested
                if (includePlayerView)
                {
                    povList.Add(new Dictionary<string, object> { { "name", "player" }, { "preset", "player" } });
                }

                if (povList.Count == 0)
                    return JsonError("No POVs specified. Use presets='all', a presets array, or custom povs.");

                // Resolve target bounds
                Vector3 boundsCenter = Vector3.zero;
                float autoSize = 5f;
                bool hasTarget = false;

                if (targetInstanceId > 0)
                {
                    var targetGO = EditorUtility.EntityIdToObject(targetInstanceId) as GameObject;
                    if (targetGO == null)
                        return JsonError($"Target GameObject not found: instanceId={targetInstanceId}");

                    var renderers = targetGO.GetComponentsInChildren<Renderer>(true);
                    if (renderers.Length > 0)
                    {
                        var combinedBounds = renderers[0].bounds;
                        for (int i = 1; i < renderers.Length; i++)
                            combinedBounds.Encapsulate(renderers[i].bounds);
                        boundsCenter = combinedBounds.center;
                        autoSize = Mathf.Max(combinedBounds.extents.magnitude, 0.5f) * sizeMultiplier;
                    }
                    else
                    {
                        boundsCenter = targetGO.transform.position;
                        autoSize = 5f * sizeMultiplier;
                    }
                    hasTarget = true;
                }

                // Save current scene view state
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null)
                    return JsonError("No active SceneView found. Open a Scene View in the editor.");

                var savedPivot = sceneView.pivot;
                var savedRotation = sceneView.rotation;
                var savedSize = sceneView.size;
                var savedOrtho = sceneView.orthographic;

                // If no target, use current pivot as boundsCenter
                if (!hasTarget)
                {
                    boundsCenter = savedPivot;
                    autoSize = savedSize;
                }

                // Capture loop
                var snapshots = new List<object>();
                var errors = new List<string>();

                try
                {
                    foreach (var pov in povList)
                    {
                        string povName = pov.ContainsKey("name") ? pov["name"]?.ToString() ?? "unnamed" : "unnamed";
                        string preset = pov.ContainsKey("preset") ? pov["preset"]?.ToString() : null;

                        try
                        {
                            // Player view: capture from Camera.main game view
                            if (preset != null && preset.Equals("player", StringComparison.OrdinalIgnoreCase))
                            {
                                if (Camera.main == null)
                                {
                                    var skipped = new Dictionary<string, object>
                                    {
                                        { "name", "player" },
                                        { "skipped", true },
                                        { "reason", "Camera.main is null" }
                                    };
                                    snapshots.Add(skipped);
                                    continue;
                                }

                                var gameResult = TakeScreenshot("game",
                                    includeBase64: false,
                                    includeHandle: true,
                                    source: "multi-pov:player",
                                    requestedWidth: width,
                                    requestedHeight: height,
                                    imageFormat: format);

                                var gameParsed = MiniJSON.Json.Deserialize(gameResult) as Dictionary<string, object>;
                                if (gameParsed != null && gameParsed.ContainsKey("imageHandle"))
                                {
                                    var entry = new Dictionary<string, object>
                                    {
                                        { "name", "player" },
                                        { "imageHandle", gameParsed["imageHandle"] },
                                        { "width", gameParsed.ContainsKey("width") ? gameParsed["width"] : width },
                                        { "height", gameParsed.ContainsKey("height") ? gameParsed["height"] : height }
                                    };
                                    if (!brief)
                                        entry["camera"] = "Camera.main (game view)";
                                    snapshots.Add(entry);
                                }
                                else
                                {
                                    string errMsg = gameParsed != null && gameParsed.ContainsKey("error") ? gameParsed["error"]?.ToString() : "Unknown error";
                                    var skipped = new Dictionary<string, object>
                                    {
                                        { "name", "player" },
                                        { "skipped", true },
                                        { "reason", errMsg }
                                    };
                                    snapshots.Add(skipped);
                                    errors.Add($"player: {errMsg}");
                                }
                                continue;
                            }

                            // Scene view capture: set camera position
                            Vector3 eulerRotation;
                            float povSize = autoSize;

                            if (!string.IsNullOrWhiteSpace(preset) && _povPresetRotations.TryGetValue(preset, out var presetRot))
                            {
                                eulerRotation = presetRot;
                            }
                            else if (pov.ContainsKey("rotation"))
                            {
                                var rotObj = pov["rotation"] as List<object>;
                                if (rotObj != null && rotObj.Count >= 3)
                                    eulerRotation = new Vector3(Convert.ToSingle(rotObj[0]), Convert.ToSingle(rotObj[1]), Convert.ToSingle(rotObj[2]));
                                else
                                    eulerRotation = Vector3.zero;
                            }
                            else
                            {
                                eulerRotation = Vector3.zero;
                            }

                            // Custom pivot override
                            Vector3 povPivot = boundsCenter;
                            if (pov.ContainsKey("pivot"))
                            {
                                var pivotObj = pov["pivot"] as List<object>;
                                if (pivotObj != null && pivotObj.Count >= 3)
                                    povPivot = new Vector3(Convert.ToSingle(pivotObj[0]), Convert.ToSingle(pivotObj[1]), Convert.ToSingle(pivotObj[2]));
                            }

                            // Custom size override
                            if (pov.ContainsKey("size") && pov["size"] != null)
                            {
                                float customSize = Convert.ToSingle(pov["size"]);
                                if (customSize > 0) povSize = customSize;
                            }

                            // Apply scene view state
                            sceneView.pivot = povPivot;
                            sceneView.rotation = Quaternion.Euler(eulerRotation);
                            sceneView.size = povSize;
                            sceneView.orthographic = false;
                            sceneView.Repaint();

                            // Capture
                            var sceneResult = TakeScreenshot("scene",
                                includeBase64: false,
                                includeHandle: true,
                                source: $"multi-pov:{povName}",
                                requestedWidth: width,
                                requestedHeight: height,
                                imageFormat: format);

                            var sceneParsed = MiniJSON.Json.Deserialize(sceneResult) as Dictionary<string, object>;
                            if (sceneParsed != null && sceneParsed.ContainsKey("imageHandle"))
                            {
                                var entry = new Dictionary<string, object>
                                {
                                    { "name", povName },
                                    { "imageHandle", sceneParsed["imageHandle"] },
                                    { "width", sceneParsed.ContainsKey("width") ? sceneParsed["width"] : width },
                                    { "height", sceneParsed.ContainsKey("height") ? sceneParsed["height"] : height }
                                };
                                if (!brief)
                                {
                                    entry["camera"] = $"scene view (euler: [{eulerRotation.x},{eulerRotation.y},{eulerRotation.z}], size: {povSize:F1})";
                                    entry["pivot"] = new List<object> { (double)Math.Round(povPivot.x, 3), (double)Math.Round(povPivot.y, 3), (double)Math.Round(povPivot.z, 3) };
                                }
                                snapshots.Add(entry);
                            }
                            else
                            {
                                string errMsg = sceneParsed != null && sceneParsed.ContainsKey("error") ? sceneParsed["error"]?.ToString() : "Unknown error";
                                var skipped = new Dictionary<string, object>
                                {
                                    { "name", povName },
                                    { "skipped", true },
                                    { "reason", errMsg }
                                };
                                snapshots.Add(skipped);
                                errors.Add($"{povName}: {errMsg}");
                            }
                        }
                        catch (Exception povEx)
                        {
                            errors.Add($"{povName}: {povEx.Message}");
                            snapshots.Add(new Dictionary<string, object>
                            {
                                { "name", povName },
                                { "skipped", true },
                                { "reason", povEx.Message }
                            });
                        }
                    }
                }
                finally
                {
                    // Restore camera state
                    sceneView.pivot = savedPivot;
                    sceneView.rotation = savedRotation;
                    sceneView.size = savedSize;
                    sceneView.orthographic = savedOrtho;
                    sceneView.Repaint();
                }

                sw.Stop();
                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "snapshotCount", snapshots.Count },
                    { "snapshots", snapshots },
                    { "captureTimeMs", sw.ElapsedMilliseconds }
                };

                if (errors.Count > 0)
                    result["errors"] = errors;

                if (!brief && hasTarget)
                {
                    result["target"] = new Dictionary<string, object>
                    {
                        { "instanceId", targetInstanceId },
                        { "boundsCenter", new List<object> { (double)Math.Round(boundsCenter.x, 3), (double)Math.Round(boundsCenter.y, 3), (double)Math.Round(boundsCenter.z, 3) } },
                        { "autoSize", (double)Math.Round(autoSize, 3) }
                    };
                }

                return JsonResult(result);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        #endregion

        #region Screenshot Sampling Methods

        [BridgeRoute("POST", "/screenshot/sample-colors", Category = "screenshot", Description = "Sample pixel colors from screenshot", ReadOnly = true, TimeoutDefault = 10000)]
        public static string SampleScreenshotColors(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null)
                    return JsonError("Failed to parse sample-colors request");

                // Parse sample points
                var spRaw = request.ContainsKey("samplePoints") ? request["samplePoints"] as List<object> : null;
                if (spRaw == null || spRaw.Count == 0)
                    return JsonError("samplePoints must have at least 1 point");

                var samplePoints = new List<float[]>();
                for (int i = 0; i < spRaw.Count; i++)
                {
                    var pt = spRaw[i] as List<object>;
                    if (pt == null || pt.Count < 2)
                        return JsonError($"samplePoints[{i}] must have 2 values [x,y]");
                    samplePoints.Add(new float[] { Convert.ToSingle(pt[0]), Convert.ToSingle(pt[1]) });
                }

                int sampleRadius = request.ContainsKey("sampleRadius") ? Convert.ToInt32(request["sampleRadius"]) : 1;
                sampleRadius = Mathf.Clamp(sampleRadius, 1, 11);

                // Get or capture screenshot
                Texture2D texture = null;
                if (request.ContainsKey("imageHandle") && request["imageHandle"] != null)
                {
                    string handle = request["imageHandle"].ToString();
                    if (!TryGetStoredImage(handle, out _, out var fromHandle))
                    {
                        return JsonError($"Image handle not found: {handle}");
                    }

                    byte[] imgBytes = Convert.FromBase64String(fromHandle);
                    texture = new Texture2D(2, 2);
                    texture.LoadImage(imgBytes);
                }
                else if (request.ContainsKey("imageBase64") && request["imageBase64"] != null)
                {
                    string b64 = NormalizeBase64Payload(request["imageBase64"].ToString());
                    byte[] imgBytes = Convert.FromBase64String(b64);
                    texture = new Texture2D(2, 2);
                    texture.LoadImage(imgBytes);
                }
                else
                {
                    string viewType = request.ContainsKey("screenshotView") ? (request["screenshotView"]?.ToString() ?? "game") : "game";
                    if (!TryGetScreenshotBase64(viewType, out var base64, out var captureError))
                        return JsonError(captureError);

                    byte[] imgBytes = Convert.FromBase64String(base64);
                    texture = new Texture2D(2, 2);
                    texture.LoadImage(imgBytes);
                }

                int width = texture.width;
                int height = texture.height;

                var samples = new List<object>();
                foreach (var sp in samplePoints)
                {
                    float nx = Mathf.Clamp01(sp[0]);
                    float ny = Mathf.Clamp01(sp[1]);

                    // Convert normalized coords to pixel coords (flip Y: 0,0 = top-left in input, but bottom-left in Unity texture)
                    int px = Mathf.Clamp(Mathf.RoundToInt(nx * (width - 1)), 0, width - 1);
                    int py = Mathf.Clamp(Mathf.RoundToInt((1f - ny) * (height - 1)), 0, height - 1);

                    Color sampledColor;
                    if (sampleRadius <= 1)
                    {
                        sampledColor = texture.GetPixel(px, py);
                    }
                    else
                    {
                        // Median filter
                        int halfR = sampleRadius / 2;
                        var reds = new List<float>();
                        var greens = new List<float>();
                        var blues = new List<float>();
                        var alphas = new List<float>();

                        for (int dy = -halfR; dy <= halfR; dy++)
                        {
                            for (int dx = -halfR; dx <= halfR; dx++)
                            {
                                int sx = Mathf.Clamp(px + dx, 0, width - 1);
                                int sy = Mathf.Clamp(py + dy, 0, height - 1);
                                var c = texture.GetPixel(sx, sy);
                                reds.Add(c.r);
                                greens.Add(c.g);
                                blues.Add(c.b);
                                alphas.Add(c.a);
                            }
                        }

                        reds.Sort();
                        greens.Sort();
                        blues.Sort();
                        alphas.Sort();
                        int mid = reds.Count / 2;
                        sampledColor = new Color(reds[mid], greens[mid], blues[mid], alphas[mid]);
                    }

                    // Compute derived values
                    Color.RGBToHSV(sampledColor, out float h, out float s, out float v);
                    float luminance = 0.2126f * sampledColor.r + 0.7152f * sampledColor.g + 0.0722f * sampledColor.b;
                    string hex = "#" + ColorUtility.ToHtmlStringRGBA(sampledColor);

                    samples.Add(new Dictionary<string, object>
                    {
                        { "x", (double)Math.Round(nx, 4) },
                        { "y", (double)Math.Round(ny, 4) },
                        { "r", (double)Math.Round(sampledColor.r, 4) },
                        { "g", (double)Math.Round(sampledColor.g, 4) },
                        { "b", (double)Math.Round(sampledColor.b, 4) },
                        { "a", (double)Math.Round(sampledColor.a, 4) },
                        { "hex", hex },
                        { "hsv", new List<object> { (double)Math.Round(h * 360f, 1), (double)Math.Round(s, 4), (double)Math.Round(v, 4) } },
                        { "luminance", (double)Math.Round(luminance, 4) }
                    });
                }

                UnityEngine.Object.DestroyImmediate(texture);

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "width", width },
                    { "height", height },
                    { "samples", samples }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        #endregion

    }
}
