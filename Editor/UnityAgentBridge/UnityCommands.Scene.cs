using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Profiling;
using Unity.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Audio;
using TMPro;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        private static Vector2 GetGameViewSize()
        {
            try
            {
                var T = Type.GetType("UnityEditor.GameView,UnityEditor");
                var method = T?.GetMethod("GetSizeOfMainGameView", BindingFlags.NonPublic | BindingFlags.Static);
                if (method != null)
                    return (Vector2)method.Invoke(null, null);
            }
            catch { }
            return new Vector2(1920, 1080);
        }

        [BridgeRoute("GET", "/scene", Category = "scene", Description = "Current scene info", ReadOnly = true)]
        public static string GetCurrentScene()
        {
            var scene = SceneManager.GetActiveScene();
            var allScenes = new List<SceneInfo>();

            // Get all scenes in build settings
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
            {
                var scenePath = SceneUtility.GetScenePathByBuildIndex(i);
                allScenes.Add(new SceneInfo
                {
                    name = System.IO.Path.GetFileNameWithoutExtension(scenePath),
                    path = scenePath,
                    buildIndex = i,
                    isLoaded = false
                });
            }

            // Mark loaded scenes
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var loadedScene = SceneManager.GetSceneAt(i);
                var existing = allScenes.FirstOrDefault(s => s.path == loadedScene.path);
                if (existing != null)
                {
                    existing.isLoaded = true;
                }
            }

            return JsonResult(new Dictionary<string, object> {
                { "current", new Dictionary<string, object> {
                    { "name", scene.name },
                    { "path", scene.path },
                    { "buildIndex", scene.buildIndex },
                    { "isLoaded", scene.isLoaded },
                    { "isDirty", scene.isDirty }
                }},
                { "allScenes", allScenes }
            });
        }

        public static string GetSceneLayoutSnapshot(string tileRootName, int maxTiles)
        {
            try
            {
                maxTiles = Mathf.Clamp(maxTiles <= 0 ? 600 : maxTiles, 10, 1500);
                var response = new Dictionary<string, object>();

                // Camera info
                var cam = Camera.main;
                if (cam != null)
                {
                    var t = cam.transform;
                    response["camera"] = new Dictionary<string, object>
                    {
                        { "position", new List<object> { (double)Math.Round(t.position.x, 3), (double)Math.Round(t.position.y, 3), (double)Math.Round(t.position.z, 3) } },
                        { "rotation", new List<object> { (double)Math.Round(t.eulerAngles.x, 2), (double)Math.Round(t.eulerAngles.y, 2), (double)Math.Round(t.eulerAngles.z, 2) } },
                        { "fov", (double)Math.Round(cam.fieldOfView, 2) },
                        { "nearClip", (double)Math.Round(cam.nearClipPlane, 3) },
                        { "farClip", (double)Math.Round(cam.farClipPlane, 1) }
                    };
                }

                // Player info
                var player = GameObject.FindWithTag("Player");
                if (player == null)
                {
                    var candidates = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None);
                    foreach (var c in candidates)
                    {
                        if (c.name.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            player = c.gameObject;
                            break;
                        }
                    }
                }
                if (player != null)
                {
                    var pp = player.transform.position;
                    response["player"] = new Dictionary<string, object>
                    {
                        { "name", player.name },
                        { "instanceId", player.GetInstanceID() },
                        { "position", new List<object> { (double)Math.Round(pp.x, 3), (double)Math.Round(pp.y, 3), (double)Math.Round(pp.z, 3) } },
                        { "active", player.activeInHierarchy }
                    };
                }

                // Tile stats - find root by name or auto-detect (root with most children)
                GameObject tileRoot = null;
                if (!string.IsNullOrWhiteSpace(tileRootName))
                {
                    var allRoots = new List<GameObject>();
                    for (int s = 0; s < SceneManager.sceneCount; s++)
                    {
                        var scene = SceneManager.GetSceneAt(s);
                        if (!scene.isLoaded) continue;
                        allRoots.AddRange(scene.GetRootGameObjects());
                    }
                    tileRoot = FindByNameRecursive(allRoots, tileRootName);
                }
                if (tileRoot == null)
                {
                    // auto-detect: root with most children
                    int maxChildren = 0;
                    for (int s = 0; s < SceneManager.sceneCount; s++)
                    {
                        var scene = SceneManager.GetSceneAt(s);
                        if (!scene.isLoaded) continue;
                        foreach (var root in scene.GetRootGameObjects())
                        {
                            if (root.transform.childCount > maxChildren)
                            {
                                maxChildren = root.transform.childCount;
                                tileRoot = root;
                            }
                        }
                    }
                }

                if (tileRoot != null && tileRoot.transform.childCount >= 2)
                {
                    var positions = new List<Vector3>();
                    Bounds? firstTileBounds = null;
                    int count = 0;
                    float xMin = float.MaxValue, xMax = float.MinValue;
                    float zMin = float.MaxValue, zMax = float.MinValue;

                    foreach (Transform child in tileRoot.transform)
                    {
                        if (count >= maxTiles) break;
                        if (child == null || !child.gameObject.activeInHierarchy) continue;
                        var pos = child.position;
                        positions.Add(pos);
                        if (pos.x < xMin) xMin = pos.x;
                        if (pos.x > xMax) xMax = pos.x;
                        if (pos.z < zMin) zMin = pos.z;
                        if (pos.z > zMax) zMax = pos.z;

                        if (firstTileBounds == null)
                        {
                            if (TryGetAggregateBounds(child.gameObject, false, out var b, out _))
                                firstTileBounds = b;
                        }
                        count++;
                    }

                    if (count >= 2)
                    {
                        // Sampled nearest-neighbor spacing (O(n) via capped search)
                        var spacings = new List<float>();
                        int maxCheck = Mathf.Min(20, count - 1);
                        // Sort by Z then X for locality
                        positions.Sort((a, b) =>
                        {
                            int cmp = a.z.CompareTo(b.z);
                            return cmp != 0 ? cmp : a.x.CompareTo(b.x);
                        });
                        for (int i = 0; i < count; i++)
                        {
                            float nearest = float.MaxValue;
                            int start = Mathf.Max(0, i - maxCheck);
                            int end = Mathf.Min(count - 1, i + maxCheck);
                            for (int j = start; j <= end; j++)
                            {
                                if (j == i) continue;
                                float dx = positions[i].x - positions[j].x;
                                float dz = positions[i].z - positions[j].z;
                                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                                if (dist < nearest) nearest = dist;
                            }
                            if (nearest < float.MaxValue) spacings.Add(nearest);
                        }

                        spacings.Sort();
                        float min = spacings.Count > 0 ? spacings[0] : 0f;
                        float max = spacings.Count > 0 ? spacings[spacings.Count - 1] : 0f;
                        float avg = spacings.Count > 0 ? spacings.Average() : 0f;
                        float median = spacings.Count > 0 ? spacings[spacings.Count / 2] : 0f;

                        // Estimate lane count via X-clustering
                        var xPositions = positions.Select(p => p.x).OrderBy(x => x).ToList();
                        int laneCount = 1;
                        float laneSpacing = 0f;
                        if (xPositions.Count >= 2)
                        {
                            float clusterGap = median * 0.6f;
                            var clusters = new List<List<float>> { new List<float> { xPositions[0] } };
                            for (int i = 1; i < xPositions.Count; i++)
                            {
                                if (xPositions[i] - clusters.Last().Last() > clusterGap)
                                    clusters.Add(new List<float>());
                                clusters.Last().Add(xPositions[i]);
                            }
                            laneCount = clusters.Count;
                            if (clusters.Count >= 2)
                            {
                                var centers = clusters.Select(c => c.Average()).ToList();
                                var gaps = new List<float>();
                                for (int i = 1; i < centers.Count; i++)
                                    gaps.Add(centers[i] - centers[i - 1]);
                                laneSpacing = gaps.Count > 0 ? gaps.Average() : 0f;
                            }
                        }

                        var tileStats = new Dictionary<string, object>
                        {
                            { "rootName", tileRoot.name },
                            { "rootInstanceId", tileRoot.GetInstanceID() },
                            { "count", count },
                            { "spacingStats", new Dictionary<string, object>
                                {
                                    { "min", (double)Math.Round(min, 3) },
                                    { "max", (double)Math.Round(max, 3) },
                                    { "avg", (double)Math.Round(avg, 3) },
                                    { "median", (double)Math.Round(median, 3) }
                                }
                            },
                            { "estimatedLanes", laneCount },
                            { "laneSpacing", (double)Math.Round(laneSpacing, 3) },
                            { "trackBoundsMin", new List<object> { (double)Math.Round(xMin, 2), (double)Math.Round(zMin, 2) } },
                            { "trackBoundsMax", new List<object> { (double)Math.Round(xMax, 2), (double)Math.Round(zMax, 2) } }
                        };

                        if (firstTileBounds.HasValue)
                        {
                            var s = firstTileBounds.Value.size;
                            tileStats["tileBoundsSize"] = new List<object> { (double)Math.Round(s.x, 3), (double)Math.Round(s.y, 3), (double)Math.Round(s.z, 3) };
                        }

                        // Check if player is on track
                        if (player != null)
                        {
                            var pp = player.transform.position;
                            bool onTrack = pp.x >= xMin && pp.x <= xMax && pp.z >= zMin && pp.z <= zMax;
                            ((Dictionary<string, object>)response["player"])["onTrack"] = onTrack;
                        }

                        response["tileStats"] = tileStats;
                    }
                }

                // Render summary
                var renderSummary = new Dictionary<string, object>
                {
                    { "ambientColor", new List<object> {
                        (double)Math.Round(RenderSettings.ambientLight.r, 3),
                        (double)Math.Round(RenderSettings.ambientLight.g, 3),
                        (double)Math.Round(RenderSettings.ambientLight.b, 3) } },
                    { "fogEnabled", RenderSettings.fog }
                };

                // Check Volume profile for bloom
                bool bloomEnabled = false;
                var volumes = UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsSortMode.None);
                foreach (var vol in volumes)
                {
                    if (vol == null || vol.profile == null || !vol.isActiveAndEnabled) continue;
                    if (vol.profile.TryGet<Bloom>(out var bloom) && bloom.active)
                    {
                        bloomEnabled = true;
                        break;
                    }
                }
                renderSummary["bloomEnabled"] = bloomEnabled;
                response["renderSummary"] = renderSummary;

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static GameObject FindByNameRecursive(List<GameObject> roots, string name)
        {
            foreach (var root in roots)
            {
                if (root.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return root;
                var found = FindByNameInChildren(root.transform, name);
                if (found != null) return found;
            }
            return null;
        }

        private static GameObject FindByNameInChildren(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return child.gameObject;
                var found = FindByNameInChildren(child, name);
                if (found != null) return found;
            }
            return null;
        }

        [BridgeRoute("POST", "/scene", Category = "scene", Description = "Load scene")]
        public static string LoadScene(string jsonData)
        {
            var request = JsonUtility.FromJson<LoadSceneRequest>(jsonData);

            if (string.IsNullOrEmpty(request.scenePath) && string.IsNullOrEmpty(request.sceneName))
            {
                return JsonError("Scene path or name required");
            }

            string scenePath = request.scenePath;

            // If only name provided, find the scene
            if (string.IsNullOrEmpty(scenePath) && !string.IsNullOrEmpty(request.sceneName))
            {
                var guids = AssetDatabase.FindAssets($"t:Scene {request.sceneName}");
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (System.IO.Path.GetFileNameWithoutExtension(path) == request.sceneName)
                    {
                        scenePath = path;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(scenePath))
            {
                return JsonError($"Scene not found: {request.sceneName}");
            }

            if (request.saveCurrentScene)
            {
                EditorSceneManager.SaveOpenScenes();
            }

            EditorSceneManager.OpenScene(scenePath);
            var scene = SceneManager.GetActiveScene();

            return JsonResult(new Dictionary<string, object> {
                { "success", true },
                { "scene", new Dictionary<string, object> {
                    { "name", scene.name },
                    { "path", scene.path },
                    { "buildIndex", scene.buildIndex },
                    { "isLoaded", scene.isLoaded }
                }}
            });
        }

        [BridgeRoute("GET", "/scene/dirty", Category = "scene", Description = "Scene dirty state", ReadOnly = true)]
        public static string GetSceneDirtyState()
        {
            var activeScene = SceneManager.GetActiveScene();
            var dirtyScenes = new List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var loaded = SceneManager.GetSceneAt(i);
                if (!loaded.IsValid() || !loaded.isLoaded || !loaded.isDirty) continue;
                dirtyScenes.Add(new Dictionary<string, object>
                {
                    { "name", loaded.name },
                    { "path", loaded.path },
                    { "buildIndex", loaded.buildIndex },
                    { "isDirty", loaded.isDirty }
                });
            }

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "activeScene", new Dictionary<string, object>
                    {
                        { "name", activeScene.name },
                        { "path", activeScene.path },
                        { "buildIndex", activeScene.buildIndex },
                        { "isLoaded", activeScene.isLoaded },
                        { "isDirty", activeScene.isDirty }
                    }
                },
                { "dirtySceneCount", dirtyScenes.Count },
                { "dirtyScenes", dirtyScenes }
            });
        }

        [BridgeRoute("POST", "/scene/save", Category = "scene", Description = "Save scene")]
        public static string SaveScene(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) as Dictionary<string, object>;
                if (request == null)
                {
                    return JsonError("Failed to parse save-scene request");
                }

                string scenePath = ReadString(request, "scenePath");
                bool saveAsCopy = ReadBool(request, "saveAsCopy", false);
                bool onlyIfDirty = ReadBool(request, "onlyIfDirty", false);

                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    return JsonError("No active loaded scene");
                }

                if (onlyIfDirty && !scene.isDirty)
                {
                    return JsonResult(new Dictionary<string, object>
                    {
                        { "success", true },
                        { "saved", false },
                        { "reason", "Scene is not dirty" },
                        { "scenePath", scene.path }
                    });
                }

                bool saved = string.IsNullOrWhiteSpace(scenePath)
                    ? EditorSceneManager.SaveScene(scene)
                    : EditorSceneManager.SaveScene(scene, scenePath, saveAsCopy);

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", saved },
                    { "saved", saved },
                    { "sceneName", scene.name },
                    { "scenePath", string.IsNullOrWhiteSpace(scenePath) ? scene.path : scenePath },
                    { "saveAsCopy", saveAsCopy },
                    { "isDirtyAfterSave", SceneManager.GetActiveScene().isDirty }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static bool TrySaveActiveScene(out string error)
        {
            error = null;
            try
            {
                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    error = "No active loaded scene";
                    return false;
                }

                if (!scene.isDirty)
                {
                    return true;
                }

                if (!EditorSceneManager.SaveScene(scene))
                {
                    error = $"Failed to save scene: {scene.path}";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        #region Scene Builder Methods

        [BridgeRoute("POST", "/scene/create", Category = "scene", Description = "Build scene from JSON descriptor", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string CreateSceneFromDescriptor(string jsonData)
        {
            try
            {
                var descriptor = JsonUtility.FromJson<SceneDescriptor>(jsonData);
                if (descriptor == null || descriptor.objects == null)
                {
                    return JsonUtility.ToJson(new SceneCreateResponse { success = false, error = "Invalid scene descriptor" });
                }

                var createdObjects = new List<int>();
                var materialCache = new Dictionary<string, Material>();
                var warnings = new List<string>();

                Undo.SetCurrentGroupName($"Create Scene: {descriptor.name ?? "Unnamed"}");
                int undoGroup = Undo.GetCurrentGroup();

                foreach (var objDesc in descriptor.objects)
                {
                    var go = CreateObjectFromDescriptor(objDesc, null, materialCache, warnings);
                    if (go != null)
                    {
                        createdObjects.Add(go.GetInstanceID());
                    }
                }

                Undo.CollapseUndoOperations(undoGroup);

                return JsonUtility.ToJson(new SceneCreateResponse
                {
                    success = true,
                    createdCount = createdObjects.Count,
                    instanceIds = createdObjects,
                    warnings = warnings
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new SceneCreateResponse { success = false, error = ex.Message });
            }
        }

        [BridgeRoute("POST", "/scene/build-and-screenshot", Category = "scene", Description = "Build scene + capture screenshot", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string BuildSceneAndScreenshot(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<BuildAndScreenshotRequest>(jsonData);
                if (string.IsNullOrEmpty(request.descriptor))
                {
                    return JsonUtility.ToJson(new BuildAndScreenshotResponse { success = false, error = "Descriptor JSON is required" });
                }

                // Parse the nested descriptor JSON
                var descriptor = JsonUtility.FromJson<SceneDescriptor>(request.descriptor);
                if (descriptor == null || descriptor.objects == null)
                {
                    return JsonUtility.ToJson(new BuildAndScreenshotResponse { success = false, error = "Invalid scene descriptor" });
                }

                // Create objects in a single undo group
                var createdObjects = new List<int>();
                var materialCache = new Dictionary<string, Material>();
                var warnings = new List<string>();
                Undo.SetCurrentGroupName($"Build Scene: {descriptor.name ?? "Unnamed"}");
                int undoGroup = Undo.GetCurrentGroup();

                foreach (var objDesc in descriptor.objects)
                {
                    var go = CreateObjectFromDescriptor(objDesc, null, materialCache, warnings);
                    if (go != null)
                    {
                        createdObjects.Add(go.GetInstanceID());
                    }
                }

                Undo.CollapseUndoOperations(undoGroup);

                // Optionally position scene camera
                if (request.cameraPosition != null && request.cameraPosition.Length >= 3)
                {
                    var sceneView = SceneView.lastActiveSceneView;
                    if (sceneView != null)
                    {
                        sceneView.pivot = new Vector3(request.cameraPosition[0], request.cameraPosition[1], request.cameraPosition[2]);

                        if (request.cameraRotation != null && request.cameraRotation.Length >= 3)
                        {
                            sceneView.rotation = Quaternion.Euler(request.cameraRotation[0], request.cameraRotation[1], request.cameraRotation[2]);
                        }

                        sceneView.Repaint();
                    }
                }

                // Force a repaint so newly created objects are rendered
                SceneView.RepaintAll();

                // Small delay to let Unity finish rendering
                System.Threading.Thread.Sleep(200);

                // Take screenshot
                var viewType = string.IsNullOrEmpty(request.screenshotView) ? "scene" : request.screenshotView;
                var screenshotJson = TakeScreenshot(viewType);

                // Parse screenshot result to extract data
                ScreenshotData screenshotData = null;
                var screenshotDict = MiniJSON.Json.Deserialize(screenshotJson) as Dictionary<string, object>;
                if (screenshotDict != null && screenshotDict.ContainsKey("base64"))
                {
                    screenshotData = new ScreenshotData
                    {
                        base64 = screenshotDict["base64"] as string,
                        mimeType = screenshotDict.ContainsKey("mimeType") ? screenshotDict["mimeType"] as string : "image/jpeg",
                        width = screenshotDict.ContainsKey("width") ? Convert.ToInt32(screenshotDict["width"]) : 0,
                        height = screenshotDict.ContainsKey("height") ? Convert.ToInt32(screenshotDict["height"]) : 0
                    };
                }

                return JsonUtility.ToJson(new BuildAndScreenshotResponse
                {
                    success = true,
                    createdCount = createdObjects.Count,
                    instanceIds = createdObjects,
                    screenshot = screenshotData
                }, true);
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new BuildAndScreenshotResponse { success = false, error = ex.Message });
            }
        }

        #endregion
    }
}
