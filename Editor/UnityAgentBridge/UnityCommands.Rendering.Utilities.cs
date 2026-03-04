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
        // ==================== SCENE KNOWLEDGE TOOLS ====================

        // Phase 1: Snap/Align Helper

        private static Bounds GetObjectBounds(GameObject go)
        {
            var renderer = go.GetComponentInChildren<Renderer>();
            if (renderer != null) return renderer.bounds;
            var collider = go.GetComponentInChildren<Collider>();
            if (TryGetSafeColliderBounds(collider, out var cb)) return cb;
            return new Bounds(go.transform.position, Vector3.one);
        }

        [BridgeRoute("POST", "/snap", Category = "layout", Description = "Snap-align objects")]
        public static string SnapObjects(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<SnapObjectsRequest>(jsonData);
                if (request == null) return JsonError("Invalid request");

                var sourceObj = EditorUtility.EntityIdToObject(request.sourceId) as GameObject;
                if (sourceObj == null) return JsonError($"Source GameObject not found: {request.sourceId}");

                var targetObj = EditorUtility.EntityIdToObject(request.targetId) as GameObject;
                if (targetObj == null) return JsonError($"Target GameObject not found: {request.targetId}");

                var sourceBounds = GetObjectBounds(sourceObj);
                var targetBounds = GetObjectBounds(targetObj);

                var oldPosition = sourceObj.transform.position;
                var sourceExtents = sourceBounds.extents;
                var targetExtents = targetBounds.extents;

                // Offset between transform origin and bounds center
                var centerOffset = sourceObj.transform.position - sourceBounds.center;

                Vector3 newCenter;
                var alignment = (request.alignment ?? "right-of").ToLowerInvariant().Replace(" ", "-");
                float gap = request.gap;

                switch (alignment)
                {
                    case "right-of":
                        newCenter = new Vector3(
                            targetBounds.max.x + sourceExtents.x + gap,
                            targetBounds.center.y,
                            targetBounds.center.z);
                        break;
                    case "left-of":
                        newCenter = new Vector3(
                            targetBounds.min.x - sourceExtents.x - gap,
                            targetBounds.center.y,
                            targetBounds.center.z);
                        break;
                    case "above":
                        newCenter = new Vector3(
                            targetBounds.center.x,
                            targetBounds.max.y + sourceExtents.y + gap,
                            targetBounds.center.z);
                        break;
                    case "below":
                        newCenter = new Vector3(
                            targetBounds.center.x,
                            targetBounds.min.y - sourceExtents.y - gap,
                            targetBounds.center.z);
                        break;
                    case "in-front-of":
                        newCenter = new Vector3(
                            targetBounds.center.x,
                            targetBounds.center.y,
                            targetBounds.max.z + sourceExtents.z + gap);
                        break;
                    case "behind":
                        newCenter = new Vector3(
                            targetBounds.center.x,
                            targetBounds.center.y,
                            targetBounds.min.z - sourceExtents.z - gap);
                        break;
                    case "on-top-of":
                        newCenter = new Vector3(
                            targetBounds.center.x,
                            targetBounds.max.y + sourceExtents.y + gap,
                            targetBounds.center.z);
                        break;
                    default:
                        return JsonError($"Unknown alignment: {alignment}. Valid: right-of, left-of, above, below, in-front-of, behind, on-top-of");
                }

                var newPosition = newCenter + centerOffset;

                Undo.RecordObject(sourceObj.transform, "Snap Objects");
                sourceObj.transform.position = newPosition;
                EditorUtility.SetDirty(sourceObj);

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "alignment", alignment },
                    { "gap", gap },
                    { "oldPosition", new List<object> { oldPosition.x, oldPosition.y, oldPosition.z } },
                    { "newPosition", new List<object> { newPosition.x, newPosition.y, newPosition.z } },
                    { "sourceBoundsSize", new List<object> { sourceBounds.size.x, sourceBounds.size.y, sourceBounds.size.z } },
                    { "targetBoundsSize", new List<object> { targetBounds.size.x, targetBounds.size.y, targetBounds.size.z } }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        // Phase 2: Look Preset Save/Load

        private static List<Dictionary<string, object>> CaptureSceneLights()
        {
            var lights = new List<Dictionary<string, object>>();
            var allLights = Resources.FindObjectsOfTypeAll<Light>()
                .Where(l => l != null && l.gameObject.scene.IsValid() && l.gameObject.scene.isLoaded)
                .ToList();

            foreach (var light in allLights)
            {
                var info = new Dictionary<string, object>
                {
                    { "name", light.gameObject.name },
                    { "type", light.type.ToString() },
                    { "position", new List<object> { light.transform.position.x, light.transform.position.y, light.transform.position.z } },
                    { "rotation", new List<object> { light.transform.eulerAngles.x, light.transform.eulerAngles.y, light.transform.eulerAngles.z } },
                    { "color", new List<object> { light.color.r, light.color.g, light.color.b, light.color.a } },
                    { "intensity", light.intensity },
                    { "range", light.range },
                    { "spotAngle", light.spotAngle },
                    { "shadows", light.shadows.ToString() }
                };
                lights.Add(info);
            }
            return lights;
        }

        private static Dictionary<string, object> CaptureRenderSettingsDict()
        {
            var skyboxMat = RenderSettings.skybox;
            string skyboxPath = skyboxMat != null ? AssetDatabase.GetAssetPath(skyboxMat) : null;

            return new Dictionary<string, object>
            {
                { "ambientMode", RenderSettings.ambientMode.ToString() },
                { "ambientLight", new List<object> { RenderSettings.ambientLight.r, RenderSettings.ambientLight.g, RenderSettings.ambientLight.b, RenderSettings.ambientLight.a } },
                { "ambientSkyColor", new List<object> { RenderSettings.ambientSkyColor.r, RenderSettings.ambientSkyColor.g, RenderSettings.ambientSkyColor.b, RenderSettings.ambientSkyColor.a } },
                { "ambientEquatorColor", new List<object> { RenderSettings.ambientEquatorColor.r, RenderSettings.ambientEquatorColor.g, RenderSettings.ambientEquatorColor.b, RenderSettings.ambientEquatorColor.a } },
                { "ambientGroundColor", new List<object> { RenderSettings.ambientGroundColor.r, RenderSettings.ambientGroundColor.g, RenderSettings.ambientGroundColor.b, RenderSettings.ambientGroundColor.a } },
                { "fog", RenderSettings.fog },
                { "fogMode", RenderSettings.fogMode.ToString() },
                { "fogColor", new List<object> { RenderSettings.fogColor.r, RenderSettings.fogColor.g, RenderSettings.fogColor.b, RenderSettings.fogColor.a } },
                { "fogDensity", RenderSettings.fogDensity },
                { "fogStartDistance", RenderSettings.fogStartDistance },
                { "fogEndDistance", RenderSettings.fogEndDistance },
                { "skyboxMaterial", skyboxPath ?? "" },
                { "reflectionIntensity", RenderSettings.reflectionIntensity }
            };
        }

        // SaveLookPreset, LoadLookPreset, ListLookPresets, ApplySeparationSafeLook → UnityCommands.LookPresets.cs
        // ExtractSceneProfile, GetSavedSceneProfile → UnityCommands.SceneProfiles.cs
        // GenerateAssetCatalog, GetSavedAssetCatalog, PinAssetPackContext, GetAssetPackContextPin, ListAssetPackContextPins → UnityCommands.Catalogs.cs

        // [Methods extracted to UnityCommands.LookPresets.cs, UnityCommands.SceneProfiles.cs, UnityCommands.Catalogs.cs]
        // Remaining below: EnsureAssetFolder, MakeSafeFileName (shared utilities)

        // ==================== END SCENE KNOWLEDGE TOOLS ====================
        private static void EnsureAssetFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath)) return;

            var segments = folderPath.Split('/');
            string current = segments[0];
            for (int i = 1; i < segments.Length; i++)
            {
                var next = $"{current}/{segments[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, segments[i]);
                }
                current = next;
            }
        }

        private static string MakeSafeFileName(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "Volume";
            var sb = new StringBuilder(input.Length);
            foreach (var c in input)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            }
            return sb.ToString().Trim('_');
        }
    }
}
