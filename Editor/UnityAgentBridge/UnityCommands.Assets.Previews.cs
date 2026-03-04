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

        #region Asset Preview Methods

        public static string GetPrefabGeometry(string jsonData)
        {
            var request = JsonUtility.FromJson<PrefabGeometryRequest>(jsonData);
            if (request == null || string.IsNullOrWhiteSpace(request.path))
            {
                return JsonError("Prefab path is required");
            }

            if (ValidateAssetPath(request.path) == null)
            {
                return JsonError($"Invalid path: {request.path}");
            }

            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(request.path);
            if (prefabAsset == null)
            {
                return JsonError($"Prefab not found: {request.path}");
            }

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(request.path);

                var renderers = root.GetComponentsInChildren<Renderer>(true);
                var colliders3D = root.GetComponentsInChildren<Collider>(true);
                var colliders2D = root.GetComponentsInChildren<Collider2D>(true);
                var meshFilters = root.GetComponentsInChildren<MeshFilter>(true);

                var hasRendererBounds = false;
                var rendererBounds = new Bounds(Vector3.zero, Vector3.zero);
                foreach (var renderer in renderers)
                {
                    if (!hasRendererBounds)
                    {
                        rendererBounds = renderer.bounds;
                        hasRendererBounds = true;
                    }
                    else
                    {
                        rendererBounds.Encapsulate(renderer.bounds);
                    }
                }

                // Fallback: LoadPrefabContents doesn't render, so renderer.bounds
                // may be zero. Compute from MeshFilter.sharedMesh.bounds instead.
                if (hasRendererBounds && rendererBounds.size == Vector3.zero && meshFilters.Length > 0)
                {
                    hasRendererBounds = false;
                    foreach (var mf in meshFilters)
                    {
                        if (mf.sharedMesh == null) continue;
                        // Transform local mesh bounds to world space
                        var meshBounds = mf.sharedMesh.bounds;
                        var worldCenter = mf.transform.TransformPoint(meshBounds.center);
                        var worldSize = Vector3.Scale(meshBounds.size, mf.transform.lossyScale);
                        var worldBounds = new Bounds(worldCenter, worldSize);
                        if (!hasRendererBounds)
                        {
                            rendererBounds = worldBounds;
                            hasRendererBounds = true;
                        }
                        else
                        {
                            rendererBounds.Encapsulate(worldBounds);
                        }
                    }
                }

	                var hasColliderBounds = false;
	                var colliderBounds = new Bounds(Vector3.zero, Vector3.zero);
	                foreach (var collider in colliders3D)
	                {
	                    if (!TryGetSafeColliderBounds(collider, out var cb)) continue;
	                    if (!hasColliderBounds)
	                    {
	                        colliderBounds = cb;
	                        hasColliderBounds = true;
	                    }
	                    else
	                    {
	                        colliderBounds.Encapsulate(cb);
	                    }
	                }

                foreach (var collider in colliders2D)
                {
                    if (!hasColliderBounds)
                    {
                        colliderBounds = collider.bounds;
                        hasColliderBounds = true;
                    }
                    else
                    {
                        colliderBounds.Encapsulate(collider.bounds);
                    }
                }

                float recommendedGroundOffset = 0f;
                if (hasRendererBounds)
                {
                    recommendedGroundOffset = -rendererBounds.min.y;
                }
                else if (hasColliderBounds)
                {
                    recommendedGroundOffset = -colliderBounds.min.y;
                }

                var socketPrefixes = (request.socketPrefixes != null && request.socketPrefixes.Length > 0)
                    ? request.socketPrefixes
                    : new[] { "Socket_", "Snap_", "Attach_", "Connector_" };

                var connectors = new List<object>();
                if (request.includeSockets != 0)
                {
                    foreach (var tr in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (tr == root.transform) continue;

                        bool isConnector = socketPrefixes.Any(prefix =>
                            !string.IsNullOrWhiteSpace(prefix)
                            && tr.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                        if (!isConnector) continue;

                        connectors.Add(new Dictionary<string, object>
                        {
                            { "name", tr.name },
                            { "path", GetTransformPath(root.transform, tr) },
                            { "localPosition", new List<object> { tr.localPosition.x, tr.localPosition.y, tr.localPosition.z } },
                            { "localRotation", new List<object> { tr.localEulerAngles.x, tr.localEulerAngles.y, tr.localEulerAngles.z } },
                            { "localScale", new List<object> { tr.localScale.x, tr.localScale.y, tr.localScale.z } },
                            { "forward", new List<object> { tr.forward.x, tr.forward.y, tr.forward.z } }
                        });
                    }
                }

                var children = new List<object>();
                if (request.includeChildren != 0)
                {
                    foreach (Transform child in root.transform)
                    {
                        children.Add(new Dictionary<string, object>
                        {
                            { "name", child.name },
                            { "path", GetTransformPath(root.transform, child) },
                            { "localPosition", new List<object> { child.localPosition.x, child.localPosition.y, child.localPosition.z } },
                            { "localRotation", new List<object> { child.localEulerAngles.x, child.localEulerAngles.y, child.localEulerAngles.z } },
                            { "localScale", new List<object> { child.localScale.x, child.localScale.y, child.localScale.z } }
                        });
                    }
                }

                var dependencies = AssetDatabase.GetDependencies(request.path, false)
                    .Where(dep => dep != request.path)
                    .Cast<object>()
                    .ToList();

                // Accurate bounds via temp-instantiate (renderer.bounds are valid in world space)
                object accurateBoundsJson = null;
                if (request.includeAccurateBounds == 1)
                {
                    var prefabAssetForInstantiate = AssetDatabase.LoadAssetAtPath<GameObject>(request.path);
                    if (prefabAssetForInstantiate != null)
                    {
                        var wasActive = prefabAssetForInstantiate.activeSelf;
                        prefabAssetForInstantiate.SetActive(false);
                        var temp = GameObject.Instantiate(prefabAssetForInstantiate);
                        prefabAssetForInstantiate.SetActive(wasActive);
                        temp.SetActive(true);
                        var tempRenderers = temp.GetComponentsInChildren<Renderer>(true);
                        if (tempRenderers.Length > 0)
                        {
                            var ab = tempRenderers[0].bounds;
                            for (int i = 1; i < tempRenderers.Length; i++)
                                ab.Encapsulate(tempRenderers[i].bounds);
                            accurateBoundsJson = BoundsToJson(ab);
                        }
                        GameObject.DestroyImmediate(temp);
                    }
                }

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "path", request.path },
                    { "name", root.name },
                    { "rendererCount", renderers.Length },
                    { "colliderCount3D", colliders3D.Length },
                    { "colliderCount2D", colliders2D.Length },
                    { "meshCount", meshFilters.Count(m => m.sharedMesh != null) },
                    { "recommendedGroundOffset", recommendedGroundOffset },
                    { "bounds", hasRendererBounds ? BoundsToJson(rendererBounds) : null },
                    { "accurateBounds", accurateBoundsJson },
                    { "colliderBounds", hasColliderBounds ? BoundsToJson(colliderBounds) : null },
                    { "connectors", connectors },
                    { "children", children },
                    { "dependencies", dependencies }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
            finally
            {
                if (root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        public static string GetPrefabFootprint2D(string jsonData)
        {
            var request = JsonUtility.FromJson<PrefabFootprint2DRequest>(jsonData ?? "{}");
            if (request == null || string.IsNullOrWhiteSpace(request.path))
            {
                return JsonError("Prefab path is required");
            }

            if (ValidateAssetPath(request.path) == null)
            {
                return JsonError($"Invalid path: {request.path}");
            }

            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(request.path);
            if (prefabAsset == null)
            {
                return JsonError($"Prefab not found: {request.path}");
            }

            var samplingSource = NormalizeFootprintSource(request.source);
            var maxPoints = Mathf.Clamp(request.maxPoints <= 0 ? 6000 : request.maxPoints, 128, 50000);
            var targetMinEdgeGap = Mathf.Max(0f, request.targetMinEdgeGap);

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(request.path);

                var localPoints = new List<Vector3>();
                int meshCount = 0;
                int colliderCount3D = 0;
                int colliderCount2D = 0;
                int rendererCount = 0;

                if (samplingSource == "mesh" || samplingSource == "hybrid")
                {
                    CollectPrefabMeshPoints(root, maxPoints, localPoints, out meshCount);
                }

                bool hasGeometrySamples = localPoints.Count > 0;

                if ((samplingSource == "collider" || (samplingSource == "hybrid" && !hasGeometrySamples)) && localPoints.Count < maxPoints)
                {
                    CollectPrefabColliderPoints(root, maxPoints, localPoints, out colliderCount3D, out colliderCount2D);
                    hasGeometrySamples = localPoints.Count > 0;
                }

                if ((samplingSource == "rendererbounds" || (samplingSource == "hybrid" && !hasGeometrySamples)) && localPoints.Count < maxPoints)
                {
                    CollectPrefabRendererBoundsPoints(root, maxPoints, localPoints, out rendererCount);
                }

                if (localPoints.Count == 0)
                {
                    return JsonError("Failed to sample prefab footprint points. Prefab may have no renderable/collider geometry.");
                }

                var projectedPoints = localPoints
                    .Select(p => new Vector2(p.x, p.z))
                    .ToList();

                var hull = ComputeConvexHull2D(projectedPoints);
                if (hull.Count == 0)
                {
                    return JsonError("Could not compute footprint hull");
                }

                var boundsRect = ComputeBoundsRect2D(projectedPoints);
                float area = Mathf.Abs(ComputePolygonArea2D(hull));
                float perimeter = ComputePolygonPerimeter2D(hull);
                float maxDiameter = ComputeMaxPointDistance2D(hull);

                float width = boundsRect.width;
                float depth = boundsRect.height;
                bool hexLike =
                    hull.Count >= 6 &&
                    hull.Count <= 12 &&
                    width > 0.0001f &&
                    depth > 0.0001f &&
                    Mathf.Abs(width - depth) <= Mathf.Max(width, depth) * 0.45f;

                float laneCenterSpacing = width + targetMinEdgeGap;
                float forwardCenterSpacing = depth + targetMinEdgeGap;
                float interlockForwardStep = hexLike
                    ? (depth * 0.75f) + targetMinEdgeGap
                    : forwardCenterSpacing;
                float rowStaggerOffset = width * 0.5f;
                float curvatureWideningBase = Mathf.Clamp(
                    0.05f + (targetMinEdgeGap / Mathf.Max(0.0001f, Mathf.Max(width, depth))) * 0.2f,
                    0.03f,
                    0.4f);

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "path", request.path },
                    { "name", root.name },
                    { "source", samplingSource },
                    { "hexLike", hexLike },
                    { "sampling", new Dictionary<string, object>
                        {
                            { "pointCount", projectedPoints.Count },
                            { "meshCount", meshCount },
                            { "colliderCount3D", colliderCount3D },
                            { "colliderCount2D", colliderCount2D },
                            { "rendererCount", rendererCount },
                            { "maxPoints", maxPoints }
                        }
                    },
                    { "footprint", new Dictionary<string, object>
                        {
                            { "bounds2D", BoundsRectToJson(boundsRect) },
                            { "area", area },
                            { "perimeter", perimeter },
                            { "maxDiameter", maxDiameter }
                        }
                    },
                    { "recommendedPlacement", new Dictionary<string, object>
                        {
                            { "targetMinEdgeGap", targetMinEdgeGap },
                            { "laneCenterSpacing", laneCenterSpacing },
                            { "forwardCenterSpacing", forwardCenterSpacing },
                            { "rowStaggerOffset", rowStaggerOffset },
                            { "interlockForwardStep", interlockForwardStep },
                            { "curvatureWideningBase", curvatureWideningBase },
                            { "notes", new List<object>
                                {
                                    "Use laneCenterSpacing/forwardCenterSpacing as minimum center distances.",
                                    "For tighter curves, multiply lane spacing by (1 + curvatureWideningBase * curveMagnitude).",
                                    hexLike ? "Hex-like footprint detected; interlockForwardStep is recommended for staggered rows." : "Non-hex footprint detected; use forwardCenterSpacing directly."
                                }
                            }
                        }
                    }
                };

                if (request.includeHull != 0)
                {
                    response["hull"] = hull
                        .Select(v => (object)new List<object> { v.x, v.y })
                        .ToList();
                }

                if (request.includeSamplePoints != 0)
                {
                    response["samplePoints"] = projectedPoints
                        .Take(1024)
                        .Select(v => (object)new List<object> { v.x, v.y })
                        .ToList();
                    response["samplePointsTruncated"] = projectedPoints.Count > 1024;
                }

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
            finally
            {
                if (root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        public static string GetPrefabPreview(string prefabPath, int size = 128)
        {
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null)
                {
                    return JsonUtility.ToJson(new PrefabPreviewResponse { success = false, error = "Prefab not found" });
                }

                // Get Unity's built-in asset preview
                var preview = AssetPreview.GetAssetPreview(prefab);

                // If preview not ready, use mini thumbnail
                if (preview == null)
                {
                    AssetPreview.SetPreviewTextureCacheSize(100);
                    preview = AssetPreview.GetMiniThumbnail(prefab);
                }

                if (preview == null)
                {
                    return JsonUtility.ToJson(new PrefabPreviewResponse { success = false, error = "Preview not available" });
                }

                // Resize if needed
                var resized = ResizeTexture(preview, size, size);
                var bytes = resized.EncodeToPNG();
                var base64 = Convert.ToBase64String(bytes);

                // Clean up
                if (resized != preview)
                {
                    UnityEngine.Object.DestroyImmediate(resized);
                }

                return JsonUtility.ToJson(new PrefabPreviewResponse
                {
                    success = true,
                    prefabPath = prefabPath,
                    prefabName = prefab.name,
                    width = size,
                    height = size,
                    base64 = base64
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new PrefabPreviewResponse { success = false, error = ex.Message });
            }
        }

        public static string GetPrefabPreviews(string searchQuery, int maxResults = 20, int size = 64, bool includeThumbnails = false)
        {
            try
            {
                var guids = AssetDatabase.FindAssets($"t:Prefab {searchQuery}");
                var results = new PrefabPreviewsResponse();

                foreach (var guid in guids.Take(maxResults))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) continue;

                    string base64 = null;
                    if (includeThumbnails)
                    {
                        var preview = AssetPreview.GetMiniThumbnail(prefab);
                        if (preview != null)
                        {
                            var resized = ResizeTexture(preview, size, size);
                            base64 = Convert.ToBase64String(resized.EncodeToPNG());
                            if (resized != preview)
                            {
                                UnityEngine.Object.DestroyImmediate(resized);
                            }
                        }
                    }

                    results.prefabs.Add(new PrefabPreviewInfo
                    {
                        name = prefab.name,
                        path = path,
                        thumbnail = base64
                    });
                }

                return JsonUtility.ToJson(results, false);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        public static string GetMaterialPreview(string materialPath, int size = 128)
        {
            try
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    return JsonUtility.ToJson(new MaterialPreviewResponse { success = false, error = "Material not found" });
                }

                var preview = AssetPreview.GetAssetPreview(material);
                if (preview == null)
                {
                    preview = AssetPreview.GetMiniThumbnail(material);
                }

                if (preview == null)
                {
                    return JsonUtility.ToJson(new MaterialPreviewResponse { success = false, error = "Preview not available" });
                }

                var resized = ResizeTexture(preview, size, size);
                var bytes = resized.EncodeToPNG();
                var base64 = Convert.ToBase64String(bytes);

                if (resized != preview)
                {
                    UnityEngine.Object.DestroyImmediate(resized);
                }

                return JsonUtility.ToJson(new MaterialPreviewResponse
                {
                    success = true,
                    materialPath = materialPath,
                    materialName = material.name,
                    shaderName = material.shader.name,
                    width = size,
                    height = size,
                    base64 = base64
                });
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new MaterialPreviewResponse { success = false, error = ex.Message });
            }
        }

        public static string GetMaterialPreviews(string searchQuery, int maxResults = 20, int size = 64, bool includeThumbnails = false)
        {
            try
            {
                var guids = AssetDatabase.FindAssets($"t:Material {searchQuery}");
                var results = new MaterialPreviewsResponse();

                foreach (var guid in guids.Take(maxResults))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (material == null) continue;

                    string base64 = null;
                    if (includeThumbnails)
                    {
                        var preview = AssetPreview.GetMiniThumbnail(material);
                        if (preview != null)
                        {
                            var resized = ResizeTexture(preview, size, size);
                            base64 = Convert.ToBase64String(resized.EncodeToPNG());
                            if (resized != preview)
                            {
                                UnityEngine.Object.DestroyImmediate(resized);
                            }
                        }
                    }

                    // Get main color if available
                    string mainColor = null;
                    if (material.HasProperty("_Color"))
                    {
                        var color = material.GetColor("_Color");
                        mainColor = $"#{ColorUtility.ToHtmlStringRGBA(color)}";
                    }
                    else if (material.HasProperty("_BaseColor"))
                    {
                        var color = material.GetColor("_BaseColor");
                        mainColor = $"#{ColorUtility.ToHtmlStringRGBA(color)}";
                    }

                    results.materials.Add(new MaterialPreviewInfo
                    {
                        name = material.name,
                        path = path,
                        shaderName = material.shader.name,
                        mainColor = mainColor,
                        thumbnail = base64
                    });
                }

                return JsonUtility.ToJson(results, false);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        public static string GetAssetCatalog(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<AssetCatalogRequest>(jsonData);
                var response = new AssetCatalogResponse { success = true };

                bool includeThumbnails = request.includeThumbnails == 1;

                // Gather prefab previews
                var prefabSearch = request.prefabSearch ?? "";
                var prefabGuids = AssetDatabase.FindAssets($"t:Prefab {prefabSearch}");
                int maxPrefabs = request.maxPrefabs > 0 ? request.maxPrefabs : 30;
                int thumbSize = request.thumbnailSize > 0 ? request.thumbnailSize : 64;
                thumbSize = Mathf.Clamp(thumbSize, 32, 128);

                foreach (var guid in prefabGuids.Take(maxPrefabs))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null) continue;

                    string base64 = null;
                    if (includeThumbnails)
                    {
                        var preview = AssetPreview.GetMiniThumbnail(prefab);
                        if (preview != null)
                        {
                            var resized = ResizeTexture(preview, thumbSize, thumbSize);
                            base64 = Convert.ToBase64String(resized.EncodeToPNG());
                            if (resized != preview) UnityEngine.Object.DestroyImmediate(resized);
                        }
                    }

                    response.prefabs.Add(new AssetCatalogPrefabInfo
                    {
                        name = prefab.name,
                        path = path,
                        thumbnail = base64
                    });
                }

                // Gather material previews
                var materialSearch = request.materialSearch ?? "";
                var materialGuids = AssetDatabase.FindAssets($"t:Material {materialSearch}");
                int maxMaterials = request.maxMaterials > 0 ? request.maxMaterials : 30;

                foreach (var guid in materialGuids.Take(maxMaterials))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (material == null) continue;

                    string base64 = null;
                    if (includeThumbnails)
                    {
                        var preview = AssetPreview.GetMiniThumbnail(material);
                        if (preview != null)
                        {
                            var resized = ResizeTexture(preview, thumbSize, thumbSize);
                            base64 = Convert.ToBase64String(resized.EncodeToPNG());
                            if (resized != preview) UnityEngine.Object.DestroyImmediate(resized);
                        }
                    }

                    string mainColor = null;
                    if (material.HasProperty("_Color"))
                    {
                        mainColor = $"#{ColorUtility.ToHtmlStringRGBA(material.GetColor("_Color"))}";
                    }
                    else if (material.HasProperty("_BaseColor"))
                    {
                        mainColor = $"#{ColorUtility.ToHtmlStringRGBA(material.GetColor("_BaseColor"))}";
                    }

                    response.materials.Add(new AssetCatalogMaterialInfo
                    {
                        name = material.name,
                        path = path,
                        shaderName = material.shader.name,
                        mainColor = mainColor,
                        thumbnail = base64
                    });
                }

                // Optionally gather shaders
                if (request.includeShaders == 1)
                {
                    var shaderGuids = AssetDatabase.FindAssets("t:Shader");
                    foreach (var guid in shaderGuids)
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                        if (shader == null) continue;

                        response.shaders.Add(new AssetCatalogShaderInfo
                        {
                            name = shader.name,
                            path = path
                        });
                    }
                }

                return JsonUtility.ToJson(response, false);
            }
            catch (Exception ex)
            {
                return JsonUtility.ToJson(new AssetCatalogResponse { success = false, error = ex.Message });
            }
        }

        private static Texture2D ResizeTexture(Texture2D source, int targetWidth, int targetHeight)
        {
            if (source.width == targetWidth && source.height == targetHeight && source.isReadable)
            {
                return source;
            }

            // Create a temporary RenderTexture
            RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;

            // Blit the source texture to the render texture
            RenderTexture.active = rt;
            Graphics.Blit(source, rt);

            // Read the pixels from the render texture
            Texture2D result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
            result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
            result.Apply();

            // Clean up
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);

            return result;
        }

        #endregion

    }
}
