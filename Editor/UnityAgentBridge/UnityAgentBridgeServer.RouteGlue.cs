using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    // Route-glue wrappers: query-parameter parsing for attribute-based dispatch.
    // Each method has [BridgeRoute] and uses the raw-context signature so the router
    // handles main-thread queueing automatically.
    public static partial class UnityAgentBridgeServer
    {
        // ==================== SERVER-INTERNAL ====================

        [BridgeRoute("GET", "/routes", Category = "meta", Description = "List all available routes", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetRoutes(string path, string method, string body, NameValueCollection query)
        {
            var catalog = BridgeRouter.GetCatalog();
            var categoryFilter = query["category"];
            var searchFilter = query["search"] ?? query["q"];
            int maxResults = ParseInt(query["max"], 0);
            bool compact = ParseBool(query["compact"] ?? query["brief"], false);

            IEnumerable<BridgeRouter.CatalogEntry> filtered = catalog;

            if (!string.IsNullOrWhiteSpace(categoryFilter))
            {
                filtered = filtered.Where(e => string.Equals(e.category, categoryFilter, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(searchFilter))
            {
                var needle = searchFilter.Trim();
                filtered = filtered.Where(e =>
                    e.path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                    || e.method.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                    || e.category.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                    || e.description.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (maxResults > 0)
            {
                filtered = filtered.Take(Math.Clamp(maxResults, 1, 500));
            }

            var serializable = new List<Dictionary<string, string>>();
            foreach (var entry in filtered)
            {
                var serializedEntry = new Dictionary<string, string>
                {
                    { "method", entry.method },
                    { "path", entry.path },
                    { "category", entry.category }
                };

                if (!compact)
                {
                    serializedEntry["description"] = entry.description;
                }

                serializable.Add(serializedEntry);
            }
            return (MiniJSON.Json.Serialize(serializable), 200);
        }

        [BridgeRoute("GET", "/health", Category = "meta", Description = "Server health + queue stats", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetHealth(string path, string method, string body, NameValueCollection query)
        {
            return (JsonUtility.ToJson(new HealthResponse
            {
                status = "ok",
                unityVersion = Application.unityVersion,
                projectName = Application.productName,
                isPlaying = EditorApplication.isPlaying,
                isCompiling = _isCompiling,
                pendingQueueSize = _mainThreadQueue.Count + _readQueue.Count,
                readQueueSize = _readQueue.Count,
                writeQueueSize = _mainThreadQueue.Count,
                activeMainThreadRequests = Math.Max(0, (int)Interlocked.Read(ref _activeMainThreadRequests) - 1),
                timedOutRequestCount = (int)Interlocked.Read(ref _timedOutRequestCount),
                canceledBeforeExecutionCount = (int)Interlocked.Read(ref _canceledBeforeExecutionCount),
                completedAfterTimeoutCount = (int)Interlocked.Read(ref _completedAfterTimeoutCount),
                domainReloadCount = _domainReloadCount,
                lastTickAge = (float)(DateTime.UtcNow - _lastTickTime).TotalSeconds,
                serverUptimeSeconds = (float)(DateTime.UtcNow - _serverStartTime).TotalSeconds
            }), 200);
        }

        [BridgeRoute("GET", "/events", Category = "meta", Description = "Poll event stream (long-poll supported)", Direct = true)]
        private static (string body, int statusCode) Route_GetEvents(string path, string method, string body, NameValueCollection query)
        {
            int sinceId = ParseInt(query["since"], 0);
            int timeoutSec = Math.Clamp(ParseInt(query["timeout"], 0), 0, 10);
            bool includeStackTrace = ParseBool(query["includeStackTrace"], false);
            return (GetEvents(sinceId, timeoutSec * 1000, includeStackTrace), 200);
        }

        // ==================== HIERARCHY / GAMEOBJECT / COMPONENTS ====================

        [BridgeRoute("GET", "/hierarchy", Category = "gameobjects", Description = "Get scene hierarchy tree", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetHierarchy(string path, string method, string body, NameValueCollection query)
        {
            int depth = ParseInt(query["depth"], 0);
            bool brief = ParseBool(query["brief"], true);
            bool pretty = ParseBool(query["pretty"], false);
            return (UnityCommands.GetHierarchy(depth, brief, pretty), 200);
        }

        [BridgeRoute("GET", "/gameobject/{id}", Category = "gameobjects", Description = "Get GameObject details by instance ID", ReadOnly = true, FailCode = 404)]
        private static (string body, int statusCode) Route_GetGameObject(string path, string method, string body, NameValueCollection query)
        {
            if (!TryParseIdFromPath(path, "/gameobject/", out int instanceId))
                return BadRequest("Invalid instance ID", "GET /gameobject/{id}");
            bool includeComponents = ParseBool(query["include_components"], false);
            bool transformOnly = ParseBool(query["transform_only"], false);
            return (UnityCommands.GetGameObject(instanceId, includeComponents, transformOnly), 200);
        }

        [BridgeRoute("GET", "/components/{id}", Category = "gameobjects", Description = "Get components on a GameObject", ReadOnly = true, FailCode = 404)]
        private static (string body, int statusCode) Route_GetComponents(string path, string method, string body, NameValueCollection query)
        {
            if (!TryParseIdFromPath(path, "/components/", out int instanceId))
                return BadRequest("Invalid instance ID", "GET /components/{id}");
            bool namesOnly = ParseBool(query["names_only"], true);
            return (UnityCommands.GetComponents(instanceId, namesOnly), 200);
        }

        [BridgeRoute("GET", "/gameobjects/find", Category = "gameobjects", Description = "Find GameObjects by name/component/tag/layer", ReadOnly = true)]
        private static (string body, int statusCode) Route_FindGameObjects(string path, string method, string body, NameValueCollection query)
        {
            var namePattern = query["name"] ?? query["namePattern"] ?? query["name_pattern"];
            var component = query["component"];
            var goTag = query["tag"];
            var goLayer = query["layer"];
            bool includeComponents = ParseBool(query["includeComponents"] ?? query["include_components"], false);
            int activeFilter = -1;
            if (!string.IsNullOrEmpty(query["active"]))
                activeFilter = query["active"].ToLowerInvariant() == "true" ? 1 : 0;
            int maxResults = ParseInt(query["max"], 100);
            if (maxResults <= 0) maxResults = 100;
            return (UnityCommands.FindGameObjects(namePattern, component, goTag, goLayer, activeFilter, maxResults, includeComponents), 200);
        }

        // ==================== CONSOLE ====================

        [BridgeRoute("GET", "/console", Category = "console", Description = "Get console log buffer", Direct = true)]
        private static (string body, int statusCode) Route_GetConsole(string path, string method, string body, NameValueCollection query)
        {
            int count = ParseInt(query["count"], 50);
            bool includeStackTrace = ParseBool(query["includeStackTrace"], false);
            return (UnityCommands.GetConsoleLogs(count, query["type"], query["text"], includeStackTrace), 200);
        }

        // ==================== SCREENSHOT ====================

        [BridgeRoute("GET", "/screenshot", Category = "screenshot", Description = "Capture screenshot (handle/base64/raw)")]
        private static (string body, int statusCode) Route_GetScreenshot(string path, string method, string body, NameValueCollection query)
        {
            var viewType = query["view"] ?? query["viewType"] ?? "game";
            string mode = (query["mode"] ?? string.Empty).Trim().ToLowerInvariant();
            bool hasMode = !string.IsNullOrWhiteSpace(mode);
            bool usesLegacyViewType = !string.IsNullOrWhiteSpace(query["viewType"]) && string.IsNullOrWhiteSpace(query["view"]);
            bool hasExplicitFlags = query["mode"] != null || query["includeBase64"] != null || query["includeHandle"] != null || query["raw"] != null || query["download"] != null;
            bool defaultRawLegacy = usesLegacyViewType && !hasExplicitFlags;
            bool raw = defaultRawLegacy || ParseBool(query["raw"], false) || ParseBool(query["download"], false);
            bool includeBase64;
            bool includeHandle;

            if (hasMode)
            {
                switch (mode)
                {
                    case "handle": includeBase64 = false; includeHandle = true; break;
                    case "base64": includeBase64 = true; includeHandle = false; break;
                    case "both": includeBase64 = true; includeHandle = true; break;
                    default: return BadRequest("Invalid screenshot mode. Use one of: handle, base64, both", "GET /screenshot");
                }
                if (query["includeBase64"] != null) includeBase64 = ParseBool(query["includeBase64"], includeBase64);
                if (query["includeHandle"] != null) includeHandle = ParseBool(query["includeHandle"], includeHandle);
            }
            else
            {
                includeBase64 = raw || ParseBool(query["includeBase64"], true);
                includeHandle = ParseBool(query["includeHandle"], !raw);
            }

            if (raw) includeBase64 = true;

            int reqWidth = ParseInt(query["width"], 0);
            int reqHeight = ParseInt(query["height"], 0);
            string requestedFormat = (query["format"] ?? string.Empty).Trim().ToLowerInvariant();
            string screenshotFormat = requestedFormat == "png" ? "png" : "jpeg";
            if (defaultRawLegacy && string.IsNullOrWhiteSpace(requestedFormat)) screenshotFormat = "png";

            return (UnityCommands.TakeScreenshot(viewType, includeBase64, includeHandle,
                requestedWidth: reqWidth, requestedHeight: reqHeight, imageFormat: screenshotFormat), 200);
        }

        // ==================== SCENE ====================

        [BridgeRoute("GET", "/scene/layout-snapshot", Category = "scene", Description = "Get spatial layout snapshot of scene tiles", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetSceneLayoutSnapshot(string path, string method, string body, NameValueCollection query)
        {
            var tileRoot = query["tileRoot"] ?? query["tileRootName"];
            int maxTiles = ParseInt(query["maxTiles"], 600);
            return (UnityCommands.GetSceneLayoutSnapshot(tileRoot, maxTiles), 200);
        }

        [BridgeRoute("GET", "/scene/export", Category = "scene", Description = "Export scene descriptor")]
        private static (string body, int statusCode) Route_GetSceneExport(string path, string method, string body, NameValueCollection query)
        {
            return (UnityCommands.ExportSceneDescriptor(body), 200);
        }

        [BridgeRoute("GET", "/scene/profile", Category = "scene", Description = "Get saved scene profile", ReadOnly = true, TimeoutDefault = 5000)]
        private static (string body, int statusCode) Route_GetSceneProfile(string path, string method, string body, NameValueCollection query)
        {
            var profileName = query["name"] ?? "";
            bool brief = ParseBool(query["brief"], true);
            int maxEntries = Math.Clamp(ParseInt(query["maxEntries"], 25), 1, 200);
            return (UnityCommands.GetSavedSceneProfile(profileName, brief, maxEntries), 200);
        }

        // ==================== IMAGE HANDLES ====================

        [BridgeRoute("GET", "/image/handle", Category = "screenshot", Description = "Get image handle metadata")]
        private static (string body, int statusCode) Route_GetImageHandle(string path, string method, string body, NameValueCollection query)
        {
            var requestJson = MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "imageHandle", query["imageHandle"] ?? query["handle"] ?? string.Empty },
                { "includeBase64", ParseBool(query["includeBase64"], false) ? 1 : 0 }
            });
            return (UnityCommands.GetImageHandle(requestJson), 200);
        }

        [BridgeRoute("DELETE", "/image/handle", Category = "screenshot", Description = "Delete image handle")]
        private static (string body, int statusCode) Route_DeleteImageHandle(string path, string method, string body, NameValueCollection query)
        {
            var requestJson = MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "imageHandle", query["imageHandle"] ?? query["handle"] ?? string.Empty }
            });
            return (UnityCommands.DeleteImageHandle(requestJson), 200);
        }

        // ==================== PREFABS ====================

        [BridgeRoute("GET", "/prefabs", Category = "assets", Description = "Find prefabs by search query", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetPrefabs(string path, string method, string body, NameValueCollection query)
        {
            var searchQuery = query["search"] ?? "";
            int maxResults = ParseInt(query["max"], 100);
            return (UnityCommands.FindPrefabs(searchQuery, maxResults), 200);
        }

        [BridgeRoute("GET", "/prefabs/scoped", Category = "assets", Description = "Find prefabs with folder scoping", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetPrefabsScoped(string path, string method, string body, NameValueCollection query)
        {
            var searchQuery = query["search"] ?? "";
            var includeRoots = ParseCsvList(query, "includeRoots");
            var excludeRoots = ParseCsvList(query, "excludeRoots");
            bool includeSubfolders = ParseBool(query["includeSubfolders"], true);
            int maxResults = ParseInt(query["max"], 200);
            var matchMode = query["matchMode"] ?? "contains";
            return (UnityCommands.FindPrefabsScoped(searchQuery, includeRoots, excludeRoots, includeSubfolders, maxResults, matchMode), 200);
        }

        [BridgeRoute("GET", "/prefab/geometry", Category = "assets", Description = "Get prefab geometry/sockets", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetPrefabGeometry(string path, string method, string body, NameValueCollection query)
        {
            var prefabPath = query["path"];
            if (string.IsNullOrEmpty(prefabPath))
                return BadRequest("Prefab path required", "GET /prefab/geometry");
            var socketPrefixes = ParseCsvList(query, "socketPrefixes");
            var geometryRequest = MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "path", prefabPath },
                { "includeSockets", ParseBool(query["includeSockets"], true) ? 1 : 0 },
                { "includeChildren", ParseBool(query["includeChildren"], false) ? 1 : 0 },
                { "socketPrefixes", socketPrefixes.Cast<object>().ToList() },
                { "includeAccurateBounds", ParseBool(query["includeAccurateBounds"], false) ? 1 : 0 }
            });
            return (UnityCommands.GetPrefabGeometry(geometryRequest), 200);
        }

        [BridgeRoute("GET", "/prefab/footprint2d", Category = "assets", Description = "Get prefab 2D footprint polygon", ReadOnly = true,
            TimeoutDefault = 12000, TimeoutMin = 250, TimeoutMax = 120000)]
        private static (string body, int statusCode) Route_GetPrefabFootprint2D(string path, string method, string body, NameValueCollection query)
        {
            var prefabPath = query["path"];
            if (string.IsNullOrEmpty(prefabPath))
                return BadRequest("Prefab path required", "GET /prefab/footprint2d");
            var requestJson = MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "path", prefabPath },
                { "source", query["source"] ?? "hybrid" },
                { "targetMinEdgeGap", ParseFloat(query["targetMinEdgeGap"], 0.04f) },
                { "maxPoints", ParseInt(query["maxPoints"], 6000) },
                { "includeHull", ParseBool(query["includeHull"], true) ? 1 : 0 },
                { "includeSamplePoints", ParseBool(query["includeSamplePoints"], false) ? 1 : 0 }
            });
            return (UnityCommands.GetPrefabFootprint2D(requestJson), 200);
        }

        [BridgeRoute("GET", "/prefab/preview", Category = "assets", Description = "Get prefab thumbnail preview", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetPrefabPreview(string path, string method, string body, NameValueCollection query)
        {
            var prefabPath = query["path"];
            int size = ParseInt(query["size"], 128);
            if (size <= 0) size = 128;
            if (string.IsNullOrEmpty(prefabPath))
                return BadRequest("Prefab path required", "GET /prefab/preview");
            return (UnityCommands.GetPrefabPreview(prefabPath, size), 200);
        }

        [BridgeRoute("GET", "/prefabs/previews", Category = "assets", Description = "Batch prefab previews with thumbnails", ReadOnly = true,
            TimeoutDefault = 10000, TimeoutMin = 250, TimeoutMax = 120000)]
        private static (string body, int statusCode) Route_GetPrefabPreviews(string path, string method, string body, NameValueCollection query)
        {
            var searchQuery = query["search"] ?? "";
            int maxResults = ParseInt(query["max"], 20);
            int size = ParseInt(query["size"], 64);
            bool includeThumbnails = ParseBool(query["includeThumbnails"], false);
            if (maxResults <= 0) maxResults = 20;
            if (size <= 0) size = 64;
            return (UnityCommands.GetPrefabPreviews(searchQuery, maxResults, size, includeThumbnails), 200);
        }

        // ==================== MATERIALS ====================

        [BridgeRoute("GET", "/material", Category = "assets", Description = "Find materials by search query", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetMaterial(string path, string method, string body, NameValueCollection query)
        {
            var searchQuery = query["search"] ?? "";
            int maxResults = ParseInt(query["max"], 100);
            return (UnityCommands.FindMaterials(searchQuery, maxResults), 200);
        }

        [BridgeRoute("GET", "/materials/scoped", Category = "assets", Description = "Find materials with folder scoping", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetMaterialsScoped(string path, string method, string body, NameValueCollection query)
        {
            var searchQuery = query["search"] ?? "";
            var includeRoots = ParseCsvList(query, "includeRoots");
            var excludeRoots = ParseCsvList(query, "excludeRoots");
            bool includeSubfolders = ParseBool(query["includeSubfolders"], true);
            int maxResults = ParseInt(query["max"], 200);
            var matchMode = query["matchMode"] ?? "contains";
            return (UnityCommands.FindMaterialsScoped(searchQuery, includeRoots, excludeRoots, includeSubfolders, maxResults, matchMode), 200);
        }

        [BridgeRoute("GET", "/material/preview", Category = "assets", Description = "Get material thumbnail preview", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetMaterialPreview(string path, string method, string body, NameValueCollection query)
        {
            var materialPath = query["path"];
            int size = ParseInt(query["size"], 128);
            if (size <= 0) size = 128;
            if (string.IsNullOrEmpty(materialPath))
                return BadRequest("Material path required", "GET /material/preview");
            return (UnityCommands.GetMaterialPreview(materialPath, size), 200);
        }

        [BridgeRoute("GET", "/materials/previews", Category = "assets", Description = "Batch material previews with thumbnails", ReadOnly = true,
            TimeoutDefault = 10000, TimeoutMin = 250, TimeoutMax = 120000)]
        private static (string body, int statusCode) Route_GetMaterialPreviews(string path, string method, string body, NameValueCollection query)
        {
            var searchQuery = query["search"] ?? "";
            int maxResults = ParseInt(query["max"], 20);
            int size = ParseInt(query["size"], 64);
            bool includeThumbnails = ParseBool(query["includeThumbnails"], false);
            if (maxResults <= 0) maxResults = 20;
            if (size <= 0) size = 64;
            return (UnityCommands.GetMaterialPreviews(searchQuery, maxResults, size, includeThumbnails), 200);
        }

        // ==================== SCRIPTS ====================

        [BridgeRoute("GET", "/scripts/list", Category = "scripts", Description = "List scripts with filters",
            TimeoutDefault = 10000, TimeoutMin = 250, TimeoutMax = 120000)]
        private static (string body, int statusCode) Route_GetScriptsList(string path, string method, string body, NameValueCollection query)
        {
            var nameFilter = query["name"];
            int isMB = ParseBoolInt(query["isMonoBehaviour"]);
            int isSO = ParseBoolInt(query["isScriptableObject"]);
            int offset = ParseInt(query["offset"], 0);
            int limit = ParseInt(query["limit"], 50);
            if (limit <= 0) limit = 50;
            return (UnityCommands.ListScripts(nameFilter, isMB, isSO, offset, limit), 200);
        }

        [BridgeRoute("GET", "/scripts/structure", Category = "scripts", Description = "Get script structure (methods/fields/properties)")]
        private static (string body, int statusCode) Route_GetScriptsStructure(string path, string method, string body, NameValueCollection query)
        {
            var scriptPath = query["path"];
            if (string.IsNullOrEmpty(scriptPath))
                return BadRequest("Script path required", "GET /scripts/structure");
            bool includeMethods = ParseBool(query["includeMethods"], true);
            bool includeFields = ParseBool(query["includeFields"], true);
            bool includeProperties = ParseBool(query["includeProperties"], true);
            bool includeEvents = ParseBool(query["includeEvents"], true);
            int maxMethods = ParseInt(query["maxMethods"], -1);
            int maxFields = ParseInt(query["maxFields"], -1);
            int maxProperties = ParseInt(query["maxProperties"], -1);
            int maxEvents = ParseInt(query["maxEvents"], -1);
            bool includeAttributes = ParseBool(query["includeAttributes"], true);
            bool includeMethodParameters = ParseBool(query["includeMethodParameters"] ?? query["includeParameters"], true);
            return (UnityCommands.GetScriptStructure(scriptPath, includeMethods, includeFields, includeProperties, includeEvents,
                maxMethods, maxFields, maxProperties, maxEvents, includeAttributes, includeMethodParameters), 200);
        }

        [BridgeRoute("GET", "/script", Category = "scripts", Description = "Get script source code")]
        private static (string body, int statusCode) Route_GetScript(string path, string method, string body, NameValueCollection query)
        {
            var scriptPath = query["path"];
            if (string.IsNullOrEmpty(scriptPath))
                return BadRequest("Script path required", "GET /script");
            return (UnityCommands.GetScript(scriptPath), 200);
        }

        // ==================== SHADERS ====================

        [BridgeRoute("GET", "/shader", Category = "shaders", Description = "Get shader source")]
        private static (string body, int statusCode) Route_GetShader(string path, string method, string body, NameValueCollection query)
        {
            var shaderPath = query["path"];
            if (string.IsNullOrEmpty(shaderPath))
                return BadRequest("Shader path required", "GET /shader");
            return (UnityCommands.GetShader(shaderPath), 200);
        }

        [BridgeRoute("GET", "/shaders", Category = "shaders", Description = "Find shaders by search query", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetShaders(string path, string method, string body, NameValueCollection query)
        {
            var searchQuery = query["search"] ?? "";
            int maxResults = ParseInt(query["max"], 100);
            return (UnityCommands.FindShaders(searchQuery, maxResults), 200);
        }

        [BridgeRoute("GET", "/shaders/scoped", Category = "shaders", Description = "Find shaders with folder scoping", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetShadersScoped(string path, string method, string body, NameValueCollection query)
        {
            var searchQuery = query["search"] ?? "";
            var includeRoots = ParseCsvList(query, "includeRoots");
            var excludeRoots = ParseCsvList(query, "excludeRoots");
            bool includeSubfolders = ParseBool(query["includeSubfolders"], true);
            int maxResults = ParseInt(query["max"], 200);
            var matchMode = query["matchMode"] ?? "contains";
            return (UnityCommands.FindShadersScoped(searchQuery, includeRoots, excludeRoots, includeSubfolders, maxResults, matchMode), 200);
        }

        [BridgeRoute("GET", "/shader/properties", Category = "shaders", Description = "Get shader property list", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetShaderProperties(string path, string method, string body, NameValueCollection query)
        {
            var shaderPath = query["path"];
            if (string.IsNullOrEmpty(shaderPath))
                return BadRequest("Shader path required", "GET /shader/properties");
            return (UnityCommands.GetShaderProperties(shaderPath), 200);
        }

        // ==================== TAGS / LAYERS ====================

        [BridgeRoute("POST", "/tag", Category = "gameobjects", Description = "Create a new tag")]
        private static (string body, int statusCode) Route_PostTag(string path, string method, string body, NameValueCollection query)
        {
            var tagName = query["name"];
            if (string.IsNullOrEmpty(tagName) && !string.IsNullOrEmpty(body))
            {
                try
                {
                    var dict = MiniJSON.Json.Deserialize(body) as Dictionary<string, object>;
                    if (dict != null && dict.ContainsKey("name"))
                        tagName = dict["name"]?.ToString();
                }
                catch { }
            }
            return (UnityCommands.CreateTag(tagName), 200);
        }

        [BridgeRoute("POST", "/layer", Category = "gameobjects", Description = "Create a new layer")]
        private static (string body, int statusCode) Route_PostLayer(string path, string method, string body, NameValueCollection query)
        {
            var layerName = query["name"];
            int layerIndex = -1;
            int.TryParse(query["index"], out layerIndex);
            if (string.IsNullOrEmpty(layerName) && !string.IsNullOrEmpty(body))
            {
                try
                {
                    var dict = MiniJSON.Json.Deserialize(body) as Dictionary<string, object>;
                    if (dict != null)
                    {
                        if (dict.ContainsKey("name")) layerName = dict["name"]?.ToString();
                        if (dict.ContainsKey("index")) int.TryParse(dict["index"]?.ToString(), out layerIndex);
                    }
                }
                catch { }
            }
            return (UnityCommands.CreateLayer(layerName, layerIndex), 200);
        }

        // ==================== PROJECT INDEX ====================

        [BridgeRoute("GET", "/index", Category = "assets", Description = "Get project asset index",
            TimeoutDefault = 10000, TimeoutMin = 250, TimeoutMax = 120000)]
        private static (string body, int statusCode) Route_GetIndex(string path, string method, string body, NameValueCollection query)
        {
            bool pretty = ParseBool(query["pretty"], false);
            bool summary = ParseBool(query["summary"], true);
            int maxEntries = ParseInt(query["maxEntries"], 50);
            int cacheSeconds = ParseInt(query["cacheSeconds"], 15);
            bool includeScriptMembers = ParseBool(query["includeScriptMembers"], false);
            return (ProjectIndexer.GetProjectIndex(pretty, summary, maxEntries, cacheSeconds, includeScriptMembers), 200);
        }

        [BridgeRoute("GET", "/search", Category = "assets", Description = "Search project assets")]
        private static (string body, int statusCode) Route_GetSearch(string path, string method, string body, NameValueCollection query)
        {
            var searchQuery = query["query"] ?? query["q"] ?? "";
            var assetType = query["type"];
            int maxResults = ParseInt(query["max"], 50);
            bool includeGuids = ParseBool(query["includeGuids"], false);
            int cacheSeconds = ParseInt(query["cacheSeconds"], 10);
            return (ProjectIndexer.SearchProject(searchQuery, assetType, maxResults, includeGuids, cacheSeconds), 200);
        }

        // ==================== CHECKPOINTS ====================

        [BridgeRoute("GET", "/checkpoint", Category = "checkpoints", Description = "List all checkpoints", TimeoutDefault = 5000)]
        private static (string body, int statusCode) Route_GetCheckpoint(string path, string method, string body, NameValueCollection query)
        {
            return (CheckpointManager.ListCheckpoints(), 200);
        }

        [BridgeRoute("POST", "/checkpoint", Category = "checkpoints", Description = "Create a checkpoint",
            TimeoutDefault = 10000, TimeoutMin = 250, TimeoutMax = 120000)]
        private static (string body, int statusCode) Route_PostCheckpoint(string path, string method, string body, NameValueCollection query)
        {
            var checkpointName = query["name"];
            bool includeRecentScripts = ParseBool(query["includeRecentScripts"], false);
            int maxRecentScripts = ParseInt(query["maxRecentScripts"], 50);
            if (string.IsNullOrEmpty(checkpointName) && !string.IsNullOrEmpty(body))
            {
                try
                {
                    var dict = MiniJSON.Json.Deserialize(body) as Dictionary<string, object>;
                    if (dict != null)
                    {
                        if (dict.ContainsKey("name")) checkpointName = dict["name"]?.ToString();
                        if (dict.ContainsKey("includeRecentScripts")) includeRecentScripts = ParseBool(dict["includeRecentScripts"]?.ToString(), includeRecentScripts);
                        if (dict.ContainsKey("maxRecentScripts")) maxRecentScripts = ParseInt(dict["maxRecentScripts"]?.ToString(), maxRecentScripts);
                    }
                }
                catch { }
            }
            return (CheckpointManager.CreateCheckpoint(checkpointName, includeRecentScripts, maxRecentScripts), 200);
        }

        [BridgeRoute("DELETE", "/checkpoint/{id}", Category = "checkpoints", Description = "Delete a checkpoint by ID")]
        private static (string body, int statusCode) Route_DeleteCheckpoint(string path, string method, string body, NameValueCollection query)
        {
            var checkpointId = path.Substring("/checkpoint/".Length);
            return (CheckpointManager.DeleteCheckpoint(checkpointId), 200);
        }

        [BridgeRoute("POST", "/restore/{id}", Category = "checkpoints", Description = "Restore a checkpoint by ID",
            TimeoutDefault = 10000, TimeoutMin = 250, TimeoutMax = 120000)]
        private static (string body, int statusCode) Route_PostRestore(string path, string method, string body, NameValueCollection query)
        {
            var checkpointId = path.Substring("/restore/".Length);
            return (CheckpointManager.RestoreCheckpoint(checkpointId), 200);
        }

        [BridgeRoute("GET", "/diff", Category = "checkpoints", Description = "Get file diff against checkpoint", Direct = true)]
        private static (string body, int statusCode) Route_GetDiff(string path, string method, string body, NameValueCollection query)
        {
            var filePath = query["path"];
            if (string.IsNullOrEmpty(filePath))
                return BadRequest("File path required", "GET /diff");
            return (CheckpointManager.GetDiff(filePath, query["checkpoint"]), 200);
        }

        // ==================== UI TOOLKIT ====================

        [BridgeRoute("GET", "/uitoolkit/panelsettings", Category = "uitoolkit", Description = "Get PanelSettings asset details", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetPanelSettings(string path, string method, string body, NameValueCollection query)
        {
            var psPath = query["path"];
            if (string.IsNullOrEmpty(psPath))
                return BadRequest("PanelSettings path required", "GET /uitoolkit/panelsettings");
            return (UnityCommands.GetPanelSettings(psPath), 200);
        }

        [BridgeRoute("GET", "/uitoolkit/uxml", Category = "uitoolkit", Description = "Read UXML document", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetUxml(string path, string method, string body, NameValueCollection query)
        {
            var uxmlPath = query["path"];
            if (string.IsNullOrEmpty(uxmlPath))
                return BadRequest("UXML path required", "GET /uitoolkit/uxml");
            return (UnityCommands.ReadUXML(uxmlPath), 200);
        }

        [BridgeRoute("GET", "/uitoolkit/uss", Category = "uitoolkit", Description = "Read USS stylesheet", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetUss(string path, string method, string body, NameValueCollection query)
        {
            var ussPath = query["path"];
            if (string.IsNullOrEmpty(ussPath))
                return BadRequest("USS path required", "GET /uitoolkit/uss");
            return (UnityCommands.ReadUSS(ussPath), 200);
        }

        [BridgeRoute("GET", "/uitoolkit/tree/{id}", Category = "uitoolkit", Description = "Get visual tree for UIDocument", ReadOnly = true, FailCode = 404)]
        private static (string body, int statusCode) Route_GetVisualTree(string path, string method, string body, NameValueCollection query)
        {
            if (!TryParseIdFromPath(path, "/uitoolkit/tree/", out int instanceId))
                return BadRequest("Invalid instance ID", "GET /uitoolkit/tree/{id}");
            int maxDepth = ParseInt(query["maxDepth"], -1);
            int inclStyles = ParseBoolInt(query["includeStyles"]);
            int offset = ParseInt(query["offset"], -1);
            int limit = ParseInt(query["limit"], -1);
            int compact = ParseBoolInt(query["compact"]);
            int includeBounds = ParseBoolInt(query["includeBounds"]);
            int includeClasses = ParseBoolInt(query["includeClasses"]);
            int includeText = ParseBoolInt(query["includeText"]);
            string queryJson = null;
            if (maxDepth >= 0 || inclStyles >= 0 || offset >= 0 || limit >= 0 || compact >= 0 || includeBounds >= 0 || includeClasses >= 0 || includeText >= 0)
            {
                var qDict = new Dictionary<string, object>();
                if (maxDepth >= 0) qDict["maxDepth"] = maxDepth;
                if (inclStyles >= 0) qDict["includeStyles"] = inclStyles;
                if (offset >= 0) qDict["offset"] = offset;
                if (limit >= 0) qDict["limit"] = limit;
                if (compact >= 0) qDict["compact"] = compact;
                if (includeBounds >= 0) qDict["includeBounds"] = includeBounds;
                if (includeClasses >= 0) qDict["includeClasses"] = includeClasses;
                if (includeText >= 0) qDict["includeText"] = includeText;
                queryJson = MiniJSON.Json.Serialize(qDict);
            }
            return (UnityCommands.GetVisualTree(instanceId, queryJson), 200);
        }

        [BridgeRoute("GET", "/uitoolkit/query/{id}", Category = "uitoolkit", Description = "Query visual elements by selector", ReadOnly = true, FailCode = 404)]
        private static (string body, int statusCode) Route_QueryVisualElements(string path, string method, string body, NameValueCollection query)
        {
            if (!TryParseIdFromPath(path, "/uitoolkit/query/", out int instanceId))
                return BadRequest("Invalid instance ID", "GET /uitoolkit/query/{id}");
            var qDict = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(query["name"])) qDict["name"] = query["name"];
            if (!string.IsNullOrEmpty(query["className"])) qDict["className"] = query["className"];
            if (!string.IsNullOrEmpty(query["typeName"])) qDict["typeName"] = query["typeName"];
            int inclStyles = ParseBoolInt(query["includeStyles"]);
            if (inclStyles >= 0) qDict["includeStyles"] = inclStyles;
            int offset = ParseInt(query["offset"], -1);
            if (offset >= 0) qDict["offset"] = offset;
            int limit = ParseInt(query["limit"], -1);
            if (limit >= 0) qDict["limit"] = limit;
            int compact = ParseBoolInt(query["compact"]);
            if (compact >= 0) qDict["compact"] = compact;
            int includeBounds = ParseBoolInt(query["includeBounds"]);
            if (includeBounds >= 0) qDict["includeBounds"] = includeBounds;
            int includeClasses = ParseBoolInt(query["includeClasses"]);
            if (includeClasses >= 0) qDict["includeClasses"] = includeClasses;
            int includeText = ParseBoolInt(query["includeText"]);
            if (includeText >= 0) qDict["includeText"] = includeText;
            var queryJson = MiniJSON.Json.Serialize(qDict);
            return (UnityCommands.QueryVisualElements(instanceId, queryJson), 200);
        }

        // ==================== SERIALIZATION ====================

        [BridgeRoute("GET", "/serialization/types", Category = "serialization", Description = "Get derived types for a base type")]
        private static (string body, int statusCode) Route_GetSerializationTypes(string path, string method, string body, NameValueCollection query)
        {
            var baseTypeName = query["baseType"];
            if (string.IsNullOrEmpty(baseTypeName))
                return BadRequest("baseType query parameter required", "GET /serialization/types");
            return (UnityCommands.GetDerivedTypes(baseTypeName), 200);
        }

        // ==================== ASSET CATALOG ====================

        [BridgeRoute("GET", "/assets/catalog", Category = "assets", Description = "Get combined prefab+material catalog with thumbnails",
            TimeoutDefault = 10000, TimeoutMin = 250, TimeoutMax = 120000)]
        private static (string body, int statusCode) Route_GetAssetsCatalog(string path, string method, string body, NameValueCollection query)
        {
            int maxPrefabs = ParseInt(query["maxPrefabs"], 30);
            int maxMaterials = ParseInt(query["maxMaterials"], 30);
            int thumbSize = ParseInt(query["thumbnailSize"], 64);
            int includeShaders = ParseInt(query["includeShaders"], 0);
            int includeThumbnails = ParseBool(query["includeThumbnails"], false) ? 1 : 0;
            if (maxPrefabs <= 0) maxPrefabs = 30;
            if (maxMaterials <= 0) maxMaterials = 30;
            if (thumbSize <= 0) thumbSize = 64;
            var catalogRequest = JsonUtility.ToJson(new UnityCommands.AssetCatalogRequest
            {
                prefabSearch = query["prefabSearch"] ?? "",
                materialSearch = query["materialSearch"] ?? "",
                maxPrefabs = maxPrefabs,
                maxMaterials = maxMaterials,
                thumbnailSize = thumbSize,
                includeShaders = includeShaders,
                includeThumbnails = includeThumbnails
            });
            return (UnityCommands.GetAssetCatalog(catalogRequest), 200);
        }

        [BridgeRoute("GET", "/catalog", Category = "assets", Description = "Get saved asset catalog", ReadOnly = true, TimeoutDefault = 5000)]
        private static (string body, int statusCode) Route_GetCatalog(string path, string method, string body, NameValueCollection query)
        {
            var catalogName = query["name"] ?? "";
            bool brief = ParseBool(query["brief"], true);
            int maxEntries = Math.Clamp(ParseInt(query["maxEntries"], 40), 1, 500);
            return (UnityCommands.GetSavedAssetCatalog(catalogName, brief, maxEntries), 200);
        }

        [BridgeRoute("GET", "/asset-pack/pin", Category = "assets", Description = "Get asset pack context pin", ReadOnly = true, TimeoutDefault = 5000)]
        private static (string body, int statusCode) Route_GetAssetPackPin(string path, string method, string body, NameValueCollection query)
        {
            var pinName = query["name"] ?? "";
            bool brief = ParseBool(query["brief"], true);
            int maxEntries = Math.Clamp(ParseInt(query["maxEntries"], 40), 1, 500);
            return (UnityCommands.GetAssetPackContextPin(pinName, brief, maxEntries), 200);
        }

        [BridgeRoute("GET", "/asset/info", Category = "assets", Description = "Get asset info by path", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetAssetInfo(string path, string method, string body, NameValueCollection query)
        {
            var assetPath = query["path"] ?? "";
            var requestJson = MiniJSON.Json.Serialize(new Dictionary<string, object> { { "path", assetPath } });
            return (UnityCommands.GetAssetInfo(requestJson), 200);
        }

        // ==================== ANIMATION ====================

        [BridgeRoute("GET", "/animator/info", Category = "animation", Description = "Get animator controller info", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetAnimatorInfo(string path, string method, string body, NameValueCollection query)
        {
            var controllerPath = query["path"] ?? "";
            int layerIndex = ParseInt(query["layerIndex"], -1);
            var requestJson = MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "controllerPath", controllerPath },
                { "layerIndex", layerIndex }
            });
            return (UnityCommands.GetAnimatorInfo(requestJson), 200);
        }

        [BridgeRoute("GET", "/animator/fbx-clips", Category = "animation", Description = "Get FBX animation clips", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetFbxClips(string path, string method, string body, NameValueCollection query)
        {
            var fbxPath = query["path"] ?? "";
            var requestJson = MiniJSON.Json.Serialize(new Dictionary<string, object> { { "fbxPath", fbxPath } });
            return (UnityCommands.GetFbxClips(requestJson), 200);
        }

        // ==================== RENDERING ====================

        [BridgeRoute("GET", "/volume/profile", Category = "rendering", Description = "Get volume profile overrides", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetVolumeProfile(string path, string method, string body, NameValueCollection query)
        {
            var requestJson = MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "profilePath", query["path"] ?? string.Empty },
                { "volumeInstanceId", ParseInt(query["volumeInstanceId"], 0) },
                { "includeRenderHooks", ParseBoolInt(query["includeRenderHooks"]) < 0 ? 1 : ParseBoolInt(query["includeRenderHooks"]) }
            });
            return (UnityCommands.GetVolumeProfile(requestJson), 200);
        }

        [BridgeRoute("GET", "/camera/rendering", Category = "rendering", Description = "Get camera rendering settings", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetCameraRendering(string path, string method, string body, NameValueCollection query)
        {
            var requestJson = MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "instanceId", ParseInt(query["instanceId"], 0) },
                { "cameraName", query["name"] ?? query["cameraName"] ?? string.Empty }
            });
            return (UnityCommands.GetCameraRendering(requestJson), 200);
        }

        // ==================== PARTICLES ====================

        [BridgeRoute("GET", "/particle-system", Category = "rendering", Description = "Get particle system module data", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetParticleSystem(string path, string method, string body, NameValueCollection query)
        {
            int instanceId = ParseInt(query["instanceId"], 0);
            string modules = query["modules"] ?? "";
            var requestJson = MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "instanceId", instanceId },
                { "modules", modules }
            });
            return (UnityCommands.GetParticleSystem(requestJson), 200);
        }

        // ==================== AUDIO ====================

        [BridgeRoute("GET", "/audio/source", Category = "audio", Description = "Get AudioSource component details", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetAudioSource(string path, string method, string body, NameValueCollection query)
        {
            int instanceId = ParseInt(query["instanceId"], 0);
            bool includeClipMeta = ParseBool(query["includeClipMeta"], true);
            bool includeMixerInfo = ParseBool(query["includeMixerInfo"], true);
            var requestJson = MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "instanceId", instanceId },
                { "includeClipMeta", includeClipMeta ? 1 : 0 },
                { "includeMixerInfo", includeMixerInfo ? 1 : 0 }
            });
            return (UnityCommands.GetAudioSource(requestJson), 200);
        }

        [BridgeRoute("GET", "/audio/mixer", Category = "audio", Description = "Get AudioMixer asset details", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetAudioMixer(string path, string method, string body, NameValueCollection query)
        {
            var mixerPath = query["path"] ?? "";
            bool brief = ParseBool(query["brief"], true);
            int maxGroups = Math.Clamp(ParseInt(query["maxGroups"], 50), 1, 500);
            int maxParameters = Math.Clamp(ParseInt(query["maxParameters"], 50), 1, 500);
            int maxSnapshots = Math.Clamp(ParseInt(query["maxSnapshots"], 20), 1, 100);
            var requestJson = MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "mixerPath", mixerPath },
                { "brief", brief ? 1 : 0 },
                { "maxGroups", maxGroups },
                { "maxParameters", maxParameters },
                { "maxSnapshots", maxSnapshots }
            });
            return (UnityCommands.GetAudioMixer(requestJson), 200);
        }

        // ==================== FRAME SEQUENCE ====================

        [BridgeRoute("GET", "/screenshot/sequence/{id}", Category = "screenshot", Description = "Get frame sequence capture status",
            TimeoutDefault = 5000, TimeoutMin = 500, TimeoutMax = 30000)]
        private static (string body, int statusCode) Route_GetFrameSequenceStatus(string path, string method, string body, NameValueCollection query)
        {
            var captureId = path.Substring("/screenshot/sequence/".Length);
            var statusJson = MiniJSON.Json.Serialize(new Dictionary<string, object> { { "captureId", captureId } });
            return (UnityCommands.GetFrameSequenceStatus(statusJson), 200);
        }

        [BridgeRoute("POST", "/screenshot/sequence/{id}/cancel", Category = "screenshot", Description = "Cancel a running frame sequence capture",
            TimeoutDefault = 5000, TimeoutMin = 500, TimeoutMax = 30000)]
        private static (string body, int statusCode) Route_PostFrameSequenceCancel(string path, string method, string body, NameValueCollection query)
        {
            var seg = path.Substring("/screenshot/sequence/".Length);
            var captureId = seg.Substring(0, seg.Length - "/cancel".Length);
            var cancelJson = MiniJSON.Json.Serialize(new Dictionary<string, object> { { "captureId", captureId } });
            return (UnityCommands.CancelFrameSequence(cancelJson), 200);
        }

        // ==================== DELTA CACHE ====================

        [BridgeRoute("GET", "/delta", Category = "delta", Description = "Get delta changes since snapshot", ReadOnly = true)]
        private static (string body, int statusCode) Route_GetDelta(string path, string method, string body, NameValueCollection query)
        {
            var snapshotName = query["name"] ?? query["snapshot"] ?? "";
            return (UnityCommands.GetDelta(snapshotName), 200);
        }

        [BridgeRoute("DELETE", "/delta", Category = "delta", Description = "Delete a delta snapshot")]
        private static (string body, int statusCode) Route_DeleteDelta(string path, string method, string body, NameValueCollection query)
        {
            var snapshotName = query["name"] ?? query["snapshot"] ?? "";
            return (UnityCommands.DeleteDeltaSnapshot(snapshotName), 200);
        }
    }
}
