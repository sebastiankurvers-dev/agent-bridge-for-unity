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

        #region Material Operations

        private static string GetDefaultShaderName()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline != null && pipeline.GetType().Name.Contains("Universal"))
                return "Universal Render Pipeline/Lit";
            if (pipeline != null)
                return "HDRP/Lit";
            return "Standard";
        }

        [BridgeRoute("POST", "/material", Category = "materials", Description = "Create material")]
        public static string CreateMaterial(string jsonData)
        {
            var request = JsonUtility.FromJson<MaterialRequest>(NormalizeColorFields(jsonData));

            var shaderName = request.shaderName ?? GetDefaultShaderName();
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                shader = Shader.Find(GetDefaultShaderName());
            }

            var path = request.ResolvedPath;
            if (string.IsNullOrEmpty(path))
            {
                path = $"Assets/{request.name ?? "NewMaterial"}.mat";
            }
            if (!path.StartsWith("Assets/"))
            {
                path = "Assets/" + path;
            }
            if (!path.EndsWith(".mat"))
            {
                path += ".mat";
            }

            if (ValidateAssetPath(path) == null)
            {
                return JsonError("Path is outside the project directory");
            }

            try
            {
                var material = new Material(shader);

                if (!string.IsNullOrEmpty(request.name))
                {
                    material.name = request.name;
                }

                ApplyMaterialProperties(material, request);

                // Create directory if needed
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                AssetDatabase.CreateAsset(material, path);
                AssetDatabase.SaveAssets();

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "path", path }, { "shaderName", shader.name }, { "message", $"Created material at {path}" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("PUT", "/material", Category = "materials", Description = "Modify material")]
        public static string ModifyMaterial(string jsonData)
        {
            var request = JsonUtility.FromJson<MaterialRequest>(NormalizeColorFields(jsonData));

            if (string.IsNullOrEmpty(request.path))
            {
                return JsonError("Material path is required");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(request.path);
            if (material == null)
            {
                return JsonError($"Material not found: {request.path}");
            }

            try
            {
                Undo.RecordObject(material, "Agent Bridge Modify Material");

                // Change shader if specified
                if (!string.IsNullOrEmpty(request.shaderName))
                {
                    var shader = Shader.Find(request.shaderName);
                    if (shader != null)
                    {
                        material.shader = shader;
                    }
                }

                ApplyMaterialProperties(material, request);
                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();

                // Echo back verified material state
                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "path", request.path },
                    { "message", $"Modified material {material.name}" },
                    { "shader", material.shader != null ? material.shader.name : "null" },
                    { "emissionEnabled", material.IsKeywordEnabled("_EMISSION") }
                };
                if (material.HasProperty("_EmissionColor"))
                {
                    var ec = material.GetColor("_EmissionColor");
                    result["emissionColor"] = new Dictionary<string, object>
                    {
                        { "r", (double)System.Math.Round(ec.r, 4) },
                        { "g", (double)System.Math.Round(ec.g, 4) },
                        { "b", (double)System.Math.Round(ec.b, 4) },
                        { "a", (double)System.Math.Round(ec.a, 4) }
                    };
                }
                if (material.HasProperty("_BaseColor"))
                {
                    var bc = material.GetColor("_BaseColor");
                    result["baseColor"] = new Dictionary<string, object>
                    {
                        { "r", (double)System.Math.Round(bc.r, 4) },
                        { "g", (double)System.Math.Round(bc.g, 4) },
                        { "b", (double)System.Math.Round(bc.b, 4) },
                        { "a", (double)System.Math.Round(bc.a, 4) }
                    };
                }
                if (material.HasProperty("_Metallic"))
                    result["metallic"] = (double)System.Math.Round(material.GetFloat("_Metallic"), 4);
                if (material.HasProperty("_Smoothness"))
                    result["smoothness"] = (double)System.Math.Round(material.GetFloat("_Smoothness"), 4);

                return JsonResult(result);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        public static string FindMaterials(string searchQuery, int maxResults = 100)
        {
            var materials = new List<MaterialInfo>();
            var filter = string.IsNullOrWhiteSpace(searchQuery) ? "t:Material" : $"t:Material {searchQuery}";
            var guids = AssetDatabase.FindAssets(filter);
            maxResults = Math.Clamp(maxResults, 1, 5000);

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var name = System.IO.Path.GetFileNameWithoutExtension(path);

                if (string.IsNullOrEmpty(searchQuery) ||
                    name.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                    materials.Add(new MaterialInfo
                    {
                        name = name,
                        path = path,
                        shaderName = mat?.shader?.name ?? "Unknown"
                    });
                    if (materials.Count >= maxResults)
                    {
                        break;
                    }
                }
            }

            var jsonParts = materials.Select(m => JsonUtility.ToJson(m));
            return "{\"materials\":[" + string.Join(",", jsonParts) + "]}";
        }

        public static string FindMaterialsScoped(
            string searchQuery,
            string[] includeRoots = null,
            string[] excludeRoots = null,
            bool includeSubfolders = true,
            int maxResults = 200,
            string matchMode = "contains")
        {
            try
            {
                var include = NormalizeScopeRoots(includeRoots);
                var exclude = NormalizeScopeRoots(excludeRoots);
                maxResults = Math.Clamp(maxResults, 1, 5000);

                var matches = new List<SearchMatch<MaterialInfo>>();
                var guids = AssetDatabase.FindAssets("t:Material");

                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!PassesScope(path, include, exclude, includeSubfolders))
                    {
                        continue;
                    }

                    var name = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (!TryScoreMatch(searchQuery, name, path, matchMode, out int score))
                    {
                        continue;
                    }

                    var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                    matches.Add(new SearchMatch<MaterialInfo>
                    {
                        score = score,
                        item = new MaterialInfo
                        {
                            name = name,
                            path = path,
                            shaderName = mat?.shader?.name ?? "Unknown"
                        }
                    });
                }

                var ordered = matches
                    .OrderBy(m => m.score)
                    .ThenBy(m => m.item.name, StringComparer.OrdinalIgnoreCase)
                    .Take(maxResults)
                    .Select(m => new Dictionary<string, object>
                    {
                        { "name", m.item.name },
                        { "path", m.item.path },
                        { "shaderName", m.item.shaderName }
                    })
                    .ToList<object>();

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "materials", ordered },
                    { "meta", BuildScopedSearchMeta(searchQuery, include, exclude, includeSubfolders, maxResults, matchMode, ordered.Count) }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static void ApplyMaterialProperties(Material material, MaterialRequest request)
        {
            var resolvedColor = request.ResolvedColor;
            if (resolvedColor != null && resolvedColor.Length >= 3)
            {
                var color = new Color(
                    resolvedColor[0],
                    resolvedColor[1],
                    resolvedColor[2],
                    resolvedColor.Length >= 4 ? resolvedColor[3] : 1f);
                material.color = color;
            }

            if (request.mainTexturePath != null)
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture>(request.mainTexturePath);
                if (texture != null)
                {
                    material.mainTexture = texture;
                }
            }

            if (request.renderQueue >= 0)
            {
                material.renderQueue = request.renderQueue;
            }

            // Emission
            if (request.emissionColor != null && request.emissionColor.Length >= 3)
            {
                float a = request.emissionColor.Length >= 4 ? request.emissionColor[3] : 1f;
                float intensity = request.emissionIntensity > 0f ? request.emissionIntensity : 1f;
                var hdrColor = new Color(
                    request.emissionColor[0] * intensity,
                    request.emissionColor[1] * intensity,
                    request.emissionColor[2] * intensity,
                    a);
                material.SetColor("_EmissionColor", hdrColor);
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            // Metallic
            if (request.metallic >= 0f)
                material.SetFloat("_Metallic", request.metallic);

            // Smoothness (try _Smoothness first, fall back to _Glossiness)
            if (request.smoothness >= 0f)
            {
                if (material.HasProperty("_Smoothness"))
                    material.SetFloat("_Smoothness", request.smoothness);
                else if (material.HasProperty("_Glossiness"))
                    material.SetFloat("_Glossiness", request.smoothness);
            }
        }

        #endregion

        #region Material Validation

        [BridgeRoute("POST", "/material/validate", Category = "materials", Description = "Validate material shader compatibility", ReadOnly = true)]
        public static string ValidateMaterial(string jsonData)
        {
            var request = JsonUtility.FromJson<ValidateMaterialRequest>(jsonData);

            Material material = null;

            // Load from path or from renderer on instanceId
            if (!string.IsNullOrEmpty(request.materialPath))
            {
                material = AssetDatabase.LoadAssetAtPath<Material>(request.materialPath);
                if (material == null)
                    return JsonError($"Material not found: {request.materialPath}");
            }
            else if (request.instanceId != 0)
            {
                var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
                if (go == null)
                    return JsonError("GameObject not found");
                var renderer = go.GetComponent<Renderer>();
                if (renderer == null)
                    return JsonError("No Renderer found on this GameObject");
                material = renderer.sharedMaterial;
                if (material == null)
                    return JsonError("Renderer has no material assigned");
            }
            else
            {
                return JsonError("Either materialPath or instanceId is required");
            }

            var shaderName = material.shader != null ? material.shader.name : "None";

            // Detect active render pipeline
            var rpAsset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            string pipelineName;
            bool isURP = false;
            if (rpAsset != null)
            {
                var rpTypeName = rpAsset.GetType().Name;
                isURP = rpTypeName.Contains("Universal") || rpTypeName.Contains("URP");
                pipelineName = isURP ? "URP" : rpTypeName;
            }
            else
            {
                pipelineName = "Built-in";
            }

            var issues = new List<object>();
            string suggestedShader = null;
            bool compatible = true;

            if (isURP)
            {
                // Check for built-in shaders that don't work in URP
                if (shaderName == "Standard" || shaderName == "Standard (Specular setup)")
                {
                    compatible = false;
                    issues.Add("Shader 'Standard' is not compatible with URP. Objects will render pink.");
                    suggestedShader = "Universal Render Pipeline/Lit";
                }
                else if (shaderName.StartsWith("Particles/Standard"))
                {
                    compatible = false;
                    issues.Add($"Shader '{shaderName}' is a built-in particle shader, not compatible with URP.");
                    suggestedShader = "Universal Render Pipeline/Particles/Unlit";
                }
                else if (shaderName.StartsWith("Legacy Shaders/") || shaderName.StartsWith("Mobile/"))
                {
                    compatible = false;
                    issues.Add($"Shader '{shaderName}' is a legacy/mobile built-in shader, not compatible with URP.");
                    suggestedShader = "Universal Render Pipeline/Simple Lit";
                }
                else if (shaderName.StartsWith("Unlit/") && !shaderName.Contains("Universal"))
                {
                    compatible = false;
                    issues.Add($"Shader '{shaderName}' is a built-in unlit shader.");
                    suggestedShader = "Universal Render Pipeline/Unlit";
                }
                else if (!shaderName.Contains("Universal Render Pipeline")
                    && !shaderName.Contains("Hidden/")
                    && !shaderName.Contains("Shader Graphs/")
                    && !shaderName.Contains("TextMeshPro/")
                    && !shaderName.Contains("Sprites/")
                    && !shaderName.Contains("UI/"))
                {
                    // Unknown shader in URP — warn but don't flag as incompatible
                    issues.Add($"Shader '{shaderName}' is not a known URP shader. It may work if it's a custom shader.");
                }
            }
            else
            {
                // Built-in pipeline
                if (shaderName.Contains("Universal Render Pipeline"))
                {
                    compatible = false;
                    issues.Add($"Shader '{shaderName}' is a URP shader but the project uses the Built-in pipeline.");
                    suggestedShader = "Standard";
                }
            }

            // Check for potential blend mode issues
            if (isURP && material.HasProperty("_SrcBlend") && material.HasProperty("_DstBlend"))
            {
                float src = material.GetFloat("_SrcBlend");
                float dst = material.GetFloat("_DstBlend");
                if (src != 1 || dst != 0)
                {
                    issues.Add("Material uses custom blend modes (SrcBlend/DstBlend). Verify these are compatible with URP transparency.");
                }
            }

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "compatible", compatible },
                { "materialName", material.name },
                { "materialPath", AssetDatabase.GetAssetPath(material) },
                { "shader", shaderName },
                { "pipeline", pipelineName },
                { "issues", issues },
                { "issueCount", issues.Count }
            };

            if (suggestedShader != null)
                result["suggestedShader"] = suggestedShader;

            return JsonResult(result);
        }

        #endregion

    }
}
