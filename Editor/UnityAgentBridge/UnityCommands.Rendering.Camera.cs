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
        // Camera Configuration, Renderer State & Mesh Info

        public static string GetCameraRendering(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<GetCameraRenderingRequest>(jsonData ?? "{}");
                if (request == null) request = new GetCameraRenderingRequest();

                if (!TryResolveCamera(request.instanceId, request.cameraName, out var camera, out var error))
                {
                    return JsonError(error);
                }

                var urpData = camera.GetComponent<UniversalAdditionalCameraData>();
                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", camera.gameObject.GetInstanceID() },
                    { "cameraName", camera.gameObject.name },
                    { "clearFlags", camera.clearFlags.ToString() },
                    { "fieldOfView", camera.fieldOfView },
                    { "nearClipPlane", camera.nearClipPlane },
                    { "farClipPlane", camera.farClipPlane },
                    { "orthographic", camera.orthographic },
                    { "orthographicSize", camera.orthographicSize },
                    { "allowHDR", camera.allowHDR },
                    { "cullingMask", camera.cullingMask },
                    { "backgroundColor", new List<object> { camera.backgroundColor.r, camera.backgroundColor.g, camera.backgroundColor.b, camera.backgroundColor.a } },
                    { "urpDataPresent", urpData != null }
                };

                if (urpData != null)
                {
                    response["renderType"] = urpData.renderType.ToString();
                    response["renderPostProcessing"] = urpData.renderPostProcessing;
                    response["antialiasing"] = urpData.antialiasing.ToString();
                    response["antialiasingQuality"] = urpData.antialiasingQuality.ToString();
                    response["stopNaN"] = urpData.stopNaN;
                    response["dithering"] = urpData.dithering;
                    response["allowXRRendering"] = urpData.allowXRRendering;
                    response["renderShadows"] = urpData.renderShadows;
                    response["requiresColorOption"] = urpData.requiresColorOption.ToString();
                    response["requiresDepthOption"] = urpData.requiresDepthOption.ToString();
                    response["volumeLayerMask"] = urpData.volumeLayerMask.value;
                    response["volumeLayerNames"] = LayerMaskToNames(urpData.volumeLayerMask);
                    response["volumeTriggerInstanceId"] = urpData.volumeTrigger != null ? urpData.volumeTrigger.gameObject.GetInstanceID() : 0;
                    response["volumeTriggerName"] = urpData.volumeTrigger != null ? urpData.volumeTrigger.name : string.Empty;
                }

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("PUT", "/camera/rendering", Category = "camera", Description = "Set URP camera rendering config")]
        public static string SetCameraRendering(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<SetCameraRenderingRequest>(jsonData);
                if (request == null)
                {
                    return JsonError("Failed to parse camera rendering request");
                }

                if (!TryResolveCamera(request.instanceId, request.cameraName, out var camera, out var error))
                {
                    return JsonError(error);
                }

                var urpData = camera.GetUniversalAdditionalCameraData();
                Undo.RecordObject(camera, "Set Camera Rendering");
                Undo.RecordObject(urpData, "Set Camera Rendering");

                if (!string.IsNullOrWhiteSpace(request.clearFlags)
                    && Enum.TryParse<CameraClearFlags>(request.clearFlags, true, out var clearFlags))
                {
                    camera.clearFlags = clearFlags;
                }

                if (request.backgroundColor != null && request.backgroundColor.Length >= 3)
                {
                    float a = request.backgroundColor.Length >= 4 ? request.backgroundColor[3] : 1f;
                    camera.backgroundColor = new Color(request.backgroundColor[0], request.backgroundColor[1], request.backgroundColor[2], a);
                }

                if (request.fieldOfView >= 0f) camera.fieldOfView = request.fieldOfView;
                if (request.nearClipPlane >= 0f) camera.nearClipPlane = request.nearClipPlane;
                if (request.farClipPlane >= 0f) camera.farClipPlane = request.farClipPlane;
                if (request.orthographicSize >= 0f) camera.orthographicSize = request.orthographicSize;
                if (request.allowHDR >= 0) camera.allowHDR = request.allowHDR == 1;

                if (!string.IsNullOrWhiteSpace(request.renderType)
                    && Enum.TryParse<CameraRenderType>(request.renderType, true, out var renderType))
                {
                    urpData.renderType = renderType;
                }

                if (request.renderPostProcessing >= 0) urpData.renderPostProcessing = request.renderPostProcessing == 1;
                if (request.stopNaN >= 0) urpData.stopNaN = request.stopNaN == 1;
                if (request.dithering >= 0) urpData.dithering = request.dithering == 1;
                if (request.allowXRRendering >= 0) urpData.allowXRRendering = request.allowXRRendering == 1;
                if (request.renderShadows >= 0) urpData.renderShadows = request.renderShadows == 1;

                if (!string.IsNullOrWhiteSpace(request.antialiasing)
                    && Enum.TryParse<AntialiasingMode>(request.antialiasing, true, out var aaMode))
                {
                    urpData.antialiasing = aaMode;
                }

                if (!string.IsNullOrWhiteSpace(request.antialiasingQuality)
                    && Enum.TryParse<AntialiasingQuality>(request.antialiasingQuality, true, out var aaQuality))
                {
                    urpData.antialiasingQuality = aaQuality;
                }

                if (!string.IsNullOrWhiteSpace(request.requiresColorOption)
                    && Enum.TryParse<CameraOverrideOption>(request.requiresColorOption, true, out var colorOption))
                {
                    urpData.requiresColorOption = colorOption;
                }

                if (!string.IsNullOrWhiteSpace(request.requiresDepthOption)
                    && Enum.TryParse<CameraOverrideOption>(request.requiresDepthOption, true, out var depthOption))
                {
                    urpData.requiresDepthOption = depthOption;
                }

                if (request.volumeLayerMask >= 0)
                {
                    urpData.volumeLayerMask = request.volumeLayerMask;
                }

                if (request.volumeTriggerInstanceId != int.MinValue)
                {
                    if (request.volumeTriggerInstanceId <= 0)
                    {
                        urpData.volumeTrigger = null;
                    }
                    else
                    {
                        var triggerObj = EditorUtility.EntityIdToObject(request.volumeTriggerInstanceId) as GameObject;
                        urpData.volumeTrigger = triggerObj != null ? triggerObj.transform : null;
                    }
                }

                EditorUtility.SetDirty(camera);
                EditorUtility.SetDirty(urpData);
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

                return GetCameraRendering(JsonUtility.ToJson(new GetCameraRenderingRequest
                {
                    instanceId = camera.gameObject.GetInstanceID()
                }));
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static bool TryResolveCamera(int instanceId, string cameraName, out Camera camera, out string error)
        {
            camera = null;
            error = null;

            if (instanceId != 0)
            {
                var obj = EditorUtility.EntityIdToObject(instanceId);
                if (obj is Camera directCamera)
                {
                    camera = directCamera;
                    return true;
                }

                if (obj is GameObject goFromId)
                {
                    camera = goFromId.GetComponent<Camera>();
                    if (camera != null) return true;
                }

                error = $"Camera not found for instanceId {instanceId}";
                return false;
            }

            var loadedCameras = Resources.FindObjectsOfTypeAll<Camera>()
                .Where(c => c != null && c.gameObject.scene.IsValid() && c.gameObject.scene.isLoaded)
                .ToList();

            if (!string.IsNullOrWhiteSpace(cameraName))
            {
                camera = loadedCameras.FirstOrDefault(c =>
                    string.Equals(c.gameObject.name, cameraName, StringComparison.OrdinalIgnoreCase));
                if (camera != null) return true;

                error = $"Camera not found: {cameraName}";
                return false;
            }

            camera = Camera.main ?? loadedCameras.FirstOrDefault();
            if (camera == null)
            {
                error = "No camera found in loaded scenes";
                return false;
            }

            return true;
        }

        private static List<object> LayerMaskToNames(LayerMask mask)
        {
            var names = new List<object>();
            for (int i = 0; i < 32; i++)
            {
                int bit = 1 << i;
                if ((mask.value & bit) == 0) continue;
                var layerName = LayerMask.LayerToName(i);
                names.Add(string.IsNullOrWhiteSpace(layerName) ? i.ToString() : layerName);
            }
            return names;
        }

        private static Dictionary<string, object> CaptureCameraDict()
        {
            var camera = Camera.main;
            if (camera == null)
            {
                var allCams = Resources.FindObjectsOfTypeAll<Camera>()
                    .Where(c => c != null && c.gameObject.scene.IsValid() && c.gameObject.scene.isLoaded)
                    .ToList();
                camera = allCams.FirstOrDefault();
            }
            if (camera == null) return null;

            var result = new Dictionary<string, object>
            {
                { "cameraName", camera.gameObject.name },
                { "clearFlags", camera.clearFlags.ToString() },
                { "fieldOfView", camera.fieldOfView },
                { "nearClipPlane", camera.nearClipPlane },
                { "farClipPlane", camera.farClipPlane },
                { "orthographic", camera.orthographic },
                { "orthographicSize", camera.orthographicSize },
                { "allowHDR", camera.allowHDR },
                { "backgroundColor", new List<object> { camera.backgroundColor.r, camera.backgroundColor.g, camera.backgroundColor.b, camera.backgroundColor.a } }
            };

            var urpData = camera.GetComponent<UniversalAdditionalCameraData>();
            if (urpData != null)
            {
                result["renderType"] = urpData.renderType.ToString();
                result["renderPostProcessing"] = urpData.renderPostProcessing;
                result["antialiasing"] = urpData.antialiasing.ToString();
                result["antialiasingQuality"] = urpData.antialiasingQuality.ToString();
                result["stopNaN"] = urpData.stopNaN;
                result["dithering"] = urpData.dithering;
                result["renderShadows"] = urpData.renderShadows;
            }

            return result;
        }

        // ==================== Renderer State Inspection ====================

        [BridgeRoute("POST", "/renderer/state", Category = "rendering", Description = "Get renderer state (materials, keywords, MPB)", ReadOnly = true)]
        public static string GetRendererState(string jsonData)
        {
            var request = JsonUtility.FromJson<GetRendererStateRequest>(jsonData);

            var go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            if (go == null)
                return JsonError("GameObject not found");

            var renderers = go.GetComponents<Renderer>();
            if (renderers == null || renderers.Length == 0)
                return JsonError("No Renderer component found on this GameObject");

            int idx = Mathf.Clamp(request.rendererIndex, 0, renderers.Length - 1);
            var renderer = renderers[idx];

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "rendererType", renderer.GetType().Name },
                { "enabled", renderer.enabled },
                { "shadowCastingMode", renderer.shadowCastingMode.ToString() },
                { "receiveShadows", renderer.receiveShadows },
                { "sortingLayer", renderer.sortingLayerName },
                { "sortingOrder", renderer.sortingOrder },
                { "rendererCount", renderers.Length }
            };

            // Bounds
            var b = renderer.bounds;
            result["bounds"] = new Dictionary<string, object>
            {
                { "center", new List<object> { b.center.x, b.center.y, b.center.z } },
                { "size", new List<object> { b.size.x, b.size.y, b.size.z } }
            };

            // Materials
            var materials = renderer.sharedMaterials;
            var matList = new List<object>();
            foreach (var mat in materials)
            {
                if (mat == null)
                {
                    matList.Add(new Dictionary<string, object> { { "name", "(null)" }, { "shader", "None" } });
                    continue;
                }

                var matInfo = new Dictionary<string, object>
                {
                    { "name", mat.name },
                    { "shader", mat.shader != null ? mat.shader.name : "None" },
                    { "renderQueue", mat.renderQueue }
                };

                // Enabled keywords
                var keywords = mat.shaderKeywords;
                matInfo["keywords"] = keywords != null ? new List<object>(keywords) : new List<object>();

                // Key properties from shader (raw HDR + hex for readability)
                var props = new Dictionary<string, object>();
                if (mat.HasProperty("_BaseColor"))
                {
                    var bc = mat.GetColor("_BaseColor");
                    props["_BaseColor"] = new Dictionary<string, object>
                    {
                        { "r", (double)System.Math.Round(bc.r, 4) }, { "g", (double)System.Math.Round(bc.g, 4) },
                        { "b", (double)System.Math.Round(bc.b, 4) }, { "a", (double)System.Math.Round(bc.a, 4) },
                        { "hex", "#" + ColorUtility.ToHtmlStringRGBA(bc) }
                    };
                }
                else if (mat.HasProperty("_Color"))
                {
                    var cc = mat.GetColor("_Color");
                    props["_Color"] = new Dictionary<string, object>
                    {
                        { "r", (double)System.Math.Round(cc.r, 4) }, { "g", (double)System.Math.Round(cc.g, 4) },
                        { "b", (double)System.Math.Round(cc.b, 4) }, { "a", (double)System.Math.Round(cc.a, 4) },
                        { "hex", "#" + ColorUtility.ToHtmlStringRGBA(cc) }
                    };
                }
                if (mat.HasProperty("_EmissionColor"))
                {
                    var ec = mat.GetColor("_EmissionColor");
                    props["_EmissionColor"] = new Dictionary<string, object>
                    {
                        { "r", (double)System.Math.Round(ec.r, 4) }, { "g", (double)System.Math.Round(ec.g, 4) },
                        { "b", (double)System.Math.Round(ec.b, 4) }, { "a", (double)System.Math.Round(ec.a, 4) },
                        { "hex", "#" + ColorUtility.ToHtmlStringRGBA(ec) }
                    };
                }
                matInfo["properties"] = props;

                matList.Add(matInfo);
            }
            result["materials"] = matList;

            // MaterialPropertyBlock snapshot
            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);
            var mpbResult = new Dictionary<string, object>();
            bool hasOverrides = !block.isEmpty;
            mpbResult["hasOverrides"] = hasOverrides;

            if (hasOverrides)
            {
                var mpbProps = new Dictionary<string, object>();

                // Determine which properties to check
                string[] propsToCheck = request.propertyNames;
                if (propsToCheck == null || propsToCheck.Length == 0)
                {
                    // Auto-detect from shader using ShaderUtil
                    var mat0 = materials.Length > 0 ? materials[0] : null;
                    if (mat0 != null && mat0.shader != null)
                    {
                        var shader = mat0.shader;
                        int propCount = shader.GetPropertyCount();
                        var detected = new List<string>();
                        for (int pi = 0; pi < propCount; pi++)
                        {
                            var propName = shader.GetPropertyName(pi);
                            int nameId = Shader.PropertyToID(propName);
                            if (block.HasProperty(nameId))
                                detected.Add(propName);
                        }
                        propsToCheck = detected.ToArray();
                    }
                }

                if (propsToCheck != null)
                {
                    foreach (var propName in propsToCheck)
                    {
                        int nameId = Shader.PropertyToID(propName);
                        if (!block.HasProperty(nameId))
                            continue;

                        // Determine type from shader
                        var mat0 = materials.Length > 0 ? materials[0] : null;
                        if (mat0 != null && mat0.shader != null)
                        {
                            var shader = mat0.shader;
                            int propCount = shader.GetPropertyCount();
                            for (int pi = 0; pi < propCount; pi++)
                            {
                                if (shader.GetPropertyName(pi) != propName) continue;
                                var propType = shader.GetPropertyType(pi);

                                switch ((UnityEngine.Rendering.ShaderPropertyType)propType)
                                {
                                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                                        var c = block.GetColor(nameId);
                                        mpbProps[propName] = new Dictionary<string, object>
                                        {
                                            { "type", "Color" },
                                            { "value", "#" + ColorUtility.ToHtmlStringRGBA(c) },
                                            { "rgba", new List<object> { c.r, c.g, c.b, c.a } }
                                        };
                                        break;
                                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                                        mpbProps[propName] = new Dictionary<string, object>
                                        {
                                            { "type", "Float" },
                                            { "value", block.GetFloat(nameId) }
                                        };
                                        break;
                                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                                        var v = block.GetVector(nameId);
                                        mpbProps[propName] = new Dictionary<string, object>
                                        {
                                            { "type", "Vector" },
                                            { "value", new List<object> { v.x, v.y, v.z, v.w } }
                                        };
                                        break;
                                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                                        var tex = block.GetTexture(nameId);
                                        mpbProps[propName] = new Dictionary<string, object>
                                        {
                                            { "type", "Texture" },
                                            { "value", tex != null ? tex.name : "(null)" }
                                        };
                                        break;
                                    default:
                                        mpbProps[propName] = new Dictionary<string, object>
                                        {
                                            { "type", propType.ToString() },
                                            { "value", "(unsupported)" }
                                        };
                                        break;
                                }
                                break;
                            }
                        }
                    }
                }

                mpbResult["properties"] = mpbProps;
            }

            result["materialPropertyBlock"] = mpbResult;

            return JsonResult(result);
        }

        // ==================== Mesh Info Inspection ====================

        [BridgeRoute("POST", "/mesh/info", Category = "rendering", Description = "Get mesh topology: vertices, submeshes, material correlation", ReadOnly = true, TimeoutDefault = 10000)]
        public static string GetMeshInfo(string jsonData)
        {
            var request = JsonUtility.FromJson<GetMeshInfoRequest>(jsonData);

            GameObject go = null;
            if (request.instanceId != 0)
            {
                go = EditorUtility.EntityIdToObject(request.instanceId) as GameObject;
            }
            if (go == null && !string.IsNullOrEmpty(request.name))
            {
                go = GameObject.Find(request.name);
            }
            if (go == null)
                return JsonError("GameObject not found. Provide a valid instanceId or name.");

            // Try MeshFilter first, then SkinnedMeshRenderer
            Mesh mesh = null;
            string meshSource = null;
            var meshFilter = go.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                mesh = meshFilter.sharedMesh;
                meshSource = "MeshFilter";
            }
            else
            {
                var smr = go.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null)
                {
                    mesh = smr.sharedMesh;
                    meshSource = "SkinnedMeshRenderer";
                }
            }

            if (mesh == null)
                return JsonError("No mesh found on this GameObject (checked MeshFilter.sharedMesh and SkinnedMeshRenderer.sharedMesh).");

            // Submesh info
            var submeshes = new List<object>();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var desc = mesh.GetSubMesh(i);
                submeshes.Add(new Dictionary<string, object>
                {
                    { "index", i },
                    { "firstVertex", desc.firstVertex },
                    { "vertexCount", desc.vertexCount },
                    { "indexStart", desc.indexStart },
                    { "indexCount", desc.indexCount },
                    { "topology", desc.topology.ToString() }
                });
            }

            // Material correlation: renderer materials correspond to submesh indices
            var materialCorrelation = new List<object>();
            var meshRenderer = go.GetComponent<Renderer>();
            if (meshRenderer != null)
            {
                var sharedMats = meshRenderer.sharedMaterials;
                for (int i = 0; i < Mathf.Max(mesh.subMeshCount, sharedMats.Length); i++)
                {
                    var entry = new Dictionary<string, object> { { "submeshIndex", i } };
                    if (i < sharedMats.Length && sharedMats[i] != null)
                    {
                        entry["materialName"] = sharedMats[i].name;
                        entry["shaderName"] = sharedMats[i].shader != null ? sharedMats[i].shader.name : "None";
                    }
                    else
                    {
                        entry["materialName"] = i < sharedMats.Length ? "(null)" : "(no slot)";
                        entry["shaderName"] = "None";
                    }
                    materialCorrelation.Add(entry);
                }
            }

            var bounds = mesh.bounds;
            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "gameObjectName", go.name },
                { "instanceId", go.GetInstanceID() },
                { "meshSource", meshSource },
                { "meshName", mesh.name },
                { "vertexCount", mesh.vertexCount },
                { "triangleCount", mesh.triangles.Length / 3 },
                { "subMeshCount", mesh.subMeshCount },
                { "submeshes", submeshes },
                { "materialCorrelation", materialCorrelation },
                { "localBounds", new Dictionary<string, object>
                    {
                        { "center", new List<object> { bounds.center.x, bounds.center.y, bounds.center.z } },
                        { "size", new List<object> { bounds.size.x, bounds.size.y, bounds.size.z } }
                    }
                },
                { "hasNormals", mesh.normals != null && mesh.normals.Length > 0 },
                { "hasUVs", mesh.uv != null && mesh.uv.Length > 0 },
                { "hasColors", mesh.colors != null && mesh.colors.Length > 0 },
                { "hasTangents", mesh.tangents != null && mesh.tangents.Length > 0 },
                { "hasUV2", mesh.uv2 != null && mesh.uv2.Length > 0 },
                { "hasBoneWeights", mesh.boneWeights != null && mesh.boneWeights.Length > 0 },
                { "blendShapeCount", mesh.blendShapeCount }
            };

            return JsonResult(result);
        }
    }
}
