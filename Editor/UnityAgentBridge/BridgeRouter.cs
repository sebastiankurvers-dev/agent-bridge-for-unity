using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;

namespace UnityAgentBridge
{
    /// <summary>
    /// Attribute-based route dispatcher. Discovers [BridgeRoute] methods via TypeCache,
    /// builds a two-tier lookup (exact dictionary + parameterized list), and provides
    /// catalog generation from attribute metadata.
    /// </summary>
    public static class BridgeRouter
    {
        // ─── Types ───────────────────────────────────────────────────

        /// <summary>
        /// Delegate that a resolved route's Invoke calls. Returns (responseBody, statusCode).
        /// The router wraps the actual handler method in the correct dispatch
        /// (RunOnMainThread / RunOnMainThreadRead / direct) based on attribute flags.
        /// </summary>
        public delegate (string body, int statusCode) RouteInvoker(
            string path, string method, string requestBody,
            NameValueCollection query, bool debug);

        public sealed class RouteRegistration
        {
            public BridgeRouteAttribute Attribute;
            public RouteInvoker Invoke;
        }

        private sealed class ParameterizedRoute
        {
            public string Method;           // "GET"
            public string Pattern;          // "/gameobject/{id}"
            public string[] Segments;       // ["", "gameobject", "{id}"]
            public int[] ParamIndices;      // [2] — segment indices that are {params}
            public string[] ParamNames;     // ["id"]
            public RouteRegistration Registration;
        }

        // ─── State ───────────────────────────────────────────────────

        private static Dictionary<string, RouteRegistration> _exactRoutes;
        private static List<ParameterizedRoute> _paramRoutes;
        private static List<CatalogEntry> _catalog;
        private static bool _initialized;

        public struct CatalogEntry
        {
            public string method;
            public string path;
            public string category;
            public string description;
        }

        // ─── Public API ──────────────────────────────────────────────

        /// <summary>
        /// Discovers all [BridgeRoute] methods and builds dispatch tables.
        /// Called once from UnityAgentBridgeServer static constructor.
        /// </summary>
        public static void Initialize()
        {
            _exactRoutes = new Dictionary<string, RouteRegistration>(StringComparer.OrdinalIgnoreCase);
            _paramRoutes = new List<ParameterizedRoute>();
            _catalog = new List<CatalogEntry>();

            var methods = TypeCache.GetMethodsWithAttribute<BridgeRouteAttribute>();

            foreach (var mi in methods)
            {
                var attrs = mi.GetCustomAttributes<BridgeRouteAttribute>();
                foreach (var attr in attrs)
                {
                    var reg = BuildRegistration(mi, attr);
                    if (reg == null) continue;

                    _catalog.Add(new CatalogEntry
                    {
                        method = attr.Method,
                        path = attr.Path,
                        category = attr.Category,
                        description = attr.Description
                    });

                    if (attr.Path.Contains("{"))
                    {
                        var segments = attr.Path.Split('/');
                        var paramIndices = new List<int>();
                        var paramNames = new List<string>();
                        for (int i = 0; i < segments.Length; i++)
                        {
                            if (segments[i].StartsWith("{") && segments[i].EndsWith("}"))
                            {
                                paramIndices.Add(i);
                                paramNames.Add(segments[i].Substring(1, segments[i].Length - 2));
                            }
                        }

                        _paramRoutes.Add(new ParameterizedRoute
                        {
                            Method = attr.Method,
                            Pattern = attr.Path,
                            Segments = segments,
                            ParamIndices = paramIndices.ToArray(),
                            ParamNames = paramNames.ToArray(),
                            Registration = reg
                        });
                    }
                    else
                    {
                        string key = $"{attr.Method} {attr.Path}";
                        _exactRoutes[key] = reg;
                    }
                }
            }

            // Sort parameterized routes: more segments first (most specific)
            _paramRoutes.Sort((a, b) => b.Segments.Length.CompareTo(a.Segments.Length));

            _initialized = true;
        }

        /// <summary>
        /// Resolves a route. Returns null if no attribute-based handler is registered (fallthrough).
        /// </summary>
        public static RouteRegistration Resolve(string method, string path)
        {
            if (!_initialized) return null;

            // Tier 1: exact dictionary lookup
            string key = $"{method} {path}";
            if (_exactRoutes.TryGetValue(key, out var exact))
                return exact;

            // Tier 2: parameterized pattern scan
            var pathSegments = path.Split('/');
            foreach (var pr in _paramRoutes)
            {
                if (!string.Equals(pr.Method, method, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (pathSegments.Length != pr.Segments.Length)
                    continue;

                bool match = true;
                for (int i = 0; i < pr.Segments.Length; i++)
                {
                    if (pr.Segments[i].StartsWith("{")) continue; // param slot — matches anything
                    if (!string.Equals(pr.Segments[i], pathSegments[i], StringComparison.OrdinalIgnoreCase))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    return pr.Registration;
            }

            return null;
        }

        /// <summary>
        /// Returns the catalog entries from all registered [BridgeRoute] methods.
        /// </summary>
        public static List<CatalogEntry> GetCatalog()
        {
            return _catalog ?? new List<CatalogEntry>();
        }

        /// <summary>
        /// Number of registered routes (exact + parameterized).
        /// </summary>
        public static int RouteCount => (_exactRoutes?.Count ?? 0) + (_paramRoutes?.Count ?? 0);

        // ─── Internals ──────────────────────────────────────────────

        /// <summary>
        /// Extracts path parameters from a concrete path using a parameterized route pattern.
        /// E.g., path="/gameobject/12345" pattern="/gameobject/{id}" → {"id": "12345"}
        /// </summary>
        private static Dictionary<string, string> ExtractParams(string path, ParameterizedRoute pr)
        {
            var pathSegments = path.Split('/');
            var result = new Dictionary<string, string>();
            for (int i = 0; i < pr.ParamIndices.Length; i++)
            {
                result[pr.ParamNames[i]] = pathSegments[pr.ParamIndices[i]];
            }
            return result;
        }

        /// <summary>
        /// Builds a RouteRegistration for a method + attribute. Inspects the method's
        /// parameter signature to determine how to bind arguments.
        /// </summary>
        private static RouteRegistration BuildRegistration(MethodInfo mi, BridgeRouteAttribute attr)
        {
            var parameters = mi.GetParameters();
            var returnType = mi.ReturnType;

            // Determine the invocation delegate based on signature
            bool returnsTuple = returnType == typeof(ValueTuple<string, int>)
                || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTuple<,>));

            // We need a wrapper that always returns (string, int)
            Func<string, string, string, NameValueCollection, (string body, int statusCode)> typedInvoke = null;

            if (parameters.Length == 0)
            {
                // () → string  or  () → (string, int)
                if (returnsTuple)
                {
                    var del = (Func<(string, int)>)Delegate.CreateDelegate(typeof(Func<(string, int)>), mi);
                    typedInvoke = (path, method, body, query) => del();
                }
                else
                {
                    var del = (Func<string>)Delegate.CreateDelegate(typeof(Func<string>), mi);
                    typedInvoke = (path, method, body, query) => (del(), 200);
                }
            }
            else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
            {
                // (string jsonData) → string  or  → (string, int)
                if (returnsTuple)
                {
                    var del = (Func<string, (string, int)>)Delegate.CreateDelegate(typeof(Func<string, (string, int)>), mi);
                    typedInvoke = (path, method, body, query) => del(body);
                }
                else
                {
                    var del = (Func<string, string>)Delegate.CreateDelegate(typeof(Func<string, string>), mi);
                    typedInvoke = (path, method, body, query) => (del(body), 200);
                }
            }
            else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(int))
            {
                // (int instanceId) → string  — extract {id} from path
                if (returnsTuple)
                {
                    var del = (Func<int, (string, int)>)Delegate.CreateDelegate(typeof(Func<int, (string, int)>), mi);
                    typedInvoke = (path, method, body, query) =>
                    {
                        int id = ExtractFirstIntParam(path, attr.Path);
                        return del(id);
                    };
                }
                else
                {
                    var del = (Func<int, string>)Delegate.CreateDelegate(typeof(Func<int, string>), mi);
                    typedInvoke = (path, method, body, query) =>
                    {
                        int id = ExtractFirstIntParam(path, attr.Path);
                        return (del(id), 200);
                    };
                }
            }
            else if (parameters.Length == 2
                && parameters[0].ParameterType == typeof(int)
                && parameters[1].ParameterType == typeof(string)
                && attr.Path.Contains("{") && attr.Path.Split('{').Length > 2)
            {
                // (int id, string type) → string — for routes with 2+ path params like /component/{id}/{type}
                if (returnsTuple)
                {
                    var del = (Func<int, string, (string, int)>)Delegate.CreateDelegate(typeof(Func<int, string, (string, int)>), mi);
                    typedInvoke = (path, method, body, query) =>
                    {
                        var (id, typeStr) = ExtractIntAndStringParam(path, attr.Path);
                        return del(id, typeStr);
                    };
                }
                else
                {
                    var del = (Func<int, string, string>)Delegate.CreateDelegate(typeof(Func<int, string, string>), mi);
                    typedInvoke = (path, method, body, query) =>
                    {
                        var (id, typeStr) = ExtractIntAndStringParam(path, attr.Path);
                        return (del(id, typeStr), 200);
                    };
                }
            }
            else if (parameters.Length == 2
                && parameters[0].ParameterType == typeof(int)
                && parameters[1].ParameterType == typeof(string))
            {
                // (int instanceId, string jsonData) → string — single {id} param, string is request body
                if (returnsTuple)
                {
                    var del = (Func<int, string, (string, int)>)Delegate.CreateDelegate(typeof(Func<int, string, (string, int)>), mi);
                    typedInvoke = (path, method, body, query) =>
                    {
                        int id = ExtractFirstIntParam(path, attr.Path);
                        return del(id, body);
                    };
                }
                else
                {
                    var del = (Func<int, string, string>)Delegate.CreateDelegate(typeof(Func<int, string, string>), mi);
                    typedInvoke = (path, method, body, query) =>
                    {
                        int id = ExtractFirstIntParam(path, attr.Path);
                        return (del(id, body), 200);
                    };
                }
            }
            else if (parameters.Length == 4
                && parameters[0].ParameterType == typeof(string)
                && parameters[1].ParameterType == typeof(string)
                && parameters[2].ParameterType == typeof(string)
                && parameters[3].ParameterType == typeof(NameValueCollection))
            {
                // (string path, string method, string body, NameValueCollection query) → (string, int)
                if (returnsTuple)
                {
                    var del = (Func<string, string, string, NameValueCollection, (string, int)>)Delegate.CreateDelegate(
                        typeof(Func<string, string, string, NameValueCollection, (string, int)>), mi);
                    typedInvoke = (path, method, body, query) => del(path, method, body, query);
                }
                else
                {
                    var del = (Func<string, string, string, NameValueCollection, string>)Delegate.CreateDelegate(
                        typeof(Func<string, string, string, NameValueCollection, string>), mi);
                    typedInvoke = (path, method, body, query) => (del(path, method, body, query), 200);
                }
            }
            else if (parameters.Length == 2
                && parameters[0].ParameterType == typeof(int)
                && parameters[1].ParameterType == typeof(int))
            {
                // (int id1, int id2) → string — for /component/{id}/{type} where both are ints
                if (returnsTuple)
                {
                    var del = (Func<int, int, (string, int)>)Delegate.CreateDelegate(typeof(Func<int, int, (string, int)>), mi);
                    typedInvoke = (path, method, body, query) =>
                    {
                        var ids = ExtractIntParams(path, attr.Path);
                        return del(ids[0], ids[1]);
                    };
                }
                else
                {
                    var del = (Func<int, int, string>)Delegate.CreateDelegate(typeof(Func<int, int, string>), mi);
                    typedInvoke = (path, method, body, query) =>
                    {
                        var ids = ExtractIntParams(path, attr.Path);
                        return (del(ids[0], ids[1]), 200);
                    };
                }
            }
            else
            {
                UnityEngine.Debug.LogWarning(
                    $"[BridgeRouter] Unsupported handler signature for [{attr.Method} {attr.Path}] " +
                    $"on {mi.DeclaringType?.Name}.{mi.Name} — skipping");
                return null;
            }

            // Wrap in dispatch (main thread / read / direct) with timeout
            var reg = new RouteRegistration { Attribute = attr };
            var captured = typedInvoke; // capture for closure

            int defaultTimeout = attr.TimeoutDefault > 0 ? attr.TimeoutDefault : 5000;
            int minTimeout = attr.TimeoutMin > 0 ? attr.TimeoutMin : 250;
            int maxTimeout = attr.TimeoutMax > 0 ? attr.TimeoutMax : 180000;
            int failCode = attr.FailCode;
            string routeLabel = $"{attr.Method} {attr.Path}";

            if (attr.Direct)
            {
                // Direct: call on listener thread, no queue
                reg.Invoke = (path, method, body, query, debug) =>
                {
                    try
                    {
                        return captured(path, method, body, query);
                    }
                    catch (Exception ex)
                    {
                        return (UnityAgentBridgeServer.BuildRouterErrorEnvelope(
                            ex.Message, "INTERNAL_ERROR", routeLabel, false), failCode);
                    }
                };
            }
            else if (attr.ReadOnly)
            {
                // Read queue
                reg.Invoke = (path, method, body, query, debug) =>
                {
                    int timeout = UnityAgentBridgeServer.ResolveTimeoutMsPublic(query, defaultTimeout, minTimeout, maxTimeout);
                    return UnityAgentBridgeServer.RunOnMainThreadReadPublic(
                        () =>
                        {
                            var result = captured(path, method, body, query);
                            return result.body;
                        },
                        timeoutMs: timeout,
                        successCode: 200,
                        failCode: failCode,
                        routeLabel: routeLabel,
                        debug: debug);
                };
            }
            else
            {
                // Mutation queue (default)
                reg.Invoke = (path, method, body, query, debug) =>
                {
                    int timeout = UnityAgentBridgeServer.ResolveTimeoutMsPublic(query, defaultTimeout, minTimeout, maxTimeout);
                    return UnityAgentBridgeServer.RunOnMainThreadPublic(
                        () =>
                        {
                            var result = captured(path, method, body, query);
                            return result.body;
                        },
                        timeoutMs: timeout,
                        successCode: 200,
                        failCode: failCode,
                        routeLabel: routeLabel,
                        debug: debug);
                };
            }

            return reg;
        }

        /// <summary>
        /// Extracts the first {id} int param from a path using the pattern.
        /// E.g., path="/gameobject/12345", pattern="/gameobject/{id}" → 12345
        /// </summary>
        private static int ExtractFirstIntParam(string path, string pattern)
        {
            var pathSegs = path.Split('/');
            var patternSegs = pattern.Split('/');
            for (int i = 0; i < patternSegs.Length && i < pathSegs.Length; i++)
            {
                if (patternSegs[i].StartsWith("{") && patternSegs[i].EndsWith("}"))
                {
                    if (int.TryParse(pathSegs[i], out int val))
                        return val;
                }
            }
            throw new ArgumentException($"No int param found in path '{path}' for pattern '{pattern}'");
        }

        /// <summary>
        /// Extracts all int params from path segments matching {param} slots.
        /// </summary>
        private static int[] ExtractIntParams(string path, string pattern)
        {
            var pathSegs = path.Split('/');
            var patternSegs = pattern.Split('/');
            var result = new List<int>();
            for (int i = 0; i < patternSegs.Length && i < pathSegs.Length; i++)
            {
                if (patternSegs[i].StartsWith("{") && patternSegs[i].EndsWith("}"))
                {
                    if (int.TryParse(pathSegs[i], out int val))
                        result.Add(val);
                }
            }
            return result.ToArray();
        }

        /// <summary>
        /// Extracts an (int, string) pair from path: first {param} as int, second as string.
        /// E.g., path="/component/12345/MeshRenderer", pattern="/component/{id}/{type}" → (12345, "MeshRenderer")
        /// </summary>
        private static (int, string) ExtractIntAndStringParam(string path, string pattern)
        {
            var pathSegs = path.Split('/');
            var patternSegs = pattern.Split('/');
            int intVal = 0;
            string strVal = "";
            bool foundInt = false;
            for (int i = 0; i < patternSegs.Length && i < pathSegs.Length; i++)
            {
                if (patternSegs[i].StartsWith("{") && patternSegs[i].EndsWith("}"))
                {
                    if (!foundInt)
                    {
                        int.TryParse(pathSegs[i], out intVal);
                        foundInt = true;
                    }
                    else
                    {
                        strVal = Uri.UnescapeDataString(pathSegs[i]);
                    }
                }
            }
            return (intVal, strVal);
        }
    }
}
