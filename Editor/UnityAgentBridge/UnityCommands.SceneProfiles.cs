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
        // Scene Profiles: ExtractSceneProfile, GetSavedSceneProfile

        [BridgeRoute("POST", "/scene/profile", Category = "profiles", Description = "Extract scene profile", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string ExtractSceneProfile(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<ExtractSceneProfileRequest>(jsonData ?? "{}");
                if (request == null) request = new ExtractSceneProfileRequest();

                var scene = SceneManager.GetActiveScene();
                var profileName = !string.IsNullOrWhiteSpace(request.name) ? request.name : MakeSafeFileName(scene.name) + "_Profile";
                var safeName = MakeSafeFileName(profileName);

                var savePath = !string.IsNullOrWhiteSpace(request.savePath) ? request.savePath : "Assets/Editor/SceneProfiles";
                EnsureAssetFolder(savePath);

                // 1. Scene descriptor (reuse existing)
                var descriptorJson = ExportSceneDescriptor(null);
                var sceneDescriptor = MiniJSON.Json.Deserialize(descriptorJson);

                // 2. Look preset data
                Dictionary<string, object> volumeSnapshot = null;
                if (TryResolveVolumeProfile(null, 0, false, out var vol, out var prof, out var rp, out var ve))
                {
                    volumeSnapshot = BuildVolumeProfileSnapshot(prof);
                }

                var look = new Dictionary<string, object>
                {
                    { "lights", CaptureSceneLights() },
                    { "renderSettings", CaptureRenderSettingsDict() }
                };
                if (volumeSnapshot != null) look["volume"] = volumeSnapshot;
                var cameraDict = CaptureCameraDict();
                if (cameraDict != null) look["camera"] = cameraDict;

                // 3. Material usage map & 4. Prefab frequency
                var materialUsage = new Dictionary<string, List<string>>();
                var prefabCounts = new Dictionary<string, List<List<object>>>();
                int totalObjects = 0;

                var rootObjects = scene.GetRootGameObjects();
                var stack = new Stack<GameObject>(rootObjects);
                while (stack.Count > 0)
                {
                    var go = stack.Pop();
                    totalObjects++;

                    // Material usage
                    var renderers = go.GetComponents<Renderer>();
                    foreach (var r in renderers)
                    {
                        foreach (var mat in r.sharedMaterials)
                        {
                            if (mat == null) continue;
                            var matPath = AssetDatabase.GetAssetPath(mat);
                            if (string.IsNullOrEmpty(matPath)) continue;
                            if (!materialUsage.ContainsKey(matPath))
                                materialUsage[matPath] = new List<string>();
                            if (!materialUsage[matPath].Contains(go.name))
                                materialUsage[matPath].Add(go.name);
                        }
                    }

                    // Prefab usage
                    var prefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (prefabAsset != null)
                    {
                        var prefabPath = AssetDatabase.GetAssetPath(prefabAsset);
                        if (!string.IsNullOrEmpty(prefabPath))
                        {
                            if (!prefabCounts.ContainsKey(prefabPath))
                                prefabCounts[prefabPath] = new List<List<object>>();
                            if (prefabCounts[prefabPath].Count < 3) // limit sample positions
                            {
                                prefabCounts[prefabPath].Add(new List<object>
                                {
                                    go.transform.position.x,
                                    go.transform.position.y,
                                    go.transform.position.z
                                });
                            }
                        }
                    }

                    // Recurse children
                    for (int i = go.transform.childCount - 1; i >= 0; i--)
                    {
                        stack.Push(go.transform.GetChild(i).gameObject);
                    }
                }

                // Build prefab usage list
                var prefabUsage = new List<Dictionary<string, object>>();
                foreach (var kv in prefabCounts.OrderByDescending(k => k.Value.Count))
                {
                    prefabUsage.Add(new Dictionary<string, object>
                    {
                        { "prefabPath", kv.Key },
                        { "count", kv.Value.Count },
                        { "samplePositions", kv.Value.Cast<object>().ToList() }
                    });
                }

                // Convert material usage to serializable
                var matUsageSerializable = new Dictionary<string, object>();
                foreach (var kv in materialUsage)
                {
                    matUsageSerializable[kv.Key] = kv.Value.Cast<object>().ToList();
                }

                var lightCount = CaptureSceneLights().Count;

                // 5. Statistics
                var statistics = new Dictionary<string, object>
                {
                    { "totalObjects", totalObjects },
                    { "lightCount", lightCount },
                    { "uniqueMaterialCount", materialUsage.Count },
                    { "uniquePrefabCount", prefabCounts.Count },
                    { "prefabDiversity", totalObjects > 0 ? (float)prefabCounts.Count / totalObjects : 0f }
                };

                var profileData = new Dictionary<string, object>
                {
                    { "profileVersion", 1 },
                    { "name", profileName },
                    { "sceneName", scene.name },
                    { "scenePath", scene.path },
                    { "statistics", statistics },
                    { "sceneDescriptor", sceneDescriptor },
                    { "look", look },
                    { "materialUsage", matUsageSerializable },
                    { "prefabUsage", prefabUsage.Cast<object>().ToList() }
                };

                var profileJson = MiniJSON.Json.Serialize(profileData);
                var filePath = $"{savePath}/{safeName}.json";

                if (ValidateAssetPath(filePath) == null)
                    return JsonError("Invalid profile path");

                var projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..");
                var fullPath = System.IO.Path.Combine(projectRoot, filePath);
                System.IO.File.WriteAllText(fullPath, profileJson);
                AssetDatabase.Refresh();

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "path", filePath },
                    { "statistics", statistics }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        public static string GetSavedSceneProfile(string name, bool brief = true, int maxEntries = 25)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name))
                    return JsonError("Profile name is required");

                var safeName = MakeSafeFileName(name);
                var filePath = $"Assets/Editor/SceneProfiles/{safeName}.json";
                var projectRoot = System.IO.Path.GetFullPath(Application.dataPath + "/..");
                var fullPath = System.IO.Path.Combine(projectRoot, filePath);

                if (!System.IO.File.Exists(fullPath))
                    return JsonError($"Profile not found: {filePath}");

                var content = System.IO.File.ReadAllText(fullPath);
                if (!brief)
                    return content;

                var parsed = MiniJSON.Json.Deserialize(content) as Dictionary<string, object>;
                if (parsed == null)
                    return JsonError("Failed to parse profile JSON");

                maxEntries = Mathf.Clamp(maxEntries, 1, 200);

                var statistics = parsed.TryGetValue("statistics", out var statsObj)
                    ? statsObj as Dictionary<string, object>
                    : null;
                var prefabUsage = parsed.TryGetValue("prefabUsage", out var prefabUsageObj)
                    ? prefabUsageObj as IList<object>
                    : null;
                var materialUsage = parsed.TryGetValue("materialUsage", out var materialUsageObj)
                    ? materialUsageObj as Dictionary<string, object>
                    : null;

                var topPrefabUsage = new List<object>();
                if (prefabUsage != null)
                {
                    for (int i = 0; i < prefabUsage.Count && i < maxEntries; i++)
                        topPrefabUsage.Add(prefabUsage[i]);
                }

                var topMaterials = new List<object>();
                if (materialUsage != null)
                {
                    foreach (var kv in materialUsage.Take(maxEntries))
                    {
                        int objectCount = 0;
                        if (kv.Value is IList<object> list)
                            objectCount = list.Count;

                        topMaterials.Add(new Dictionary<string, object>
                        {
                            { "materialPath", kv.Key },
                            { "objectCount", objectCount }
                        });
                    }
                }

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "brief", true },
                    { "name", ReadString(parsed, "name") ?? safeName },
                    { "sceneName", ReadString(parsed, "sceneName") ?? string.Empty },
                    { "scenePath", ReadString(parsed, "scenePath") ?? string.Empty },
                    { "statistics", statistics ?? new Dictionary<string, object>() },
                    { "prefabUsageTotal", prefabUsage?.Count ?? 0 },
                    { "prefabUsageTop", topPrefabUsage },
                    { "materialUsageTotal", materialUsage?.Count ?? 0 },
                    { "materialUsageTop", topMaterials },
                    { "path", filePath }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }
    }
}
