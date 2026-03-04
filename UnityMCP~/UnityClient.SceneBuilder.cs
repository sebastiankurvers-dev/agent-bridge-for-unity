using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace UnityMCP;

public partial class UnityClient
{
    #region Scene Builder Operations

    public async Task<string> CreateSceneFromDescriptorAsync(object descriptor)
    {
        return await PostAsyncNoRetry("/scene/create", descriptor);
    }

    public async Task<string> ExportSceneDescriptorAsync(int[]? instanceIds = null)
    {
        if (instanceIds != null && instanceIds.Length > 0)
        {
            return await GetAsync($"/scene/export?instanceIds={string.Join(",", instanceIds)}");
        }
        return await GetAsync("/scene/export");
    }

    public async Task<string> GetPrefabPreviewAsync(string prefabPath, int size = 128)
    {
        return await GetAsync($"/prefab/preview?path={Uri.EscapeDataString(prefabPath)}&size={size}");
    }

    public async Task<string> GetPrefabGeometryAsync(
        string prefabPath,
        bool includeSockets = true,
        bool includeChildren = false,
        string[]? socketPrefixes = null,
        bool includeAccurateBounds = false)
    {
        var parts = new List<string>
        {
            $"path={Uri.EscapeDataString(prefabPath)}",
            $"includeSockets={(includeSockets ? "true" : "false")}",
            $"includeChildren={(includeChildren ? "true" : "false")}",
            $"includeAccurateBounds={(includeAccurateBounds ? "true" : "false")}"
        };

        if (socketPrefixes != null && socketPrefixes.Length > 0)
        {
            var prefixes = string.Join(",", socketPrefixes.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (!string.IsNullOrWhiteSpace(prefixes))
            {
                parts.Add($"socketPrefixes={Uri.EscapeDataString(prefixes)}");
            }
        }

        return await GetAsync("/prefab/geometry?" + string.Join("&", parts));
    }

    public async Task<string> GetPrefabFootprint2DAsync(
        string prefabPath,
        string source = "hybrid",
        float targetMinEdgeGap = 0.04f,
        int maxPoints = 6000,
        bool includeHull = true,
        bool includeSamplePoints = false)
    {
        var parts = new List<string>
        {
            $"path={Uri.EscapeDataString(prefabPath)}",
            $"source={Uri.EscapeDataString(source)}",
            $"targetMinEdgeGap={targetMinEdgeGap.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"maxPoints={Math.Clamp(maxPoints, 128, 50000)}",
            $"includeHull={(includeHull ? "true" : "false")}",
            $"includeSamplePoints={(includeSamplePoints ? "true" : "false")}"
        };

        return await GetAsync("/prefab/footprint2d?" + string.Join("&", parts));
    }

    public async Task<string> GetPrefabPreviewsAsync(string search = "", int maxResults = 20, int size = 64, bool includeThumbnails = false)
    {
        return await GetAsync($"/prefabs/previews?search={Uri.EscapeDataString(search)}&max={maxResults}&size={size}&includeThumbnails={(includeThumbnails ? "true" : "false")}");
    }

    public async Task<string> GetMaterialPreviewAsync(string materialPath, int size = 128)
    {
        return await GetAsync($"/material/preview?path={Uri.EscapeDataString(materialPath)}&size={size}");
    }

    public async Task<string> GetMaterialPreviewsAsync(string search = "", int maxResults = 20, int size = 64, bool includeThumbnails = false)
    {
        return await GetAsync($"/materials/previews?search={Uri.EscapeDataString(search)}&max={maxResults}&size={size}&includeThumbnails={(includeThumbnails ? "true" : "false")}");
    }

    public async Task<string> GetAssetCatalogAsync(string prefabSearch = "", string materialSearch = "", int maxPrefabs = 30, int maxMaterials = 30, int thumbnailSize = 64, bool includeShaders = false, bool includeThumbnails = false)
    {
        var includeS = includeShaders ? 1 : 0;
        var includeT = includeThumbnails ? 1 : 0;
        return await GetAsync($"/assets/catalog?prefabSearch={Uri.EscapeDataString(prefabSearch)}&materialSearch={Uri.EscapeDataString(materialSearch)}&maxPrefabs={maxPrefabs}&maxMaterials={maxMaterials}&thumbnailSize={thumbnailSize}&includeShaders={includeS}&includeThumbnails={includeT}");
    }

    public async Task<string> BuildSceneAndScreenshotAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/scene/build-and-screenshot", timeoutMs), data);
    }

    public async Task<string> SceneTransactionAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/scene/transaction", timeoutMs), data);
    }

    public async Task<string> ApplyScenePatchBatchAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/scene/patch-batch", timeoutMs), data);
    }

    public async Task<string> ReproStepAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/scene/repro-step", timeoutMs), data);
    }

    public async Task<string> PlanSceneReconstructionAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/scene/repro-plan", timeoutMs), data);
    }

    public async Task<string> SolveLayoutConstraintsAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/scene/layout-solve", timeoutMs), data);
    }

    public async Task<string> CompareImagesAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/image/compare", timeoutMs), data);
    }

    public async Task<string> CompareImagesSemanticAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/image/compare-semantic", timeoutMs), data);
    }

    public async Task<string> MeasureTileSeparationAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/scene/tile-separation", timeoutMs), data);
    }

    public async Task<string> ResolveTileOverlapsAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/scene/resolve-tile-overlaps", timeoutMs), data);
    }

    public async Task<string> RunSceneQualityChecksAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/scene/quality-checks", timeoutMs), data);
    }

    public async Task<string> SpawnAlongPathAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/scene/spawn-along-path", timeoutMs), data);
    }

    public async Task<string> SampleScreenshotColorsAsync(object data)
    {
        return await PostAsync("/screenshot/sample-colors", data);
    }

    public async Task<string> MultiPovSnapshotAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/screenshot/multi-pov", timeoutMs), data);
    }

    public async Task<string> ParameterSweepAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/screenshot/parameter-sweep", timeoutMs ?? 60000), data);
    }

    public async Task<string> StartFrameSequenceAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/screenshot/sequence/start", timeoutMs), data);
    }

    public async Task<string> GetFrameSequenceStatusAsync(string captureId)
    {
        return await GetAsync($"/screenshot/sequence/{Uri.EscapeDataString(captureId)}");
    }

    public async Task<string> CancelFrameSequenceAsync(string captureId)
    {
        return await PostAsyncNoRetry($"/screenshot/sequence/{Uri.EscapeDataString(captureId)}/cancel", new { });
    }

    /// <summary>
    /// Start a frame sequence and poll until all frames are captured.
    /// </summary>
    public async Task<string> CaptureFrameSequenceAsync(object startRequest, int pollIntervalMs = 250, int maxPollMs = 35000)
    {
        var startResult = await StartFrameSequenceAsync(startRequest);

        string? captureId = null;
        try
        {
            var startJson = JsonSerializer.Deserialize<JsonElement>(startResult);
            if (!startJson.TryGetProperty("captureId", out var idEl))
                return startResult;
            captureId = idEl.GetString();
        }
        catch (JsonException)
        {
            return startResult; // pass through unparseable response
        }

        if (string.IsNullOrWhiteSpace(captureId)) return startResult;

        var deadline = DateTime.UtcNow.AddMilliseconds(maxPollMs);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(pollIntervalMs);
            var status = await GetFrameSequenceStatusAsync(captureId!);

            try
            {
                var statusJson = JsonSerializer.Deserialize<JsonElement>(status);
                if ((statusJson.TryGetProperty("completed", out var c) && c.GetBoolean()) ||
                    (statusJson.TryGetProperty("failed", out var f) && f.GetBoolean()) ||
                    (statusJson.TryGetProperty("cancelled", out var x) && x.GetBoolean()) ||
                    statusJson.TryGetProperty("error", out _))
                    return status;
            }
            catch (JsonException)
            {
                return status; // malformed response — return as-is
            }
        }

        // Timed out — cancel the capture so it doesn't linger
        try { await CancelFrameSequenceAsync(captureId!); } catch { /* best effort */ }
        return JsonSerializer.Serialize(new { error = "Frame sequence capture timed out", captureId }, _jsonOptions);
    }

    #endregion

    #region Performance Telemetry Operations

    public async Task<string> GetPerformanceTelemetryAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/performance/telemetry", timeoutMs), data);
    }

    public async Task<string> CapturePerformanceBaselineAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/performance/baseline", timeoutMs), data);
    }

    public async Task<string> CheckPerformanceBudgetAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/performance/budget-check", timeoutMs), data);
    }

    public async Task<string> GetScriptHotspotsAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/performance/hotspots", timeoutMs), data);
    }

    #endregion

    #region Scene View Camera Operations

    public async Task<string> GetSceneViewCameraAsync()
    {
        return await GetAsync("/sceneview/camera");
    }

    public async Task<string> SetSceneViewCameraAsync(object data)
    {
        return await PostAsyncNoRetry("/sceneview/camera", data);
    }

    public async Task<string> FrameObjectAsync(object data)
    {
        return await PostAsyncNoRetry("/sceneview/frame", data);
    }

    public async Task<string> LookAtPointAsync(object data)
    {
        return await PostAsyncNoRetry("/sceneview/lookat", data);
    }

    public async Task<string> OrbitCameraAsync(object data)
    {
        return await PostAsyncNoRetry("/sceneview/orbit", data);
    }

    public async Task<string> PanCameraAsync(object data)
    {
        return await PostAsyncNoRetry("/sceneview/pan", data);
    }

    public async Task<string> ZoomCameraAsync(object data)
    {
        return await PostAsyncNoRetry("/sceneview/zoom", data);
    }

    public async Task<string> PickAtScreenAsync(object data)
    {
        return await PostAsyncNoRetry("/sceneview/pick", data);
    }

    #endregion

    #region Lighting Operations

    public async Task<string> CreateLightAsync(object data)
    {
        return await PostAsyncNoRetry("/light", data);
    }

    public async Task<string> ModifyLightAsync(object data)
    {
        return await PutAsyncNoRetry("/light", data);
    }

    public async Task<string> CreateReflectionProbeAsync(object data)
    {
        return await PostAsyncNoRetry("/reflection/probe", data);
    }

    public async Task<string> ModifyReflectionProbeAsync(object data)
    {
        return await PutAsyncNoRetry("/reflection/probe", data);
    }

    public async Task<string> BakeReflectionProbeAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/reflection/probe/bake", timeoutMs), data);
    }

    public async Task<string> CreateDecalProjectorAsync(object data)
    {
        return await PostAsyncNoRetry("/decal/projector", data);
    }

    public async Task<string> ModifyDecalProjectorAsync(object data)
    {
        return await PutAsyncNoRetry("/decal/projector", data);
    }

    public async Task<string> GetRenderSettingsAsync()
    {
        return await GetAsync("/rendersettings");
    }

    public async Task<string> SetRenderSettingsAsync(object data)
    {
        return await PostAsyncNoRetry("/rendersettings", data);
    }

    public async Task<string> CreateProceduralSkyboxAsync(object data)
    {
        return await PostAsyncNoRetry("/skybox/procedural", data);
    }

    public async Task<string> GetVolumeProfileAsync(string? profilePath = null, int volumeInstanceId = 0, bool includeRenderHooks = true)
    {
        var parts = new List<string>
        {
            $"includeRenderHooks={(includeRenderHooks ? "true" : "false")}"
        };

        if (!string.IsNullOrWhiteSpace(profilePath))
        {
            parts.Add($"path={Uri.EscapeDataString(profilePath)}");
        }

        if (volumeInstanceId != 0)
        {
            parts.Add($"volumeInstanceId={volumeInstanceId}");
        }

        return await GetAsync("/volume/profile?" + string.Join("&", parts));
    }

    public async Task<string> SetVolumeProfileOverridesAsync(object data)
    {
        return await PutAsyncNoRetry("/volume/profile/overrides", data);
    }

    public async Task<string> GetCameraRenderingAsync(int? instanceId = null, string? cameraName = null)
    {
        var parts = new List<string>();
        if (instanceId.HasValue && instanceId.Value != 0) parts.Add($"instanceId={instanceId.Value}");
        if (!string.IsNullOrWhiteSpace(cameraName)) parts.Add($"name={Uri.EscapeDataString(cameraName)}");
        var query = parts.Count > 0 ? "?" + string.Join("&", parts) : string.Empty;
        return await GetAsync("/camera/rendering" + query);
    }

    public async Task<string> SetCameraRenderingAsync(object data)
    {
        return await PutAsyncNoRetry("/camera/rendering", data);
    }

    #endregion

    #region Physics Operations

    public async Task<string> ConfigureRigidbodyAsync(object data)
    {
        return await PutAsyncNoRetry("/physics/rigidbody", data);
    }

    public async Task<string> ConfigureColliderAsync(object data)
    {
        return await PutAsyncNoRetry("/physics/collider", data);
    }

    public async Task<string> GetPhysicsSettingsAsync()
    {
        return await GetAsync("/physics/settings");
    }

    public async Task<string> SetPhysicsSettingsAsync(object data)
    {
        return await PostAsyncNoRetry("/physics/settings", data);
    }

    #endregion

    #region ParticleSystem Operations

    public async Task<string> GetParticleSystemAsync(int instanceId, string? modules = null)
    {
        var query = $"?instanceId={instanceId}";
        if (!string.IsNullOrEmpty(modules)) query += $"&modules={Uri.EscapeDataString(modules)}";
        return await GetAsync($"/particle-system{query}");
    }

    public async Task<string> ConfigureParticleSystemAsync(object data)
    {
        return await PutAsyncNoRetry("/particle-system", data);
    }

    public async Task<string> CreateParticleTemplateAsync(object data)
    {
        return await PostAsyncNoRetry("/particle-system/template", data);
    }

    #endregion

    #region Audio Operations

    public async Task<string> GetAudioSourceAsync(int instanceId, bool includeClipMeta = false, bool includeMixerInfo = false)
    {
        var parts = new List<string> { $"instanceId={instanceId}" };
        if (!includeClipMeta) parts.Add("includeClipMeta=false");
        if (!includeMixerInfo) parts.Add("includeMixerInfo=false");
        return await GetAsync($"/audio/source?{string.Join("&", parts)}");
    }

    public async Task<string> ConfigureAudioSourceAsync(object data)
    {
        return await PutAsyncNoRetry("/audio/source", data);
    }

    public async Task<string> GetAudioMixerAsync(string mixerPath, bool brief = true, int maxGroups = 50, int maxParameters = 50, int maxSnapshots = 20)
    {
        var parts = new List<string>
        {
            $"path={Uri.EscapeDataString(mixerPath)}",
            $"brief={(brief ? "true" : "false")}",
            $"maxGroups={maxGroups}",
            $"maxParameters={maxParameters}",
            $"maxSnapshots={maxSnapshots}"
        };
        return await GetAsync($"/audio/mixer?{string.Join("&", parts)}");
    }

    public async Task<string> ConfigureAudioMixerAsync(object data)
    {
        return await PutAsyncNoRetry("/audio/mixer", data);
    }

    #endregion

    #region Runtime/Gameplay Operations

    public async Task<string> GetRuntimeValuesAsync(object data)
    {
        return await PostAsync("/runtime/values", data);
    }

    public async Task<string> SetRuntimeFieldsAsync(object data)
    {
        return await PostAsyncNoRetry("/runtime/fields/set", data);
    }

    public async Task<string> InvokeMethodAsync(object data)
    {
        return await PostAsyncNoRetry("/runtime/invoke", data);
    }

    public async Task<string> InvokeSequenceAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/runtime/invoke-sequence", timeoutMs), data);
    }

    #endregion

    #region Renderer Operations

    public async Task<string> GetRendererStateAsync(object data)
    {
        return await PostAsync("/renderer/state", data);
    }

    public async Task<string> GetHierarchyRenderersAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/renderer/hierarchy", timeoutMs), data);
    }

    public async Task<string> AuditRenderersAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/renderer/audit", timeoutMs), data);
    }

    public async Task<string> GetMeshInfoAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/mesh/info", timeoutMs), data);
    }

    #endregion

    #region Lighting Audit Operations

    public async Task<string> AuditSceneLightingAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/lighting/audit", timeoutMs), data);
    }

    #endregion

    #region Spatial Audit Operations

    public async Task<string> CameraVisibilityAuditAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/spatial/visibility-audit", timeoutMs), data);
    }

    public async Task<string> RaycastCoverageCheckAsync(object data, int? timeoutMs = null)
    {
        return await PostAsync(AppendTimeoutQuery("/spatial/coverage-check", timeoutMs), data);
    }

    #endregion

    #region Material Validation Operations

    public async Task<string> ValidateMaterialAsync(object data)
    {
        return await PostAsync("/material/validate", data);
    }

    #endregion

    #region Asset Management Operations

    public async Task<string> MoveAssetAsync(object data)
    {
        return await PostAsyncNoRetry("/asset/move", data);
    }

    public async Task<string> DuplicateAssetAsync(object data)
    {
        return await PostAsyncNoRetry("/asset/duplicate", data);
    }

    public async Task<string> DeleteAssetAsync(object data)
    {
        return await PostAsyncNoRetry("/asset/delete", data);
    }

    public async Task<string> CreateFolderAsync(object data)
    {
        return await PostAsyncNoRetry("/asset/folder", data);
    }

    public async Task<string> GetAssetInfoAsync(string path)
    {
        var encodedPath = Uri.EscapeDataString(path);
        return await GetAsync($"/asset/info?path={encodedPath}");
    }

    #endregion

    #region Animation Operations

    public async Task<string> CreateAnimatorControllerAsync(object data)
    {
        return await PostAsyncNoRetry("/animator/controller", data);
    }

    public async Task<string> AddAnimationStateAsync(object data)
    {
        return await PostAsyncNoRetry("/animator/state", data);
    }

    public async Task<string> AddAnimationTransitionAsync(object data)
    {
        return await PostAsyncNoRetry("/animator/transition", data);
    }

    public async Task<string> SetAnimationParameterAsync(object data)
    {
        return await PostAsyncNoRetry("/animator/parameter", data);
    }

    public async Task<string> CreateAnimationClipAsync(object data)
    {
        return await PostAsyncNoRetry("/animator/clip", data);
    }

    public async Task<string> GetAnimatorInfoAsync(string controllerPath, int layerIndex = -1)
    {
        var encodedPath = Uri.EscapeDataString(controllerPath);
        return await GetAsync($"/animator/info?path={encodedPath}&layerIndex={layerIndex}");
    }

    public async Task<string> GetFbxClipsAsync(string fbxPath)
    {
        var encodedPath = Uri.EscapeDataString(fbxPath);
        return await GetAsync($"/animator/fbx-clips?path={encodedPath}");
    }

    #endregion

    #region Snap/Align Operations

    public async Task<string> SnapObjectsAsync(object data)
    {
        return await PostAsyncNoRetry("/snap", data);
    }

    #endregion

    #region Look Preset Operations

    public async Task<string> SaveLookPresetAsync(object data)
    {
        return await PostAsyncNoRetry("/look/preset", data);
    }

    public async Task<string> LoadLookPresetAsync(object data)
    {
        return await PutAsyncNoRetry("/look/preset", data);
    }

    public async Task<string> ListLookPresetsAsync()
    {
        return await GetAsync("/look/presets");
    }

    public async Task<string> ApplySeparationSafeLookAsync(object data)
    {
        return await PostAsyncNoRetry("/look/separation-safe", data);
    }

    #endregion
}
