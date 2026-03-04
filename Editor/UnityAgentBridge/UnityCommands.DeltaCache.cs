using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        // ==================== Delta Cache ====================

        private static readonly Dictionary<string, DeltaSnapshot> _deltaSnapshots = new Dictionary<string, DeltaSnapshot>();
        private static readonly LinkedList<string> _deltaSnapshotLru = new LinkedList<string>();
        private const int MaxDeltaSnapshots = 16;

        private class DeltaSnapshot
        {
            public string name;
            public DateTime capturedAt;
            public Dictionary<int, DeltaObjectState> objects = new Dictionary<int, DeltaObjectState>();
        }

        private class DeltaObjectState
        {
            public string name;
            public bool active;
            public float posX, posY, posZ;
            public float rotX, rotY, rotZ;
            public float scaleX, scaleY, scaleZ;
            public List<string> componentTypes = new List<string>();
        }

        [BridgeRoute("POST", "/delta/capture", Category = "delta", Description = "Capture delta snapshot")]
        public static string CaptureDeltaSnapshot(string jsonData)
        {
            Dictionary<string, object> req;
            try
            {
                req = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
                if (req == null) return JsonError("Invalid request JSON.");
            }
            catch (Exception ex)
            {
                return JsonError($"Failed to parse request: {ex.Message}");
            }

            string snapshotName = req.TryGetValue("name", out var n) ? n?.ToString() : "default";
            if (string.IsNullOrEmpty(snapshotName))
                return JsonError("Snapshot name is required.");

            int[] instanceIds = null;
            if (req.TryGetValue("instanceIds", out var idsObj) && idsObj is List<object> idsList)
                instanceIds = idsList.Select(x => Convert.ToInt32(x)).ToArray();

            var snapshot = new DeltaSnapshot
            {
                name = snapshotName,
                capturedAt = DateTime.UtcNow
            };

            if (instanceIds != null && instanceIds.Length > 0)
            {
                // Snapshot specific objects
                foreach (var id in instanceIds)
                {
                    var go = EditorUtility.EntityIdToObject(id) as GameObject;
                    if (go == null) continue;
                    snapshot.objects[id] = CaptureObjectState(go);
                }
            }
            else
            {
                // Snapshot all root objects in active scene
                var scene = SceneManager.GetActiveScene();
                foreach (var root in scene.GetRootGameObjects())
                {
                    CaptureObjectStateRecursive(root, snapshot.objects);
                }
            }

            // LRU eviction
            if (_deltaSnapshots.ContainsKey(snapshotName))
            {
                _deltaSnapshotLru.Remove(snapshotName);
            }
            else if (_deltaSnapshots.Count >= MaxDeltaSnapshots)
            {
                var oldest = _deltaSnapshotLru.First.Value;
                _deltaSnapshotLru.RemoveFirst();
                _deltaSnapshots.Remove(oldest);
            }

            _deltaSnapshots[snapshotName] = snapshot;
            _deltaSnapshotLru.AddLast(snapshotName);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "snapshotName", snapshotName },
                { "objectCount", snapshot.objects.Count },
                { "capturedAt", snapshot.capturedAt.ToString("o") }
            });
        }

        public static string GetDelta(string snapshotName)
        {
            if (string.IsNullOrEmpty(snapshotName))
                return JsonError("Snapshot name is required.");

            if (!_deltaSnapshots.TryGetValue(snapshotName, out var snapshot))
                return JsonError($"Snapshot '{snapshotName}' not found.");

            // Capture current state for the same objects
            var currentState = new Dictionary<int, DeltaObjectState>();
            var scene = SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
            {
                CaptureObjectStateRecursive(root, currentState);
            }

            // Compare
            var added = new List<object>();
            var removed = new List<object>();
            var modified = new List<object>();

            // Find removed and modified
            foreach (var kvp in snapshot.objects)
            {
                if (!currentState.TryGetValue(kvp.Key, out var current))
                {
                    removed.Add(new Dictionary<string, object>
                    {
                        { "instanceId", kvp.Key },
                        { "name", kvp.Value.name }
                    });
                }
                else
                {
                    var changes = CompareObjectStates(kvp.Value, current);
                    if (changes.Count > 0)
                    {
                        var modEntry = new Dictionary<string, object>
                        {
                            { "instanceId", kvp.Key },
                            { "name", current.name },
                            { "changes", changes }
                        };
                        modified.Add(modEntry);
                    }
                }
            }

            // Find added
            foreach (var kvp in currentState)
            {
                if (!snapshot.objects.ContainsKey(kvp.Key))
                {
                    added.Add(new Dictionary<string, object>
                    {
                        { "instanceId", kvp.Key },
                        { "name", kvp.Value.name }
                    });
                }
            }

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "snapshotName", snapshotName },
                { "capturedAt", snapshot.capturedAt.ToString("o") },
                { "addedCount", added.Count },
                { "removedCount", removed.Count },
                { "modifiedCount", modified.Count },
                { "added", added },
                { "removed", removed },
                { "modified", modified }
            });
        }

        [BridgeRoute("GET", "/delta/list", Category = "delta", Description = "List delta snapshots", ReadOnly = true)]
        public static string ListDeltaSnapshots()
        {
            var snapshots = new List<object>();
            foreach (var kvp in _deltaSnapshots)
            {
                snapshots.Add(new Dictionary<string, object>
                {
                    { "name", kvp.Key },
                    { "objectCount", kvp.Value.objects.Count },
                    { "capturedAt", kvp.Value.capturedAt.ToString("o") }
                });
            }

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "snapshots", snapshots },
                { "count", snapshots.Count }
            });
        }

        public static string DeleteDeltaSnapshot(string snapshotName)
        {
            if (string.IsNullOrEmpty(snapshotName))
                return JsonError("Snapshot name is required.");

            if (_deltaSnapshots.Remove(snapshotName))
            {
                _deltaSnapshotLru.Remove(snapshotName);
                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "deleted", snapshotName }
                });
            }

            return JsonError($"Snapshot '{snapshotName}' not found.");
        }

        // ==================== Batch Read ====================

        private static readonly HashSet<string> _batchReadWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/health", "/hierarchy", "/scene", "/scene/dirty", "/scene/layout-snapshot",
            "/console", "/events", "/compilation/status", "/compilation/errors",
            "/gameobjects/find", "/prefabs", "/material", "/scripts/list",
            "/tag", "/layer", "/rendersettings", "/physics/settings",
            "/sceneview/camera", "/editor/runtime", "/look/presets",
            "/volume/profile", "/camera/rendering", "/index", "/search"
        };

        [BridgeRoute("POST", "/batch-read", Category = "batch", Description = "Execute multiple read-only routes in one call", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string ExecuteBatchRead(string jsonData)
        {
            List<object> requests;
            try
            {
                var parsed = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
                if (parsed == null || !parsed.ContainsKey("requests"))
                    return JsonError("Request must contain a 'requests' array.");
                requests = parsed["requests"] as List<object>;
                if (requests == null || requests.Count == 0)
                    return JsonError("requests array is empty.");
                if (requests.Count > 10)
                    return JsonError("Maximum 10 requests per batch.");
            }
            catch (Exception ex)
            {
                return JsonError($"Failed to parse request: {ex.Message}");
            }

            var results = new List<object>();
            foreach (var item in requests)
            {
                var reqDict = item as Dictionary<string, object>;
                if (reqDict == null)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "error", "Invalid request entry" },
                        { "statusCode", 400 }
                    });
                    continue;
                }

                string route = reqDict.TryGetValue("route", out var r) ? r?.ToString()?.Trim() : "";

                // Strip HTTP method prefix if agents include it (e.g., "GET /hierarchy" → "/hierarchy")
                if (route.Length > 0 && !route.StartsWith("/"))
                {
                    var spaceIdx = route.IndexOf(' ');
                    if (spaceIdx > 0)
                        route = route.Substring(spaceIdx + 1).Trim();
                }

                // Validate route is whitelisted
                string routePath = route;
                if (routePath.Contains("?"))
                    routePath = routePath.Substring(0, routePath.IndexOf('?'));

                // Check if the path starts with a whitelisted prefix (for paths like /gameobject/123)
                bool allowed = _batchReadWhitelist.Contains(routePath);
                if (!allowed)
                {
                    // Check for paths with dynamic segments
                    if (routePath.StartsWith("/gameobject/") || routePath.StartsWith("/components/"))
                        allowed = true;
                }

                if (!allowed)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "route", route },
                        { "error", $"Route '{routePath}' is not allowed in batch read. Only read routes are permitted." },
                        { "statusCode", 403 }
                    });
                    continue;
                }

                // Execute the read by re-dispatching through GetAsync on the server
                // Since we're already on the main thread, we call the commands directly
                try
                {
                    string body = DispatchBatchReadRoute(route);
                    results.Add(new Dictionary<string, object>
                    {
                        { "route", route },
                        { "statusCode", 200 },
                        { "body", ParseJsonBodyOrString(body) }
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "route", route },
                        { "error", ex.Message },
                        { "statusCode", 500 }
                    });
                }
            }

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "results", results },
                { "count", results.Count }
            });
        }

        private static object ParseJsonBodyOrString(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return string.Empty;

            var trimmed = body.TrimStart();
            if (!trimmed.StartsWith("{") && !trimmed.StartsWith("["))
                return body;

            try
            {
                return MiniJSON.Json.Deserialize(body) ?? body;
            }
            catch
            {
                return body;
            }
        }

        private static string DispatchBatchReadRoute(string route)
        {
            // Parse the route into path and query
            string path = route;
            var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            int qIdx = route.IndexOf('?');
            if (qIdx >= 0)
            {
                path = route.Substring(0, qIdx);
                var queryStr = route.Substring(qIdx + 1);
                foreach (var pair in queryStr.Split('&'))
                {
                    var eqIdx = pair.IndexOf('=');
                    if (eqIdx > 0)
                    {
                        var key = Uri.UnescapeDataString(pair.Substring(0, eqIdx));
                        var val = Uri.UnescapeDataString(pair.Substring(eqIdx + 1));
                        queryParams[key] = val;
                    }
                }
            }

            path = path.ToLowerInvariant();

            // Dispatch to known read commands
            if (path == "/health")
                return "{\"status\":\"ok\"}";

            if (path == "/hierarchy")
            {
                int depth = queryParams.TryGetValue("depth", out var d) && int.TryParse(d, out var dv) ? dv : 0;
                bool brief = !queryParams.TryGetValue("brief", out var b) || b != "false";
                bool pretty = queryParams.TryGetValue("pretty", out var p) && p == "true";
                return GetHierarchy(depth, brief, pretty);
            }

            if (path == "/scene")
                return GetCurrentScene();

            if (path == "/scene/dirty")
                return GetSceneDirtyState();

            if (path == "/compilation/status")
                return GetCompilationStatus();

            if (path == "/compilation/errors")
                return GetCompilationErrors();

            if (path == "/rendersettings")
                return GetRenderSettings();

            if (path == "/sceneview/camera")
                return GetSceneViewCamera();

            if (path == "/tag")
                return GetTags();

            if (path == "/layer")
                return GetLayers();

            if (path.StartsWith("/gameobject/"))
            {
                var idStr = path.Substring("/gameobject/".Length);
                if (int.TryParse(idStr, out var instanceId))
                {
                    bool inclComp = queryParams.TryGetValue("include_components", out var ic) && ic == "true";
                    bool transOnly = queryParams.TryGetValue("transform_only", out var to) && to == "true";
                    return GetGameObject(instanceId, inclComp, transOnly);
                }
            }

            if (path.StartsWith("/components/"))
            {
                var idStr = path.Substring("/components/".Length);
                if (int.TryParse(idStr, out var instanceId))
                {
                    bool namesOnly = !queryParams.TryGetValue("names_only", out var no) || no != "false";
                    return GetComponents(instanceId, namesOnly);
                }
            }

            if (path == "/gameobjects/find")
            {
                queryParams.TryGetValue("name", out var nameP);
                queryParams.TryGetValue("component", out var compP);
                queryParams.TryGetValue("tag", out var tagP);
                queryParams.TryGetValue("layer", out var layerP);
                int activeF = -1;
                if (queryParams.TryGetValue("active", out var actP))
                    activeF = actP == "true" ? 1 : 0;
                int maxR = queryParams.TryGetValue("max", out var maxP) && int.TryParse(maxP, out var mv) ? mv : 100;
                bool inclComps = queryParams.TryGetValue("includeComponents", out var icP) && icP == "true";
                return FindGameObjects(nameP, compP, tagP, layerP, activeF, maxR, inclComps);
            }

            if (path == "/console")
            {
                int count = queryParams.TryGetValue("count", out var cP) && int.TryParse(cP, out var cv) ? cv : 50;
                queryParams.TryGetValue("type", out var typeP);
                queryParams.TryGetValue("text", out var textP);
                bool includeStackTrace = queryParams.TryGetValue("includeStackTrace", out var includeStackTraceParam)
                    && string.Equals(includeStackTraceParam, "true", StringComparison.OrdinalIgnoreCase);
                return GetConsoleLogs(count, typeP, textP, includeStackTrace);
            }

            return JsonError($"Batch read route '{path}' not implemented.");
        }

        // ==================== Context Grounding ====================

        public static string BuildContextSummary(int[] instanceIds)
        {
            if (instanceIds == null || instanceIds.Length == 0)
                return "{}";

            var context = new List<object>();
            foreach (var id in instanceIds)
            {
                var go = EditorUtility.EntityIdToObject(id) as GameObject;
                if (go == null) continue;

                var entry = new Dictionary<string, object>
                {
                    { "instanceId", id },
                    { "name", go.name },
                    { "active", go.activeSelf },
                    { "position", new Dictionary<string, object>
                        {
                            { "x", go.transform.position.x },
                            { "y", go.transform.position.y },
                            { "z", go.transform.position.z }
                        }
                    },
                    { "components", go.GetComponents<Component>()
                        .Where(c => c != null)
                        .Select(c => c.GetType().Name)
                        .ToList()
                    }
                };
                context.Add(entry);
            }

            return MiniJSON.Json.Serialize(new Dictionary<string, object>
            {
                { "affectedObjects", context },
                { "count", context.Count }
            });
        }

        // ==================== Private Helpers ====================

        private static DeltaObjectState CaptureObjectState(GameObject go)
        {
            var pos = go.transform.position;
            var rot = go.transform.eulerAngles;
            var scale = go.transform.localScale;

            return new DeltaObjectState
            {
                name = go.name,
                active = go.activeSelf,
                posX = pos.x, posY = pos.y, posZ = pos.z,
                rotX = rot.x, rotY = rot.y, rotZ = rot.z,
                scaleX = scale.x, scaleY = scale.y, scaleZ = scale.z,
                componentTypes = go.GetComponents<Component>()
                    .Where(c => c != null)
                    .Select(c => c.GetType().Name)
                    .ToList()
            };
        }

        private static void CaptureObjectStateRecursive(GameObject go, Dictionary<int, DeltaObjectState> dict)
        {
            dict[go.GetInstanceID()] = CaptureObjectState(go);
            for (int i = 0; i < go.transform.childCount; i++)
            {
                CaptureObjectStateRecursive(go.transform.GetChild(i).gameObject, dict);
            }
        }

        private static List<object> CompareObjectStates(DeltaObjectState before, DeltaObjectState after)
        {
            var changes = new List<object>();

            if (before.name != after.name)
                changes.Add(new Dictionary<string, object> { { "field", "name" }, { "before", before.name }, { "after", after.name } });

            if (before.active != after.active)
                changes.Add(new Dictionary<string, object> { { "field", "active" }, { "before", before.active }, { "after", after.active } });

            float posDelta = Mathf.Sqrt(
                (before.posX - after.posX) * (before.posX - after.posX) +
                (before.posY - after.posY) * (before.posY - after.posY) +
                (before.posZ - after.posZ) * (before.posZ - after.posZ));

            if (posDelta > 0.001f)
            {
                changes.Add(new Dictionary<string, object>
                {
                    { "field", "position" },
                    { "before", new Dictionary<string, object> { { "x", before.posX }, { "y", before.posY }, { "z", before.posZ } } },
                    { "after", new Dictionary<string, object> { { "x", after.posX }, { "y", after.posY }, { "z", after.posZ } } },
                    { "delta", posDelta }
                });
            }

            float rotDelta = Mathf.Sqrt(
                (before.rotX - after.rotX) * (before.rotX - after.rotX) +
                (before.rotY - after.rotY) * (before.rotY - after.rotY) +
                (before.rotZ - after.rotZ) * (before.rotZ - after.rotZ));

            if (rotDelta > 0.1f)
            {
                changes.Add(new Dictionary<string, object>
                {
                    { "field", "rotation" },
                    { "before", new Dictionary<string, object> { { "x", before.rotX }, { "y", before.rotY }, { "z", before.rotZ } } },
                    { "after", new Dictionary<string, object> { { "x", after.rotX }, { "y", after.rotY }, { "z", after.rotZ } } }
                });
            }

            float scaleDelta = Mathf.Sqrt(
                (before.scaleX - after.scaleX) * (before.scaleX - after.scaleX) +
                (before.scaleY - after.scaleY) * (before.scaleY - after.scaleY) +
                (before.scaleZ - after.scaleZ) * (before.scaleZ - after.scaleZ));

            if (scaleDelta > 0.001f)
            {
                changes.Add(new Dictionary<string, object>
                {
                    { "field", "scale" },
                    { "before", new Dictionary<string, object> { { "x", before.scaleX }, { "y", before.scaleY }, { "z", before.scaleZ } } },
                    { "after", new Dictionary<string, object> { { "x", after.scaleX }, { "y", after.scaleY }, { "z", after.scaleZ } } }
                });
            }

            // Check component changes
            var beforeComps = new HashSet<string>(before.componentTypes);
            var afterComps = new HashSet<string>(after.componentTypes);

            var addedComps = afterComps.Except(beforeComps).ToList();
            var removedComps = beforeComps.Except(afterComps).ToList();

            if (addedComps.Count > 0 || removedComps.Count > 0)
            {
                changes.Add(new Dictionary<string, object>
                {
                    { "field", "components" },
                    { "added", addedComps },
                    { "removed", removedComps }
                });
            }

            return changes;
        }
    }
}
