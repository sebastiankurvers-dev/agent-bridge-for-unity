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
        #region Reparent Operations

        [BridgeRoute("POST", "/reparent", Category = "gameobjects", Description = "Reparent a GameObject")]
        public static string ReparentGameObject(string jsonData)
        {
            var request = JsonUtility.FromJson<ReparentRequest>(jsonData);

            var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            Transform newParent = null;
            if (request.newParentId != 0)
            {
                var parentGo = EditorUtility.EntityIdToObject(request.newParentId) as GameObject;
                if (parentGo == null)
                {
                    return JsonError("Parent GameObject not found");
                }
                newParent = parentGo.transform;
            }

            try
            {
                Undo.SetTransformParent(go.transform, newParent, "Agent Bridge Reparent");

                if (request.siblingIndex >= 0)
                {
                    go.transform.SetSiblingIndex(request.siblingIndex);
                }

                if (request.worldPositionStays == 0)
                {
                    // Position was already handled by SetTransformParent
                }

                EditorUtility.SetDirty(go);

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "instanceId", go.GetInstanceID() }, { "newParent", newParent != null ? newParent.gameObject.name : "Scene Root" }, { "message", $"Reparented {go.name}" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        #endregion

        #region Batch Modify Children

        [BridgeRoute("POST", "/gameobject/batch-modify-children", Category = "gameobjects", Description = "Bulk-modify children transform/active/tag/layer", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string BatchModifyChildren(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<BatchModifyChildrenRequest>(
                    string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData);
                if (request == null || request.parentInstanceId == 0)
                    return JsonError("parentInstanceId is required");

                // JsonUtility.FromJson always creates nested reference-type objects
                // (Vector3Data) with default (0,0,0), even when absent from JSON.
                // Parse raw JSON to detect which keys were actually sent.
                var rawKeys = new HashSet<string>();
                var rawDict = MiniJSON.Json.Deserialize(jsonData ?? "{}") as Dictionary<string, object>;
                if (rawDict != null)
                    foreach (var key in rawDict.Keys)
                        rawKeys.Add(key);

                bool hasLocalPosition = rawKeys.Contains("localPosition");
                bool hasLocalScale = rawKeys.Contains("localScale");
                bool hasRotation = rawKeys.Contains("rotation");

                var parent = EditorUtility.EntityIdToObject(request.parentInstanceId) as GameObject;
                if (parent == null)
                    return JsonError("Parent GameObject not found for instanceId " + request.parentInstanceId);

                bool recursive = request.recursive != 0;
                string nameContains = request.nameContains ?? "";
                string tagFilter = request.tag ?? "";
                string componentFilter = request.componentType ?? "";

                // Collect children
                var children = new List<Transform>();
                if (recursive)
                {
                    CollectDescendants(parent.transform, children);
                }
                else
                {
                    for (int i = 0; i < parent.transform.childCount; i++)
                        children.Add(parent.transform.GetChild(i));
                }

                // Apply filters (AND logic)
                if (!string.IsNullOrWhiteSpace(nameContains))
                {
                    children = children.Where(t =>
                        t.gameObject.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                }

                if (!string.IsNullOrWhiteSpace(tagFilter))
                {
                    children = children.Where(t =>
                    {
                        try { return string.Equals(t.gameObject.tag, tagFilter, StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    }).ToList();
                }

                if (!string.IsNullOrWhiteSpace(componentFilter))
                {
                    var compType = TypeResolver.FindComponentType(componentFilter);
                    if (compType == null)
                        return JsonError($"Component type not found: {componentFilter}");
                    children = children.Where(t => t.gameObject.GetComponent(compType) != null).ToList();
                }

                int matchCount = children.Count;
                if (matchCount == 0)
                {
                    return JsonResult(new Dictionary<string, object>
                    {
                        { "success", true },
                        { "matchCount", 0 },
                        { "modifiedCount", 0 },
                        { "message", "No children matched the filters" }
                    });
                }

                Undo.SetCurrentGroupName("Batch Modify Children");
                int undoGroup = Undo.GetCurrentGroup();

                int modifiedCount = 0;
                var sample = new List<object>();

                foreach (var child in children)
                {
                    var go = child.gameObject;
                    var before = new Dictionary<string, object>
                    {
                        { "position", new float[] { child.position.x, child.position.y, child.position.z } },
                        { "active", go.activeSelf }
                    };

                    bool changed = false;

                    Undo.RecordObject(child, "Batch Modify Child Transform");
                    Undo.RecordObject(go, "Batch Modify Child GO");

                    // Individual axis position overrides (world space)
                    if (request.positionX > -998f)
                    {
                        var pos = child.position;
                        pos.x = request.positionX;
                        child.position = pos;
                        changed = true;
                    }
                    if (request.positionY > -998f)
                    {
                        var pos = child.position;
                        pos.y = request.positionY;
                        child.position = pos;
                        changed = true;
                    }
                    if (request.positionZ > -998f)
                    {
                        var pos = child.position;
                        pos.z = request.positionZ;
                        child.position = pos;
                        changed = true;
                    }

                    if (hasLocalPosition && request.localPosition != null)
                    {
                        child.localPosition = request.localPosition.ToVector3();
                        changed = true;
                    }

                    if (hasRotation && request.rotation != null)
                    {
                        child.eulerAngles = request.rotation.ToVector3();
                        changed = true;
                    }

                    if (hasLocalScale && request.localScale != null)
                    {
                        child.localScale = request.localScale.ToVector3();
                        changed = true;
                    }

                    if (request.active >= 0)
                    {
                        go.SetActive(request.active == 1);
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(request.setTag))
                    {
                        go.tag = request.setTag;
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(request.setLayer))
                    {
                        int layerIdx = LayerMask.NameToLayer(request.setLayer);
                        if (layerIdx >= 0)
                        {
                            go.layer = layerIdx;
                            changed = true;
                        }
                    }

                    if (changed)
                    {
                        EditorUtility.SetDirty(go);
                        modifiedCount++;

                        if (sample.Count < 5)
                        {
                            sample.Add(new Dictionary<string, object>
                            {
                                { "name", go.name },
                                { "instanceId", go.GetInstanceID() },
                                { "before", before },
                                { "after", new Dictionary<string, object>
                                    {
                                        { "position", new float[] { child.position.x, child.position.y, child.position.z } },
                                        { "active", go.activeSelf }
                                    }
                                }
                            });
                        }
                    }
                }

                Undo.CollapseUndoOperations(undoGroup);

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "matchCount", matchCount },
                    { "modifiedCount", modifiedCount },
                    { "sample", sample }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static void CollectDescendants(Transform parent, List<Transform> result)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                result.Add(child);
                CollectDescendants(child, result);
            }
        }

        #endregion

        #region Group Objects

        [BridgeRoute("POST", "/group", Category = "gameobjects", Description = "Create an empty parent and group multiple objects under it")]
        public static string GroupObjects(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<GroupObjectsRequest>(jsonData);
                if (request.instanceIds == null || request.instanceIds.Length == 0)
                    return JsonError("instanceIds array is required");

                var objects = new List<GameObject>();
                foreach (var id in request.instanceIds)
                {
                    var go = EditorUtility.EntityIdToObject(id) as GameObject;
                    if (go != null) objects.Add(go);
                }

                if (objects.Count == 0)
                    return JsonError("No valid GameObjects found for the given instanceIds");

                string groupName = request.name ?? "Group";
                var groupGo = new GameObject(groupName);
                Undo.RegisterCreatedObjectUndo(groupGo, "Agent Bridge Group Objects");

                // Position at center of children
                if (request.centerOnChildren == 1 && objects.Count > 0)
                {
                    var center = Vector3.zero;
                    foreach (var go in objects) center += go.transform.position;
                    center /= objects.Count;
                    groupGo.transform.position = center;
                }

                // Set parent of group if specified
                if (request.parentId != 0)
                {
                    var parent = EditorUtility.EntityIdToObject(request.parentId) as GameObject;
                    if (parent != null)
                        groupGo.transform.SetParent(parent.transform, true);
                }

                // Reparent objects under the group
                foreach (var go in objects)
                {
                    Undo.SetTransformParent(go.transform, groupGo.transform, "Agent Bridge Group Reparent");
                }

                EditorUtility.SetDirty(groupGo);

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", groupGo.GetInstanceID() },
                    { "name", groupGo.name },
                    { "childCount", objects.Count },
                    { "message", $"Grouped {objects.Count} objects under '{groupName}'" }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        #endregion

        #region Scatter Objects

        [BridgeRoute("POST", "/scatter", Category = "gameobjects", Description = "Scatter copies of an object or prefab within a bounding box with randomized transforms")]
        public static string ScatterObjects(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<ScatterObjectsRequest>(NormalizeColorFields(jsonData));
                int count = Mathf.Clamp(request.count, 1, 200);

                if (request.boundsCenter == null || request.boundsCenter.Length < 3)
                    return JsonError("boundsCenter [x,y,z] is required");
                if (request.boundsSize == null || request.boundsSize.Length < 3)
                    return JsonError("boundsSize [x,y,z] is required");

                var center = new Vector3(request.boundsCenter[0], request.boundsCenter[1], request.boundsCenter[2]);
                var size = new Vector3(request.boundsSize[0], request.boundsSize[1], request.boundsSize[2]);
                var halfSize = size * 0.5f;

                // Determine source
                GameObject sourcePrefab = null;
                GameObject sourceScene = null;
                bool usePrefab = false;

                if (!string.IsNullOrWhiteSpace(request.prefabPath))
                {
                    sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(request.prefabPath);
                    if (sourcePrefab == null)
                        return JsonError($"Prefab not found: {request.prefabPath}");
                    usePrefab = true;
                }
                else if (request.sourceInstanceId != 0)
                {
                    sourceScene = EditorUtility.EntityIdToObject(request.sourceInstanceId) as GameObject;
                    if (sourceScene == null)
                        return JsonError("Source GameObject not found");
                }
                else
                {
                    return JsonError("Either sourceInstanceId or prefabPath is required");
                }

                Transform parentTransform = null;
                if (request.parentId != 0)
                {
                    var parent = EditorUtility.EntityIdToObject(request.parentId) as GameObject;
                    if (parent != null) parentTransform = parent.transform;
                }

                var rng = request.seed != 0 ? new System.Random(request.seed) : new System.Random();
                float scaleMin = Mathf.Max(0.01f, request.scaleMin);
                float scaleMax = Mathf.Max(scaleMin, request.scaleMax);
                bool uniformScale = request.uniformScale == 1;

                var rotMin = request.rotationMin != null && request.rotationMin.Length >= 3
                    ? new Vector3(request.rotationMin[0], request.rotationMin[1], request.rotationMin[2])
                    : Vector3.zero;
                var rotMax = request.rotationMax != null && request.rotationMax.Length >= 3
                    ? new Vector3(request.rotationMax[0], request.rotationMax[1], request.rotationMax[2])
                    : Vector3.zero;

                Undo.SetCurrentGroupName("Agent Bridge Scatter Objects");
                int undoGroup = Undo.GetCurrentGroup();

                string baseName = request.name ?? (usePrefab ? sourcePrefab.name : sourceScene.name);
                var created = new List<Dictionary<string, object>>();

                // Prepare shared color material
                Material sharedMat = null;
                if (request.color != null && request.color.Length >= 3)
                {
                    var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                    sharedMat = new Material(shader);
                    var color = new Color(request.color[0], request.color[1], request.color[2],
                        request.color.Length >= 4 ? request.color[3] : 1f);
                    sharedMat.color = color;
                    if (sharedMat.HasProperty("_BaseColor")) sharedMat.SetColor("_BaseColor", color);
                }

                for (int i = 0; i < count; i++)
                {
                    GameObject instance;
                    if (usePrefab)
                    {
                        instance = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab);
                    }
                    else
                    {
                        instance = UnityEngine.Object.Instantiate(sourceScene);
                    }

                    Undo.RegisterCreatedObjectUndo(instance, "Scatter");
                    instance.name = $"{baseName}_{i}";

                    // Random position within bounds
                    float px = center.x + (float)(rng.NextDouble() * 2 - 1) * halfSize.x;
                    float py = center.y + (float)(rng.NextDouble() * 2 - 1) * halfSize.y;
                    float pz = center.z + (float)(rng.NextDouble() * 2 - 1) * halfSize.z;
                    instance.transform.position = new Vector3(px, py, pz);

                    // Random rotation
                    float rx = Mathf.Lerp(rotMin.x, rotMax.x, (float)rng.NextDouble());
                    float ry = Mathf.Lerp(rotMin.y, rotMax.y, (float)rng.NextDouble());
                    float rz = Mathf.Lerp(rotMin.z, rotMax.z, (float)rng.NextDouble());
                    instance.transform.eulerAngles = new Vector3(rx, ry, rz);

                    // Random scale
                    if (uniformScale)
                    {
                        float s = Mathf.Lerp(scaleMin, scaleMax, (float)rng.NextDouble());
                        instance.transform.localScale = new Vector3(s, s, s);
                    }
                    else
                    {
                        float sx = Mathf.Lerp(scaleMin, scaleMax, (float)rng.NextDouble());
                        float sy = Mathf.Lerp(scaleMin, scaleMax, (float)rng.NextDouble());
                        float sz = Mathf.Lerp(scaleMin, scaleMax, (float)rng.NextDouble());
                        instance.transform.localScale = new Vector3(sx, sy, sz);
                    }

                    if (parentTransform != null)
                        instance.transform.SetParent(parentTransform, true);

                    if (sharedMat != null)
                    {
                        var renderer = instance.GetComponent<Renderer>();
                        if (renderer != null) renderer.sharedMaterial = sharedMat;
                    }

                    created.Add(new Dictionary<string, object>
                    {
                        { "instanceId", instance.GetInstanceID() },
                        { "name", instance.name }
                    });
                }

                Undo.CollapseUndoOperations(undoGroup);

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "count", created.Count },
                    { "objects", created.Take(10).Cast<object>().ToList() },
                    { "message", $"Scattered {created.Count} objects within bounds" }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        #endregion

        #region ScriptableObject Operations

        [BridgeRoute("POST", "/scriptableobject", Category = "assets", Description = "Create ScriptableObject")]
        public static string CreateScriptableObject(string jsonData)
        {
            var request = JsonUtility.FromJson<ScriptableObjectRequest>(jsonData);

            if (string.IsNullOrEmpty(request.typeName))
            {
                return JsonError("ScriptableObject type is required");
            }

            var type = TypeResolver.FindScriptableObjectType(request.typeName);
            if (type == null)
            {
                return JsonError($"ScriptableObject type not found: {request.typeName}");
            }

            var path = request.savePath;
            if (string.IsNullOrEmpty(path))
            {
                path = $"Assets/{request.typeName}.asset";
            }
            if (!path.StartsWith("Assets/"))
            {
                path = "Assets/" + path;
            }
            if (!path.EndsWith(".asset"))
            {
                path += ".asset";
            }

            if (ValidateAssetPath(path) == null)
            {
                return JsonError("Path is outside the project directory");
            }

            try
            {
                var instance = ScriptableObject.CreateInstance(type);

                // Apply properties if provided
                if (!string.IsNullOrEmpty(request.properties))
                {
                    JsonUtility.FromJsonOverwrite(request.properties, instance);
                }

                // Create directory if needed
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                AssetDatabase.CreateAsset(instance, path);
                AssetDatabase.SaveAssets();

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "path", path }, { "typeName", type.Name }, { "message", $"Created ScriptableObject at {path}" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        #endregion
    }
}
