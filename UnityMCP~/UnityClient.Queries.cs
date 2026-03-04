using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Linq;

namespace UnityMCP;

public partial class UnityClient
{
    #region Scene Profile Operations

    public async Task<string> ExtractSceneProfileAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/scene/profile", timeoutMs), data);
    }

    public async Task<string> GetSavedSceneProfileAsync(string name, bool brief = true, int maxEntries = 25)
    {
        return await GetAsync($"/scene/profile?name={Uri.EscapeDataString(name)}&brief={(brief ? "true" : "false")}&maxEntries={maxEntries}");
    }

    #endregion

    #region Asset Catalog Generator Operations

    public async Task<string> GenerateAssetCatalogNewAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/catalog/generate", timeoutMs), data);
    }

    public async Task<string> GetSavedAssetCatalogAsync(string name, bool brief = true, int maxEntries = 40)
    {
        return await GetAsync($"/catalog?name={Uri.EscapeDataString(name)}&brief={(brief ? "true" : "false")}&maxEntries={maxEntries}");
    }

    public async Task<string> PinAssetPackContextAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/asset-pack/pin", timeoutMs), data);
    }

    public async Task<string> GetAssetPackContextPinAsync(string name, bool brief = true, int maxEntries = 40)
    {
        return await GetAsync($"/asset-pack/pin?name={Uri.EscapeDataString(name)}&brief={(brief ? "true" : "false")}&maxEntries={maxEntries}");
    }

    public async Task<string> ListAssetPackContextPinsAsync()
    {
        return await GetAsync("/asset-pack/pins");
    }

    #endregion

    #region Contract Operations

    public async Task<string> RegisterContractsAsync(object data)
    {
        return await PostAsyncNoRetry("/contracts/register", data);
    }

    public async Task<string> QueryContractsAsync()
    {
        return await GetAsync("/contracts");
    }

    public async Task<string> ClearContractsAsync()
    {
        return await DeleteAsyncNoRetry("/contracts");
    }

    #endregion

    #region Replay Operations

    public async Task<string> ReplayStartRecordingAsync(object data)
    {
        return await PostAsyncNoRetry("/replay/start", data);
    }

    public async Task<string> ReplayStopRecordingAsync()
    {
        return await PostAsyncNoRetry("/replay/stop", new { });
    }

    public async Task<string> ReplayExecuteAsync(object data, int? timeoutMs = null)
    {
        return await PostAsyncNoRetry(AppendTimeoutQuery("/replay/execute", timeoutMs), data);
    }

    public async Task<string> ReplayGetStatusAsync()
    {
        return await GetAsync("/replay/status");
    }

    public async Task<string> ReplayRecordInputAsync(object data)
    {
        return await PostAsyncNoRetry("/replay/input", data);
    }

    #endregion

    #region Delta Cache Operations

    public async Task<string> CaptureDeltaSnapshotAsync(object data)
    {
        return await PostAsyncNoRetry("/delta/capture", data);
    }

    public async Task<string> GetDeltaAsync(string snapshotName)
    {
        return await GetAsync($"/delta?name={Uri.EscapeDataString(snapshotName)}");
    }

    public async Task<string> ListDeltaSnapshotsAsync()
    {
        return await GetAsync("/delta/list");
    }

    public async Task<string> DeleteDeltaSnapshotAsync(string snapshotName)
    {
        return await DeleteAsyncNoRetry($"/delta?name={Uri.EscapeDataString(snapshotName)}");
    }

    public async Task<string> BatchReadAsync(object data)
    {
        return await PostAsyncNoRetry("/batch-read", data);
    }

    public async Task<string> BatchModifyChildrenAsync(object data)
    {
        return await PostAsyncNoRetry("/gameobject/batch-modify-children", data);
    }

    public async Task<string> CheckSpatialEnclosureAsync(object data)
    {
        return await PostAsyncNoRetry("/spatial/check-enclosure", data);
    }

    #endregion

    // ==================== HTTP METHODS WITH RETRY ====================

    private static string AppendTimeoutQuery(string endpoint, int? timeoutMs)
    {
        if (!timeoutMs.HasValue || timeoutMs.Value <= 0)
        {
            return endpoint;
        }

        var separator = endpoint.Contains('?') ? '&' : '?';
        return $"{endpoint}{separator}timeoutMs={timeoutMs.Value}";
    }

    // ─── AI Bridge Editor endpoints ───

    public async Task<string> GetAiBridgeEditorsAsync()
    {
        return await GetAsync("/component/ai-editors");
    }

    public async Task<string> AiInspectComponentAsync(int instanceId, string componentType)
    {
        return await GetAsync($"/component/ai-inspect/{instanceId}/{Uri.EscapeDataString(componentType)}");
    }

    public async Task<string> AiApplyComponentAsync(object request)
    {
        return await PutAsyncNoRetry("/component/ai-apply", request);
    }

    private async Task<string> GetAsync(string endpoint)
    {
        return await WithRetry(endpoint, async () =>
        {
            var response = await _httpClient.GetAsync($"{_baseUrl}{endpoint}");
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return BuildHttpErrorResponse(response, content);
            }

            return FormatJson(content);
        });
    }

    private async Task<string> PostAsync(string endpoint, object data)
    {
        return await WithRetry(endpoint, async () =>
        {
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_baseUrl}{endpoint}", content);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return BuildHttpErrorResponse(response, responseContent);
            }

            return FormatJson(responseContent);
        });
    }

    private async Task<string> PutAsync(string endpoint, object data)
    {
        return await WithRetry(endpoint, async () =>
        {
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PutAsync($"{_baseUrl}{endpoint}", content);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return BuildHttpErrorResponse(response, responseContent);
            }

            return FormatJson(responseContent);
        });
    }

    private async Task<string> DeleteAsync(string endpoint)
    {
        return await WithRetry(endpoint, async () =>
        {
            var response = await _httpClient.DeleteAsync($"{_baseUrl}{endpoint}");
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return BuildHttpErrorResponse(response, content);
            }

            return FormatJson(content);
        });
    }

    // ==================== HTTP METHODS WITHOUT RETRY (MUTATIONS) ====================

    private async Task<string> PostAsyncNoRetry(string endpoint, object data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_baseUrl}{endpoint}", content);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return BuildHttpErrorResponse(response, responseContent);
            }

            return FormatJson(responseContent);
        }
        catch (HttpRequestException ex)
        {
            var diagnosis = await DiagnoseBridgeState();
            return JsonSerializer.Serialize(new
            {
                error = "Unity Editor not available",
                message = diagnosis,
                details = ex.Message
            }, _jsonOptions);
        }
        catch (TaskCanceledException)
        {
            return JsonSerializer.Serialize(new
            {
                error = "Request timed out",
                message = "The request exceeded the timeout limit."
            }, _jsonOptions);
        }
    }

    private async Task<string> PutAsyncNoRetry(string endpoint, object data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PutAsync($"{_baseUrl}{endpoint}", content);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return BuildHttpErrorResponse(response, responseContent);
            }

            return FormatJson(responseContent);
        }
        catch (HttpRequestException ex)
        {
            var diagnosis = await DiagnoseBridgeState();
            return JsonSerializer.Serialize(new
            {
                error = "Unity Editor not available",
                message = diagnosis,
                details = ex.Message
            }, _jsonOptions);
        }
        catch (TaskCanceledException)
        {
            return JsonSerializer.Serialize(new
            {
                error = "Request timed out",
                message = "The request exceeded the timeout limit."
            }, _jsonOptions);
        }
    }

    private async Task<string> DeleteAsyncNoRetry(string endpoint)
    {
        try
        {
            var response = await _httpClient.DeleteAsync($"{_baseUrl}{endpoint}");
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return BuildHttpErrorResponse(response, content);
            }

            return FormatJson(content);
        }
        catch (HttpRequestException ex)
        {
            var diagnosis = await DiagnoseBridgeState();
            return JsonSerializer.Serialize(new
            {
                error = "Unity Editor not available",
                message = diagnosis,
                details = ex.Message
            }, _jsonOptions);
        }
        catch (TaskCanceledException)
        {
            return JsonSerializer.Serialize(new
            {
                error = "Request timed out",
                message = "The request exceeded the timeout limit."
            }, _jsonOptions);
        }
    }

    // ==================== RETRY HELPER ====================

    /// <summary>
    /// Retry an HTTP action with exponential backoff (1s, 2s, 4s).
    /// On final failure, diagnoses the bridge state and returns an actionable error.
    /// </summary>
    private async Task<string> WithRetry(string endpoint, Func<Task<string>> action)
    {
        int maxRetries = ResolveMaxRetries(endpoint);
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                return await action();
            }
            catch (HttpRequestException) when (i < maxRetries)
            {
                await Task.Delay(1000 * (1 << i)); // 1s, 2s, 4s
            }
            catch (TaskCanceledException) when (i < maxRetries)
            {
                await Task.Delay(1000 * (1 << i));
            }
        }

        // Final attempt — on failure, diagnose and return structured error
        try
        {
            return await action();
        }
        catch (HttpRequestException ex)
        {
            var diagnosis = await DiagnoseBridgeState();
            return JsonSerializer.Serialize(new
            {
                error = "Unity Editor not available",
                message = diagnosis,
                details = ex.Message,
                retriesExhausted = true
            }, _jsonOptions);
        }
        catch (TaskCanceledException)
        {
            var diagnosis = await DiagnoseBridgeState();
            return JsonSerializer.Serialize(new
            {
                error = "Request timed out",
                message = diagnosis,
                retriesExhausted = true
            }, _jsonOptions);
        }
    }

    private static int ResolveMaxRetries(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return DefaultMaxRetries;
        }

        var path = endpoint.ToLowerInvariant();
        if (path.Contains("/scene/patch-batch")
            || path.Contains("/scene/repro-step")
            || path.Contains("/scene/build-and-screenshot")
            || path.Contains("/scene/transaction")
            || path.Contains("/scene/spawn-along-path")
            || path.Contains("/scene/tile-separation")
            || path.Contains("/scene/resolve-tile-overlaps")
            || path.Contains("/scene/profile")
            || path.Contains("/catalog/generate")
            || path.Contains("/asset-pack/pin")
            || path.Contains("/image/compare")
            || path.Contains("/replay/execute")
            || path.Contains("/batch-read"))
        {
            // Heavy routes should not stack retries aggressively.
            return 1;
        }

        return DefaultMaxRetries;
    }

    // ==================== UTILITIES ====================

    private string FormatJson(string json)
    {
        if (!_formatJsonResponses)
        {
            return json;
        }

        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(json);
            return JsonSerializer.Serialize(element, _jsonOptions);
        }
        catch
        {
            return json;
        }
    }

    private string BuildHttpErrorResponse(HttpResponseMessage response, string content)
    {
        // Preserve bridge/domain JSON error payloads so callers keep code/route/retriable fields.
        if (TryParseJsonElement(content, out var parsed)
            && (parsed.ValueKind == JsonValueKind.Object || parsed.ValueKind == JsonValueKind.Array))
        {
            return FormatJson(content);
        }

        return JsonSerializer.Serialize(new
        {
            success = false,
            error = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
            code = "HTTP_ERROR",
            retriable = false,
            details = string.IsNullOrWhiteSpace(content) ? null : content
        }, _jsonOptions);
    }

    private static bool TryParseJsonElement(string json, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            element = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static object ParseJsonOrString(string raw)
    {
        return TryParseJsonElement(raw, out var element) ? element : raw;
    }

    private static bool IsBridgeErrorPayload(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("error", out var errorEl)
            && errorEl.ValueKind == JsonValueKind.String;
    }

    private static string ResolveScreenshotPayloadMode(string? payloadMode, bool includeBase64, bool includeHandle)
    {
        if (!string.IsNullOrWhiteSpace(payloadMode))
        {
            var normalized = payloadMode.Trim().ToLowerInvariant();
            if (normalized == "auto")
            {
                // Fall through to derivation from boolean flags.
            }
            else if (normalized is "handle" or "base64" or "both" or "none")
            {
                return normalized;
            }
        }

        if (includeBase64 && includeHandle)
        {
            return "both";
        }
        if (includeBase64)
        {
            return "base64";
        }
        if (includeHandle)
        {
            return "handle";
        }

        return "none";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static bool ParseBoolEnv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
