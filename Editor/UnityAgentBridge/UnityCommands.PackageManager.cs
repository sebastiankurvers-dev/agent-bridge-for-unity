using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        private static T WaitForPackageRequest<T>(T request, int timeoutMs = 30000) where T : Request
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!request.IsCompleted && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(50);
            }

            if (!request.IsCompleted)
            {
                throw new TimeoutException($"Package Manager request timed out after {timeoutMs}ms");
            }

            if (request.Status == StatusCode.Failure)
            {
                throw new Exception($"Package Manager error: {request.Error?.message ?? "Unknown error"} (code: {request.Error?.errorCode})");
            }

            return request;
        }

        [BridgeRoute("GET", "/packages", Category = "packages", Description = "List installed packages",
            ReadOnly = true, TimeoutDefault = 30000, TimeoutMin = 1000, TimeoutMax = 60000)]
        public static string ListPackages()
        {
            try
            {
                var request = WaitForPackageRequest(Client.List(true));

                var packages = new List<Dictionary<string, object>>();
                foreach (var pkg in request.Result)
                {
                    var entry = new Dictionary<string, object>
                    {
                        { "name", pkg.name },
                        { "version", pkg.version },
                        { "displayName", pkg.displayName ?? pkg.name },
                        { "source", pkg.source.ToString() },
                    };
                    if (!string.IsNullOrEmpty(pkg.description))
                        entry["description"] = pkg.description.Length > 200
                            ? pkg.description.Substring(0, 200) + "..."
                            : pkg.description;
                    if (!string.IsNullOrEmpty(pkg.author?.name))
                        entry["author"] = pkg.author.name;
                    packages.Add(entry);
                }

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "count", packages.Count },
                    { "packages", packages }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/packages/add", Category = "packages", Description = "Install a package by name, git URL, or local path",
            TimeoutDefault = 60000, TimeoutMin = 5000, TimeoutMax = 300000)]
        public static string AddPackage(string jsonData)
        {
            try
            {
                var parsed = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
                if (parsed == null)
                    return JsonError("Invalid JSON body");

                var identifier = parsed.ContainsKey("identifier") ? parsed["identifier"] as string : null;
                if (string.IsNullOrWhiteSpace(identifier))
                    return JsonError("'identifier' is required (e.g., 'com.unity.textmeshpro', 'com.unity.textmeshpro@3.0.6', or a git URL)");

                var request = WaitForPackageRequest(Client.Add(identifier), 120000);
                var pkg = request.Result;

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "name", pkg.name },
                    { "version", pkg.version },
                    { "displayName", pkg.displayName ?? pkg.name },
                    { "source", pkg.source.ToString() }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/packages/remove", Category = "packages", Description = "Uninstall a package by name",
            TimeoutDefault = 60000, TimeoutMin = 5000, TimeoutMax = 300000)]
        public static string RemovePackage(string jsonData)
        {
            try
            {
                var parsed = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
                if (parsed == null)
                    return JsonError("Invalid JSON body");

                var packageName = parsed.ContainsKey("name") ? parsed["name"] as string : null;
                if (string.IsNullOrWhiteSpace(packageName))
                    return JsonError("'name' is required (e.g., 'com.unity.textmeshpro')");

                WaitForPackageRequest(Client.Remove(packageName), 60000);

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "removed", packageName }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("GET", "/packages/search", Category = "packages", Description = "Search Unity Package Registry",
            ReadOnly = true, TimeoutDefault = 30000, TimeoutMin = 1000, TimeoutMax = 60000)]
        public static string SearchPackages(string path, string method, string body, System.Collections.Specialized.NameValueCollection query)
        {
            try
            {
                var searchQuery = query?["query"] ?? "";

                var request = WaitForPackageRequest(Client.SearchAll());

                var results = new List<Dictionary<string, object>>();
                int maxResults = 50;
                if (query?["max"] != null && int.TryParse(query["max"], out var m))
                    maxResults = Math.Clamp(m, 1, 200);

                foreach (var pkg in request.Result)
                {
                    if (results.Count >= maxResults) break;

                    // Client-side filtering since SearchAll() no longer accepts a query string
                    if (!string.IsNullOrEmpty(searchQuery) &&
                        (pkg.name?.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) < 0) &&
                        (pkg.displayName?.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;

                    var entry = new Dictionary<string, object>
                    {
                        { "name", pkg.name },
                        { "version", pkg.versions.latest ?? pkg.versions.recommended ?? "" },
                        { "displayName", pkg.displayName ?? pkg.name },
                    };
                    if (!string.IsNullOrEmpty(pkg.description))
                        entry["description"] = pkg.description.Length > 150
                            ? pkg.description.Substring(0, 150) + "..."
                            : pkg.description;
                    results.Add(entry);
                }

                return (MiniJSON.Json.Serialize(new Dictionary<string, object>
                {
                    { "success", true },
                    { "query", searchQuery },
                    { "count", results.Count },
                    { "packages", results }
                }), 200).Item1;
            }
            catch (Exception ex)
            {
                return (JsonError(ex.Message), 500).Item1;
            }
        }
    }
}
