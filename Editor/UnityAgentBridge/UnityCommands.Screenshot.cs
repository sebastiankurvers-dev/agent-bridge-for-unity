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
        public static string TakeScreenshot(
            string viewType,
            bool includeBase64 = true,
            bool includeHandle = true,
            string source = "screenshot",
            int requestedWidth = 0,
            int requestedHeight = 0,
            string imageFormat = "jpeg")
        {
            try
            {
                Texture2D screenshot = null;
                int width = 0;
                int height = 0;

                if (viewType == "game")
                {
                    // Capture game view
                    int captureWidth, captureHeight;
                    if (requestedWidth > 0 && requestedHeight > 0)
                    {
                        captureWidth = Mathf.Clamp(requestedWidth, 64, 1920);
                        captureHeight = Mathf.Clamp(requestedHeight, 64, 1920);
                    }
                    else
                    {
                        var gameView = GetGameViewSize();
                        captureWidth = (int)gameView.x;
                        captureHeight = (int)gameView.y;
                    }
                    width = captureWidth;
                    height = captureHeight;

                    var cam = Camera.main;
                    if (cam == null)
                        cam = UnityEngine.Object.FindFirstObjectByType<Camera>();
                    if (cam != null)
                    {
                        var renderTexture = new RenderTexture(width, height, 24);
                        cam.targetTexture = renderTexture;
                        cam.Render();

                        RenderTexture.active = renderTexture;
                        screenshot = new Texture2D(width, height, TextureFormat.RGB24, false);
                        screenshot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                        screenshot.Apply();

                        cam.targetTexture = null;
                        RenderTexture.active = null;
                        UnityEngine.Object.DestroyImmediate(renderTexture);
                    }
                    else
                    {
                        return JsonError("No camera found for game view capture. Create a Camera in the scene.");
                    }
                }
                else
                {
                    // Scene view - render from scene view camera
                    var sceneView = SceneView.lastActiveSceneView;
                    if (sceneView != null && sceneView.camera != null)
                    {
                        if (requestedWidth > 0 && requestedHeight > 0)
                        {
                            width = Mathf.Clamp(requestedWidth, 64, 1920);
                            height = Mathf.Clamp(requestedHeight, 64, 1920);
                        }
                        else
                        {
                            width = (int)sceneView.position.width;
                            height = (int)sceneView.position.height;
                            if (width <= 0) width = 800;
                            if (height <= 0) height = 600;
                        }

                        // Force SceneView to sync its internal camera transform
                        // (pivot/rotation/size changes aren't applied to camera until next layout event)
                        sceneView.Repaint();
                        var sceneCamera = sceneView.camera;
                        float cameraDistance = sceneView.cameraDistance;
                        Vector3 cameraPos = sceneView.pivot - sceneView.rotation * new Vector3(0, 0, cameraDistance);
                        sceneCamera.transform.position = cameraPos;
                        sceneCamera.transform.rotation = sceneView.rotation;

                        if (sceneView.orthographic)
                        {
                            sceneCamera.orthographic = true;
                            sceneCamera.orthographicSize = sceneView.size;
                        }
                        else
                        {
                            sceneCamera.orthographic = false;
                        }

                        sceneCamera.nearClipPlane = 0.03f;
                        sceneCamera.farClipPlane = 10000f;

                        // Enable post-processing effects for scene view captures
                        var savedImageEffects = sceneView.sceneViewState.showImageEffects;
                        sceneView.sceneViewState.showImageEffects = true;

                        var renderTexture = new RenderTexture(width, height, 24);
                        sceneCamera.targetTexture = renderTexture;
                        sceneCamera.Render();

                        RenderTexture.active = renderTexture;
                        screenshot = new Texture2D(width, height, TextureFormat.RGB24, false);
                        screenshot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                        screenshot.Apply();

                        sceneCamera.targetTexture = null;
                        RenderTexture.active = null;
                        UnityEngine.Object.DestroyImmediate(renderTexture);

                        // Restore original image effects state
                        sceneView.sceneViewState.showImageEffects = savedImageEffects;
                    }
                    else
                    {
                        return JsonError("No active scene view found");
                    }
                }

                if (screenshot == null)
                {
                    return JsonError("Failed to capture screenshot");
                }

                // Resize to max 800px maintaining aspect ratio (skip when explicit dimensions requested)
                bool hasExplicitDimensions = requestedWidth > 0 && requestedHeight > 0;
                int maxDim = hasExplicitDimensions ? 1920 : 800;
                if (width > maxDim || height > maxDim)
                {
                    float scale = Mathf.Min((float)maxDim / width, (float)maxDim / height);
                    int newWidth = Mathf.Max(1, (int)(width * scale));
                    int newHeight = Mathf.Max(1, (int)(height * scale));
                    var resized = ResizeTexture(screenshot, newWidth, newHeight);
                    if (resized != screenshot)
                    {
                        UnityEngine.Object.DestroyImmediate(screenshot);
                        screenshot = resized;
                    }
                    width = newWidth;
                    height = newHeight;
                }

                string normalizedFormat = string.IsNullOrWhiteSpace(imageFormat) ? "jpeg" : imageFormat.Trim().ToLowerInvariant();
                bool encodePng = normalizedFormat == "png";
                string mimeType = encodePng ? "image/png" : "image/jpeg";

                if (!includeBase64 && !includeHandle)
                {
                    UnityEngine.Object.DestroyImmediate(screenshot);
                    return JsonResult(new Dictionary<string, object> {
                        { "success", true },
                        { "includeBase64", false },
                        { "includeHandle", false },
                        { "mimeType", mimeType },
                        { "width", width },
                        { "height", height }
                    });
                }

                // Default is JPEG for smaller payloads; PNG can be requested when exact lossless output is needed.
                byte[] imageBytes = encodePng ? screenshot.EncodeToPNG() : screenshot.EncodeToJPG(80);
                UnityEngine.Object.DestroyImmediate(screenshot);
                var base64 = Convert.ToBase64String(imageBytes);
                string imageHandle = null;
                if (includeHandle)
                {
                    imageHandle = StoreImage(base64, width, height, mimeType, source);
                }

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "mimeType", mimeType },
                    { "width", width },
                    { "height", height }
                };

                if (includeBase64)
                {
                    result["base64"] = base64;
                }
                else
                {
                    result["includeBase64"] = false;
                }

                if (includeHandle && !string.IsNullOrWhiteSpace(imageHandle))
                {
                    result["imageHandle"] = imageHandle;
                    result["handle"] = imageHandle; // alias for convenience
                }

                return JsonResult(result);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/image/store", Category = "screenshot", Description = "Store image handle")]
        public static string StoreImageHandle(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null)
                {
                    return JsonError("Failed to parse image-store request");
                }

                var imageBase64 = ReadString(request, "imageBase64");
                if (string.IsNullOrWhiteSpace(imageBase64))
                {
                    return JsonError("imageBase64 is required");
                }

                var normalized = NormalizeBase64Payload(imageBase64);
                int width = TryReadInt(request, "width", out var parsedWidth) ? Mathf.Max(0, parsedWidth) : 0;
                int height = TryReadInt(request, "height", out var parsedHeight) ? Mathf.Max(0, parsedHeight) : 0;
                string mimeType = ReadString(request, "mimeType") ?? "image/jpeg";
                string source = ReadString(request, "source") ?? "external";

                if ((width <= 0 || height <= 0) && TryDecodeImage(normalized, out var texture, out _))
                {
                    width = texture.width;
                    height = texture.height;
                    UnityEngine.Object.DestroyImmediate(texture);
                }

                string handle = StoreImage(normalized, width, height, mimeType, source);
                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "imageHandle", handle },
                    { "width", width },
                    { "height", height },
                    { "mimeType", mimeType }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        public static string GetImageHandle(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null)
                {
                    return JsonError("Failed to parse image-handle request");
                }

                string handle = ReadString(request, "imageHandle");
                if (string.IsNullOrWhiteSpace(handle))
                {
                    return JsonError("imageHandle is required");
                }

                bool includeBase64 = ReadBool(request, "includeBase64", false);
                if (!TryGetStoredImage(handle, out var entry, out var base64))
                {
                    return JsonError($"Image handle not found: {handle}");
                }

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "imageHandle", entry.handle },
                    { "mimeType", entry.mimeType ?? "image/jpeg" },
                    { "width", entry.width },
                    { "height", entry.height },
                    { "source", entry.source ?? string.Empty },
                    { "createdAtUtc", entry.createdAtUtc.ToString("o") },
                    { "lastAccessUtc", entry.lastAccessUtc.ToString("o") },
                    { "byteSize", entry.byteSize }
                };

                if (includeBase64 && !string.IsNullOrWhiteSpace(base64))
                {
                    response["base64"] = base64;
                }

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        public static string DeleteImageHandle(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null)
                {
                    return JsonError("Failed to parse image-handle delete request");
                }

                string handle = ReadString(request, "imageHandle");
                if (string.IsNullOrWhiteSpace(handle))
                {
                    return JsonError("imageHandle is required");
                }

                bool removed;
                lock (_imageStoreLock)
                {
                    removed = _imageStore.Remove(handle);
                }

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "imageHandle", handle },
                    { "removed", removed }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }


        private static bool TryGetScreenshotBase64(
            string viewType,
            out string base64,
            out string error,
            int requestedWidth = 0,
            int requestedHeight = 0)
        {
            return TryGetScreenshotData(
                viewType,
                out base64,
                out _,
                out error,
                includeHandle: false,
                requestedWidth: requestedWidth,
                requestedHeight: requestedHeight);
        }

        private static bool TryGetScreenshotData(
            string viewType,
            out string base64,
            out string imageHandle,
            out string error,
            bool includeHandle,
            int requestedWidth = 0,
            int requestedHeight = 0)
        {
            base64 = null;
            imageHandle = null;
            error = null;

            var screenshotJson = TakeScreenshot(
                string.IsNullOrWhiteSpace(viewType) ? "scene" : viewType,
                includeBase64: true,
                includeHandle: includeHandle,
                source: $"capture:{(string.IsNullOrWhiteSpace(viewType) ? "scene" : viewType)}",
                requestedWidth: requestedWidth,
                requestedHeight: requestedHeight);
            var parsed = MiniJSON.Json.Deserialize(screenshotJson) as Dictionary<string, object>;
            if (parsed == null)
            {
                error = "Failed to parse screenshot response";
                return false;
            }

            if (parsed.TryGetValue("error", out var errObj) && errObj != null)
            {
                error = errObj.ToString();
                return false;
            }

            base64 = ReadString(parsed, "base64");
            imageHandle = ReadString(parsed, "imageHandle");
            if (string.IsNullOrWhiteSpace(base64))
            {
                error = "Screenshot did not return base64 image data";
                return false;
            }

            return true;
        }

        private static string NormalizeBase64Payload(string rawBase64)
        {
            if (string.IsNullOrWhiteSpace(rawBase64))
            {
                return string.Empty;
            }

            string normalized = rawBase64.Trim();
            int comma = normalized.IndexOf(',');
            if (normalized.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            {
                normalized = normalized.Substring(comma + 1);
            }

            return normalized;
        }

        private static string StoreImage(string rawBase64, int width, int height, string mimeType, string source)
        {
            string normalized = NormalizeBase64Payload(rawBase64);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            string handle = $"img_{Guid.NewGuid():N}";
            var now = DateTime.UtcNow;
            var entry = new StoredImageEntry
            {
                handle = handle,
                base64 = normalized,
                width = width,
                height = height,
                mimeType = string.IsNullOrWhiteSpace(mimeType) ? "image/jpeg" : mimeType.Trim(),
                source = source ?? string.Empty,
                createdAtUtc = now,
                lastAccessUtc = now,
                byteSize = EstimateBase64ByteCount(normalized)
            };

            lock (_imageStoreLock)
            {
                _imageStore[handle] = entry;
                PruneImageStoreLocked();
            }

            return handle;
        }

        private static bool TryGetStoredImage(string handle, out StoredImageEntry entry, out string base64)
        {
            entry = null;
            base64 = null;

            if (string.IsNullOrWhiteSpace(handle))
            {
                return false;
            }

            lock (_imageStoreLock)
            {
                if (!_imageStore.TryGetValue(handle.Trim(), out var stored))
                {
                    return false;
                }

                stored.lastAccessUtc = DateTime.UtcNow;
                entry = stored;
                base64 = stored.base64;
                return true;
            }
        }

        private static bool TryResolveImageInput(
            string label,
            string directBase64,
            string handle,
            bool allowEmpty,
            out string base64,
            out string resolvedHandle,
            out string error)
        {
            base64 = null;
            resolvedHandle = null;
            error = null;

            if (!string.IsNullOrWhiteSpace(directBase64))
            {
                base64 = NormalizeBase64Payload(directBase64);
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(handle))
            {
                if (TryGetStoredImage(handle, out var _, out var fromHandle))
                {
                    base64 = fromHandle;
                    resolvedHandle = handle.Trim();
                    return true;
                }

                error = $"{label} imageHandle not found: {handle}";
                return false;
            }

            if (allowEmpty)
            {
                return true;
            }

            error = $"{label} image input is required";
            return false;
        }

        private static int EstimateBase64ByteCount(string normalizedBase64)
        {
            if (string.IsNullOrWhiteSpace(normalizedBase64))
            {
                return 0;
            }

            int padding = 0;
            if (normalizedBase64.EndsWith("==", StringComparison.Ordinal)) padding = 2;
            else if (normalizedBase64.EndsWith("=", StringComparison.Ordinal)) padding = 1;
            return Math.Max(0, (normalizedBase64.Length * 3 / 4) - padding);
        }

        // Image comparison logic moved to UnityCommands.Screenshot.Compare.cs
        // Multi-POV, sampling, and frame sequence moved to UnityCommands.Screenshot.Analysis.cs
    }
}
