using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
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
        [Serializable]
        private class ProceduralTextureRequest
        {
            public string textureType;
            public string name;
            public string path;
            public int width = 256;
            public int height = 256;
            // Noise params
            public float scale = 4f;
            public int octaves = 4;
            public float persistence = 0.5f;
            public float[] tint;
            // Gradient params
            public float[] colorA;
            public float[] colorB;
            public float angle = 0f;
            public string gradientMode;
            // Pattern params
            public float[] color1;
            public float[] color2;
            public int tilesX = 4;
            public int tilesY = 4;
            public float mortarWidth = 0.05f;
            // General
            public bool assignToMaterial;
            public string materialPath;
            public int seed = 0;
        }

        [BridgeRoute("POST", "/texture/procedural", Category = "assets", Description = "Generate procedural texture")]
        public static string CreateProceduralTexture(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<ProceduralTextureRequest>(jsonData);

                if (string.IsNullOrEmpty(request.textureType))
                    return JsonError("textureType is required (noise, gradient, checkerboard, bricks, stripes)");

                var type = request.textureType.ToLowerInvariant();
                var validTypes = new[] { "noise", "gradient", "checkerboard", "bricks", "stripes" };
                if (Array.IndexOf(validTypes, type) < 0)
                    return JsonError($"Invalid textureType '{request.textureType}'. Must be one of: noise, gradient, checkerboard, bricks, stripes");

                int w = Mathf.Clamp(request.width, 64, 512);
                int h = Mathf.Clamp(request.height, 64, 512);

                var texName = request.name;
                if (string.IsNullOrEmpty(texName))
                    texName = $"Procedural_{type}_{w}x{h}";

                var savePath = request.path;
                if (string.IsNullOrEmpty(savePath))
                    savePath = $"Assets/Textures/{texName}.png";
                if (!savePath.StartsWith("Assets/"))
                    savePath = "Assets/" + savePath;
                if (!savePath.EndsWith(".png"))
                    savePath += ".png";

                if (ValidateAssetPath(savePath) == null)
                    return JsonError("Path is outside the project directory");

                var dir = System.IO.Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                {
                    CreateFolderRecursive(dir);
                }

                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var pixels = new Color[w * h];

                switch (type)
                {
                    case "noise":
                        GenerateNoise(pixels, w, h, request);
                        break;
                    case "gradient":
                        GenerateGradient(pixels, w, h, request);
                        break;
                    case "checkerboard":
                        GenerateCheckerboard(pixels, w, h, request);
                        break;
                    case "bricks":
                        GenerateBricks(pixels, w, h, request);
                        break;
                    case "stripes":
                        GenerateStripes(pixels, w, h, request);
                        break;
                }

                tex.SetPixels(pixels);
                tex.Apply();

                var pngData = tex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(tex);

                var fullPath = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(Application.dataPath),
                    savePath);
                System.IO.File.WriteAllBytes(fullPath, pngData);
                AssetDatabase.ImportAsset(savePath, ImportAssetOptions.ForceUpdate);

                string materialAssigned = null;
                if (request.assignToMaterial && !string.IsNullOrEmpty(request.materialPath))
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(request.materialPath);
                    if (mat != null)
                    {
                        var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(savePath);
                        if (texture != null)
                        {
                            if (mat.HasProperty("_BaseMap"))
                                mat.SetTexture("_BaseMap", texture);
                            else if (mat.HasProperty("_MainTex"))
                                mat.SetTexture("_MainTex", texture);
                            EditorUtility.SetDirty(mat);
                            AssetDatabase.SaveAssets();
                            materialAssigned = request.materialPath;
                        }
                    }
                    else
                    {
                        materialAssigned = $"WARNING: Material not found at '{request.materialPath}'";
                    }
                }

                var resultDict = new Dictionary<string, object>
                {
                    { "success", true },
                    { "textureType", type },
                    { "path", savePath },
                    { "width", w },
                    { "height", h },
                    { "name", texName }
                };
                if (materialAssigned != null)
                    resultDict["materialAssigned"] = materialAssigned;
                return JsonResult(resultDict);
            }
            catch (Exception ex)
            {
                return JsonError($"Failed to create procedural texture: {ex.Message}");
            }
        }

        private static void GenerateNoise(Color[] pixels, int w, int h, ProceduralTextureRequest request)
        {
            var tint = ParseColor(request.tint, Color.white);
            float seedOffsetX = request.seed * 100f;
            float seedOffsetY = request.seed * 100f + 50f;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float nx = (float)x / w;
                    float ny = (float)y / h;

                    float value = 0f;
                    float amplitude = 1f;
                    float frequency = 1f;
                    float maxValue = 0f;

                    for (int o = 0; o < request.octaves; o++)
                    {
                        float sx = (nx * request.scale * frequency) + seedOffsetX;
                        float sy = (ny * request.scale * frequency) + seedOffsetY;
                        value += Mathf.PerlinNoise(sx, sy) * amplitude;
                        maxValue += amplitude;
                        amplitude *= request.persistence;
                        frequency *= 2f;
                    }

                    value /= maxValue;
                    pixels[y * w + x] = new Color(value * tint.r, value * tint.g, value * tint.b, 1f);
                }
            }
        }

        private static void GenerateGradient(Color[] pixels, int w, int h, ProceduralTextureRequest request)
        {
            var cA = ParseColor(request.colorA, Color.black);
            var cB = ParseColor(request.colorB, Color.white);
            bool radial = !string.IsNullOrEmpty(request.gradientMode) &&
                          request.gradientMode.Equals("radial", StringComparison.OrdinalIgnoreCase);

            float angleRad = request.angle * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(angleRad);
            float sinA = Mathf.Sin(angleRad);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float nx = (float)x / (w - 1);
                    float ny = (float)y / (h - 1);
                    float t;

                    if (radial)
                    {
                        float dx = nx - 0.5f;
                        float dy = ny - 0.5f;
                        t = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) * 2f);
                    }
                    else
                    {
                        float cx = nx - 0.5f;
                        float cy = ny - 0.5f;
                        t = Mathf.Clamp01((cx * cosA + cy * sinA) + 0.5f);
                    }

                    pixels[y * w + x] = Color.Lerp(cA, cB, t);
                }
            }
        }

        private static void GenerateCheckerboard(Color[] pixels, int w, int h, ProceduralTextureRequest request)
        {
            var c1 = ParseColor(request.color1, Color.white);
            var c2 = ParseColor(request.color2, Color.black);
            int tilesX = Mathf.Max(1, request.tilesX);
            int tilesY = Mathf.Max(1, request.tilesY);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int tx = (x * tilesX) / w;
                    int ty = (y * tilesY) / h;
                    bool even = (tx + ty) % 2 == 0;
                    pixels[y * w + x] = even ? c1 : c2;
                }
            }
        }

        private static void GenerateBricks(Color[] pixels, int w, int h, ProceduralTextureRequest request)
        {
            var c1 = ParseColor(request.color1, new Color(0.76f, 0.23f, 0.13f)); // brick red
            var c2 = ParseColor(request.color2, new Color(0.8f, 0.8f, 0.75f));   // mortar grey
            int tilesX = Mathf.Max(1, request.tilesX);
            int tilesY = Mathf.Max(1, request.tilesY);
            float mortar = Mathf.Clamp(request.mortarWidth, 0f, 0.5f);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float ny = (float)y / h * tilesY;
                    int row = Mathf.FloorToInt(ny);
                    float fy = ny - row;

                    float nx = (float)x / w * tilesX;
                    // Offset every other row by half a brick
                    if (row % 2 == 1)
                        nx += 0.5f;
                    float fx = nx - Mathf.Floor(nx);

                    bool isMortar = fy < mortar || fy > (1f - mortar) ||
                                    fx < mortar || fx > (1f - mortar);
                    pixels[y * w + x] = isMortar ? c2 : c1;
                }
            }
        }

        private static void GenerateStripes(Color[] pixels, int w, int h, ProceduralTextureRequest request)
        {
            var c1 = ParseColor(request.color1, Color.white);
            var c2 = ParseColor(request.color2, Color.black);
            int tilesX = Mathf.Max(1, request.tilesX);

            float angleRad = request.angle * Mathf.Deg2Rad;
            float cosA = Mathf.Cos(angleRad);
            float sinA = Mathf.Sin(angleRad);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float nx = (float)x / w;
                    float ny = (float)y / h;
                    float projected = nx * cosA + ny * sinA;
                    float stripe = projected * tilesX;
                    int band = Mathf.FloorToInt(stripe);
                    bool even = band % 2 == 0;
                    pixels[y * w + x] = even ? c1 : c2;
                }
            }
        }

        private static Color ParseColor(float[] rgb, Color fallback)
        {
            if (rgb == null || rgb.Length < 3)
                return fallback;
            return new Color(
                Mathf.Clamp01(rgb[0]),
                Mathf.Clamp01(rgb[1]),
                Mathf.Clamp01(rgb[2]),
                1f);
        }
    }
}
