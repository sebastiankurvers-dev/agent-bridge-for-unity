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

        public static string FindPrefabs(string searchQuery, int maxResults = 100)
        {
            var prefabs = new List<PrefabInfo>();
            var filter = string.IsNullOrWhiteSpace(searchQuery) ? "t:Prefab" : $"t:Prefab {searchQuery}";
            var guids = AssetDatabase.FindAssets(filter);
            maxResults = Math.Clamp(maxResults, 1, 5000);

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var name = System.IO.Path.GetFileNameWithoutExtension(path);

                if (string.IsNullOrEmpty(searchQuery) ||
                    name.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    prefabs.Add(new PrefabInfo
                    {
                        name = name,
                        path = path
                    });
                    if (prefabs.Count >= maxResults)
                    {
                        break;
                    }
                }
            }

            var jsonParts = prefabs.Select(p => JsonUtility.ToJson(p));
            return "{\"prefabs\":[" + string.Join(",", jsonParts) + "]}";
        }

        public static string FindPrefabsScoped(
            string searchQuery,
            string[] includeRoots = null,
            string[] excludeRoots = null,
            bool includeSubfolders = true,
            int maxResults = 200,
            string matchMode = "contains")
        {
            try
            {
                var include = NormalizeScopeRoots(includeRoots);
                var exclude = NormalizeScopeRoots(excludeRoots);
                maxResults = Math.Clamp(maxResults, 1, 5000);

                var matches = new List<SearchMatch<PrefabInfo>>();
                var guids = AssetDatabase.FindAssets("t:Prefab");

                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!PassesScope(path, include, exclude, includeSubfolders))
                    {
                        continue;
                    }

                    var name = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (!TryScoreMatch(searchQuery, name, path, matchMode, out int score))
                    {
                        continue;
                    }

                    matches.Add(new SearchMatch<PrefabInfo>
                    {
                        score = score,
                        item = new PrefabInfo
                        {
                            name = name,
                            path = path
                        }
                    });
                }

                var ordered = matches
                    .OrderBy(m => m.score)
                    .ThenBy(m => m.item.name, StringComparer.OrdinalIgnoreCase)
                    .Take(maxResults)
                    .Select(m => new Dictionary<string, object>
                    {
                        { "name", m.item.name },
                        { "path", m.item.path }
                    })
                    .ToList<object>();

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefabs", ordered },
                    { "meta", BuildScopedSearchMeta(searchQuery, include, exclude, includeSubfolders, maxResults, matchMode, ordered.Count) }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }


        #region Prefab Operations

        [BridgeRoute("POST", "/prefab", Category = "prefabs", Description = "Create prefab from GameObject")]
        public static string CreatePrefab(string jsonData)
        {
            var request = JsonUtility.FromJson<PrefabRequest>(jsonData);

            var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            var path = request.savePath;
            if (string.IsNullOrEmpty(path))
            {
                path = $"Assets/Prefabs/{go.name}.prefab";
            }
            if (!path.StartsWith("Assets/"))
            {
                path = "Assets/" + path;
            }
            if (!path.EndsWith(".prefab"))
            {
                path += ".prefab";
            }

            try
            {
                // Create directory if needed
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "path", path }, { "name", prefab.name }, { "message", $"Created prefab at {path}" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("PUT", "/prefab", Category = "prefabs", Description = "Modify prefab")]
        public static string ModifyPrefab(string jsonData)
        {
            var request = JsonUtility.FromJson<PrefabModifyRequest>(jsonData);

            if (string.IsNullOrEmpty(request.prefabPath))
            {
                return JsonError("Prefab path is required");
            }

            try
            {
                var prefabRoot = PrefabUtility.LoadPrefabContents(request.prefabPath);

                // Apply modifications
                if (!string.IsNullOrEmpty(request.name))
                {
                    prefabRoot.name = request.name;
                }

                if (request.addComponents != null)
                {
                    foreach (var componentType in request.addComponents)
                    {
                        var type = TypeResolver.FindComponentType(componentType);
                        if (type != null)
                        {
                            prefabRoot.AddComponent(type);
                        }
                    }
                }

                if (request.removeComponents != null)
                {
                    foreach (var componentType in request.removeComponents)
                    {
                        var type = TypeResolver.FindComponentType(componentType);
                        if (type != null)
                        {
                            var component = prefabRoot.GetComponent(type);
                            if (component != null)
                            {
                                UnityEngine.Object.DestroyImmediate(component);
                            }
                        }
                    }
                }

                PrefabUtility.SaveAsPrefabAsset(prefabRoot, request.prefabPath);
                PrefabUtility.UnloadPrefabContents(prefabRoot);

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "path", request.prefabPath }, { "message", "Prefab modified successfully" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/prefab/apply", Category = "prefabs", Description = "Apply prefab instance overrides back to the prefab asset")]
        public static string ApplyPrefabOverrides(string jsonData)
        {
            var request = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
            if (request == null) return JsonError("Invalid JSON body");

            int instanceId = request.ContainsKey("instanceId") ? Convert.ToInt32(request["instanceId"]) : 0;
            if (instanceId == 0) return JsonError("instanceId is required");

            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null) return JsonError($"GameObject with instanceId {instanceId} not found");

            if (!PrefabUtility.IsPartOfPrefabInstance(go))
                return JsonError("GameObject is not a prefab instance");

            try
            {
                var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                PrefabUtility.ApplyPrefabInstance(go, InteractionMode.UserAction);
                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefabPath", prefabPath },
                    { "message", $"Applied overrides from '{go.name}' to prefab at {prefabPath}" }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/prefab/variant", Category = "prefabs", Description = "Create prefab variant")]
        public static string CreatePrefabVariant(string jsonData)
        {
            var request = JsonUtility.FromJson<PrefabVariantRequest>(jsonData);

            if (string.IsNullOrEmpty(request.basePrefabPath))
            {
                return JsonError("Base prefab path is required");
            }

            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(request.basePrefabPath);
            if (basePrefab == null)
            {
                return JsonError($"Base prefab not found: {request.basePrefabPath}");
            }

            var path = request.savePath;
            if (string.IsNullOrEmpty(path))
            {
                path = request.basePrefabPath.Replace(".prefab", "_Variant.prefab");
            }
            if (!path.StartsWith("Assets/"))
            {
                path = "Assets/" + path;
            }
            if (!path.EndsWith(".prefab"))
            {
                path += ".prefab";
            }

            try
            {
                // Create directory if needed
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
                var variant = PrefabUtility.SaveAsPrefabAsset(instance, path);
                UnityEngine.Object.DestroyImmediate(instance);

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "path", path }, { "basePrefab", request.basePrefabPath }, { "message", $"Created prefab variant at {path}" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("POST", "/import/fbx-to-prefab", Category = "import", Description = "Import FBX and create configured prefab")]
        public static string ImportFbxToPrefab(string jsonData)
        {
            var request = JsonUtility.FromJson<ImportFbxToPrefabRequest>(jsonData);
            if (request == null || string.IsNullOrWhiteSpace(request.fbxPath))
                return JsonError("fbxPath is required");

            if (!request.fbxPath.StartsWith("Assets/"))
                return JsonError("fbxPath must start with Assets/");

            if (ValidateAssetPath(request.fbxPath) == null)
                return JsonError("fbxPath is outside the project directory");

            var fullPath = System.IO.Path.Combine(Application.dataPath, "..", request.fbxPath);
            if (!System.IO.File.Exists(fullPath))
                return JsonError($"FBX file not found: {request.fbxPath}");

            try
            {
                // Force reimport so Unity recognizes the file
                AssetDatabase.ImportAsset(request.fbxPath, ImportAssetOptions.ForceUpdate);

                // Configure the model importer
                var importer = AssetImporter.GetAtPath(request.fbxPath) as ModelImporter;
                if (importer == null)
                    return JsonError($"Failed to get ModelImporter for: {request.fbxPath}");

                importer.globalScale = request.scaleFactor;
                importer.importAnimation = false;
                importer.animationType = ModelImporterAnimationType.None;

                // Material import
                if (request.importMaterials == 0)
                {
                    importer.materialImportMode = ModelImporterMaterialImportMode.None;
                }
                else
                {
                    importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
                    if (!string.IsNullOrEmpty(request.materialLocation) &&
                        request.materialLocation.Equals("External", StringComparison.OrdinalIgnoreCase))
                    {
                        importer.materialLocation = ModelImporterMaterialLocation.External;
                    }
                    else
                    {
                        importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
                    }
                }

                // Collider settings
                if (request.generateColliders == 0)
                    importer.addCollider = false;
                else if (request.generateColliders >= 1)
                    importer.addCollider = true;

                importer.SaveAndReimport();

                // Load the imported model
                var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(request.fbxPath);
                if (modelAsset == null)
                    return JsonError($"Failed to load imported model: {request.fbxPath}");

                // Derive prefab path if not specified
                var prefabPath = request.prefabPath;
                if (string.IsNullOrWhiteSpace(prefabPath))
                {
                    // Strip LOD suffix and change extension
                    prefabPath = request.fbxPath;
                    // Remove _LOD0, _LOD1, etc.
                    var lodPattern = new System.Text.RegularExpressions.Regex(@"_LOD\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    prefabPath = lodPattern.Replace(prefabPath, "");
                    prefabPath = System.IO.Path.ChangeExtension(prefabPath, ".prefab");
                }
                if (!prefabPath.StartsWith("Assets/"))
                    prefabPath = "Assets/" + prefabPath;
                if (!prefabPath.EndsWith(".prefab"))
                    prefabPath += ".prefab";

                // Create directory if needed
                var dir = System.IO.Path.GetDirectoryName(prefabPath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                // Instantiate, optionally add collider, save as prefab
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);

                // Add box collider if requested and not already handled by importer
                if (request.generateColliders == 2)
                {
                    if (instance.GetComponent<BoxCollider>() == null)
                    {
                        var box = instance.AddComponent<BoxCollider>();
                        // Fit to renderer bounds
                        var renderers = instance.GetComponentsInChildren<Renderer>();
                        if (renderers.Length > 0)
                        {
                            var combinedBounds = renderers[0].bounds;
                            for (int i = 1; i < renderers.Length; i++)
                                combinedBounds.Encapsulate(renderers[i].bounds);
                            box.center = instance.transform.InverseTransformPoint(combinedBounds.center);
                            box.size = combinedBounds.size;
                        }
                    }
                }

                var prefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
                UnityEngine.Object.DestroyImmediate(instance);

                // Gather metadata about the created prefab
                var prefabGo = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var prefabRenderers = prefabGo != null ? prefabGo.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
                var materialSet = new HashSet<string>();
                foreach (var r in prefabRenderers)
                {
                    if (r.sharedMaterials != null)
                    {
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m != null)
                                materialSet.Add(m.name);
                        }
                    }
                }

                // Compute bounds from mesh filters (prefab asset doesn't have valid renderer bounds)
                var meshFilters = prefabGo != null ? prefabGo.GetComponentsInChildren<MeshFilter>(true) : new MeshFilter[0];
                var hasBounds = false;
                var totalBounds = new Bounds(Vector3.zero, Vector3.zero);
                foreach (var mf in meshFilters)
                {
                    if (mf.sharedMesh == null) continue;
                    var mb = mf.sharedMesh.bounds;
                    var worldCenter = mf.transform.TransformPoint(mb.center);
                    var worldSize = Vector3.Scale(mb.size, mf.transform.lossyScale);
                    var wb = new Bounds(worldCenter, worldSize);
                    if (!hasBounds) { totalBounds = wb; hasBounds = true; }
                    else totalBounds.Encapsulate(wb);
                }

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefabPath", prefabPath },
                    { "fbxPath", request.fbxPath },
                    { "rendererCount", prefabRenderers.Length },
                    { "materialCount", materialSet.Count },
                    { "materials", materialSet.ToArray() },
                    { "bounds", new Dictionary<string, object>
                        {
                            { "center", new float[] { totalBounds.center.x, totalBounds.center.y, totalBounds.center.z } },
                            { "size", new float[] { totalBounds.size.x, totalBounds.size.y, totalBounds.size.z } }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                return JsonError($"FBX import failed: {ex.Message}");
            }
        }

        #endregion

        #region Asset Management Operations

        [BridgeRoute("POST", "/asset/move", Category = "assets", Description = "Move/rename asset")]
        public static string MoveAsset(string jsonData)
        {
            var request = JsonUtility.FromJson<MoveAssetRequest>(jsonData);
            if (request == null)
                return JsonError("Failed to parse MoveAssetRequest");

            if (ValidateAssetPath(request.sourcePath) == null)
                return JsonError($"Invalid source path: {request.sourcePath}");
            if (ValidateAssetPath(request.destinationPath) == null)
                return JsonError($"Invalid destination path: {request.destinationPath}");

            if (AssetDatabase.LoadMainAssetAtPath(request.sourcePath) == null)
                return JsonError($"Source asset not found: {request.sourcePath}");

            // Ensure destination directory exists
            var destDir = System.IO.Path.GetDirectoryName(request.destinationPath);
            if (!string.IsNullOrEmpty(destDir) && !AssetDatabase.IsValidFolder(destDir))
            {
                CreateFolderRecursive(destDir);
            }

            Undo.SetCurrentGroupName("Agent Bridge: Move Asset");
            var result = AssetDatabase.MoveAsset(request.sourcePath, request.destinationPath);
            if (!string.IsNullOrEmpty(result))
                return JsonError($"Move failed: {result}");

            AssetDatabase.Refresh();

            var assetName = System.IO.Path.GetFileName(request.destinationPath);
            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "oldPath", request.sourcePath },
                { "newPath", request.destinationPath },
                { "assetName", assetName },
                { "message", $"Moved asset to {request.destinationPath}" }
            });
        }

        [BridgeRoute("POST", "/asset/duplicate", Category = "assets", Description = "Duplicate asset")]
        public static string DuplicateAsset(string jsonData)
        {
            var request = JsonUtility.FromJson<DuplicateAssetRequest>(jsonData);
            if (request == null)
                return JsonError("Failed to parse DuplicateAssetRequest");

            if (ValidateAssetPath(request.sourcePath) == null)
                return JsonError($"Invalid source path: {request.sourcePath}");
            if (ValidateAssetPath(request.destinationPath) == null)
                return JsonError($"Invalid destination path: {request.destinationPath}");

            if (AssetDatabase.LoadMainAssetAtPath(request.sourcePath) == null)
                return JsonError($"Source asset not found: {request.sourcePath}");

            // Ensure destination directory exists
            var destDir = System.IO.Path.GetDirectoryName(request.destinationPath);
            if (!string.IsNullOrEmpty(destDir) && !AssetDatabase.IsValidFolder(destDir))
            {
                CreateFolderRecursive(destDir);
            }

            Undo.SetCurrentGroupName("Agent Bridge: Duplicate Asset");
            var success = AssetDatabase.CopyAsset(request.sourcePath, request.destinationPath);
            if (!success)
                return JsonError($"Failed to duplicate asset from {request.sourcePath} to {request.destinationPath}");

            AssetDatabase.Refresh();

            var asset = AssetDatabase.LoadMainAssetAtPath(request.destinationPath);
            var assetName = System.IO.Path.GetFileName(request.destinationPath);
            var typeName = asset != null ? asset.GetType().Name : "Unknown";

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "sourcePath", request.sourcePath },
                { "newPath", request.destinationPath },
                { "assetName", assetName },
                { "type", typeName },
                { "message", $"Duplicated asset to {request.destinationPath}" }
            });
        }

        [BridgeRoute("POST", "/asset/delete", Category = "assets", Description = "Delete asset")]
        public static string DeleteAsset(string jsonData)
        {
            var dict = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
            if (dict == null || !dict.ContainsKey("path"))
                return JsonError("path is required");

            var path = dict["path"] as string;
            if (ValidateAssetPath(path) == null)
                return JsonError($"Invalid path: {path}");

            if (AssetDatabase.LoadMainAssetAtPath(path) == null && !AssetDatabase.IsValidFolder(path))
                return JsonError($"Asset not found: {path}");

            Undo.SetCurrentGroupName("Agent Bridge: Delete Asset");
            var success = AssetDatabase.DeleteAsset(path);
            if (!success)
                return JsonError($"Failed to delete asset: {path}");

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "deletedPath", path },
                { "message", $"Deleted asset at {path}" }
            });
        }

        [BridgeRoute("POST", "/asset/folder", Category = "assets", Description = "Create folder")]
        public static string CreateFolder(string jsonData)
        {
            var dict = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
            if (dict == null || !dict.ContainsKey("path"))
                return JsonError("path is required");

            var path = dict["path"] as string;
            if (ValidateAssetPath(path) == null)
                return JsonError($"Invalid path: {path}");

            if (AssetDatabase.IsValidFolder(path))
            {
                var existingGuid = AssetDatabase.AssetPathToGUID(path);
                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "path", path },
                    { "guid", existingGuid },
                    { "existed", true },
                    { "message", $"Folder already exists: {path}" }
                });
            }

            var guid = CreateFolderRecursive(path);
            if (string.IsNullOrEmpty(guid))
                return JsonError($"Failed to create folder: {path}");

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "guid", guid },
                { "existed", false },
                { "message", $"Created folder at {path}" }
            });
        }

        public static string GetAssetInfo(string jsonData)
        {
            var request = JsonUtility.FromJson<GetAssetInfoRequest>(jsonData);
            if (request == null)
                return JsonError("Failed to parse GetAssetInfoRequest");

            if (ValidateAssetPath(request.path) == null)
                return JsonError($"Invalid path: {request.path}");

            var isFolder = AssetDatabase.IsValidFolder(request.path);
            var asset = AssetDatabase.LoadMainAssetAtPath(request.path);

            if (asset == null && !isFolder)
                return JsonError($"Asset not found: {request.path}");

            var guid = AssetDatabase.AssetPathToGUID(request.path);
            var typeName = isFolder ? "Folder" : (asset != null ? asset.GetType().Name : "Unknown");
            var fullTypeName = isFolder ? "UnityEditor.DefaultAsset" : (asset != null ? asset.GetType().FullName : "Unknown");

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "path", request.path },
                { "guid", guid },
                { "type", typeName },
                { "fullType", fullTypeName },
                { "isFolder", isFolder },
                { "name", System.IO.Path.GetFileName(request.path) }
            };

            // File size
            var fullPath = ValidateAssetPath(request.path);
            if (fullPath != null && System.IO.File.Exists(fullPath))
            {
                var fileInfo = new System.IO.FileInfo(fullPath);
                result["fileSize"] = fileInfo.Length;
            }

            // Dependencies (not for folders)
            if (!isFolder)
            {
                var deps = AssetDatabase.GetDependencies(request.path, false);
                var depList = new List<object>();
                foreach (var dep in deps)
                {
                    if (dep != request.path)
                        depList.Add(dep);
                }
                result["dependencies"] = depList;
            }

            return JsonResult(result);
        }

        private static string CreateFolderRecursive(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return AssetDatabase.AssetPathToGUID(path);

            var parent = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent))
                return null;

            // Normalize separators
            parent = parent.Replace('\\', '/');

            if (!AssetDatabase.IsValidFolder(parent))
            {
                var parentGuid = CreateFolderRecursive(parent);
                if (string.IsNullOrEmpty(parentGuid))
                    return null;
            }

            var folderName = System.IO.Path.GetFileName(path);
            return AssetDatabase.CreateFolder(parent, folderName);
        }

        #endregion

    }
}
