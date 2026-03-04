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
        #region Shader Operations

        [BridgeRoute("POST", "/shader", Category = "shaders", Description = "Create shader")]
        public static string CreateShader(string jsonData)
        {
            var request = JsonUtility.FromJson<ShaderRequest>(jsonData);

            var path = request.path;
            if (string.IsNullOrEmpty(path))
            {
                return JsonError("Shader path is required");
            }

            if (!path.EndsWith(".shader")) path += ".shader";
            if (!path.StartsWith("Assets/")) path = "Assets/" + path;

            if (ValidateAssetPath(path) == null)
            {
                return JsonError("Path is outside the project directory");
            }

            try
            {
                // Create directory if needed
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                string content = request.content;
                if (string.IsNullOrEmpty(content))
                {
                    var shaderName = request.name ?? System.IO.Path.GetFileNameWithoutExtension(path);
                    content = ShaderTemplates.GenerateShaderTemplate(request.shaderType, shaderName);
                }

                System.IO.File.WriteAllText(path, content);

                // Track for checkpointing
                CheckpointManager.TrackScript(path);

                AssetDatabase.Refresh();

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "path", path }, { "message", "Shader created successfully" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("PUT", "/shader", Category = "shaders", Description = "Modify shader source")]
        public static string ModifyShader(string jsonData)
        {
            var request = JsonUtility.FromJson<ShaderRequest>(jsonData);

            if (string.IsNullOrEmpty(request.path))
            {
                return JsonError("Shader path is required");
            }

            if (ValidateAssetPath(request.path) == null)
            {
                return JsonError("Path is outside the project directory");
            }

            if (!System.IO.File.Exists(request.path))
            {
                return JsonError($"Shader not found: {request.path}");
            }

            try
            {
                // Track for checkpointing before modification
                CheckpointManager.TrackScript(request.path);

                System.IO.File.WriteAllText(request.path, request.content);
                AssetDatabase.Refresh();

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "path", request.path }, { "message", "Shader modified successfully" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        public static string GetShader(string path)
        {
            if (ValidateAssetPath(path) == null)
            {
                return JsonError("Path is outside the project directory");
            }
            if (!System.IO.File.Exists(path))
            {
                return JsonError($"Shader not found: {path}");
            }

            try
            {
                var content = System.IO.File.ReadAllText(path);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);

                var response = new ShaderResponse
                {
                    success = true,
                    path = path,
                    content = content,
                    lineCount = content.Split('\n').Length
                };

                if (shader != null)
                {
                    response.name = shader.name;
                    response.propertyCount = shader.GetPropertyCount();
                    response.isSupported = shader.isSupported;
                }

                return JsonUtility.ToJson(response, false);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        public static string FindShaders(string searchQuery, int maxResults = 100)
        {
            var shaders = new List<ShaderInfo>();
            var filter = string.IsNullOrWhiteSpace(searchQuery) ? "t:Shader" : $"t:Shader {searchQuery}";
            var guids = AssetDatabase.FindAssets(filter);
            maxResults = Math.Clamp(maxResults, 1, 5000);

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);

                if (shader != null)
                {
                    if (string.IsNullOrEmpty(searchQuery) ||
                        shader.name.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        path.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        shaders.Add(new ShaderInfo
                        {
                            name = shader.name,
                            path = path,
                            isSupported = shader.isSupported,
                            propertyCount = shader.GetPropertyCount()
                        });
                        if (shaders.Count >= maxResults)
                        {
                            break;
                        }
                    }
                }
            }

            var jsonParts = shaders.Select(s => JsonUtility.ToJson(s));
            return "{\"shaders\":[" + string.Join(",", jsonParts) + "]}";
        }

        public static string FindShadersScoped(
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

                var matches = new List<SearchMatch<ShaderInfo>>();
                var guids = AssetDatabase.FindAssets("t:Shader");

                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!PassesScope(path, include, exclude, includeSubfolders))
                    {
                        continue;
                    }

                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                    if (shader == null)
                    {
                        continue;
                    }

                    if (!TryScoreMatch(searchQuery, shader.name, path, matchMode, out int score))
                    {
                        continue;
                    }

                    matches.Add(new SearchMatch<ShaderInfo>
                    {
                        score = score,
                        item = new ShaderInfo
                        {
                            name = shader.name,
                            path = path,
                            isSupported = shader.isSupported,
                            propertyCount = shader.GetPropertyCount()
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
                        { "isSupported", m.item.isSupported },
                        { "propertyCount", m.item.propertyCount }
                    })
                    .ToList<object>();

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "shaders", ordered },
                    { "meta", BuildScopedSearchMeta(searchQuery, include, exclude, includeSubfolders, maxResults, matchMode, ordered.Count) }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static Dictionary<string, object> BuildScopedSearchMeta(
            string searchQuery,
            List<string> includeRoots,
            List<string> excludeRoots,
            bool includeSubfolders,
            int maxResults,
            string matchMode,
            int returnedCount)
        {
            return new Dictionary<string, object>
            {
                { "search", searchQuery ?? string.Empty },
                { "includeRoots", includeRoots.Cast<object>().ToList() },
                { "excludeRoots", excludeRoots.Cast<object>().ToList() },
                { "includeSubfolders", includeSubfolders },
                { "maxResults", maxResults },
                { "matchMode", NormalizeMatchMode(matchMode) },
                { "returnedCount", returnedCount }
            };
        }

        private static List<string> NormalizeScopeRoots(string[] roots)
        {
            var normalized = new List<string>();
            if (roots == null) return normalized;

            foreach (var raw in roots)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var root = raw.Trim().Replace('\\', '/');
                if (root.StartsWith("./", StringComparison.Ordinal))
                {
                    root = root.Substring(2);
                }

                if (root.StartsWith("Assets", StringComparison.OrdinalIgnoreCase)
                    || root.StartsWith("Packages", StringComparison.OrdinalIgnoreCase))
                {
                    root = root.TrimEnd('/');
                    if (!normalized.Any(existing => string.Equals(existing, root, StringComparison.OrdinalIgnoreCase)))
                    {
                        normalized.Add(root);
                    }
                }
            }

            return normalized;
        }

        private static bool PassesScope(string assetPath, List<string> includeRoots, List<string> excludeRoots, bool includeSubfolders)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            if (includeRoots.Count > 0 && !includeRoots.Any(r => IsPathInRoot(assetPath, r, includeSubfolders)))
            {
                return false;
            }

            if (excludeRoots.Count > 0 && excludeRoots.Any(r => IsPathInRoot(assetPath, r, true)))
            {
                return false;
            }

            return true;
        }

        private static bool IsPathInRoot(string assetPath, string root, bool includeSubfolders)
        {
            var pathNorm = assetPath.Replace('\\', '/');
            var rootNorm = root.Replace('\\', '/').TrimEnd('/');

            if (includeSubfolders)
            {
                return pathNorm.StartsWith(rootNorm + "/", StringComparison.OrdinalIgnoreCase);
            }

            var dir = System.IO.Path.GetDirectoryName(pathNorm)?.Replace('\\', '/').TrimEnd('/');
            return string.Equals(dir, rootNorm, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryScoreMatch(string query, string name, string path, string matchMode, out int score)
        {
            score = 0;
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }

            var mode = NormalizeMatchMode(matchMode);
            var q = query.Trim();

            switch (mode)
            {
                case "exact":
                    if (string.Equals(name, q, StringComparison.OrdinalIgnoreCase)) { score = 0; return true; }
                    if (string.Equals(path, q, StringComparison.OrdinalIgnoreCase)) { score = 1; return true; }
                    return false;

                case "fuzzy":
                    var best = int.MaxValue;
                    if (TryFuzzySubsequenceScore(name, q, out int nameScore))
                    {
                        best = Math.Min(best, nameScore);
                    }
                    if (TryFuzzySubsequenceScore(path, q, out int pathScore))
                    {
                        best = Math.Min(best, pathScore + 50);
                    }
                    if (best == int.MaxValue)
                    {
                        return false;
                    }
                    score = best;
                    return true;

                default:
                    var nameIdx = name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                    if (nameIdx >= 0)
                    {
                        score = nameIdx;
                        return true;
                    }

                    var pathIdx = path.IndexOf(q, StringComparison.OrdinalIgnoreCase);
                    if (pathIdx >= 0)
                    {
                        score = 100 + pathIdx;
                        return true;
                    }

                    return false;
            }
        }

        private static string NormalizeMatchMode(string matchMode)
        {
            if (string.IsNullOrWhiteSpace(matchMode)) return "contains";
            var mode = matchMode.Trim().ToLowerInvariant();
            if (mode == "exact" || mode == "fuzzy" || mode == "contains")
            {
                return mode;
            }
            return "contains";
        }

        private static bool TryFuzzySubsequenceScore(string text, string query, out int score)
        {
            score = 0;
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
            {
                return false;
            }

            var t = text.ToLowerInvariant();
            var q = query.ToLowerInvariant();

            int last = -1;
            int first = -1;
            for (int i = 0; i < q.Length; i++)
            {
                var idx = t.IndexOf(q[i], last + 1);
                if (idx < 0)
                {
                    score = 0;
                    return false;
                }

                if (first < 0) first = idx;
                if (last >= 0)
                {
                    score += (idx - last - 1);
                }
                last = idx;
            }

            // Prefer earlier + tighter matches
            score += first;
            score += Math.Max(0, t.Length - q.Length) / 10;
            return true;
        }

        private static Dictionary<string, object> BoundsToJson(Bounds bounds)
        {
            return new Dictionary<string, object>
            {
                { "center", new List<object> { bounds.center.x, bounds.center.y, bounds.center.z } },
                { "size", new List<object> { bounds.size.x, bounds.size.y, bounds.size.z } },
                { "min", new List<object> { bounds.min.x, bounds.min.y, bounds.min.z } },
                { "max", new List<object> { bounds.max.x, bounds.max.y, bounds.max.z } }
            };
        }

        private static Dictionary<string, object> BoundsRectToJson(Rect rect)
        {
            return new Dictionary<string, object>
            {
                { "center", new List<object> { rect.center.x, rect.center.y } },
                { "size", new List<object> { rect.width, rect.height } },
                { "min", new List<object> { rect.xMin, rect.yMin } },
                { "max", new List<object> { rect.xMax, rect.yMax } }
            };
        }

        private static string NormalizeFootprintSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return "hybrid";
            }

            var normalized = source.Trim().ToLowerInvariant();
            return normalized switch
            {
                "mesh" => "mesh",
                "collider" => "collider",
                "rendererbounds" => "rendererbounds",
                "renderer_bounds" => "rendererbounds",
                _ => "hybrid"
            };
        }

        private static void CollectPrefabMeshPoints(GameObject root, int maxPoints, List<Vector3> points, out int meshCount)
        {
            meshCount = 0;
            if (root == null || points == null || points.Count >= maxPoints) return;

            var meshFilters = root.GetComponentsInChildren<MeshFilter>(true)
                .Where(m => m != null && m.sharedMesh != null)
                .ToArray();

            meshCount = meshFilters.Length;
            if (meshFilters.Length == 0) return;

            // Use identity so points stay in world space (includes root scale)
            var rootWorldToLocal = Matrix4x4.identity;
            int remaining = Mathf.Max(1, maxPoints - points.Count);
            int targetPerMesh = Mathf.Max(24, remaining / Mathf.Max(1, meshFilters.Length));

            foreach (var meshFilter in meshFilters)
            {
                if (points.Count >= maxPoints) break;
                var mesh = meshFilter.sharedMesh;
                if (mesh == null) continue;

                var vertices = mesh.vertices;
                if (vertices == null || vertices.Length == 0) continue;

                var toRoot = rootWorldToLocal * meshFilter.transform.localToWorldMatrix;
                int step = Mathf.Max(1, vertices.Length / Mathf.Max(1, targetPerMesh));
                for (int i = 0; i < vertices.Length; i += step)
                {
                    points.Add(toRoot.MultiplyPoint3x4(vertices[i]));
                    if (points.Count >= maxPoints) break;
                }
            }
        }

        private static void CollectPrefabColliderPoints(GameObject root, int maxPoints, List<Vector3> points, out int colliderCount3D, out int colliderCount2D)
        {
            colliderCount3D = 0;
            colliderCount2D = 0;
            if (root == null || points == null || points.Count >= maxPoints) return;

            // Use identity so points stay in world space (includes root scale)
            var rootWorldToLocal = Matrix4x4.identity;

	            var colliders3D = root.GetComponentsInChildren<Collider>(true);
	            colliderCount3D = colliders3D.Length;
	            foreach (var collider in colliders3D)
	            {
	                if (points.Count >= maxPoints) break;
	                if (!TryGetSafeColliderBounds(collider, out var cb)) continue;
	                AddBoundsCornerSamples(cb, rootWorldToLocal, points, maxPoints);
	            }

            if (points.Count >= maxPoints) return;

            var colliders2D = root.GetComponentsInChildren<Collider2D>(true);
            colliderCount2D = colliders2D.Length;
            foreach (var collider2D in colliders2D)
            {
                if (points.Count >= maxPoints) break;
                AddBoundsCornerSamples(collider2D.bounds, rootWorldToLocal, points, maxPoints);
            }
        }

        private static void CollectPrefabRendererBoundsPoints(GameObject root, int maxPoints, List<Vector3> points, out int rendererCount)
        {
            rendererCount = 0;
            if (root == null || points == null || points.Count >= maxPoints) return;

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            rendererCount = renderers.Length;
            // Use identity so points stay in world space (includes root scale)
            var rootWorldToLocal = Matrix4x4.identity;
            foreach (var renderer in renderers)
            {
                if (points.Count >= maxPoints) break;
                AddBoundsCornerSamples(renderer.bounds, rootWorldToLocal, points, maxPoints);
            }
        }

        private static void AddBoundsCornerSamples(Bounds bounds, Matrix4x4 worldToLocal, List<Vector3> points, int maxPoints)
        {
            if (points == null || points.Count >= maxPoints) return;

            var corners = new[]
            {
                new Vector3(bounds.min.x, bounds.center.y, bounds.min.z),
                new Vector3(bounds.min.x, bounds.center.y, bounds.max.z),
                new Vector3(bounds.max.x, bounds.center.y, bounds.max.z),
                new Vector3(bounds.max.x, bounds.center.y, bounds.min.z)
            };

            foreach (var corner in corners)
            {
                points.Add(worldToLocal.MultiplyPoint3x4(corner));
                if (points.Count >= maxPoints) break;
            }
        }

        private static Rect ComputeBoundsRect2D(List<Vector2> points)
        {
            if (points == null || points.Count == 0)
            {
                return new Rect(0f, 0f, 0f, 0f);
            }

            float minX = points[0].x;
            float maxX = points[0].x;
            float minY = points[0].y;
            float maxY = points[0].y;

            for (int i = 1; i < points.Count; i++)
            {
                var p = points[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }

            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private static List<Vector2> ComputeConvexHull2D(List<Vector2> points)
        {
            if (points == null || points.Count == 0)
            {
                return new List<Vector2>();
            }

            var sorted = points
                .OrderBy(p => p.x)
                .ThenBy(p => p.y)
                .Distinct(new Vector2ApproxComparer(0.0001f))
                .ToList();

            if (sorted.Count <= 2) return sorted;

            var lower = new List<Vector2>();
            foreach (var point in sorted)
            {
                while (lower.Count >= 2 && Cross2D(lower[lower.Count - 2], lower[lower.Count - 1], point) <= 0f)
                {
                    lower.RemoveAt(lower.Count - 1);
                }
                lower.Add(point);
            }

            var upper = new List<Vector2>();
            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                var point = sorted[i];
                while (upper.Count >= 2 && Cross2D(upper[upper.Count - 2], upper[upper.Count - 1], point) <= 0f)
                {
                    upper.RemoveAt(upper.Count - 1);
                }
                upper.Add(point);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static float Cross2D(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        private static float ComputePolygonArea2D(List<Vector2> polygon)
        {
            if (polygon == null || polygon.Count < 3) return 0f;
            float sum = 0f;
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                sum += (a.x * b.y) - (b.x * a.y);
            }
            return 0.5f * sum;
        }

        private static float ComputePolygonPerimeter2D(List<Vector2> polygon)
        {
            if (polygon == null || polygon.Count < 2) return 0f;
            float perimeter = 0f;
            for (int i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Count];
                perimeter += Vector2.Distance(a, b);
            }
            return perimeter;
        }

        private static float ComputeMaxPointDistance2D(List<Vector2> points)
        {
            if (points == null || points.Count < 2) return 0f;
            float maxSq = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                for (int j = i + 1; j < points.Count; j++)
                {
                    float sq = (points[i] - points[j]).sqrMagnitude;
                    if (sq > maxSq) maxSq = sq;
                }
            }

            return Mathf.Sqrt(maxSq);
        }

        private static string GetHierarchyPath(Transform target)
        {
            if (target == null) return string.Empty;
            var names = new List<string>();
            var current = target;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private static int SeverityRank(string severity)
        {
            if (string.IsNullOrWhiteSpace(severity)) return 0;
            switch (severity.Trim().ToLowerInvariant())
            {
                case "error":
                    return 3;
                case "warning":
                    return 2;
                case "info":
                    return 1;
                default:
                    return 0;
            }
        }

        private static string SeverityFromRank(int rank)
        {
            return rank switch
            {
                >= 3 => "error",
                2 => "warning",
                1 => "info",
                _ => "none"
            };
        }

        private static string NormalizeSeverity(string severity, string fallback)
        {
            var normalized = severity?.Trim().ToLowerInvariant();
            if (normalized == "error" || normalized == "warning" || normalized == "info")
            {
                return normalized;
            }

            var fallbackNormalized = fallback?.Trim().ToLowerInvariant();
            if (fallbackNormalized == "error" || fallbackNormalized == "warning" || fallbackNormalized == "info")
            {
                return fallbackNormalized;
            }

            return "warning";
        }

        private static string GetTransformPath(Transform root, Transform target)
        {
            if (target == root) return root.name;

            var names = new List<string>();
            var current = target;
            while (current != null && current != root)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return root.name + "/" + string.Join("/", names);
        }

        private class SearchMatch<T>
        {
            public int score;
            public T item;
        }

        [BridgeRoute("POST", "/material/keyword", Category = "materials", Description = "Set/unset shader keyword on material")]
        public static string SetShaderKeyword(string jsonData)
        {
            var request = JsonUtility.FromJson<ShaderKeywordRequest>(jsonData);

            if (string.IsNullOrEmpty(request.materialPath))
            {
                return JsonError("Material path is required");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(request.materialPath);
            if (material == null)
            {
                return JsonError($"Material not found: {request.materialPath}");
            }

            try
            {
                Undo.RecordObject(material, "Agent Bridge Set Shader Keyword");

                if (request.enabled)
                    material.EnableKeyword(request.keyword);
                else
                    material.DisableKeyword(request.keyword);

                EditorUtility.SetDirty(material);
                AssetDatabase.SaveAssets();

                bool actualState = material.IsKeywordEnabled(request.keyword);
                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "keyword", request.keyword },
                    { "enabled", actualState },
                    { "verified", actualState == request.enabled },
                    { "message", $"Keyword '{request.keyword}' {(actualState ? "enabled" : "disabled")} on {material.name}" }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        public static string GetShaderProperties(string path)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null)
            {
                // Try to find by name
                shader = Shader.Find(path);
            }

            if (shader == null)
            {
                return JsonError($"Shader not found: {path}");
            }

            var properties = new List<ShaderPropertyInfo>();
            int propCount = shader.GetPropertyCount();

            for (int i = 0; i < propCount; i++)
            {
                properties.Add(new ShaderPropertyInfo
                {
                    name = shader.GetPropertyName(i),
                    description = shader.GetPropertyDescription(i),
                    type = shader.GetPropertyType(i).ToString(),
                    flags = shader.GetPropertyFlags(i).ToString()
                });
            }

            var jsonParts = properties.Select(p => JsonUtility.ToJson(p));
            return "{\"success\":true,\"shaderName\":\"" + shader.name.Replace("\"", "\\\"") + "\",\"propertyCount\":" + propCount + ",\"properties\":[" + string.Join(",", jsonParts) + "]}";
        }

        #endregion
    }
}
