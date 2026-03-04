using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge
{
    public static partial class UnityAgentBridgeServer
    {
        private static readonly int MaxRequestBodyBytes = ParseIntEnv("BRIDGE_MAX_REQUEST_BODY_BYTES", 10_485_760, 1024, 16_777_216);
        private static readonly int MaxConcurrentRequests = ParseIntEnv("BRIDGE_MAX_CONCURRENT_REQUESTS", 32, 1, 512);
        private static int _activeHttpRequestCount = 0;

        private static void ListenForRequests()
        {
            while (_isRunning)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                }
                catch (HttpListenerException)
                {
                    // Expected when stopping
                }
                catch (ThreadAbortException)
                {
                    // Expected during domain reload — Unity aborts background threads
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        Debug.LogError($"[AgentBridge] Request error: {ex.Message}");
                    }
                }
            }
        }

        // ==================== MAIN THREAD DISPATCH HELPER ====================

        /// <summary>
        /// Dispatch a function to the Unity main thread and wait for its result.
        /// Replaces the 90+ copy-pasted waitHandle blocks throughout RouteRequest.
        /// </summary>
        private static (string result, int statusCode) RunOnMainThread(
            Func<string> work, int timeoutMs = 5000, int successCode = 200, int failCode = 500, string routeLabel = null, bool debug = false)
        {
            // Fall back to per-request context when explicit params aren't provided
            if (routeLabel == null) routeLabel = _currentRoute ?? "(unknown-route)";
            if (!debug) debug = _currentDebug;

            var item = new MainThreadWorkItem
            {
                id = Interlocked.Increment(ref _nextWorkItemId),
                route = routeLabel,
                work = work,
                completion = new TaskCompletionSource<MainThreadWorkResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                enqueuedAtUtc = DateTime.UtcNow,
                canceled = false,
                started = false
            };

            Interlocked.Increment(ref _activeMainThreadRequests);
            _mainThreadQueue.Enqueue(item);
            // Trigger a fast drain attempt; avoids waiting for keepalive polling under idle editor conditions.
            EditorApplication.delayCall += ProcessMainThreadQueue;

            bool completedInTime = false;
            try
            {
                completedInTime = item.completion.Task.Wait(timeoutMs);
            }
            catch (AggregateException ex)
            {
                var msg = ex.Flatten().InnerException?.Message ?? ex.Message;
                Dictionary<string, object> details = null;
                if (debug)
                    details = new Dictionary<string, object> { { "stackTrace", ex.StackTrace ?? "" } };
                return (BuildErrorEnvelope(msg, "MAIN_THREAD_ERROR", routeLabel, true, details), failCode);
            }
            finally
            {
                Interlocked.Decrement(ref _activeMainThreadRequests);
            }

            if (!completedInTime)
            {
                item.canceled = true;
                Interlocked.Increment(ref _timedOutRequestCount);
                bool canceledBeforeExecution = !item.started;
                if (canceledBeforeExecution)
                {
                    Interlocked.Increment(ref _canceledBeforeExecutionCount);
                }

                // Always push rich diagnostics to the event stream
                var eventPayload = new Dictionary<string, object>
                {
                    { "error", "Timeout waiting for Unity main thread" },
                    { "workItemId", item.id },
                    { "route", item.route },
                    { "timeoutMs", timeoutMs },
                    { "queuedForMs", (int)(DateTime.UtcNow - item.enqueuedAtUtc).TotalMilliseconds },
                    { "pendingQueueSize", _mainThreadQueue.Count },
                    { "started", item.started },
                    { "canceledBeforeExecution", canceledBeforeExecution },
                    { "isCompiling", _isCompiling },
                    { "isPlaying", _cachedIsPlaying },
                    { "playModeTransitioning", _cachedIsPlayModeTransitioning },
                    { "retryHint", "Increase timeoutMs for long operations or retry after compilation/playmode transitions." }
                };
                PushEvent("request_timeout", MiniJSON.Json.Serialize(eventPayload));

                // Return standardized error envelope (details only when debug=1)
                Dictionary<string, object> details = null;
                if (debug)
                {
                    details = new Dictionary<string, object>
                    {
                        { "workItemId", item.id },
                        { "timeoutMs", timeoutMs },
                        { "queuedForMs", (int)(DateTime.UtcNow - item.enqueuedAtUtc).TotalMilliseconds },
                        { "pendingQueueSize", _mainThreadQueue.Count },
                        { "started", item.started },
                        { "canceledBeforeExecution", canceledBeforeExecution },
                        { "isCompiling", _isCompiling },
                        { "isPlaying", _cachedIsPlaying },
                        { "playModeTransitioning", _cachedIsPlayModeTransitioning }
                    };
                }
                return (BuildErrorEnvelope("Timeout waiting for Unity main thread", "TIMEOUT", item.route, true, details), 504);
            }

            var completion = item.completion.Task.Result;
            if (completion.error != null)
            {
                Dictionary<string, object> details = null;
                if (debug)
                    details = new Dictionary<string, object> { { "stackTrace", completion.error.StackTrace ?? "" } };
                return (BuildErrorEnvelope(completion.error.Message, "MAIN_THREAD_ERROR", routeLabel, false, details), failCode);
            }

            if (completion.result == null)
                return (BuildErrorEnvelope("No result from main thread", "INTERNAL_ERROR", routeLabel, false), failCode);

            return (completion.result, successCode);
        }

        /// <summary>
        /// Dispatch a read-only function to the Unity main thread via the high-priority read queue.
        /// Reads are batched (up to 8 per tick) to avoid starving writes.
        /// </summary>
        private static (string result, int statusCode) RunOnMainThreadRead(
            Func<string> work, int timeoutMs = 5000, int successCode = 200, int failCode = 500, string routeLabel = null, bool debug = false)
        {
            if (routeLabel == null) routeLabel = _currentRoute ?? "(unknown-route)";
            if (!debug) debug = _currentDebug;

            var item = new MainThreadWorkItem
            {
                id = Interlocked.Increment(ref _nextWorkItemId),
                route = routeLabel,
                work = work,
                completion = new TaskCompletionSource<MainThreadWorkResult>(TaskCreationOptions.RunContinuationsAsynchronously),
                enqueuedAtUtc = DateTime.UtcNow,
                canceled = false,
                started = false
            };

            Interlocked.Increment(ref _activeMainThreadRequests);
            _readQueue.Enqueue(item);
            EditorApplication.delayCall += ProcessMainThreadQueue;

            bool completedInTime = false;
            try
            {
                completedInTime = item.completion.Task.Wait(timeoutMs);
            }
            catch (AggregateException ex)
            {
                var msg = ex.Flatten().InnerException?.Message ?? ex.Message;
                Dictionary<string, object> details = null;
                if (debug)
                    details = new Dictionary<string, object> { { "stackTrace", ex.StackTrace ?? "" } };
                return (BuildErrorEnvelope(msg, "MAIN_THREAD_ERROR", routeLabel, true, details), failCode);
            }
            finally
            {
                Interlocked.Decrement(ref _activeMainThreadRequests);
            }

            if (!completedInTime)
            {
                item.canceled = true;
                Interlocked.Increment(ref _timedOutRequestCount);
                bool canceledBeforeExecution = !item.started;
                if (canceledBeforeExecution)
                {
                    Interlocked.Increment(ref _canceledBeforeExecutionCount);
                }

                var eventPayload = new Dictionary<string, object>
                {
                    { "error", "Timeout waiting for Unity main thread" },
                    { "workItemId", item.id },
                    { "route", item.route },
                    { "timeoutMs", timeoutMs },
                    { "queuedForMs", (int)(DateTime.UtcNow - item.enqueuedAtUtc).TotalMilliseconds },
                    { "pendingReadQueueSize", _readQueue.Count },
                    { "pendingWriteQueueSize", _mainThreadQueue.Count },
                    { "started", item.started },
                    { "canceledBeforeExecution", canceledBeforeExecution },
                    { "isCompiling", _isCompiling },
                    { "isPlaying", _cachedIsPlaying },
                    { "playModeTransitioning", _cachedIsPlayModeTransitioning },
                    { "retryHint", "Increase timeoutMs for long operations or retry after compilation/playmode transitions." }
                };
                PushEvent("request_timeout", MiniJSON.Json.Serialize(eventPayload));

                Dictionary<string, object> details = null;
                if (debug)
                {
                    details = new Dictionary<string, object>
                    {
                        { "workItemId", item.id },
                        { "timeoutMs", timeoutMs },
                        { "queuedForMs", (int)(DateTime.UtcNow - item.enqueuedAtUtc).TotalMilliseconds },
                        { "pendingReadQueueSize", _readQueue.Count },
                        { "pendingWriteQueueSize", _mainThreadQueue.Count },
                        { "started", item.started },
                        { "canceledBeforeExecution", canceledBeforeExecution },
                        { "isCompiling", _isCompiling },
                        { "isPlaying", _cachedIsPlaying },
                        { "playModeTransitioning", _cachedIsPlayModeTransitioning }
                    };
                }
                return (BuildErrorEnvelope("Timeout waiting for Unity main thread", "TIMEOUT", item.route, true, details), 504);
            }

            var completion = item.completion.Task.Result;
            if (completion.error != null)
            {
                Dictionary<string, object> details = null;
                if (debug)
                    details = new Dictionary<string, object> { { "stackTrace", completion.error.StackTrace ?? "" } };
                return (BuildErrorEnvelope(completion.error.Message, "MAIN_THREAD_ERROR", routeLabel, false, details), failCode);
            }

            if (completion.result == null)
                return (BuildErrorEnvelope("No result from main thread", "INTERNAL_ERROR", routeLabel, false), failCode);

            return (completion.result, successCode);
        }

        // ─── Public wrappers for BridgeRouter ───────────────────────

        /// <summary>Exposes RunOnMainThread to BridgeRouter (same signature).</summary>
        internal static (string result, int statusCode) RunOnMainThreadPublic(
            Func<string> work, int timeoutMs = 5000, int successCode = 200, int failCode = 500, string routeLabel = null, bool debug = false)
            => RunOnMainThread(work, timeoutMs, successCode, failCode, routeLabel, debug);

        /// <summary>Exposes RunOnMainThreadRead to BridgeRouter (same signature).</summary>
        internal static (string result, int statusCode) RunOnMainThreadReadPublic(
            Func<string> work, int timeoutMs = 5000, int successCode = 200, int failCode = 500, string routeLabel = null, bool debug = false)
            => RunOnMainThreadRead(work, timeoutMs, successCode, failCode, routeLabel, debug);

        /// <summary>Exposes ResolveTimeoutMs to BridgeRouter.</summary>
        internal static int ResolveTimeoutMsPublic(System.Collections.Specialized.NameValueCollection query, int defaultTimeoutMs, int minTimeoutMs = 250, int maxTimeoutMs = 180000)
            => ResolveTimeoutMs(query, defaultTimeoutMs, minTimeoutMs, maxTimeoutMs);

        /// <summary>Builds a standard error envelope for router-level errors.</summary>
        internal static string BuildRouterErrorEnvelope(string error, string code, string route, bool retriable)
            => BuildErrorEnvelope(error, code, route, retriable);

        // ==================== REQUEST HANDLING ====================

        private static void HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            Interlocked.Increment(ref _activeHttpRequestCount);

            try
            {
                // Add CORS headers — restrict to localhost origins only
                var origin = request.Headers["Origin"];
                if (origin != null && (origin.StartsWith("http://localhost") || origin.StartsWith("http://127.0.0.1")
                    || origin.StartsWith("https://localhost") || origin.StartsWith("https://127.0.0.1")))
                {
                    response.Headers.Add("Access-Control-Allow-Origin", origin);
                }
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    return;
                }

                var path = request.Url.AbsolutePath.ToLowerInvariant();
                var method = request.HttpMethod;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string authStatus = "skip";
                string allowedStatus = "skip";

                string responseBody;
                int statusCode = 200;
                string route = $"{method} {path}";

                // --- Backpressure gate ---
                if (Volatile.Read(ref _activeHttpRequestCount) > MaxConcurrentRequests)
                {
                    statusCode = 429;
                    responseBody = BuildErrorEnvelope(
                        $"Too many concurrent requests (limit={MaxConcurrentRequests})",
                        "TOO_MANY_REQUESTS",
                        route,
                        true,
                        new Dictionary<string, object>
                        {
                            { "activeRequestCount", Volatile.Read(ref _activeHttpRequestCount) },
                            { "maxConcurrentRequests", MaxConcurrentRequests }
                        });
                    response.StatusCode = statusCode;
                    response.ContentType = "application/json";
                    var busyBuffer = Encoding.UTF8.GetBytes(responseBody);
                    response.ContentLength64 = busyBuffer.Length;
                    response.OutputStream.Write(busyBuffer, 0, busyBuffer.Length);
                    sw.Stop();
                    AuditLog(method, path, statusCode, sw.ElapsedMilliseconds, authStatus, allowedStatus);
                    return;
                }

                // --- Auth gate ---
                // Localhost-only by default: no token required.
                // Set BRIDGE_AUTH_TOKEN to enforce Bearer auth.
                string authToken = System.Environment.GetEnvironmentVariable("BRIDGE_AUTH_TOKEN");

                if (!string.IsNullOrEmpty(authToken))
                {
                    string authHeader = request.Headers["Authorization"];
                    if (authHeader == null || !authHeader.StartsWith("Bearer ") ||
                        authHeader.Substring(7).Trim() != authToken)
                    {
                        authStatus = "fail";
                        statusCode = 401;
                        responseBody = BuildErrorEnvelope("Missing or invalid Bearer token",
                            "UNAUTHORIZED", $"{method} {path}", false);
                        response.StatusCode = statusCode;
                        response.ContentType = "application/json";
                        var authBuffer = Encoding.UTF8.GetBytes(responseBody);
                        response.ContentLength64 = authBuffer.Length;
                        response.OutputStream.Write(authBuffer, 0, authBuffer.Length);
                        sw.Stop();
                        AuditLog(method, path, statusCode, sw.ElapsedMilliseconds, authStatus, allowedStatus);
                        return;
                    }
                    authStatus = "ok";
                }

                // --- Allowlist gate ---
                if (_allowedRoutes != null)
                {
                    string routeKey = $"{method} {path}";
                    if (!IsRouteAllowed(method, path) && path != "/health")
                    {
                        allowedStatus = "denied";
                        statusCode = 403;
                        responseBody = BuildErrorEnvelope($"Route not in allowlist: {routeKey}",
                            "FORBIDDEN", routeKey, false);
                        response.StatusCode = statusCode;
                        response.ContentType = "application/json";
                        var alBuffer = Encoding.UTF8.GetBytes(responseBody);
                        response.ContentLength64 = alBuffer.Length;
                        response.OutputStream.Write(alBuffer, 0, alBuffer.Length);
                        sw.Stop();
                        AuditLog(method, path, statusCode, sw.ElapsedMilliseconds, authStatus, allowedStatus);
                        return;
                    }
                    allowedStatus = "ok";
                }

                // Read request body if present
                string requestBody = null;
                if (request.HasEntityBody)
                {
                    if (request.ContentLength64 > MaxRequestBodyBytes)
                    {
                        statusCode = 413;
                        responseBody = BuildErrorEnvelope(
                            $"Request body too large (max {MaxRequestBodyBytes} bytes)",
                            "PAYLOAD_TOO_LARGE",
                            route,
                            false);
                        response.StatusCode = statusCode;
                        response.ContentType = "application/json";
                        var bodyLimitBuffer = Encoding.UTF8.GetBytes(responseBody);
                        response.ContentLength64 = bodyLimitBuffer.Length;
                        response.OutputStream.Write(bodyLimitBuffer, 0, bodyLimitBuffer.Length);
                        sw.Stop();
                        AuditLog(method, path, statusCode, sw.ElapsedMilliseconds, authStatus, allowedStatus);
                        return;
                    }

                    if (!TryReadRequestBodyLimited(request, MaxRequestBodyBytes, out requestBody))
                    {
                        statusCode = 413;
                        responseBody = BuildErrorEnvelope(
                            $"Request body too large (max {MaxRequestBodyBytes} bytes)",
                            "PAYLOAD_TOO_LARGE",
                            route,
                            false);
                        response.StatusCode = statusCode;
                        response.ContentType = "application/json";
                        var bodyLimitBuffer = Encoding.UTF8.GetBytes(responseBody);
                        response.ContentLength64 = bodyLimitBuffer.Length;
                        response.OutputStream.Write(bodyLimitBuffer, 0, bodyLimitBuffer.Length);
                        sw.Stop();
                        AuditLog(method, path, statusCode, sw.ElapsedMilliseconds, authStatus, allowedStatus);
                        return;
                    }
                }

                // Parse debug flag from query string — only honor when auth is not enabled
                bool debug = request.QueryString["debug"] == "1" && string.IsNullOrEmpty(authToken);

                // Route the request
                try
                {
                    (responseBody, statusCode) = RouteRequest(path, method, requestBody, request.QueryString, debug);
                }
                catch (Exception ex)
                {
                    var routeKey = $"{method} {path}";
                    Dictionary<string, object> details = null;
                    if (debug)
                        details = new Dictionary<string, object> { { "stackTrace", ex.StackTrace ?? "" } };
                    responseBody = BuildErrorEnvelope(ex.Message, "INTERNAL_ERROR", routeKey, false, details);
                    statusCode = 500;
                }

                sw.Stop();
                AuditLog(method, path, statusCode, sw.ElapsedMilliseconds, authStatus, allowedStatus);

                // Send response
                response.StatusCode = statusCode;
                bool screenshotRaw = path == "/screenshot"
                    && method == "GET"
                    && statusCode == 200
                    && (
                        ParseBool(request.QueryString["raw"], false)
                        || ParseBool(request.QueryString["download"], false)
                        || (
                            !string.IsNullOrWhiteSpace(request.QueryString["viewType"])
                            && string.IsNullOrWhiteSpace(request.QueryString["view"])
                            && request.QueryString["includeBase64"] == null
                            && request.QueryString["includeHandle"] == null
                            && request.QueryString["mode"] == null
                            && request.QueryString["raw"] == null
                            && request.QueryString["download"] == null
                        )
                    );

                if (screenshotRaw)
                {
                    var parsed = MiniJSON.Json.Deserialize(responseBody) as Dictionary<string, object>;
                    var base64 = parsed != null && parsed.TryGetValue("base64", out var base64Obj) && base64Obj != null
                        ? base64Obj.ToString()
                        : null;
                    var mimeType = parsed != null && parsed.TryGetValue("mimeType", out var mimeObj) && mimeObj != null
                        ? mimeObj.ToString()
                        : "image/jpeg";

                    if (!string.IsNullOrWhiteSpace(base64))
                    {
                        var imageBytes = Convert.FromBase64String(base64);
                        var resolvedMime = string.IsNullOrWhiteSpace(mimeType) ? "image/jpeg" : mimeType;
                        var ext = resolvedMime.Contains("png") ? "png" : "jpg";
                        response.ContentType = resolvedMime;
                        response.Headers.Add("Content-Disposition", $"attachment; filename=\"screenshot.{ext}\"");
                        response.ContentLength64 = imageBytes.Length;
                        response.OutputStream.Write(imageBytes, 0, imageBytes.Length);
                    }
                    else
                    {
                        // Raw binary was requested but no base64 available — return error, not silent JSON
                        statusCode = 500;
                        response.StatusCode = statusCode;
                        var errorBody = BuildErrorEnvelope(
                            "Raw binary screenshot requested but no base64 image data in response. Use mode=base64 or mode=both, or omit mode for default behavior.",
                            "INTERNAL_ERROR", "GET /screenshot", true, null);
                        response.ContentType = "application/json";
                        var fallbackBuffer = Encoding.UTF8.GetBytes(errorBody);
                        response.ContentLength64 = fallbackBuffer.Length;
                        response.OutputStream.Write(fallbackBuffer, 0, fallbackBuffer.Length);
                    }
                }
                else
                {
                    response.ContentType = "application/json";
                    var buffer = Encoding.UTF8.GetBytes(responseBody);
                    response.ContentLength64 = buffer.Length;
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AgentBridge] Response error: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeHttpRequestCount);
                response.Close();
            }
        }

        // ==================== ROUTE DISPATCH ====================

        private static (string body, int statusCode) RouteRequest(string path, string method, string requestBody, System.Collections.Specialized.NameValueCollection query, bool debug = false)
        {
            string route = $"{method} {path}";
            _currentRoute = route;
            _currentDebug = debug;

            // Attribute-based dispatch
            var resolved = BridgeRouter.Resolve(method, path);
            if (resolved != null)
                return resolved.Invoke(path, method, requestBody, query, debug);

            // 404 with suggestions
            var suggestions = GetRouteSuggestions(path, method, 3);
            var details404 = new Dictionary<string, object>();
            if (suggestions.Count > 0)
                details404["suggestions"] = suggestions;
            details404["hint"] = "Use GET /routes to list all available routes";
            return (BuildErrorEnvelope($"Route not found: {route}", "NOT_FOUND", route, false, details404), 404);
        }

        // ==================== HELPER METHODS ====================

        private static bool TryParseIdFromPath(string path, string prefix, out int id)
        {
            var idStr = path.Substring(prefix.Length);
            return int.TryParse(idStr, out id);
        }

        private static int ParseInt(string value, int defaultValue)
        {
            if (string.IsNullOrEmpty(value)) return defaultValue;
            return int.TryParse(value, out int result) ? result : defaultValue;
        }

        private static int ParseIntEnv(string envName, int defaultValue, int minValue, int maxValue)
        {
            var raw = Environment.GetEnvironmentVariable(envName);
            if (!int.TryParse(raw, out var parsed))
            {
                parsed = defaultValue;
            }

            return Math.Clamp(parsed, minValue, maxValue);
        }

        private static bool ParseBoolEnv(string envName, bool defaultValue)
        {
            var raw = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
            raw = raw.Trim();
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static float ParseFloat(string value, float defaultValue)
        {
            if (string.IsNullOrEmpty(value)) return defaultValue;
            if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return defaultValue;
        }

        private static int ResolveTimeoutMs(System.Collections.Specialized.NameValueCollection query, int defaultTimeoutMs, int minTimeoutMs = 250, int maxTimeoutMs = 180000)
        {
            int resolved = defaultTimeoutMs;
            if (query != null)
            {
                resolved = ParseInt(query["timeoutMs"], defaultTimeoutMs);
            }
            return Math.Clamp(resolved, minTimeoutMs, maxTimeoutMs);
        }

        private static bool ParseBool(string value, bool defaultValue)
        {
            if (string.IsNullOrEmpty(value)) return defaultValue;
            return value.ToLowerInvariant() == "true" || value == "1";
        }

        private static string EscapeJsonString(string value)
        {
            if (value == null) return string.Empty;
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
        }

        private static string[] ParseCsvList(System.Collections.Specialized.NameValueCollection query, string key)
        {
            var values = query.GetValues(key);
            if (values == null || values.Length == 0)
            {
                return Array.Empty<string>();
            }

            return values
                .SelectMany(v => (v ?? string.Empty).Split(','))
                .Select(v => v.Trim())
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool TryReadRequestBodyLimited(HttpListenerRequest request, int maxBytes, out string requestBody)
        {
            requestBody = null;
            var encoding = request.ContentEncoding ?? Encoding.UTF8;
            var buffer = new byte[8192];

            using var ms = new MemoryStream();
            int read;
            while ((read = request.InputStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (ms.Length + read > maxBytes)
                {
                    return false;
                }

                ms.Write(buffer, 0, read);
            }

            requestBody = encoding.GetString(ms.ToArray());
            return true;
        }

        /// <summary>
        /// Parse a bool query param into an int (-1 = not set, 0 = false, 1 = true).
        /// Used for the sentinel pattern in UnityCommands.
        /// </summary>
        private static int ParseBoolInt(string value)
        {
            if (string.IsNullOrEmpty(value)) return -1;
            return value.ToLowerInvariant() == "true" ? 1 : 0;
        }

        // ==================== ERROR ENVELOPE ====================

        /// <summary>
        /// Build a standardized error envelope for all bridge error responses.
        /// See docs/agent-shared/error-envelope.md for the contract.
        /// </summary>
        private static string BuildErrorEnvelope(
            string error, string code, string route, bool retriable,
            Dictionary<string, object> details = null)
        {
            var envelope = new Dictionary<string, object>
            {
                { "success", false },
                { "error", error },
                { "code", code },
                { "route", route },
                { "retriable", retriable }
            };
            if (details != null && details.Count > 0)
                envelope["details"] = details;
            return MiniJSON.Json.Serialize(envelope);
        }

        private static (string body, int statusCode) BadRequest(string message, string route = "")
        {
            return (BuildErrorEnvelope(message, "BAD_REQUEST", route, false), 400);
        }

        // ==================== SECURITY HELPERS ====================

        private static HashSet<string> ParseAllowlist(Dictionary<string, object> parsed)
        {
            var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (parsed == null) return routes;

            var activeProfileNames = new List<string>();
            if (parsed.TryGetValue("active_profiles", out var activeObj) && activeObj is List<object> activeList)
            {
                foreach (var item in activeList)
                    if (item != null) activeProfileNames.Add(item.ToString());
            }

            if (parsed.TryGetValue("profiles", out var profilesObj) && profilesObj is Dictionary<string, object> profiles)
            {
                foreach (var profileName in activeProfileNames)
                {
                    if (profiles.TryGetValue(profileName, out var profileVal) && profileVal is Dictionary<string, object> profile)
                    {
                        if (profile.TryGetValue("routes", out var routesObj) && routesObj is List<object> routeList)
                        {
                            foreach (var r in routeList)
                                if (r != null) routes.Add(r.ToString());
                        }
                    }
                }
            }

            routes.Add("GET /health");
            return routes;
        }

        private static void AuditLog(string method, string path, int status, long ms,
            string auth, string allowed)
        {
            if (_auditLog == null) return;
            lock (_auditLogLock)
            {
                try
                {
                    string ts = DateTime.UtcNow.ToString("o");
                    _auditLog.WriteLine(
                        $"{{\"ts\":\"{ts}\",\"method\":\"{EscapeJsonString(method)}\",\"path\":\"{EscapeJsonString(path)}\"," +
                        $"\"status\":{status},\"ms\":{ms},\"auth\":\"{auth}\",\"allowed\":\"{allowed}\"}}");
                }
                catch
                {
                    // Never let audit logging crash the bridge
                }
            }
        }

        /// <summary>
        /// Check if a route is in the allowlist. Supports prefix matching so
        /// "PUT /gameobject" in the allowlist allows "PUT /gameobject/123".
        /// </summary>
        private static bool IsRouteAllowed(string method, string path)
        {
            if (_allowedRoutes == null) return true;
            string routeKey = $"{method} {path}";
            if (_allowedRoutes.Contains(routeKey)) return true;
            // Prefix match: "PUT /gameobject" allows "PUT /gameobject/123"
            foreach (var allowed in _allowedRoutes)
            {
                if (routeKey.Length > allowed.Length &&
                    routeKey.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) &&
                    routeKey[allowed.Length] == '/')
                    return true;
            }
            return false;
        }

        // Route suggestions for 404 responses — uses auto-generated catalog from [BridgeRoute] attributes.

        private static List<string> GetRouteSuggestions(string requestedPath, string requestedMethod, int maxSuggestions)
        {
            var catalog = BridgeRouter.GetCatalog();
            var scored = new List<(string route, int score)>();

            foreach (var entry in catalog)
            {
                int score = 0;

                string entryPathBase = entry.path.Contains("{") ? entry.path.Substring(0, entry.path.IndexOf('{')) : entry.path;
                if (requestedPath == entry.path || requestedPath.StartsWith(entryPathBase.TrimEnd('/')))
                {
                    score += 100;
                    if (entry.method == requestedMethod) score += 50;
                }

                if (entry.path.Contains(requestedPath) || requestedPath.Contains(entry.path))
                    score += 40;

                var reqSegments = requestedPath.Trim('/').Split('/');
                var entrySegments = entry.path.Trim('/').Split('/');
                int commonSegments = 0;
                for (int i = 0; i < Math.Min(reqSegments.Length, entrySegments.Length); i++)
                {
                    if (reqSegments[i] == entrySegments[i]) commonSegments++;
                    else if (entrySegments[i].StartsWith("{")) commonSegments++;
                }
                score += commonSegments * 20;

                string lastReqSegment = reqSegments[reqSegments.Length - 1];
                string lastEntrySegment = entrySegments[entrySegments.Length - 1];
                if (lastReqSegment == lastEntrySegment) score += 30;

                if (entry.method == requestedMethod) score += 10;

                if (score > 0)
                    scored.Add(($"{entry.method} {entry.path}", score));
            }

            scored.Sort((a, b) => b.score.CompareTo(a.score));

            var result = new List<string>();
            for (int i = 0; i < Math.Min(maxSuggestions, scored.Count); i++)
                result.Add(scored[i].route);
            return result;
        }

        // ==================== DATA CLASSES ====================

        [Serializable]
        private class HealthResponse
        {
            public string status;
            public string unityVersion;
            public string projectName;
            public bool isPlaying;
            public bool isCompiling;
            public int pendingQueueSize;
            public int readQueueSize;
            public int writeQueueSize;
            public int activeMainThreadRequests;
            public int timedOutRequestCount;
            public int canceledBeforeExecutionCount;
            public int completedAfterTimeoutCount;
            public int domainReloadCount;
            public float lastTickAge;
            public float serverUptimeSeconds;
        }

        [Serializable]
        public class CompilationErrorInfo
        {
            public string message;
            public string file;
            public int line;
            public int column;
        }
    }
}
