using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    /// <summary>
    /// Provides deep indexing of Unity project assets for AI-assisted development.
    /// </summary>
    public static class ProjectIndexer
    {
        private const int DefaultSummaryMaxEntries = 50;
        private const int MaxSearchResultsHardLimit = 1000;
        private const int MaxCacheEntries = 64;
        private const int DefaultScriptIndexCacheSeconds = 30;
        private static readonly Dictionary<string, CacheEntry> _jsonCache = new Dictionary<string, CacheEntry>();
        private static readonly object _cacheLock = new object();
        private static ScriptIndexCacheEntry _scriptIndexCacheBasic;
        private static ScriptIndexCacheEntry _scriptIndexCacheDetailed;

        private sealed class CacheEntry
        {
            public string payload;
            public DateTime expiresAtUtc;
            public DateTime createdAtUtc;
        }

        private sealed class ScriptIndexCacheEntry
        {
            public List<ScriptIndex> items;
            public DateTime expiresAtUtc;
        }

        /// <summary>
        /// Get a comprehensive index of the entire project.
        /// </summary>
        public static string GetProjectIndex(bool pretty = false, bool summary = true, int maxEntries = DefaultSummaryMaxEntries, int cacheSeconds = 15, bool includeScriptMembers = false)
        {
            maxEntries = Mathf.Clamp(maxEntries, 5, 500);
            cacheSeconds = Mathf.Clamp(cacheSeconds, 0, 300);

            var cacheKey = $"index|pretty={pretty}|summary={summary}|max={maxEntries}|scriptMembers={includeScriptMembers}";
            if (cacheSeconds > 0 && TryGetCachedPayload(cacheKey, out var cachedPayload))
            {
                return cachedPayload;
            }

            string payload;
            if (summary)
            {
                payload = GetProjectIndexSummary(maxEntries);
            }
            else
            {
                var index = new ProjectIndex
                {
                    scripts = IndexScripts(includeMemberDetails: includeScriptMembers, cacheSeconds: DefaultScriptIndexCacheSeconds),
                    prefabs = IndexPrefabs(),
                    scenes = IndexScenes(),
                    materials = IndexMaterials(),
                    scriptableObjects = IndexScriptableObjects(),
                    packages = GetPackageInfo(),
                    assemblies = GetAssemblyInfo(),
                    tags = GetAllTags(),
                    layers = GetAllLayers()
                };

                payload = JsonUtility.ToJson(index, pretty);
            }

            if (cacheSeconds > 0)
            {
                StoreCachedPayload(cacheKey, payload, cacheSeconds);
            }

            return payload;
        }

        private static string GetProjectIndexSummary(int maxEntries)
        {
            var scriptGuids = AssetDatabase.FindAssets("t:Script");
            var prefabGuids = AssetDatabase.FindAssets("t:Prefab");
            var sceneGuids = AssetDatabase.FindAssets("t:Scene");
            var materialGuids = AssetDatabase.FindAssets("t:Material");
            var soGuids = AssetDatabase.FindAssets("t:ScriptableObject");
            var asmGuids = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset");
            var allTags = UnityEditorInternal.InternalEditorUtility.tags;
            var allLayers = GetAllLayers();

            var buildSceneLookup = new Dictionary<string, (int index, bool enabled)>();
            for (int i = 0; i < EditorBuildSettings.scenes.Length; i++)
            {
                var scene = EditorBuildSettings.scenes[i];
                buildSceneLookup[scene.path] = (i, scene.enabled);
            }

            var summary = new Dictionary<string, object>
            {
                { "summary", true },
                { "maxEntries", maxEntries },
                { "counts", new Dictionary<string, object>
                    {
                        { "scripts", scriptGuids.Length },
                        { "prefabs", prefabGuids.Length },
                        { "scenes", sceneGuids.Length },
                        { "materials", materialGuids.Length },
                        { "scriptableObjects", soGuids.Length },
                        { "packages", GetPackageInfo().Count },
                        { "assemblies", asmGuids.Length },
                        { "tags", allTags.Length },
                        { "layers", allLayers.Count }
                    }
                },
                { "scriptsPreview", BuildPathPreview(scriptGuids, maxEntries, includeExtension: true) },
                { "prefabsPreview", BuildPathPreview(prefabGuids, maxEntries, includeExtension: false) },
                { "materialsPreview", BuildPathPreview(materialGuids, maxEntries, includeExtension: false) },
                { "scriptableObjectsPreview", BuildTypedPreview(soGuids, maxEntries) },
                { "assembliesPreview", BuildPathPreview(asmGuids, maxEntries, includeExtension: false) },
                { "scenesPreview", BuildScenePreview(sceneGuids, maxEntries, buildSceneLookup) },
                { "packagesPreview", GetPackageInfo().Take(maxEntries).Select(p => new Dictionary<string, object>
                    {
                        { "name", p.name },
                        { "version", p.version }
                    }).Cast<object>().ToList()
                },
                { "tags", allTags.Take(maxEntries).Cast<object>().ToList() },
                { "layers", allLayers.Take(maxEntries).Select(l => new Dictionary<string, object>
                    {
                        { "index", l.index },
                        { "name", l.name },
                        { "isBuiltIn", l.isBuiltIn }
                    }).Cast<object>().ToList()
                },
                { "tagsTruncated", allTags.Length > maxEntries },
                { "layersTruncated", allLayers.Count > maxEntries }
            };

            return UnityCommands.JsonResult(summary);
        }

        private static List<object> BuildPathPreview(string[] guids, int maxEntries, bool includeExtension)
        {
            var preview = new List<object>();
            foreach (var guid in guids.Take(maxEntries))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                preview.Add(new Dictionary<string, object>
                {
                    { "name", includeExtension ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path) },
                    { "path", path }
                });
            }
            return preview;
        }

        private static List<object> BuildTypedPreview(string[] guids, int maxEntries)
        {
            var preview = new List<object>();
            foreach (var guid in guids.Take(maxEntries))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                preview.Add(new Dictionary<string, object>
                {
                    { "name", Path.GetFileNameWithoutExtension(path) },
                    { "path", path },
                    { "type", type != null ? type.Name : "Unknown" }
                });
            }
            return preview;
        }

        private static List<object> BuildScenePreview(string[] guids, int maxEntries, Dictionary<string, (int index, bool enabled)> buildSceneLookup)
        {
            var preview = new List<object>();
            foreach (var guid in guids.Take(maxEntries))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var inBuild = buildSceneLookup.TryGetValue(path, out var buildInfo);
                preview.Add(new Dictionary<string, object>
                {
                    { "name", Path.GetFileNameWithoutExtension(path) },
                    { "path", path },
                    { "isInBuildSettings", inBuild },
                    { "buildIndex", inBuild ? buildInfo.index : -1 },
                    { "enabledInBuild", inBuild && buildInfo.enabled }
                });
            }
            return preview;
        }

        /// <summary>
        /// Search project assets by query.
        /// </summary>
        public static string SearchProject(string query, string assetType = null, int maxResults = 50, bool includeGuids = false, int cacheSeconds = 10)
        {
            maxResults = Mathf.Clamp(maxResults, 1, MaxSearchResultsHardLimit);
            cacheSeconds = Mathf.Clamp(cacheSeconds, 0, 300);

            var cacheKey = $"search|q={query}|type={assetType}|max={maxResults}|guid={includeGuids}";
            if (cacheSeconds > 0 && TryGetCachedPayload(cacheKey, out var cachedPayload))
            {
                return cachedPayload;
            }

            var results = new SearchResults
            {
                query = query,
                assetType = assetType,
                maxResults = maxResults,
                includeGuids = includeGuids,
                results = new List<SearchResult>()
            };

            string filter = string.IsNullOrEmpty(assetType) ? query : $"t:{assetType} {query}";
            var guids = AssetDatabase.FindAssets(filter);

            foreach (var guid in guids.Take(maxResults))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var assetTypeAtPath = AssetDatabase.GetMainAssetTypeAtPath(path);

                results.results.Add(new SearchResult
                {
                    name = Path.GetFileNameWithoutExtension(path),
                    path = path,
                    type = assetTypeAtPath != null ? assetTypeAtPath.Name : "Unknown",
                    guid = includeGuids ? guid : null
                });
            }

            results.returnedCount = results.results.Count;
            results.truncated = guids.Length > results.returnedCount;

            var payload = JsonUtility.ToJson(results, false);
            if (cacheSeconds > 0)
            {
                StoreCachedPayload(cacheKey, payload, cacheSeconds);
            }

            return payload;
        }

        private static bool TryGetCachedPayload(string key, out string payload)
        {
            lock (_cacheLock)
            {
                payload = null;
                if (!_jsonCache.TryGetValue(key, out var entry))
                {
                    return false;
                }

                if (DateTime.UtcNow > entry.expiresAtUtc)
                {
                    _jsonCache.Remove(key);
                    return false;
                }

                payload = entry.payload;
                return !string.IsNullOrEmpty(payload);
            }
        }

        private static void StoreCachedPayload(string key, string payload, int cacheSeconds)
        {
            lock (_cacheLock)
            {
                PruneCache_NoLock();
                _jsonCache[key] = new CacheEntry
                {
                    payload = payload,
                    createdAtUtc = DateTime.UtcNow,
                    expiresAtUtc = DateTime.UtcNow.AddSeconds(cacheSeconds)
                };
            }
        }

        private static void PruneCache_NoLock()
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _jsonCache
                .Where(kvp => kvp.Value == null || now > kvp.Value.expiresAtUtc)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                _jsonCache.Remove(key);
            }

            if (_jsonCache.Count <= MaxCacheEntries)
            {
                return;
            }

            var oldestKeys = _jsonCache
                .OrderBy(kvp => kvp.Value.createdAtUtc)
                .Take(_jsonCache.Count - MaxCacheEntries)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in oldestKeys)
            {
                _jsonCache.Remove(key);
            }
        }

        /// <summary>
        /// Index all C# scripts in the project.
        /// </summary>
        public static List<ScriptIndex> IndexScripts(bool includeMemberDetails = false, int cacheSeconds = DefaultScriptIndexCacheSeconds)
        {
            cacheSeconds = Mathf.Clamp(cacheSeconds, 0, 300);

            if (cacheSeconds > 0)
            {
                lock (_cacheLock)
                {
                    var cache = includeMemberDetails ? _scriptIndexCacheDetailed : _scriptIndexCacheBasic;
                    if (cache != null && DateTime.UtcNow <= cache.expiresAtUtc && cache.items != null)
                    {
                        return cache.items;
                    }
                }
            }

            var scripts = BuildScriptIndex(includeMemberDetails);

            if (cacheSeconds > 0)
            {
                lock (_cacheLock)
                {
                    var entry = new ScriptIndexCacheEntry
                    {
                        items = scripts,
                        expiresAtUtc = DateTime.UtcNow.AddSeconds(cacheSeconds)
                    };

                    if (includeMemberDetails)
                    {
                        _scriptIndexCacheDetailed = entry;
                    }
                    else
                    {
                        _scriptIndexCacheBasic = entry;
                    }
                }
            }

            return scripts;
        }

        private static List<ScriptIndex> BuildScriptIndex(bool includeMemberDetails)
        {
            var scripts = new List<ScriptIndex>();
            var guids = AssetDatabase.FindAssets("t:Script");

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".cs")) continue;

                var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (monoScript == null) continue;

                var scriptInfo = new ScriptIndex
                {
                    path = path,
                    fileName = Path.GetFileName(path),
                    guid = guid
                };

                var scriptClass = monoScript.GetClass();
                if (scriptClass != null)
                {
                    scriptInfo.className = scriptClass.Name;
                    scriptInfo.namespaceName = scriptClass.Namespace ?? "";
                    scriptInfo.baseClass = scriptClass.BaseType?.Name ?? "";
                    scriptInfo.isMonoBehaviour = typeof(MonoBehaviour).IsAssignableFrom(scriptClass);
                    scriptInfo.isScriptableObject = typeof(ScriptableObject).IsAssignableFrom(scriptClass);
                    scriptInfo.isEditor = typeof(Editor).IsAssignableFrom(scriptClass) ||
                                          scriptClass.Namespace?.Contains("Editor") == true ||
                                          path.Contains("/Editor/");

                    if (includeMemberDetails)
                    {
                        // Member reflection is expensive; only compute when explicitly requested.
                        scriptInfo.publicMethods = scriptClass
                            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                            .Where(m => !m.IsSpecialName)
                            .Select(m => m.Name)
                            .ToList();

                        scriptInfo.publicFields = scriptClass
                            .GetFields(BindingFlags.Public | BindingFlags.Instance)
                            .Select(f => $"{f.Name}: {f.FieldType.Name}")
                            .ToList();

                        scriptInfo.serializedFields = scriptClass
                            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                            .Where(f => f.GetCustomAttribute<SerializeField>() != null)
                            .Select(f => $"{f.Name}: {f.FieldType.Name}")
                            .ToList();
                    }
                }
                else
                {
                    // Try to parse basic info from file
                    try
                    {
                        var content = File.ReadAllText(path);
                        var classMatch = Regex.Match(content, @"class\s+(\w+)");
                        if (classMatch.Success)
                        {
                            scriptInfo.className = classMatch.Groups[1].Value;
                        }

                        var namespaceMatch = Regex.Match(content, @"namespace\s+([\w.]+)");
                        if (namespaceMatch.Success)
                        {
                            scriptInfo.namespaceName = namespaceMatch.Groups[1].Value;
                        }
                    }
                    catch { }
                }

                scripts.Add(scriptInfo);
            }

            return scripts;
        }

        /// <summary>
        /// Index all prefabs in the project.
        /// </summary>
        public static List<PrefabIndex> IndexPrefabs()
        {
            var prefabs = new List<PrefabIndex>();
            var guids = AssetDatabase.FindAssets("t:Prefab");

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                if (prefab == null) continue;

                var prefabInfo = new PrefabIndex
                {
                    name = prefab.name,
                    path = path,
                    guid = guid,
                    components = prefab.GetComponents<Component>()
                        .Where(c => c != null)
                        .Select(c => c.GetType().Name)
                        .ToList(),
                    childCount = CountChildren(prefab.transform)
                };

                // Check if it's a variant
                var prefabType = PrefabUtility.GetPrefabAssetType(prefab);
                prefabInfo.isVariant = prefabType == PrefabAssetType.Variant;

                if (prefabInfo.isVariant)
                {
                    var basePrefab = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
                    if (basePrefab != null)
                    {
                        prefabInfo.basePrefab = AssetDatabase.GetAssetPath(basePrefab);
                    }
                }

                prefabs.Add(prefabInfo);
            }

            return prefabs;
        }

        /// <summary>
        /// Index all scenes in the project.
        /// </summary>
        public static List<SceneIndex> IndexScenes()
        {
            var scenes = new List<SceneIndex>();
            var guids = AssetDatabase.FindAssets("t:Scene");

            // Get build settings scenes
            var buildScenes = new HashSet<string>();
            for (int i = 0; i < EditorBuildSettings.scenes.Length; i++)
            {
                buildScenes.Add(EditorBuildSettings.scenes[i].path);
            }

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);

                var sceneInfo = new SceneIndex
                {
                    name = Path.GetFileNameWithoutExtension(path),
                    path = path,
                    guid = guid,
                    isInBuildSettings = buildScenes.Contains(path)
                };

                if (sceneInfo.isInBuildSettings)
                {
                    for (int i = 0; i < EditorBuildSettings.scenes.Length; i++)
                    {
                        if (EditorBuildSettings.scenes[i].path == path)
                        {
                            sceneInfo.buildIndex = i;
                            sceneInfo.enabledInBuild = EditorBuildSettings.scenes[i].enabled;
                            break;
                        }
                    }
                }

                scenes.Add(sceneInfo);
            }

            return scenes;
        }

        /// <summary>
        /// Index all materials in the project.
        /// </summary>
        public static List<MaterialIndex> IndexMaterials()
        {
            var materials = new List<MaterialIndex>();
            var guids = AssetDatabase.FindAssets("t:Material");

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);

                if (material == null) continue;

                var matInfo = new MaterialIndex
                {
                    name = material.name,
                    path = path,
                    guid = guid,
                    shaderName = material.shader != null ? material.shader.name : "Unknown",
                    renderQueue = material.renderQueue
                };

                // Get main properties
                if (material.HasProperty("_Color"))
                {
                    var color = material.GetColor("_Color");
                    matInfo.mainColor = $"RGBA({color.r:F2}, {color.g:F2}, {color.b:F2}, {color.a:F2})";
                }

                if (material.HasProperty("_MainTex"))
                {
                    var tex = material.GetTexture("_MainTex");
                    matInfo.mainTexture = tex != null ? tex.name : null;
                }

                materials.Add(matInfo);
            }

            return materials;
        }

        /// <summary>
        /// Index all ScriptableObjects in the project.
        /// </summary>
        public static List<ScriptableObjectIndex> IndexScriptableObjects()
        {
            var sos = new List<ScriptableObjectIndex>();
            var guids = AssetDatabase.FindAssets("t:ScriptableObject");

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);

                if (so == null) continue;

                // Skip Unity internal types
                var typeName = so.GetType().FullName;
                if (typeName.StartsWith("UnityEngine.") || typeName.StartsWith("UnityEditor."))
                    continue;

                var soInfo = new ScriptableObjectIndex
                {
                    name = so.name,
                    path = path,
                    guid = guid,
                    typeName = so.GetType().Name,
                    fullTypeName = typeName
                };

                sos.Add(soInfo);
            }

            return sos;
        }

        /// <summary>
        /// Get package information from manifest.json.
        /// </summary>
        public static List<PackageInfo> GetPackageInfo()
        {
            var packages = new List<PackageInfo>();
            var manifestPath = "Packages/manifest.json";

            if (File.Exists(manifestPath))
            {
                try
                {
                    var content = File.ReadAllText(manifestPath);
                    // Simple parsing - extract dependencies
                    var match = Regex.Match(content, @"""dependencies"":\s*\{([^}]+)\}");
                    if (match.Success)
                    {
                        var deps = match.Groups[1].Value;
                        var depMatches = Regex.Matches(deps, @"""([^""]+)""\s*:\s*""([^""]+)""");
                        foreach (Match depMatch in depMatches)
                        {
                            packages.Add(new PackageInfo
                            {
                                name = depMatch.Groups[1].Value,
                                version = depMatch.Groups[2].Value
                            });
                        }
                    }
                }
                catch { }
            }

            return packages;
        }

        /// <summary>
        /// Get assembly definition information.
        /// </summary>
        public static List<AssemblyInfo> GetAssemblyInfo()
        {
            var assemblies = new List<AssemblyInfo>();
            var guids = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset");

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                try
                {
                    var content = File.ReadAllText(path);
                    var nameMatch = Regex.Match(content, @"""name"":\s*""([^""]+)""");

                    assemblies.Add(new AssemblyInfo
                    {
                        name = nameMatch.Success ? nameMatch.Groups[1].Value : Path.GetFileNameWithoutExtension(path),
                        path = path,
                        guid = guid
                    });
                }
                catch { }
            }

            return assemblies;
        }

        /// <summary>
        /// Get all tags defined in the project.
        /// </summary>
        public static List<string> GetAllTags()
        {
            return UnityEditorInternal.InternalEditorUtility.tags.ToList();
        }

        /// <summary>
        /// Get all layers defined in the project.
        /// </summary>
        public static List<LayerInfo> GetAllLayers()
        {
            var layers = new List<LayerInfo>();
            for (int i = 0; i < 32; i++)
            {
                var layerName = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(layerName))
                {
                    layers.Add(new LayerInfo
                    {
                        index = i,
                        name = layerName,
                        isBuiltIn = i < 8
                    });
                }
            }
            return layers;
        }

        private static int CountChildren(Transform transform)
        {
            int count = transform.childCount;
            foreach (Transform child in transform)
            {
                count += CountChildren(child);
            }
            return count;
        }

        #region Data Classes

        [Serializable]
        public class ProjectIndex
        {
            public List<ScriptIndex> scripts;
            public List<PrefabIndex> prefabs;
            public List<SceneIndex> scenes;
            public List<MaterialIndex> materials;
            public List<ScriptableObjectIndex> scriptableObjects;
            public List<PackageInfo> packages;
            public List<AssemblyInfo> assemblies;
            public List<string> tags;
            public List<LayerInfo> layers;
        }

        [Serializable]
        public class ScriptIndex
        {
            public string path;
            public string fileName;
            public string guid;
            public string className;
            public string namespaceName;
            public string baseClass;
            public bool isMonoBehaviour;
            public bool isScriptableObject;
            public bool isEditor;
            public List<string> publicMethods;
            public List<string> publicFields;
            public List<string> serializedFields;
        }

        [Serializable]
        public class PrefabIndex
        {
            public string name;
            public string path;
            public string guid;
            public List<string> components;
            public int childCount;
            public bool isVariant;
            public string basePrefab;
        }

        [Serializable]
        public class SceneIndex
        {
            public string name;
            public string path;
            public string guid;
            public bool isInBuildSettings;
            public int buildIndex;
            public bool enabledInBuild;
        }

        [Serializable]
        public class MaterialIndex
        {
            public string name;
            public string path;
            public string guid;
            public string shaderName;
            public int renderQueue;
            public string mainColor;
            public string mainTexture;
        }

        [Serializable]
        public class ScriptableObjectIndex
        {
            public string name;
            public string path;
            public string guid;
            public string typeName;
            public string fullTypeName;
        }

        [Serializable]
        public class PackageInfo
        {
            public string name;
            public string version;
        }

        [Serializable]
        public class AssemblyInfo
        {
            public string name;
            public string path;
            public string guid;
        }

        [Serializable]
        public class LayerInfo
        {
            public int index;
            public string name;
            public bool isBuiltIn;
        }

        [Serializable]
        public class SearchResults
        {
            public string query;
            public string assetType;
            public int maxResults;
            public int returnedCount;
            public bool truncated;
            public bool includeGuids;
            public List<SearchResult> results;
        }

        [Serializable]
        public class SearchResult
        {
            public string name;
            public string path;
            public string type;
            public string guid;
        }

        #endregion
    }
}
