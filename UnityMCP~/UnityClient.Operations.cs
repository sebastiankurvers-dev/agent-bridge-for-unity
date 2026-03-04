using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace UnityMCP;

public partial class UnityClient
{
    /// <summary>
    /// Load a scene.
    /// </summary>
    public async Task<string> LoadSceneAsync(string? scenePath = null, string? sceneName = null, bool saveCurrentScene = true)
    {
        return await PostAsyncNoRetry("/scene", new { scenePath, sceneName, saveCurrentScene });
    }

    public async Task<string> SaveSceneAsync(object data)
    {
        return await PostAsyncNoRetry("/scene/save", data);
    }

    public async Task<string> StoreImageHandleAsync(object data)
    {
        return await PostAsyncNoRetry("/image/store", data);
    }

    public async Task<string> GetImageHandleAsync(string imageHandle, bool includeBase64 = false)
    {
        return await GetAsync($"/image/handle?imageHandle={Uri.EscapeDataString(imageHandle)}&includeBase64={(includeBase64 ? "true" : "false")}");
    }

    public async Task<string> DeleteImageHandleAsync(string imageHandle)
    {
        return await DeleteAsyncNoRetry($"/image/handle?imageHandle={Uri.EscapeDataString(imageHandle)}");
    }

    public async Task<string> GetEditorRuntimeAsync()
    {
        return await GetAsync("/editor/runtime");
    }

    /// <summary>
    /// Execute C# code.
    /// </summary>
    public async Task<string> ExecuteCSharpAsync(string code)
    {
        return await PostAsyncNoRetry("/execute", new { code });
    }

    /// <summary>
    /// Register reusable C# helpers for execute_csharp.
    /// </summary>
    public async Task<string> RegisterExecuteHelpersAsync(string name, string code)
    {
        return await PostAsyncNoRetry("/execute/register-helpers", new { name, code });
    }

    /// <summary>
    /// List registered execute_csharp helpers.
    /// </summary>
    public async Task<string> ListExecuteHelpersAsync()
    {
        return await GetAsync("/execute/helpers");
    }

    /// <summary>
    /// Clear registered execute_csharp helpers.
    /// </summary>
    public async Task<string> ClearExecuteHelpersAsync(string? name = null)
    {
        return await DeleteAsyncNoRetry("/execute/helpers");
    }

    /// <summary>
    /// Find prefabs.
    /// </summary>
    public async Task<string> FindPrefabsAsync(string search = "", int maxResults = 100)
    {
        return await GetAsync($"/prefabs?search={Uri.EscapeDataString(search)}&max={Math.Clamp(maxResults, 1, 5000)}");
    }

    public async Task<string> FindPrefabsScopedAsync(
        string search = "",
        string[]? includeRoots = null,
        string[]? excludeRoots = null,
        bool includeSubfolders = true,
        int maxResults = 200,
        string matchMode = "contains")
    {
        var parts = new List<string>
        {
            $"search={Uri.EscapeDataString(search ?? string.Empty)}",
            $"includeSubfolders={(includeSubfolders ? "true" : "false")}",
            $"max={Math.Clamp(maxResults, 1, 5000)}",
            $"matchMode={Uri.EscapeDataString(matchMode ?? "contains")}"
        };

        if (includeRoots != null && includeRoots.Length > 0)
        {
            var roots = string.Join(",", includeRoots.Where(r => !string.IsNullOrWhiteSpace(r)));
            if (!string.IsNullOrWhiteSpace(roots))
            {
                parts.Add($"includeRoots={Uri.EscapeDataString(roots)}");
            }
        }

        if (excludeRoots != null && excludeRoots.Length > 0)
        {
            var roots = string.Join(",", excludeRoots.Where(r => !string.IsNullOrWhiteSpace(r)));
            if (!string.IsNullOrWhiteSpace(roots))
            {
                parts.Add($"excludeRoots={Uri.EscapeDataString(roots)}");
            }
        }

        return await GetAsync("/prefabs/scoped?" + string.Join("&", parts));
    }

    /// <summary>
    /// Get components on a GameObject. Supports names_only mode for compact output.
    /// </summary>
    public async Task<string> GetComponentsAsync(int instanceId, bool namesOnly = true)
    {
        var url = $"/components/{instanceId}";
        if (namesOnly) url += "?names_only=true";
        return await GetAsync(url);
    }

    #region Script Operations

    public async Task<string> GetScriptAsync(string path)
    {
        return await GetAsync($"/script?path={Uri.EscapeDataString(path)}");
    }

    public async Task<string> CreateScriptAsync(object scriptData)
    {
        return await PostAsyncNoRetry("/script", scriptData);
    }

    public async Task<string> ModifyScriptAsync(object scriptData)
    {
        return await PutAsyncNoRetry("/script", scriptData);
    }

    #endregion

    #region Component Operations

    public async Task<string> AddComponentAsync(object componentData)
    {
        return await PostAsyncNoRetry("/component", componentData);
    }

    public async Task<string> RemoveComponentAsync(int instanceId, string componentType)
    {
        return await DeleteAsyncNoRetry($"/component/{instanceId}/{Uri.EscapeDataString(componentType)}");
    }

    public async Task<string> ModifyComponentAsync(object componentData)
    {
        return await PutAsyncNoRetry("/component", componentData);
    }

    public async Task<string> PatchSerializedPropertiesAsync(object patchData)
    {
        return await PostAsyncNoRetry("/component/patch", patchData);
    }

    public async Task<string> SetRendererMaterialsAsync(object data)
    {
        return await PutAsyncNoRetry("/renderer/materials", data);
    }

    #endregion

    #region Reparent Operations

    public async Task<string> ReparentGameObjectAsync(object reparentData)
    {
        return await PostAsyncNoRetry("/reparent", reparentData);
    }

    public async Task<string> GroupObjectsAsync(object data)
    {
        return await PostAsyncNoRetry("/group", data);
    }

    public async Task<string> ScatterObjectsAsync(object data)
    {
        return await PostAsyncNoRetry("/scatter", data);
    }

    #endregion

    #region ScriptableObject Operations

    public async Task<string> CreateScriptableObjectAsync(object soData)
    {
        return await PostAsyncNoRetry("/scriptableobject", soData);
    }

    #endregion

    #region Material Operations

    public async Task<string> FindMaterialsAsync(string search = "", int maxResults = 100)
    {
        return await GetAsync($"/material?search={Uri.EscapeDataString(search)}&max={Math.Clamp(maxResults, 1, 5000)}");
    }

    public async Task<string> FindMaterialsScopedAsync(
        string search = "",
        string[]? includeRoots = null,
        string[]? excludeRoots = null,
        bool includeSubfolders = true,
        int maxResults = 200,
        string matchMode = "contains")
    {
        var parts = new List<string>
        {
            $"search={Uri.EscapeDataString(search ?? string.Empty)}",
            $"includeSubfolders={(includeSubfolders ? "true" : "false")}",
            $"max={Math.Clamp(maxResults, 1, 5000)}",
            $"matchMode={Uri.EscapeDataString(matchMode ?? "contains")}"
        };

        if (includeRoots != null && includeRoots.Length > 0)
        {
            var roots = string.Join(",", includeRoots.Where(r => !string.IsNullOrWhiteSpace(r)));
            if (!string.IsNullOrWhiteSpace(roots))
            {
                parts.Add($"includeRoots={Uri.EscapeDataString(roots)}");
            }
        }

        if (excludeRoots != null && excludeRoots.Length > 0)
        {
            var roots = string.Join(",", excludeRoots.Where(r => !string.IsNullOrWhiteSpace(r)));
            if (!string.IsNullOrWhiteSpace(roots))
            {
                parts.Add($"excludeRoots={Uri.EscapeDataString(roots)}");
            }
        }

        return await GetAsync("/materials/scoped?" + string.Join("&", parts));
    }

    public async Task<string> CreateMaterialAsync(object materialData)
    {
        return await PostAsyncNoRetry("/material", materialData);
    }

    public async Task<string> ModifyMaterialAsync(object materialData)
    {
        return await PutAsyncNoRetry("/material", materialData);
    }

    #endregion

    #region Prefab Operations

    public async Task<string> CreatePrefabAsync(object prefabData)
    {
        return await PostAsyncNoRetry("/prefab", prefabData);
    }

    public async Task<string> ModifyPrefabAsync(object prefabData)
    {
        return await PutAsyncNoRetry("/prefab", prefabData);
    }

    public async Task<string> ApplyPrefabOverridesAsync(int instanceId)
    {
        return await PostAsyncNoRetry("/prefab/apply", new { instanceId });
    }

    public async Task<string> CreatePrefabVariantAsync(object variantData)
    {
        return await PostAsyncNoRetry("/prefab/variant", variantData);
    }

    public async Task<string> ImportFbxToPrefabAsync(object data)
    {
        return await PostAsyncNoRetry("/import/fbx-to-prefab", data);
    }

    #endregion

    #region Tag Operations

    public async Task<string> GetTagsAsync()
    {
        return await GetAsync("/tag");
    }

    public async Task<string> CreateTagAsync(string tagName)
    {
        return await PostAsyncNoRetry("/tag", new { name = tagName });
    }

    #endregion

    #region Layer Operations

    public async Task<string> GetLayersAsync()
    {
        return await GetAsync("/layer");
    }

    public async Task<string> CreateLayerAsync(string layerName, int index = -1)
    {
        return await PostAsyncNoRetry("/layer", new { name = layerName, index });
    }

    #endregion

    #region Project Index Operations

    public async Task<string> GetProjectIndexAsync(bool pretty = false, bool summary = true, int maxEntries = 50, int cacheSeconds = 15, bool includeScriptMembers = false)
    {
        var parts = new List<string>
        {
            $"pretty={(pretty ? "true" : "false")}",
            $"summary={(summary ? "true" : "false")}",
            $"maxEntries={Math.Clamp(maxEntries, 5, 500)}",
            $"cacheSeconds={Math.Clamp(cacheSeconds, 0, 300)}",
            $"includeScriptMembers={(includeScriptMembers ? "true" : "false")}"
        };
        return await GetAsync("/index?" + string.Join("&", parts));
    }

    public async Task<string> SearchProjectAsync(string query, string? assetType = null, int maxResults = 50, bool includeGuids = false, int cacheSeconds = 10)
    {
        var url = $"/search?query={Uri.EscapeDataString(query)}&max={Math.Clamp(maxResults, 1, 1000)}&includeGuids={(includeGuids ? "true" : "false")}&cacheSeconds={Math.Clamp(cacheSeconds, 0, 300)}";
        if (!string.IsNullOrEmpty(assetType))
        {
            url += $"&type={Uri.EscapeDataString(assetType)}";
        }
        return await GetAsync(url);
    }

    #endregion

    #region Checkpoint Operations

    public async Task<string> ListCheckpointsAsync()
    {
        return await GetAsync("/checkpoint");
    }

    public async Task<string> CreateCheckpointAsync(string name, bool includeRecentScripts = false, int maxRecentScripts = 50)
    {
        var route = "/checkpoint";
        if (includeRecentScripts)
        {
            route += $"?includeRecentScripts=true&maxRecentScripts={Math.Clamp(maxRecentScripts, 1, 500)}";
        }

        return await PostAsyncNoRetry(route, new
        {
            name,
            includeRecentScripts,
            maxRecentScripts = Math.Clamp(maxRecentScripts, 1, 500)
        });
    }

    public async Task<string> DeleteCheckpointAsync(string checkpointId)
    {
        return await DeleteAsyncNoRetry($"/checkpoint/{Uri.EscapeDataString(checkpointId)}");
    }

    public async Task<string> RestoreCheckpointAsync(string checkpointId)
    {
        return await PostAsyncNoRetry($"/restore/{Uri.EscapeDataString(checkpointId)}", new { });
    }

    public async Task<string> GetDiffAsync(string filePath, string? checkpointId = null)
    {
        var url = $"/diff?path={Uri.EscapeDataString(filePath)}";
        if (!string.IsNullOrEmpty(checkpointId))
        {
            url += $"&checkpoint={Uri.EscapeDataString(checkpointId)}";
        }
        return await GetAsync(url);
    }

    #endregion

    #region UI Operations

    public async Task<string> CreateCanvasAsync(object canvasData)
    {
        return await PostAsyncNoRetry("/ui/canvas", canvasData);
    }

    public async Task<string> CreateUIElementAsync(object elementData)
    {
        return await PostAsyncNoRetry("/ui/element", elementData);
    }

    public async Task<string> ModifyRectTransformAsync(int instanceId, object rectData)
    {
        return await PutAsyncNoRetry($"/ui/recttransform/{instanceId}", rectData);
    }

    public async Task<string> ModifyTMPTextAsync(int instanceId, object textData)
    {
        return await PutAsyncNoRetry($"/ui/text/{instanceId}", textData);
    }

    public async Task<string> SetUIColorAsync(int instanceId, float[] color)
    {
        return await PutAsyncNoRetry($"/ui/color/{instanceId}", new { color });
    }

    #endregion

    #region UI Toolkit Operations

    public async Task<string> CreateUIDocumentAsync(object data)
    {
        return await PostAsyncNoRetry("/uitoolkit/document", data);
    }

    public async Task<string> CreateUIToolkitPanelSettingsAsync(object data)
    {
        return await PostAsyncNoRetry("/uitoolkit/panelsettings", data);
    }

    public async Task<string> GetUIToolkitPanelSettingsAsync(string path)
    {
        return await GetAsync($"/uitoolkit/panelsettings?path={Uri.EscapeDataString(path)}");
    }

    public async Task<string> CreateUXMLAsync(object data)
    {
        return await PostAsyncNoRetry("/uitoolkit/uxml", data);
    }

    public async Task<string> ModifyUXMLAsync(object data)
    {
        return await PutAsyncNoRetry("/uitoolkit/uxml", data);
    }

    public async Task<string> ReadUXMLAsync(string path)
    {
        return await GetAsync($"/uitoolkit/uxml?path={Uri.EscapeDataString(path)}");
    }

    public async Task<string> CreateUSSAsync(object data)
    {
        return await PostAsyncNoRetry("/uitoolkit/uss", data);
    }

    public async Task<string> ModifyUSSAsync(object data)
    {
        return await PutAsyncNoRetry("/uitoolkit/uss", data);
    }

    public async Task<string> ReadUSSAsync(string path)
    {
        return await GetAsync($"/uitoolkit/uss?path={Uri.EscapeDataString(path)}");
    }

    public async Task<string> GetVisualTreeAsync(
        int instanceId,
        int maxDepth = -1,
        bool includeStyles = false,
        int offset = 0,
        int limit = -1,
        bool compact = false,
        bool? includeBounds = null,
        bool? includeClasses = null,
        bool? includeText = null)
    {
        var parts = new List<string>();
        if (maxDepth >= 0) parts.Add($"maxDepth={maxDepth}");
        if (includeStyles) parts.Add("includeStyles=true");
        if (offset > 0) parts.Add($"offset={Math.Max(0, offset)}");
        if (limit >= 0) parts.Add($"limit={Math.Clamp(limit, 1, 5000)}");
        if (compact) parts.Add("compact=true");
        if (includeBounds.HasValue) parts.Add($"includeBounds={(includeBounds.Value ? "true" : "false")}");
        if (includeClasses.HasValue) parts.Add($"includeClasses={(includeClasses.Value ? "true" : "false")}");
        if (includeText.HasValue) parts.Add($"includeText={(includeText.Value ? "true" : "false")}");
        var query = parts.Count > 0 ? "?" + string.Join("&", parts) : "";
        return await GetAsync($"/uitoolkit/tree/{instanceId}{query}");
    }

    public async Task<string> QueryVisualElementsAsync(
        int instanceId,
        string? name = null,
        string? className = null,
        string? typeName = null,
        bool includeStyles = false,
        int offset = 0,
        int limit = -1,
        bool compact = false,
        bool? includeBounds = null,
        bool? includeClasses = null,
        bool? includeText = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(name)) parts.Add($"name={Uri.EscapeDataString(name)}");
        if (!string.IsNullOrEmpty(className)) parts.Add($"className={Uri.EscapeDataString(className)}");
        if (!string.IsNullOrEmpty(typeName)) parts.Add($"typeName={Uri.EscapeDataString(typeName)}");
        if (includeStyles) parts.Add("includeStyles=true");
        if (offset > 0) parts.Add($"offset={Math.Max(0, offset)}");
        if (limit >= 0) parts.Add($"limit={Math.Clamp(limit, 1, 5000)}");
        if (compact) parts.Add("compact=true");
        if (includeBounds.HasValue) parts.Add($"includeBounds={(includeBounds.Value ? "true" : "false")}");
        if (includeClasses.HasValue) parts.Add($"includeClasses={(includeClasses.Value ? "true" : "false")}");
        if (includeText.HasValue) parts.Add($"includeText={(includeText.Value ? "true" : "false")}");
        var query = parts.Count > 0 ? "?" + string.Join("&", parts) : "";
        return await GetAsync($"/uitoolkit/query/{instanceId}{query}");
    }

    public async Task<string> ModifyVisualElementAsync(int instanceId, object data)
    {
        return await PutAsyncNoRetry($"/uitoolkit/element/{instanceId}", data);
    }

    public async Task<string> CreateVisualElementAsync(int instanceId, object data)
    {
        return await PostAsyncNoRetry($"/uitoolkit/element/{instanceId}", data);
    }

    public async Task<string> MigrateUGUIToUIToolkitAsync(int instanceId, object? data = null)
    {
        return await PostAsyncNoRetry($"/uitoolkit/migrate/{instanceId}", data ?? new { });
    }

    #endregion

    #region Shader Operations

    public async Task<string> GetShaderAsync(string path)
    {
        return await GetAsync($"/shader?path={Uri.EscapeDataString(path)}");
    }

    public async Task<string> CreateShaderAsync(object shaderData)
    {
        return await PostAsyncNoRetry("/shader", shaderData);
    }

    public async Task<string> ModifyShaderAsync(object shaderData)
    {
        return await PutAsyncNoRetry("/shader", shaderData);
    }

    public async Task<string> FindShadersAsync(string search = "", int maxResults = 100)
    {
        return await GetAsync($"/shaders?search={Uri.EscapeDataString(search)}&max={Math.Clamp(maxResults, 1, 5000)}");
    }

    public async Task<string> FindShadersScopedAsync(
        string search = "",
        string[]? includeRoots = null,
        string[]? excludeRoots = null,
        bool includeSubfolders = true,
        int maxResults = 200,
        string matchMode = "contains")
    {
        var parts = new List<string>
        {
            $"search={Uri.EscapeDataString(search ?? string.Empty)}",
            $"includeSubfolders={(includeSubfolders ? "true" : "false")}",
            $"max={Math.Clamp(maxResults, 1, 5000)}",
            $"matchMode={Uri.EscapeDataString(matchMode ?? "contains")}"
        };

        if (includeRoots != null && includeRoots.Length > 0)
        {
            var roots = string.Join(",", includeRoots.Where(r => !string.IsNullOrWhiteSpace(r)));
            if (!string.IsNullOrWhiteSpace(roots))
            {
                parts.Add($"includeRoots={Uri.EscapeDataString(roots)}");
            }
        }

        if (excludeRoots != null && excludeRoots.Length > 0)
        {
            var roots = string.Join(",", excludeRoots.Where(r => !string.IsNullOrWhiteSpace(r)));
            if (!string.IsNullOrWhiteSpace(roots))
            {
                parts.Add($"excludeRoots={Uri.EscapeDataString(roots)}");
            }
        }

        return await GetAsync("/shaders/scoped?" + string.Join("&", parts));
    }

    public async Task<string> GetShaderPropertiesAsync(string path)
    {
        return await GetAsync($"/shader/properties?path={Uri.EscapeDataString(path)}");
    }

    public async Task<string> SetShaderKeywordAsync(object keywordData)
    {
        return await PostAsyncNoRetry("/material/keyword", keywordData);
    }

    #endregion

    #region Compilation Operations

    public async Task<string> GetCompilationStatusAsync()
    {
        return await GetAsync("/compilation/status");
    }

    public async Task<string> GetCompilationErrorsAsync()
    {
        return await GetAsync("/compilation/errors");
    }

    public async Task<string> TriggerRecompileAsync(
        string? forceReimportPath = null,
        string[]? forceReimportPaths = null,
        bool waitForCompile = false,
        int maxWaitMs = 30000,
        int pollIntervalMs = 250)
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(forceReimportPath))
        {
            paths.Add(forceReimportPath.Trim());
        }

        if (forceReimportPaths != null)
        {
            foreach (var path in forceReimportPaths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path.Trim());
                }
            }
        }

        paths = paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var payload = new Dictionary<string, object?>();
        if (paths.Count == 1)
        {
            payload["forceReimportPath"] = paths[0];
        }
        else if (paths.Count > 1)
        {
            payload["forceReimportPaths"] = paths;
        }

        var triggerRaw = await PostAsyncNoRetry("/compilation/trigger", payload);
        if (!waitForCompile)
        {
            return triggerRaw;
        }

        int timeout = Math.Clamp(maxWaitMs, 250, 300000);
        int interval = Math.Clamp(pollIntervalMs, 50, 2000);
        var startedAt = DateTime.UtcNow;
        var deadline = startedAt.AddMilliseconds(timeout);
        JsonElement? lastStatus = null;

        while (DateTime.UtcNow <= deadline)
        {
            await Task.Delay(interval);
            var statusRaw = await GetCompilationStatusAsync();
            if (!TryParseJsonElement(statusRaw, out var status))
            {
                continue;
            }

            lastStatus = status;
            bool isCompiling = status.TryGetProperty("isCompiling", out var compilingEl) && compilingEl.GetBoolean();
            int errorCount = status.TryGetProperty("errorCount", out var errorEl) && errorEl.ValueKind == JsonValueKind.Number
                ? errorEl.GetInt32()
                : -1;

            if (!isCompiling)
            {
                var waitedMs = (int)Math.Max(0, (DateTime.UtcNow - startedAt).TotalMilliseconds);
                return JsonSerializer.Serialize(new
                {
                    success = errorCount == 0,
                    waitedMs,
                    waitForCompile = true,
                    trigger = ParseJsonOrString(triggerRaw),
                    compilationStatus = status,
                    errorCount
                }, _jsonOptions);
            }
        }

        return JsonSerializer.Serialize(new
        {
            success = false,
            error = "Timed out waiting for compilation to finish",
            waitForCompile = true,
            maxWaitMs = timeout,
            trigger = ParseJsonOrString(triggerRaw),
            compilationStatus = lastStatus.HasValue ? lastStatus.Value : default
        }, _jsonOptions);
    }

    #endregion

    #region Event Stream Operations

    public async Task<string> PollEventsAsync(int sinceId = 0, int timeoutSeconds = 0, bool includeStackTrace = false)
    {
        var url = $"/events?since={sinceId}&timeout={timeoutSeconds}";
        if (includeStackTrace)
        {
            url += "&includeStackTrace=true";
        }

        return await GetAsync(url);
    }

    #endregion

    #region Serialization Operations

    public async Task<string> SetManagedReferenceAsync(int instanceId, string propertyPath, string typeName, string? jsonData = null)
    {
        return await PutAsyncNoRetry($"/serialization/reference/{instanceId}", new
        {
            propertyPath,
            typeName,
            data = jsonData
        });
    }

    public async Task<string> GetDerivedTypesAsync(string baseTypeName)
    {
        return await GetAsync($"/serialization/types?baseType={Uri.EscapeDataString(baseTypeName)}");
    }

    #endregion

    #region Procedural Mesh Operations

    public async Task<string> CreateProceduralMeshAsync(object data)
    {
        return await PostAsyncNoRetry("/mesh/procedural", data);
    }

    public async Task<string> CreateRawMeshAsync(object data)
    {
        return await PostAsyncNoRetry("/mesh/raw", data);
    }

    #endregion

    #region Compound Shape Operations

    public async Task<string> CreateCompoundShapeAsync(object data)
    {
        return await PostAsyncNoRetry("/compound-shape", data);
    }

    #endregion

    #region Terrain Operations

    public async Task<string> CreateTerrainAsync(object data)
    {
        return await PostAsyncNoRetry("/terrain", data);
    }

    public async Task<string> GetTerrainInfoAsync(int instanceId)
    {
        return await GetAsync($"/terrain?instanceId={instanceId}");
    }

    public async Task<string> SetTerrainHeightsAsync(object data)
    {
        return await PutAsyncNoRetry("/terrain/heights", data);
    }

    public async Task<string> AddTerrainLayerAsync(object data)
    {
        return await PostAsyncNoRetry("/terrain/layer", data);
    }

    public async Task<string> PaintTerrainAsync(object data)
    {
        return await PutAsyncNoRetry("/terrain/paint", data);
    }

    public async Task<string> PlaceTerrainTreesAsync(object data)
    {
        return await PostAsyncNoRetry("/terrain/trees", data);
    }

    #endregion

    #region Procedural Texture Operations

    public async Task<string> CreateProceduralTextureAsync(object data)
    {
        return await PostAsyncNoRetry("/texture/procedural", data);
    }

    #endregion
}
