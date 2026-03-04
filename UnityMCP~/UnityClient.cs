using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace UnityMCP;

/// <summary>
/// HTTP client for communicating with the Unity Editor Agent Bridge server.
/// Includes retry-with-backoff for resilience during recompilation and backgrounding.
/// </summary>
public partial class UnityClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly bool _formatJsonResponses;
    private const int DefaultMaxRetries = 3;

    public UnityClient()
    {
        var portStr = Environment.GetEnvironmentVariable("UNITY_BRIDGE_PORT") ?? "5847";
        if (!int.TryParse(portStr, out var port) || port < 1 || port > 65535)
            throw new InvalidOperationException(
                $"UNITY_BRIDGE_PORT must be 1-65535, got: '{portStr}'");
        _baseUrl = $"http://127.0.0.1:{port}";
        var prettyJson = ParseBoolEnv(Environment.GetEnvironmentVariable("UNITY_MCP_PRETTY_JSON"));
        _formatJsonResponses = prettyJson;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(320)
        };

        var authToken = Environment.GetEnvironmentVariable("BRIDGE_AUTH_TOKEN");
        if (!string.IsNullOrEmpty(authToken))
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authToken);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = prettyJson
        };
    }

    internal UnityClient(HttpClient httpClient, string baseUrl)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl;
        _formatJsonResponses = false;
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    }

    // ==================== HEALTH & STATUS ====================

    /// <summary>
    /// Check if Unity Editor is running and the bridge is available.
    /// Returns a structured status with diagnostic info.
    /// </summary>
    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get enriched health/status information from Unity.
    /// Includes: isCompiling, pendingQueueSize, lastTickAge, serverUptimeSeconds.
    /// </summary>
    public async Task<string> GetHealthAsync()
    {
        return await GetAsync("/health");
    }

    /// <summary>
    /// Diagnose the bridge state and return an actionable error message.
    /// </summary>
    private async Task<string> DiagnoseBridgeState()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}/health");
            var content = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                // Health responds but a command failed — likely recompiling or backgrounded
                try
                {
                    var health = JsonSerializer.Deserialize<JsonElement>(content);
                    bool isCompiling = health.TryGetProperty("isCompiling", out var comp) && comp.GetBoolean();
                    float lastTickAge = health.TryGetProperty("lastTickAge", out var tick) ? tick.GetSingle() : 0;

                    if (isCompiling)
                        return "Unity is recompiling scripts. The bridge will be available after compilation finishes.";
                    if (lastTickAge > 3)
                        return $"Unity appears to be backgrounded (last tick {lastTickAge:F1}s ago). Bringing Unity to the foreground may help.";
                }
                catch { }

                return "Unity bridge is running but the command failed. The editor may be busy.";
            }

            return "Unity bridge returned an error. The editor may be in an unstable state.";
        }
        catch (HttpRequestException)
        {
            return "Unity Editor bridge is not running. Make sure Unity Editor is open and Agent Bridge server is started.";
        }
        catch (TaskCanceledException)
        {
            return "Unity Editor bridge is not responding (health check timed out). The editor may be frozen or not running.";
        }
    }

    // ==================== HIERARCHY ====================

    /// <summary>
    /// Get the scene hierarchy. Supports optional depth limit and brief mode.
    /// </summary>
    public async Task<string> GetHierarchyAsync(int depth = 0, bool brief = true, bool pretty = false)
    {
        var url = "/hierarchy";
        var parts = new List<string>();
        if (depth >= 0) parts.Add($"depth={depth}");
        parts.Add($"brief={brief.ToString().ToLowerInvariant()}");
        if (pretty) parts.Add("pretty=true");
        if (parts.Count > 0) url += "?" + string.Join("&", parts);
        return await GetAsync(url);
    }

    // ==================== GAMEOBJECT ====================

    /// <summary>
    /// Get details about a specific GameObject.
    /// </summary>
    public async Task<string> GetGameObjectAsync(int instanceId, bool includeComponents = false, bool transformOnly = false)
    {
        var url = $"/gameobject/{instanceId}";
        var parts = new List<string>();
        parts.Add($"include_components={includeComponents.ToString().ToLowerInvariant()}");
        if (transformOnly) parts.Add("transform_only=true");
        if (parts.Count > 0) url += "?" + string.Join("&", parts);
        return await GetAsync(url);
    }

    /// <summary>
    /// Modify a GameObject.
    /// </summary>
    public async Task<string> ModifyGameObjectAsync(int instanceId, object modifications)
    {
        return await PutAsyncNoRetry($"/gameobject/{instanceId}", modifications);
    }

    /// <summary>
    /// Spawn a new GameObject or prefab.
    /// </summary>
    public async Task<string> SpawnAsync(object spawnRequest)
    {
        return await PostAsyncNoRetry("/spawn", spawnRequest);
    }

    /// <summary>
    /// Spawn a Unity primitive with optional inline color material.
    /// </summary>
    public async Task<string> SpawnPrimitiveAsync(object request)
    {
        return await PostAsyncNoRetry("/spawn/primitive", request);
    }

    /// <summary>
    /// Spawn multiple GameObjects/prefabs in one call.
    /// </summary>
    public async Task<string> SpawnBatchAsync(object batchRequest, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/spawn/batch", timeoutMs), batchRequest);
    }

    /// <summary>
    /// Get the route catalog from the bridge.
    /// </summary>
    public async Task<string> GetRoutesAsync(string? category = null, string? search = null, int max = 0, bool compact = false)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(category)) parts.Add($"category={Uri.EscapeDataString(category)}");
        if (!string.IsNullOrWhiteSpace(search)) parts.Add($"search={Uri.EscapeDataString(search)}");
        if (max > 0) parts.Add($"max={Math.Clamp(max, 1, 500)}");
        if (compact) parts.Add("compact=true");

        var url = parts.Count > 0 ? "/routes?" + string.Join("&", parts) : "/routes";
        return await GetAsync(url);
    }

    /// <summary>
    /// Delete a GameObject.
    /// </summary>
    public async Task<string> DeleteGameObjectAsync(int instanceId)
    {
        return await DeleteAsyncNoRetry($"/delete/{instanceId}");
    }

    // ==================== CONSOLE ====================

    /// <summary>
    /// Get console logs with optional type and text filtering.
    /// </summary>
    public async Task<string> GetConsoleLogsAsync(int count = 50, string? typeFilter = null, string? textFilter = null, bool includeStackTrace = false)
    {
        var url = $"/console?count={count}&includeStackTrace={(includeStackTrace ? "true" : "false")}";
        if (!string.IsNullOrEmpty(typeFilter))
            url += $"&type={Uri.EscapeDataString(typeFilter)}";
        if (!string.IsNullOrEmpty(textFilter))
            url += $"&text={Uri.EscapeDataString(textFilter)}";
        return await GetAsync(url);
    }

    /// <summary>
    /// Clear all console logs.
    /// </summary>
    public async Task<string> ClearConsoleLogsAsync()
    {
        return await DeleteAsyncNoRetry("/console");
    }

    // ==================== FIND GAMEOBJECTS ====================

    /// <summary>
    /// Find GameObjects in the scene by name, component, tag, layer, or active state.
    /// </summary>
    public async Task<string> FindGameObjectsAsync(string? name = null, string? component = null, string? tag = null, string? layer = null, bool? active = null, int maxResults = 100, bool includeComponents = false)
    {
        var url = "/gameobjects/find?";
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(name)) parts.Add($"name={Uri.EscapeDataString(name)}");
        if (!string.IsNullOrEmpty(component)) parts.Add($"component={Uri.EscapeDataString(component)}");
        if (!string.IsNullOrEmpty(tag)) parts.Add($"tag={Uri.EscapeDataString(tag)}");
        if (!string.IsNullOrEmpty(layer)) parts.Add($"layer={Uri.EscapeDataString(layer)}");
        if (active.HasValue) parts.Add($"active={active.Value.ToString().ToLowerInvariant()}");
        parts.Add($"includeComponents={includeComponents.ToString().ToLowerInvariant()}");
        parts.Add($"max={maxResults}");
        url += string.Join("&", parts);
        return await GetAsync(url);
    }

    // ==================== SCRIPTS ====================

    /// <summary>
    /// List scripts in the project with optional filtering.
    /// </summary>
    public async Task<string> ListScriptsAsync(string? name = null, bool? isMonoBehaviour = null, bool? isScriptableObject = null, int offset = 0, int limit = 50)
    {
        var url = "/scripts/list?";
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(name)) parts.Add($"name={Uri.EscapeDataString(name)}");
        if (isMonoBehaviour.HasValue) parts.Add($"isMonoBehaviour={isMonoBehaviour.Value.ToString().ToLowerInvariant()}");
        if (isScriptableObject.HasValue) parts.Add($"isScriptableObject={isScriptableObject.Value.ToString().ToLowerInvariant()}");
        parts.Add($"offset={offset}");
        parts.Add($"limit={limit}");
        url += string.Join("&", parts);
        return await GetAsync(url);
    }

    /// <summary>
    /// Get detailed structure of a script via reflection.
    /// </summary>
    public async Task<string> GetScriptStructureAsync(
        string path,
        bool includeMethods = true,
        bool includeFields = true,
        bool includeProperties = true,
        bool includeEvents = true,
        int maxMethods = -1,
        int maxFields = -1,
        int maxProperties = -1,
        int maxEvents = -1,
        bool includeAttributes = true,
        bool includeMethodParameters = true)
    {
        var parts = new List<string> { $"path={Uri.EscapeDataString(path)}" };
        if (!includeMethods) parts.Add("includeMethods=false");
        if (!includeFields) parts.Add("includeFields=false");
        if (!includeProperties) parts.Add("includeProperties=false");
        if (!includeEvents) parts.Add("includeEvents=false");
        if (maxMethods >= 0) parts.Add($"maxMethods={Math.Clamp(maxMethods, 1, 2000)}");
        if (maxFields >= 0) parts.Add($"maxFields={Math.Clamp(maxFields, 1, 2000)}");
        if (maxProperties >= 0) parts.Add($"maxProperties={Math.Clamp(maxProperties, 1, 2000)}");
        if (maxEvents >= 0) parts.Add($"maxEvents={Math.Clamp(maxEvents, 1, 2000)}");
        if (!includeAttributes) parts.Add("includeAttributes=false");
        if (!includeMethodParameters) parts.Add("includeMethodParameters=false");

        return await GetAsync("/scripts/structure?" + string.Join("&", parts));
    }

    /// <summary>
    /// Control play mode.
    /// </summary>
    public async Task<string> ControlPlayModeAsync(string action)
    {
        return await PostAsyncNoRetry("/playmode", new { action });
    }

    /// <summary>
    /// Control play mode and wait until the editor health reflects the requested state.
    /// This helps avoid follow-up calls during play mode/domain transition churn.
    /// </summary>
    public async Task<string> ControlPlayModeAndWaitAsync(
        string action,
        int maxWaitMs = 15000,
        int pollIntervalMs = 250,
        bool requireHealthStable = true,
        int stablePollCount = 3,
        int minStableMs = 750)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return JsonSerializer.Serialize(new { success = false, error = "action is required" }, _jsonOptions);
        }

        var normalizedAction = action.Trim().ToLowerInvariant();
        int maxControlAttempts = normalizedAction == "step" ? 1 : 3;
        int controlAttempts = 0;
        int controlTransportFailures = 0;
        string controlResult = string.Empty;
        bool commandIssued = false;

        while (controlAttempts < maxControlAttempts && !commandIssued)
        {
            controlAttempts++;
            try
            {
                controlResult = await ControlPlayModeAsync(normalizedAction);
                commandIssued = true;
                break;
            }
            catch (HttpRequestException ex)
            {
                controlTransportFailures++;
                controlResult = JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Transport error while sending play mode command",
                    action = normalizedAction,
                    details = ex.Message
                }, _jsonOptions);
            }
            catch (TaskCanceledException ex)
            {
                controlTransportFailures++;
                controlResult = JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Timed out while sending play mode command",
                    action = normalizedAction,
                    details = ex.Message
                }, _jsonOptions);
            }

            if (!commandIssued && controlAttempts < maxControlAttempts)
            {
                int backoffMs = Math.Clamp(150 * (1 << (controlAttempts - 1)), 150, 1200);
                await Task.Delay(backoffMs);
            }
        }

        // For step (single-frame tick) just return immediate result.
        if (normalizedAction == "step")
        {
            return controlResult;
        }

        int timeout = Math.Clamp(maxWaitMs, 250, 120000);
        int interval = Math.Clamp(pollIntervalMs, 50, 2000);
        int requiredStablePolls = Math.Clamp(stablePollCount, 1, 20);
        int requiredStableMs = Math.Clamp(minStableMs, 0, 10000);
        var startedAt = DateTime.UtcNow;
        var deadline = startedAt.AddMilliseconds(timeout);
        JsonElement? lastHealth = null;
        JsonElement? lastRuntime = null;
        int stablePolls = 0;
        DateTime? stableSince = null;
        bool serverRestarted = false;
        double? previousUptime = null;
        int? previousDomainReload = null;
        int healthParseFailures = 0;
        int unavailablePolls = 0;

        bool IsTargetState(bool isPlaying, bool isPaused)
        {
            return normalizedAction switch
            {
                "play" => isPlaying,
                "stop" => !isPlaying,
                "pause" => isPlaying && isPaused,
                "resume" => isPlaying && !isPaused,
                "unpause" => isPlaying && !isPaused,
                _ => true
            };
        }

        while (DateTime.UtcNow <= deadline)
        {
            await Task.Delay(interval);

            bool hasState = false;
            bool isPlaying = false;
            bool isCompiling = false;
            bool isPaused = false;
            bool isUpdating = false;

            var healthRaw = await GetHealthAsync();
            if (TryParseJsonElement(healthRaw, out var health) && !IsBridgeErrorPayload(health))
            {
                lastHealth = health;
                hasState = true;
                isPlaying = health.TryGetProperty("isPlaying", out var playingEl) && playingEl.GetBoolean();
                isCompiling = health.TryGetProperty("isCompiling", out var compilingEl) && compilingEl.GetBoolean();

                if (health.TryGetProperty("serverUptimeSeconds", out var uptimeEl) && uptimeEl.ValueKind == JsonValueKind.Number)
                {
                    var uptime = uptimeEl.GetDouble();
                    if (previousUptime.HasValue && uptime + 0.001d < previousUptime.Value)
                    {
                        serverRestarted = true;
                    }
                    previousUptime = uptime;
                }

                if (health.TryGetProperty("domainReloadCount", out var domainReloadEl) && domainReloadEl.ValueKind == JsonValueKind.Number)
                {
                    var domainReloadCount = domainReloadEl.GetInt32();
                    if (previousDomainReload.HasValue && domainReloadCount > previousDomainReload.Value)
                    {
                        serverRestarted = true;
                    }
                    previousDomainReload = domainReloadCount;
                }
            }
            else
            {
                healthParseFailures++;
            }

            var runtimeRaw = await GetEditorRuntimeAsync();
            if (TryParseJsonElement(runtimeRaw, out var runtime) && !IsBridgeErrorPayload(runtime))
            {
                lastRuntime = runtime;
                hasState = true;
                if (runtime.TryGetProperty("isPlaying", out var runtimePlaying)
                    && (runtimePlaying.ValueKind == JsonValueKind.True || runtimePlaying.ValueKind == JsonValueKind.False))
                {
                    isPlaying = runtimePlaying.GetBoolean();
                }
                if (runtime.TryGetProperty("isCompiling", out var runtimeCompiling) && (runtimeCompiling.ValueKind == JsonValueKind.True || runtimeCompiling.ValueKind == JsonValueKind.False))
                {
                    isCompiling = runtimeCompiling.GetBoolean();
                }
                if (runtime.TryGetProperty("isPaused", out var runtimePaused) && (runtimePaused.ValueKind == JsonValueKind.True || runtimePaused.ValueKind == JsonValueKind.False))
                {
                    isPaused = runtimePaused.GetBoolean();
                }
                if (runtime.TryGetProperty("isUpdating", out var runtimeUpdating) && (runtimeUpdating.ValueKind == JsonValueKind.True || runtimeUpdating.ValueKind == JsonValueKind.False))
                {
                    isUpdating = runtimeUpdating.GetBoolean();
                }
            }
            else
            {
                healthParseFailures++;
            }

            if (!hasState)
            {
                unavailablePolls++;
                stablePolls = 0;
                stableSince = null;
                continue;
            }

            if (TryParseJsonElement(controlResult, out var controlJson)
                && controlJson.TryGetProperty("isPaused", out var pausedEl)
                && (pausedEl.ValueKind == JsonValueKind.True || pausedEl.ValueKind == JsonValueKind.False))
            {
                isPaused = pausedEl.GetBoolean();
            }

            bool ready = IsTargetState(isPlaying, isPaused) && !isCompiling && !isUpdating;
            if (ready)
            {
                stablePolls++;
                stableSince ??= DateTime.UtcNow;
                var stableMs = (int)Math.Max(0, (DateTime.UtcNow - stableSince.Value).TotalMilliseconds);
                var stabilitySatisfied = !requireHealthStable
                    || (stablePolls >= requiredStablePolls && stableMs >= requiredStableMs);

                if (!stabilitySatisfied)
                {
                    continue;
                }

                var waitedMs = (int)Math.Max(0, (DateTime.UtcNow - startedAt).TotalMilliseconds);
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    action = normalizedAction,
                    waitedMs,
                    stablePolls,
                    stableMs,
                    requiredStablePolls,
                    requiredStableMs,
                    serverRestarted,
                    commandIssued,
                    controlAttempts,
                    controlTransportFailures,
                    healthParseFailures,
                    unavailablePolls,
                    control = ParseJsonOrString(controlResult),
                    health,
                    runtime = lastRuntime.HasValue ? lastRuntime.Value : default
                }, _jsonOptions);
            }

            stablePolls = 0;
            stableSince = null;
        }

        return JsonSerializer.Serialize(new
        {
            success = false,
            error = "Timed out waiting for play mode transition",
            action = normalizedAction,
            maxWaitMs = timeout,
            requiredStablePolls,
            requiredStableMs,
            serverRestarted,
            commandIssued,
            controlAttempts,
            controlTransportFailures,
            healthParseFailures,
            unavailablePolls,
            control = ParseJsonOrString(controlResult),
            health = lastHealth.HasValue ? lastHealth.Value : default,
            runtime = lastRuntime.HasValue ? lastRuntime.Value : default
        }, _jsonOptions);
    }

    /// <summary>
    /// Take a screenshot.
    /// </summary>
    public async Task<string> TakeScreenshotAsync(
        string viewType = "game",
        bool includeBase64 = false,
        bool includeHandle = true,
        int width = 0,
        int height = 0,
        string? payloadMode = null)
    {
        var url = $"/screenshot?view={Uri.EscapeDataString(viewType)}";
        var resolvedMode = ResolveScreenshotPayloadMode(payloadMode, includeBase64, includeHandle);
        if (!string.IsNullOrWhiteSpace(resolvedMode) && !string.Equals(resolvedMode, "none", StringComparison.OrdinalIgnoreCase))
        {
            url += $"&mode={Uri.EscapeDataString(resolvedMode)}";
        }
        else
        {
            var include = includeBase64 ? "true" : "false";
            var handle = includeHandle ? "true" : "false";
            url += $"&includeBase64={include}&includeHandle={handle}";
        }
        if (width > 0 && height > 0)
        {
            url += $"&width={width}&height={height}";
        }
        return await GetAsync(url);
    }

    /// <summary>
    /// Get current scene info.
    /// </summary>
    public async Task<string> GetSceneAsync()
    {
        return await GetAsync("/scene");
    }

    public async Task<string> GetSceneDirtyStateAsync()
    {
        return await GetAsync("/scene/dirty");
    }

    /// <summary>
    /// Get a single-call scene layout snapshot (camera, player, tile stats, render summary).
    /// </summary>
    public async Task<string> GetSceneLayoutSnapshotAsync(string? tileRootName = null, int maxTiles = 600)
    {
        var url = "/scene/layout-snapshot?maxTiles=" + maxTiles;
        if (!string.IsNullOrWhiteSpace(tileRootName))
        {
            url += "&tileRoot=" + Uri.EscapeDataString(tileRootName);
        }
        return await GetAsync(url);
    }

    // ==================== PACKAGE MANAGER ====================

    public async Task<string> ListPackagesAsync()
    {
        return await GetAsync("/packages");
    }

    public async Task<string> AddPackageAsync(string identifier)
    {
        return await PostAsyncNoRetry("/packages/add", new { identifier });
    }

    public async Task<string> RemovePackageAsync(string name)
    {
        return await PostAsyncNoRetry("/packages/remove", new { name });
    }

    public async Task<string> SearchPackagesAsync(string query = "", int maxResults = 50)
    {
        var url = $"/packages/search?query={Uri.EscapeDataString(query)}&max={Math.Clamp(maxResults, 1, 200)}";
        return await GetAsync(url);
    }

    // ==================== TEST RUNNER ====================

    public async Task<string> RunTestsAsync(object request, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/tests/run", timeoutMs), request);
    }

    public async Task<string> GetTestResultsAsync()
    {
        return await GetAsync("/tests/results");
    }

    // ==================== TYPE SCHEMA ====================

    public async Task<string> GetTypeSchemaAsync(string typeName, int maxMembers = 100)
    {
        var url = $"/type/schema?typeName={Uri.EscapeDataString(typeName)}&maxMembers={Math.Clamp(maxMembers, 1, 500)}";
        return await GetAsync(url);
    }
}
