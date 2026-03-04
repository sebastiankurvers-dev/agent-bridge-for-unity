using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        // Asset Catalogs: GenerateAssetCatalog, GetSavedAssetCatalog, PinAssetPackContext, GetAssetPackContextPin, ListAssetPackContextPins and helpers

        [BridgeRoute("POST", "/catalog/generate", Category = "profiles", Description = "Generate asset catalog", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string GenerateAssetCatalog(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<GenerateAssetCatalogRequest2>(jsonData ?? "{}");
                if (request == null) request = new GenerateAssetCatalogRequest2();
                var requestMap = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;

                if (string.IsNullOrWhiteSpace(request.rootFolder))
                    return JsonError("rootFolder is required");

                if (ValidateAssetPath(request.rootFolder) == null)
                    return JsonError($"Invalid root folder: {request.rootFolder}");

                if (!AssetDatabase.IsValidFolder(request.rootFolder))
                    return JsonError($"Folder not found: {request.rootFolder}");

                var catalogName = !string.IsNullOrWhiteSpace(request.name)
                    ? request.name
                    : MakeSafeFileName(request.rootFolder.Split('/').Last());
                var safeName = MakeSafeFileName(catalogName);

                bool includeGeometry = request.includeGeometry == 1;
                if (requestMap != null && requestMap.ContainsKey("includeGeometry"))
                    includeGeometry = ReadBool(requestMap, "includeGeometry", includeGeometry);

                bool reuseExisting = requestMap == null || !requestMap.ContainsKey("reuseExisting")
                    ? true
                    : ReadBool(requestMap, "reuseExisting", true);
                bool forceRegenerate = requestMap != null && ReadBool(requestMap, "forceRegenerate", false);

                // Save path
                var folderPath = "Assets/Editor/AssetCatalogs";
                EnsureAssetFolder(folderPath);
                var filePath = $"{folderPath}/{safeName}.json";

                if (ValidateAssetPath(filePath) == null)
                    return JsonError("Invalid catalog path");

                var projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..");
                var fullPath = System.IO.Path.Combine(projectRoot, filePath);

                // Fast path: reuse existing catalog by default
                if (reuseExisting && !forceRegenerate && System.IO.File.Exists(fullPath))
                {
                    if (TryReadCatalogSummary(fullPath, out int cachedEntryCount, out var cachedCategories, out string cachedRootFolder, out string generatedAt))
                    {
                        return JsonResult(new Dictionary<string, object>
                        {
                            { "success", true },
                            { "path", filePath },
                            { "entryCount", cachedEntryCount },
                            { "categories", cachedCategories },
                            { "catalogName", catalogName },
                            { "rootFolder", string.IsNullOrWhiteSpace(cachedRootFolder) ? request.rootFolder : cachedRootFolder },
                            { "includeGeometry", includeGeometry },
                            { "cacheHit", true },
                            { "reusedExisting", true },
                            { "generatedAt", generatedAt ?? string.Empty }
                        });
                    }
                }

                // Find all prefabs
                var guids = AssetDatabase.FindAssets("t:Prefab", new[] { request.rootFolder });
                var entries = new List<Dictionary<string, object>>();
                var categories = new Dictionary<string, int>();

                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var name = System.IO.Path.GetFileNameWithoutExtension(path);

                    // Derive category from subfolder
                    var relativePath = path.Substring(request.rootFolder.Length).TrimStart('/');
                    var segments = relativePath.Split('/');
                    var category = segments.Length > 1 ? segments[0] : "Root";

                    if (!categories.ContainsKey(category))
                        categories[category] = 0;
                    categories[category]++;

                    var entry = new Dictionary<string, object>
                    {
                        { "name", name },
                        { "path", path },
                        { "category", category }
                    };

                    if (includeGeometry)
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (prefab != null)
                        {
                            var renderer = prefab.GetComponentInChildren<Renderer>();
                            if (renderer != null)
                            {
                                var bounds = renderer.bounds;
                                entry["boundsSize"] = new List<object> { bounds.size.x, bounds.size.y, bounds.size.z };
                                entry["boundsCenter"] = new List<object> { bounds.center.x, bounds.center.y, bounds.center.z };
                            }
                            else
                            {
                                var collider = prefab.GetComponentInChildren<Collider>();
                                if (TryGetSafeColliderBounds(collider, out var cb))
                                {
                                    var bounds = cb;
                                    entry["boundsSize"] = new List<object> { bounds.size.x, bounds.size.y, bounds.size.z };
                                    entry["boundsCenter"] = new List<object> { bounds.center.x, bounds.center.y, bounds.center.z };
                                }
                            }
                        }
                    }

                    entries.Add(entry);
                }

                // Build catalog JSON
                var catalogData = new Dictionary<string, object>
                {
                    { "catalogVersion", 1 },
                    { "name", catalogName },
                    { "rootFolder", request.rootFolder },
                    { "generatedAt", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") },
                    { "entryCount", entries.Count },
                    { "categories", categories.ToDictionary(k => k.Key, k => (object)k.Value) },
                    { "entries", entries.Cast<object>().ToList() }
                };

                var catalogJson = MiniJSON.Json.Serialize(catalogData);
                System.IO.File.WriteAllText(fullPath, catalogJson);
                AssetDatabase.Refresh();

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "path", filePath },
                    { "entryCount", entries.Count },
                    { "categories", categories.ToDictionary(k => k.Key, k => (object)k.Value) },
                    { "catalogName", catalogName },
                    { "rootFolder", request.rootFolder },
                    { "includeGeometry", includeGeometry },
                    { "cacheHit", false },
                    { "reusedExisting", false },
                    { "generatedAt", catalogData["generatedAt"] }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/asset-pack/pin", Category = "profiles", Description = "Pin asset-pack context", TimeoutDefault = 10000)]
        public static string PinAssetPackContext(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<PinAssetPackRequest>(jsonData ?? "{}");
                if (request == null) request = new PinAssetPackRequest();
                var requestMap = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;

                if (string.IsNullOrWhiteSpace(request.rootFolder))
                    return JsonError("rootFolder is required");

                if (ValidateAssetPath(request.rootFolder) == null || !AssetDatabase.IsValidFolder(request.rootFolder))
                    return JsonError($"Invalid root folder: {request.rootFolder}");

                bool includeGeometry = request.includeGeometry == 1;
                if (requestMap != null && requestMap.ContainsKey("includeGeometry"))
                    includeGeometry = ReadBool(requestMap, "includeGeometry", includeGeometry);

                bool reuseExisting = requestMap == null || !requestMap.ContainsKey("reuseExisting")
                    ? true
                    : ReadBool(requestMap, "reuseExisting", true);
                bool forceRefresh = requestMap != null && ReadBool(requestMap, "forceRefresh", false);
                bool captureLookPreset = requestMap != null && requestMap.ContainsKey("captureLookPreset")
                    ? ReadBool(requestMap, "captureLookPreset", false)
                    : request.captureLookPreset == 1;
                bool captureSceneProfile = requestMap != null && requestMap.ContainsKey("captureSceneProfile")
                    ? ReadBool(requestMap, "captureSceneProfile", false)
                    : request.captureSceneProfile == 1;

                var packName = !string.IsNullOrWhiteSpace(request.name)
                    ? request.name
                    : MakeSafeFileName(request.rootFolder.Split('/').Last());
                var safePackName = MakeSafeFileName(packName);

                var catalogName = !string.IsNullOrWhiteSpace(request.catalogName)
                    ? request.catalogName
                    : safePackName;

                var catalogRequest = new Dictionary<string, object>
                {
                    { "rootFolder", request.rootFolder },
                    { "name", catalogName },
                    { "includeGeometry", includeGeometry ? 1 : 0 },
                    { "reuseExisting", reuseExisting ? 1 : 0 },
                    { "forceRegenerate", forceRefresh ? 1 : 0 }
                };
                var catalogJson = GenerateAssetCatalog(MiniJSON.Json.Serialize(catalogRequest));
                var catalogResult = MiniJSON.Json.Deserialize(catalogJson) as Dictionary<string, object>;
                if (catalogResult == null || !ReadBool(catalogResult, "success", false))
                {
                    return JsonError($"Failed to prepare catalog: {ReadString(catalogResult, "error") ?? "unknown error"}");
                }

                var catalogPath = ReadString(catalogResult, "path");
                bool catalogCacheHit = ReadBool(catalogResult, "cacheHit", false);

                var lookPresetName = !string.IsNullOrWhiteSpace(request.lookPresetName)
                    ? request.lookPresetName
                    : $"{safePackName}_Look";
                var safeLookPresetName = MakeSafeFileName(lookPresetName);
                var lookPresetPath = $"Assets/Editor/LookPresets/{safeLookPresetName}.json";
                bool lookPresetExists = RelativeAssetFileExists(lookPresetPath);
                bool lookPresetReused = false;
                bool lookPresetCaptured = false;

                if (captureLookPreset)
                {
                    if (reuseExisting && lookPresetExists && !forceRefresh)
                    {
                        lookPresetReused = true;
                    }
                    else
                    {
                        var saveLookJson = SaveLookPreset(JsonUtility.ToJson(new SaveLookPresetRequest
                        {
                            name = lookPresetName,
                            description = request.description ?? string.Empty
                        }));
                        var saveLookResult = MiniJSON.Json.Deserialize(saveLookJson) as Dictionary<string, object>;
                        if (saveLookResult == null || !ReadBool(saveLookResult, "success", false))
                        {
                            return JsonError($"Failed to capture look preset: {ReadString(saveLookResult, "error") ?? "unknown error"}");
                        }

                        lookPresetCaptured = true;
                        lookPresetPath = ReadString(saveLookResult, "path") ?? lookPresetPath;
                        lookPresetExists = true;
                    }
                }

                var sceneProfileName = !string.IsNullOrWhiteSpace(request.sceneProfileName)
                    ? request.sceneProfileName
                    : $"{safePackName}_Profile";
                var safeSceneProfileName = MakeSafeFileName(sceneProfileName);
                var sceneProfilePath = $"Assets/Editor/SceneProfiles/{safeSceneProfileName}.json";
                bool sceneProfileExists = RelativeAssetFileExists(sceneProfilePath);
                bool sceneProfileReused = false;
                bool sceneProfileCaptured = false;

                if (captureSceneProfile)
                {
                    if (reuseExisting && sceneProfileExists && !forceRefresh)
                    {
                        sceneProfileReused = true;
                    }
                    else
                    {
                        var extractProfileJson = ExtractSceneProfile(JsonUtility.ToJson(new ExtractSceneProfileRequest
                        {
                            name = sceneProfileName,
                            savePath = string.IsNullOrWhiteSpace(request.sceneProfileSavePath) ? "Assets/Editor/SceneProfiles" : request.sceneProfileSavePath
                        }));
                        var extractProfileResult = MiniJSON.Json.Deserialize(extractProfileJson) as Dictionary<string, object>;
                        if (extractProfileResult == null || !ReadBool(extractProfileResult, "success", false))
                        {
                            return JsonError($"Failed to capture scene profile: {ReadString(extractProfileResult, "error") ?? "unknown error"}");
                        }

                        sceneProfileCaptured = true;
                        sceneProfilePath = ReadString(extractProfileResult, "path") ?? sceneProfilePath;
                        sceneProfileExists = true;
                    }
                }

                var pinFolder = "Assets/Editor/AssetPins";
                EnsureAssetFolder(pinFolder);
                var pinPath = $"{pinFolder}/{safePackName}.json";

                if (ValidateAssetPath(pinPath) == null)
                    return JsonError("Invalid pin path");

                var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                var existingPin = RelativeAssetFileExists(pinPath) ? ReadJsonDictionaryFromAssetPath(pinPath) : null;
                string createdAt = ReadString(existingPin, "createdAt") ?? now;

                var pinData = new Dictionary<string, object>
                {
                    { "pinVersion", 1 },
                    { "name", packName },
                    { "rootFolder", request.rootFolder },
                    { "description", request.description ?? string.Empty },
                    { "createdAt", createdAt },
                    { "updatedAt", now },
                    { "catalog", new Dictionary<string, object>
                        {
                            { "name", catalogName },
                            { "path", catalogPath ?? string.Empty },
                            { "includeGeometry", includeGeometry },
                            { "cacheHit", catalogCacheHit },
                            { "reusedExisting", catalogCacheHit }
                        }
                    },
                    { "lookPreset", new Dictionary<string, object>
                        {
                            { "captured", captureLookPreset },
                            { "name", lookPresetName },
                            { "path", lookPresetPath },
                            { "exists", lookPresetExists },
                            { "reusedExisting", lookPresetReused },
                            { "newlyCaptured", lookPresetCaptured }
                        }
                    },
                    { "sceneProfile", new Dictionary<string, object>
                        {
                            { "captured", captureSceneProfile },
                            { "name", sceneProfileName },
                            { "path", sceneProfilePath },
                            { "exists", sceneProfileExists },
                            { "reusedExisting", sceneProfileReused },
                            { "newlyCaptured", sceneProfileCaptured }
                        }
                    }
                };

                var pinJson = MiniJSON.Json.Serialize(pinData);
                var projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..");
                var fullPinPath = System.IO.Path.Combine(projectRoot, pinPath);
                System.IO.File.WriteAllText(fullPinPath, pinJson);
                AssetDatabase.Refresh();

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "path", pinPath },
                    { "name", packName },
                    { "rootFolder", request.rootFolder },
                    { "catalogPath", catalogPath ?? string.Empty },
                    { "catalogCacheHit", catalogCacheHit },
                    { "lookPresetCaptured", lookPresetCaptured },
                    { "lookPresetReused", lookPresetReused },
                    { "sceneProfileCaptured", sceneProfileCaptured },
                    { "sceneProfileReused", sceneProfileReused }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        public static string GetAssetPackContextPin(string name, bool brief = true, int maxEntries = 40)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                    return JsonError("Pin name is required");

                var safeName = MakeSafeFileName(name);
                var pinPath = $"Assets/Editor/AssetPins/{safeName}.json";
                var projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..");
                var fullPinPath = System.IO.Path.Combine(projectRoot, pinPath);

                if (!System.IO.File.Exists(fullPinPath))
                    return JsonError($"Pinned context not found: {pinPath}");

                var content = System.IO.File.ReadAllText(fullPinPath);
                if (!brief)
                    return content;

                var parsed = MiniJSON.Json.Deserialize(content) as Dictionary<string, object>;
                if (parsed == null)
                    return JsonError("Failed to parse pinned context JSON");

                maxEntries = Mathf.Clamp(maxEntries, 1, 200);
                var catalog = parsed.TryGetValue("catalog", out var catalogObj) ? catalogObj as Dictionary<string, object> : null;
                var look = parsed.TryGetValue("lookPreset", out var lookObj) ? lookObj as Dictionary<string, object> : null;
                var profile = parsed.TryGetValue("sceneProfile", out var profileObj) ? profileObj as Dictionary<string, object> : null;
                bool truncated = false;

                var trimmedCatalog = LimitNestedCollections(catalog, maxEntries, ref truncated) as Dictionary<string, object>;
                var trimmedLook = LimitNestedCollections(look, maxEntries, ref truncated) as Dictionary<string, object>;
                var trimmedProfile = LimitNestedCollections(profile, maxEntries, ref truncated) as Dictionary<string, object>;

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "brief", true },
                    { "name", ReadString(parsed, "name") ?? safeName },
                    { "rootFolder", ReadString(parsed, "rootFolder") ?? string.Empty },
                    { "updatedAt", ReadString(parsed, "updatedAt") ?? string.Empty },
                    { "catalog", trimmedCatalog ?? new Dictionary<string, object>() },
                    { "lookPreset", trimmedLook ?? new Dictionary<string, object>() },
                    { "sceneProfile", trimmedProfile ?? new Dictionary<string, object>() },
                    { "maxEntries", maxEntries },
                    { "truncated", truncated },
                    { "path", pinPath }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static object LimitNestedCollections(object value, int maxEntries, ref bool truncated, int depth = 0)
        {
            if (value == null)
                return null;

            if (depth > 8)
                return value;

            if (value is Dictionary<string, object> dict)
            {
                var trimmed = new Dictionary<string, object>(dict.Count);
                foreach (var kvp in dict)
                {
                    trimmed[kvp.Key] = LimitNestedCollections(kvp.Value, maxEntries, ref truncated, depth + 1);
                }
                return trimmed;
            }

            if (value is IList<object> list)
            {
                int take = Math.Min(list.Count, maxEntries);
                if (list.Count > take)
                    truncated = true;

                var trimmedList = new List<object>(take);
                for (int i = 0; i < take; i++)
                {
                    trimmedList.Add(LimitNestedCollections(list[i], maxEntries, ref truncated, depth + 1));
                }
                return trimmedList;
            }

            return value;
        }

        [BridgeRoute("GET", "/asset-pack/pins", Category = "profiles", Description = "List pinned contexts", ReadOnly = true)]
        public static string ListAssetPackContextPins()
        {
            try
            {
                var pinFolder = "Assets/Editor/AssetPins";
                var projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..");
                var fullPinFolder = System.IO.Path.Combine(projectRoot, pinFolder);

                var pins = new List<object>();
                if (System.IO.Directory.Exists(fullPinFolder))
                {
                    foreach (var file in System.IO.Directory.GetFiles(fullPinFolder, "*.json"))
                    {
                        try
                        {
                            var content = System.IO.File.ReadAllText(file);
                            var parsed = MiniJSON.Json.Deserialize(content) as Dictionary<string, object>;
                            if (parsed == null) continue;

                            var catalog = parsed.TryGetValue("catalog", out var catalogObj)
                                ? catalogObj as Dictionary<string, object>
                                : null;
                            var look = parsed.TryGetValue("lookPreset", out var lookObj)
                                ? lookObj as Dictionary<string, object>
                                : null;
                            var profile = parsed.TryGetValue("sceneProfile", out var profileObj)
                                ? profileObj as Dictionary<string, object>
                                : null;

                            pins.Add(new Dictionary<string, object>
                            {
                                { "name", ReadString(parsed, "name") ?? System.IO.Path.GetFileNameWithoutExtension(file) },
                                { "path", $"{pinFolder}/{System.IO.Path.GetFileName(file)}" },
                                { "rootFolder", ReadString(parsed, "rootFolder") ?? string.Empty },
                                { "updatedAt", ReadString(parsed, "updatedAt") ?? string.Empty },
                                { "catalogPath", ReadString(catalog, "path") ?? string.Empty },
                                { "lookPresetPath", ReadString(look, "path") ?? string.Empty },
                                { "sceneProfilePath", ReadString(profile, "path") ?? string.Empty }
                            });
                        }
                        catch
                        {
                            // Skip malformed pin file
                        }
                    }
                }

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "count", pins.Count },
                    { "pins", pins }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        public static string GetSavedAssetCatalog(string name, bool brief = true, int maxEntries = 40)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                    return JsonError("Catalog name is required");

                var safeName = MakeSafeFileName(name);
                var filePath = $"Assets/Editor/AssetCatalogs/{safeName}.json";
                var projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..");
                var fullPath = System.IO.Path.Combine(projectRoot, filePath);

                if (!System.IO.File.Exists(fullPath))
                    return JsonError($"Catalog not found: {filePath}");

                var content = System.IO.File.ReadAllText(fullPath);
                if (!brief)
                    return content;

                var parsed = MiniJSON.Json.Deserialize(content) as Dictionary<string, object>;
                if (parsed == null)
                    return JsonError("Failed to parse catalog JSON");

                maxEntries = Mathf.Clamp(maxEntries, 1, 500);
                var entries = parsed.TryGetValue("entries", out var entriesObj) ? entriesObj as IList<object> : null;
                var compactEntries = new List<object>();

                if (entries != null)
                {
                    for (int i = 0; i < entries.Count && i < maxEntries; i++)
                    {
                        if (entries[i] is Dictionary<string, object> entry)
                        {
                            compactEntries.Add(new Dictionary<string, object>
                            {
                                { "name", ReadString(entry, "name") ?? string.Empty },
                                { "path", ReadString(entry, "path") ?? string.Empty },
                                { "category", ReadString(entry, "category") ?? string.Empty }
                            });
                        }
                    }
                }

                var categories = parsed.TryGetValue("categories", out var categoriesObj)
                    ? categoriesObj as Dictionary<string, object>
                    : new Dictionary<string, object>();

                int entryCount = 0;
                TryReadInt(parsed, "entryCount", out entryCount);

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "brief", true },
                    { "name", ReadString(parsed, "name") ?? safeName },
                    { "rootFolder", ReadString(parsed, "rootFolder") ?? string.Empty },
                    { "generatedAt", ReadString(parsed, "generatedAt") ?? string.Empty },
                    { "entryCount", entryCount },
                    { "categories", categories ?? new Dictionary<string, object>() },
                    { "entriesPreview", compactEntries },
                    { "entriesPreviewCount", compactEntries.Count },
                    { "path", filePath }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static bool TryReadCatalogSummary(
            string fullPath,
            out int entryCount,
            out Dictionary<string, object> categories,
            out string rootFolder,
            out string generatedAt)
        {
            entryCount = 0;
            categories = new Dictionary<string, object>();
            rootFolder = null;
            generatedAt = null;

            try
            {
                if (string.IsNullOrWhiteSpace(fullPath) || !System.IO.File.Exists(fullPath))
                    return false;

                var content = System.IO.File.ReadAllText(fullPath);
                var parsed = MiniJSON.Json.Deserialize(content) as Dictionary<string, object>;
                if (parsed == null)
                    return false;

                TryReadInt(parsed, "entryCount", out entryCount);
                rootFolder = ReadString(parsed, "rootFolder");
                generatedAt = ReadString(parsed, "generatedAt");

                if (parsed.TryGetValue("categories", out var categoriesObj) && categoriesObj is Dictionary<string, object> categoryDict)
                {
                    categories = new Dictionary<string, object>(categoryDict);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool RelativeAssetFileExists(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || ValidateAssetPath(assetPath) == null)
                return false;

            var projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..");
            var fullPath = System.IO.Path.Combine(projectRoot, assetPath);
            return System.IO.File.Exists(fullPath);
        }

        private static Dictionary<string, object> ReadJsonDictionaryFromAssetPath(string assetPath)
        {
            try
            {
                if (!RelativeAssetFileExists(assetPath))
                    return null;

                var projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..");
                var fullPath = System.IO.Path.Combine(projectRoot, assetPath);
                var content = System.IO.File.ReadAllText(fullPath);
                return MiniJSON.Json.Deserialize(content) as Dictionary<string, object>;
            }
            catch
            {
                return null;
            }
        }
    }
}
