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
        #region ParticleSystem Methods

        // ---- MinMaxCurve Helpers ----

        private static Dictionary<string, object> SerializeMinMaxCurve(ParticleSystem.MinMaxCurve curve)
        {
            var result = new Dictionary<string, object>();
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    result["mode"] = "Constant";
                    result["value"] = curve.constant;
                    break;
                case ParticleSystemCurveMode.TwoConstants:
                    result["mode"] = "TwoConstants";
                    result["min"] = curve.constantMin;
                    result["max"] = curve.constantMax;
                    break;
                case ParticleSystemCurveMode.Curve:
                    result["mode"] = "Curve";
                    result["multiplier"] = curve.curveMultiplier;
                    break;
                case ParticleSystemCurveMode.TwoCurves:
                    result["mode"] = "TwoCurves";
                    result["multiplier"] = curve.curveMultiplier;
                    break;
                default:
                    result["mode"] = curve.mode.ToString();
                    break;
            }
            return result;
        }

        /// <summary>
        /// Checks for "<fieldName>Range" (two-constants) first, then "<fieldName>" (constant).
        /// Returns true if applied.
        /// </summary>
        private static bool TryApplyMinMaxCurve(Dictionary<string, object> dict, string fieldName, ref ParticleSystem.MinMaxCurve curve)
        {
            string rangeKey = fieldName + "Range";
            if (dict.TryGetValue(rangeKey, out var rangeObj) && rangeObj is IList<object> rangeList && rangeList.Count >= 2)
            {
                float min = Convert.ToSingle(rangeList[0]);
                float max = Convert.ToSingle(rangeList[1]);
                curve = new ParticleSystem.MinMaxCurve(min, max);
                return true;
            }

            if (TryReadFloatField(dict, fieldName, out float constant))
            {
                curve = new ParticleSystem.MinMaxCurve(constant);
                return true;
            }

            return false;
        }

        // ---- GET handler ----

        public static string GetParticleSystem(string jsonData)
        {
            var dict = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
            if (dict == null) return JsonError("Invalid JSON");

            if (!TryReadInt(dict, "instanceId", out int instanceId))
                return JsonError("instanceId is required");

            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null) return JsonError("GameObject not found");

            var ps = go.GetComponent<ParticleSystem>();
            if (ps == null) return JsonError("No ParticleSystem component on this GameObject");

            string modulesFilter = ReadString(dict, "modules");
            var allModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "main", "emission", "shape", "renderer", "coloroverlifetime", "sizeoverlifetime" };
            HashSet<string> requested;
            if (!string.IsNullOrEmpty(modulesFilter))
            {
                requested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in modulesFilter.Split(','))
                {
                    var trimmed = m.Trim();
                    if (trimmed.Length > 0) requested.Add(trimmed);
                }
            }
            else
            {
                requested = allModules;
            }

            var modules = new Dictionary<string, object>();

            // Main module
            if (requested.Contains("main"))
            {
                var main = ps.main;
                var mainDict = new Dictionary<string, object>
                {
                    { "duration", main.duration },
                    { "looping", main.loop },
                    { "playOnAwake", main.playOnAwake },
                    { "startLifetime", SerializeMinMaxCurve(main.startLifetime) },
                    { "startSpeed", SerializeMinMaxCurve(main.startSpeed) },
                    { "startSize", SerializeMinMaxCurve(main.startSize) },
                    { "startColor", new float[] { main.startColor.color.r, main.startColor.color.g, main.startColor.color.b, main.startColor.color.a } },
                    { "maxParticles", main.maxParticles },
                    { "gravityModifier", SerializeMinMaxCurve(main.gravityModifier) },
                    { "simulationSpeed", main.simulationSpeed },
                    { "simulationSpace", main.simulationSpace.ToString() },
                    { "scalingMode", main.scalingMode.ToString() },
                    { "startRotation", SerializeMinMaxCurve(main.startRotation) }
                };
                modules["main"] = mainDict;
            }

            // Emission module
            if (requested.Contains("emission"))
            {
                var emission = ps.emission;
                var emDict = new Dictionary<string, object>
                {
                    { "enabled", emission.enabled },
                    { "rateOverTime", SerializeMinMaxCurve(emission.rateOverTime) },
                    { "rateOverDistance", SerializeMinMaxCurve(emission.rateOverDistance) }
                };
                modules["emission"] = emDict;
            }

            // Shape module
            if (requested.Contains("shape"))
            {
                var shape = ps.shape;
                var shapeDict = new Dictionary<string, object>
                {
                    { "enabled", shape.enabled },
                    { "shapeType", shape.shapeType.ToString() },
                    { "radius", shape.radius },
                    { "radiusThickness", shape.radiusThickness },
                    { "angle", shape.angle },
                    { "arc", shape.arc },
                    { "scale", new float[] { shape.scale.x, shape.scale.y, shape.scale.z } },
                    { "position", new float[] { shape.position.x, shape.position.y, shape.position.z } },
                    { "rotation", new float[] { shape.rotation.x, shape.rotation.y, shape.rotation.z } }
                };
                modules["shape"] = shapeDict;
            }

            // Renderer module
            if (requested.Contains("renderer"))
            {
                var renderer = go.GetComponent<ParticleSystemRenderer>();
                if (renderer != null)
                {
                    var rendDict = new Dictionary<string, object>
                    {
                        { "renderMode", renderer.renderMode.ToString() },
                        { "material", renderer.sharedMaterial != null ? UnityEditor.AssetDatabase.GetAssetPath(renderer.sharedMaterial) : "" },
                        { "sortingOrder", renderer.sortingOrder },
                        { "minParticleSize", renderer.minParticleSize },
                        { "maxParticleSize", renderer.maxParticleSize }
                    };
                    modules["renderer"] = rendDict;
                }
                else
                {
                    modules["renderer"] = new Dictionary<string, object> { { "error", "No ParticleSystemRenderer found" } };
                }
            }

            // Color over Lifetime module
            if (requested.Contains("coloroverlifetime"))
            {
                var col = ps.colorOverLifetime;
                var colDict = new Dictionary<string, object>
                {
                    { "enabled", col.enabled }
                };
                if (col.enabled && col.color.mode == ParticleSystemGradientMode.Gradient && col.color.gradient != null)
                {
                    var grad = col.color.gradient;
                    var colorKeys = new List<object>();
                    foreach (var ck in grad.colorKeys)
                    {
                        colorKeys.Add(new Dictionary<string, object>
                        {
                            { "color", new float[] { ck.color.r, ck.color.g, ck.color.b } },
                            { "time", ck.time }
                        });
                    }
                    var alphaKeys = new List<object>();
                    foreach (var ak in grad.alphaKeys)
                    {
                        alphaKeys.Add(new Dictionary<string, object>
                        {
                            { "alpha", ak.alpha },
                            { "time", ak.time }
                        });
                    }
                    colDict["gradientColorKeys"] = colorKeys;
                    colDict["gradientAlphaKeys"] = alphaKeys;
                }
                else if (col.enabled && col.color.mode == ParticleSystemGradientMode.TwoColors)
                {
                    var cMin = col.color.colorMin;
                    var cMax = col.color.colorMax;
                    colDict["colorMin"] = new float[] { cMin.r, cMin.g, cMin.b, cMin.a };
                    colDict["colorMax"] = new float[] { cMax.r, cMax.g, cMax.b, cMax.a };
                }
                colDict["mode"] = col.color.mode.ToString();
                modules["colorOverLifetime"] = colDict;
            }

            // Size over Lifetime module
            if (requested.Contains("sizeoverlifetime"))
            {
                var sol = ps.sizeOverLifetime;
                var solDict = new Dictionary<string, object>
                {
                    { "enabled", sol.enabled },
                    { "sizeMultiplier", sol.sizeMultiplier },
                    { "size", SerializeMinMaxCurve(sol.size) }
                };
                modules["sizeOverLifetime"] = solDict;
            }

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "instanceId", instanceId },
                { "name", go.name },
                { "modules", modules }
            });
        }

        // ---- CONFIGURE handler ----

        [BridgeRoute("PUT", "/particle-system", Category = "particles", Description = "Configure ParticleSystem")]
        public static string ConfigureParticleSystem(string jsonData)
        {
            var dict = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
            if (dict == null) return JsonError("Invalid JSON");

            if (!TryReadInt(dict, "instanceId", out int instanceId))
                return JsonError("instanceId is required");

            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null) return JsonError("GameObject not found");

            bool createIfMissing = ReadBool(dict, "createIfMissing", true);

            var ps = go.GetComponent<ParticleSystem>();
            if (ps == null)
            {
                if (!createIfMissing)
                    return JsonError("No ParticleSystem on GameObject and createIfMissing is false");
                ps = Undo.AddComponent<ParticleSystem>(go);
            }
            else
            {
                Undo.RecordObject(ps, "Configure ParticleSystem");
            }

            var modulesApplied = new HashSet<string>();
            int fieldsApplied = 0;

            // ==================== Main module ====================
            {
                var main = ps.main;
                bool touched = false;

                if (TryReadFloatField(dict, "duration", out float duration))
                {
                    main.duration = duration;
                    touched = true; fieldsApplied++;
                }
                if (TryReadBoolField(dict, "looping", out bool looping))
                {
                    main.loop = looping;
                    touched = true; fieldsApplied++;
                }
                if (TryReadBoolField(dict, "playOnAwake", out bool playOnAwake))
                {
                    main.playOnAwake = playOnAwake;
                    touched = true; fieldsApplied++;
                }

                var startLifetimeCurve = main.startLifetime;
                if (TryApplyMinMaxCurve(dict, "startLifetime", ref startLifetimeCurve))
                {
                    main.startLifetime = startLifetimeCurve;
                    touched = true; fieldsApplied++;
                }

                var startSpeedCurve = main.startSpeed;
                if (TryApplyMinMaxCurve(dict, "startSpeed", ref startSpeedCurve))
                {
                    main.startSpeed = startSpeedCurve;
                    touched = true; fieldsApplied++;
                }

                var startSizeCurve = main.startSize;
                if (TryApplyMinMaxCurve(dict, "startSize", ref startSizeCurve))
                {
                    main.startSize = startSizeCurve;
                    touched = true; fieldsApplied++;
                }

                if (dict.TryGetValue("startColor", out var scObj) && TryReadColor(scObj, out Color sc))
                {
                    main.startColor = sc;
                    touched = true; fieldsApplied++;
                }

                if (TryReadInt(dict, "maxParticles", out int maxP))
                {
                    main.maxParticles = maxP;
                    touched = true; fieldsApplied++;
                }

                var gravModCurve = main.gravityModifier;
                if (TryApplyMinMaxCurve(dict, "gravityModifier", ref gravModCurve))
                {
                    main.gravityModifier = gravModCurve;
                    touched = true; fieldsApplied++;
                }

                if (TryReadFloatField(dict, "simulationSpeed", out float simSpeed))
                {
                    main.simulationSpeed = simSpeed;
                    touched = true; fieldsApplied++;
                }

                var simSpaceStr = ReadString(dict, "simulationSpace");
                if (simSpaceStr != null)
                {
                    if (Enum.TryParse<ParticleSystemSimulationSpace>(simSpaceStr, true, out var simSpace))
                    {
                        main.simulationSpace = simSpace;
                        touched = true; fieldsApplied++;
                    }
                    else
                    {
                        return JsonError($"Invalid simulationSpace '{simSpaceStr}'. Valid: Local, World, Custom");
                    }
                }

                var scalingModeStr = ReadString(dict, "scalingMode");
                if (scalingModeStr != null)
                {
                    if (Enum.TryParse<ParticleSystemScalingMode>(scalingModeStr, true, out var sm))
                    {
                        main.scalingMode = sm;
                        touched = true; fieldsApplied++;
                    }
                    else
                    {
                        return JsonError($"Invalid scalingMode '{scalingModeStr}'. Valid: Hierarchy, Local, Shape");
                    }
                }

                var startRotCurve = main.startRotation;
                if (TryApplyMinMaxCurve(dict, "startRotation", ref startRotCurve))
                {
                    // Convert degrees to radians for Unity
                    if (startRotCurve.mode == ParticleSystemCurveMode.Constant)
                        startRotCurve = new ParticleSystem.MinMaxCurve(startRotCurve.constant * Mathf.Deg2Rad);
                    else if (startRotCurve.mode == ParticleSystemCurveMode.TwoConstants)
                        startRotCurve = new ParticleSystem.MinMaxCurve(startRotCurve.constantMin * Mathf.Deg2Rad, startRotCurve.constantMax * Mathf.Deg2Rad);
                    main.startRotation = startRotCurve;
                    touched = true; fieldsApplied++;
                }

                if (touched) modulesApplied.Add("main");
            }

            // ==================== Emission module ====================
            {
                var emission = ps.emission;
                bool touched = false;

                if (TryReadBoolField(dict, "emissionEnabled", out bool emEnabled))
                {
                    emission.enabled = emEnabled;
                    touched = true; fieldsApplied++;
                }

                var rotCurve = emission.rateOverTime;
                if (TryApplyMinMaxCurve(dict, "emissionRateOverTime", ref rotCurve))
                {
                    emission.rateOverTime = rotCurve;
                    touched = true; fieldsApplied++;
                }

                var rodCurve = emission.rateOverDistance;
                if (TryApplyMinMaxCurve(dict, "emissionRateOverDistance", ref rodCurve))
                {
                    emission.rateOverDistance = rodCurve;
                    touched = true; fieldsApplied++;
                }

                if (touched) modulesApplied.Add("emission");
            }

            // ==================== Shape module ====================
            {
                var shape = ps.shape;
                bool touched = false;

                if (TryReadBoolField(dict, "shapeEnabled", out bool shapeEnabled))
                {
                    shape.enabled = shapeEnabled;
                    touched = true; fieldsApplied++;
                }

                var shapeTypeStr = ReadString(dict, "shapeType");
                if (shapeTypeStr != null)
                {
                    if (Enum.TryParse<ParticleSystemShapeType>(shapeTypeStr, true, out var st))
                    {
                        shape.shapeType = st;
                        touched = true; fieldsApplied++;
                    }
                    else
                    {
                        return JsonError($"Invalid shapeType '{shapeTypeStr}'. Valid: Sphere, Hemisphere, Cone, Box, Mesh, MeshRenderer, SkinnedMeshRenderer, Circle, Edge, Donut, SingleSidedEdge, Rectangle");
                    }
                }

                if (TryReadFloatField(dict, "shapeRadius", out float sr))
                {
                    shape.radius = sr;
                    touched = true; fieldsApplied++;
                }
                if (TryReadFloatField(dict, "shapeRadiusThickness", out float srt))
                {
                    shape.radiusThickness = srt;
                    touched = true; fieldsApplied++;
                }
                if (TryReadFloatField(dict, "shapeAngle", out float sa))
                {
                    shape.angle = sa;
                    touched = true; fieldsApplied++;
                }
                if (TryReadFloatField(dict, "shapeArc", out float arc))
                {
                    shape.arc = arc;
                    touched = true; fieldsApplied++;
                }

                if (dict.TryGetValue("shapeScale", out var ssObj) && TryReadVector(ssObj, 3, out float[] ssVec))
                {
                    shape.scale = new Vector3(ssVec[0], ssVec[1], ssVec[2]);
                    touched = true; fieldsApplied++;
                }
                if (dict.TryGetValue("shapePosition", out var spObj) && TryReadVector(spObj, 3, out float[] spVec))
                {
                    shape.position = new Vector3(spVec[0], spVec[1], spVec[2]);
                    touched = true; fieldsApplied++;
                }
                if (dict.TryGetValue("shapeRotation", out var srObj) && TryReadVector(srObj, 3, out float[] srVec))
                {
                    shape.rotation = new Vector3(srVec[0], srVec[1], srVec[2]);
                    touched = true; fieldsApplied++;
                }

                if (touched) modulesApplied.Add("shape");
            }

            // ==================== Renderer module ====================
            {
                var renderer = go.GetComponent<ParticleSystemRenderer>();
                bool touched = false;

                // Renderer is always present with ParticleSystem, but guard anyway
                if (renderer != null)
                {
                    Undo.RecordObject(renderer, "Configure ParticleSystem Renderer");

                    var modeStr = ReadString(dict, "rendererMode");
                    if (modeStr != null)
                    {
                        if (Enum.TryParse<ParticleSystemRenderMode>(modeStr, true, out var rm))
                        {
                            renderer.renderMode = rm;
                            touched = true; fieldsApplied++;
                        }
                        else
                        {
                            return JsonError($"Invalid rendererMode '{modeStr}'. Valid: Billboard, Stretch, HorizontalBillboard, VerticalBillboard, Mesh");
                        }
                    }

                    var matPath = ReadString(dict, "rendererMaterial");
                    if (matPath != null)
                    {
                        var validatedPath = ValidateAssetPath(matPath);
                        if (validatedPath == null)
                            return JsonError($"Invalid asset path: {matPath}");
                        var mat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(matPath);
                        if (mat == null)
                            return JsonError($"Material not found at path: {matPath}");
                        renderer.sharedMaterial = mat;
                        touched = true; fieldsApplied++;
                    }

                    if (TryReadInt(dict, "rendererSortingOrder", out int so))
                    {
                        renderer.sortingOrder = so;
                        touched = true; fieldsApplied++;
                    }
                    if (TryReadFloatField(dict, "rendererMinParticleSize", out float minPS))
                    {
                        renderer.minParticleSize = minPS;
                        touched = true; fieldsApplied++;
                    }
                    if (TryReadFloatField(dict, "rendererMaxParticleSize", out float maxPS))
                    {
                        renderer.maxParticleSize = maxPS;
                        touched = true; fieldsApplied++;
                    }

                    if (touched)
                    {
                        EditorUtility.SetDirty(renderer);
                        modulesApplied.Add("renderer");
                    }
                }
            }

            // ==================== Color over Lifetime module ====================
            {
                var col = ps.colorOverLifetime;
                bool touched = false;

                if (TryReadBoolField(dict, "colorOverLifetimeEnabled", out bool colEnabled))
                {
                    col.enabled = colEnabled;
                    touched = true; fieldsApplied++;
                }

                Color startCol = Color.white;
                Color endCol = Color.white;
                bool hasStart = dict.TryGetValue("colorOverLifetimeStart", out var startObj) && TryReadColor(startObj, out startCol);
                bool hasEnd = dict.TryGetValue("colorOverLifetimeEnd", out var endObj) && TryReadColor(endObj, out endCol);

                if (hasStart || hasEnd)
                {

                    var gradient = new Gradient();
                    gradient.SetKeys(
                        new GradientColorKey[] {
                            new GradientColorKey(startCol, 0f),
                            new GradientColorKey(endCol, 1f)
                        },
                        new GradientAlphaKey[] {
                            new GradientAlphaKey(startCol.a, 0f),
                            new GradientAlphaKey(endCol.a, 1f)
                        }
                    );
                    col.color = new ParticleSystem.MinMaxGradient(gradient);
                    if (!col.enabled) col.enabled = true; // auto-enable when setting gradient
                    touched = true; fieldsApplied++;
                }

                if (touched) modulesApplied.Add("colorOverLifetime");
            }

            // ==================== Size over Lifetime module ====================
            {
                var sol = ps.sizeOverLifetime;
                bool touched = false;

                if (TryReadBoolField(dict, "sizeOverLifetimeEnabled", out bool solEnabled))
                {
                    sol.enabled = solEnabled;
                    touched = true; fieldsApplied++;
                }

                if (TryReadFloatField(dict, "sizeOverLifetimeSizeMultiplier", out float sizeMult))
                {
                    sol.sizeMultiplier = sizeMult;
                    if (!sol.enabled) sol.enabled = true; // auto-enable
                    touched = true; fieldsApplied++;
                }

                if (touched) modulesApplied.Add("sizeOverLifetime");
            }

            EditorUtility.SetDirty(ps);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "instanceId", instanceId },
                { "name", go.name },
                { "modulesApplied", new List<object>(modulesApplied) },
                { "fieldsApplied", fieldsApplied }
            });
        }

        #endregion

        #region Particle Templates

        [BridgeRoute("POST", "/particle-system/template", Category = "particles", Description = "Create a particle system from a preset template")]
        public static string CreateParticleTemplate(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<ParticleTemplateRequest>(NormalizeColorFields(jsonData));
                if (string.IsNullOrEmpty(request.template))
                    return JsonError("template is required (fire, smoke, rain, sparks, snow, dust, fountain, fireflies)");

                string templateLower = request.template.ToLowerInvariant();
                string goName = string.IsNullOrEmpty(request.name) ? templateLower + "_particles" : request.name;

                var go = new GameObject(goName);
                Undo.RegisterCreatedObjectUndo(go, "Agent Bridge Create Particle Template");

                // Position
                if (request.position != null && request.position.Length >= 3)
                    go.transform.position = new Vector3(request.position[0], request.position[1], request.position[2]);
                if (request.rotation != null && request.rotation.Length >= 3)
                    go.transform.eulerAngles = new Vector3(request.rotation[0], request.rotation[1], request.rotation[2]);

                // Parent
                if (request.parentId != -1)
                {
                    var parent = EditorUtility.EntityIdToObject(request.parentId) as GameObject;
                    if (parent != null)
                        go.transform.SetParent(parent.transform);
                }

                var ps = go.AddComponent<ParticleSystem>();
                var renderer = go.GetComponent<ParticleSystemRenderer>();

                // Ensure default particle material
                var particleMat = FindDefaultParticleMaterial();
                if (particleMat != null)
                    renderer.sharedMaterial = particleMat;

                float scale = Mathf.Max(0.01f, request.scale);
                float intensity = Mathf.Max(0.01f, request.intensity);

                switch (templateLower)
                {
                    case "fire":
                        ApplyFireTemplate(ps, renderer, scale, intensity);
                        break;
                    case "smoke":
                        ApplySmokeTemplate(ps, renderer, scale, intensity);
                        break;
                    case "rain":
                        ApplyRainTemplate(ps, renderer, scale, intensity);
                        break;
                    case "sparks":
                        ApplySparksTemplate(ps, renderer, scale, intensity);
                        break;
                    case "snow":
                        ApplySnowTemplate(ps, renderer, scale, intensity);
                        break;
                    case "dust":
                        ApplyDustTemplate(ps, renderer, scale, intensity);
                        break;
                    case "fountain":
                        ApplyFountainTemplate(ps, renderer, scale, intensity);
                        break;
                    case "fireflies":
                        ApplyFirefliesTemplate(ps, renderer, scale, intensity);
                        break;
                    default:
                        Undo.DestroyObjectImmediate(go);
                        return JsonError($"Unknown template: '{request.template}'. Valid: fire, smoke, rain, sparks, snow, dust, fountain, fireflies");
                }

                // Override start color if provided
                if (request.color != null && request.color.Length >= 3)
                {
                    var main = ps.main;
                    main.startColor = new Color(
                        request.color[0], request.color[1], request.color[2],
                        request.color.Length >= 4 ? request.color[3] : 1f);
                }

                EditorUtility.SetDirty(ps);

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "instanceId", go.GetInstanceID() },
                    { "name", go.name },
                    { "template", templateLower }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static Material FindDefaultParticleMaterial()
        {
            // Try URP particle shader first, then built-in
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                      ?? Shader.Find("Particles/Standard Unlit")
                      ?? Shader.Find("Particles/Alpha Blended");
            return shader != null ? new Material(shader) : null;
        }

        private static void ApplyFireTemplate(ParticleSystem ps, ParticleSystemRenderer renderer, float scale, float intensity)
        {
            var main = ps.main;
            main.duration = 1f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f * scale, 1.2f * scale);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2f * scale, 5f * scale);
            main.startSize = new ParticleSystem.MinMaxCurve(0.2f * scale, 0.6f * scale);
            main.startColor = new Color(1f, 0.5f, 0f, 1f);
            main.maxParticles = (int)(200 * intensity);
            main.gravityModifier = -0.3f * scale;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 50f * intensity;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 15f;
            shape.radius = 0.3f * scale;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.8f, 0f), 0f), new GradientColorKey(new Color(1f, 0.2f, 0f), 0.5f), new GradientColorKey(new Color(0.3f, 0.05f, 0f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0f, 1f) }
            );
            col.color = new ParticleSystem.MinMaxGradient(gradient);

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.2f));

            renderer.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private static void ApplySmokeTemplate(ParticleSystem ps, ParticleSystemRenderer renderer, float scale, float intensity)
        {
            var main = ps.main;
            main.duration = 2f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2f * scale, 4f * scale);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f * scale, 1.5f * scale);
            main.startSize = new ParticleSystem.MinMaxCurve(0.3f * scale, 0.8f * scale);
            main.startColor = new Color(0.5f, 0.5f, 0.5f, 0.4f);
            main.maxParticles = (int)(100 * intensity);
            main.gravityModifier = -0.1f * scale;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 15f * intensity;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 25f;
            shape.radius = 0.5f * scale;

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.3f, 1f, 1f));

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(0.4f, 0.4f, 0.4f), 0f), new GradientColorKey(new Color(0.6f, 0.6f, 0.6f), 1f) },
                new[] { new GradientAlphaKey(0.4f, 0f), new GradientAlphaKey(0f, 1f) }
            );
            col.color = new ParticleSystem.MinMaxGradient(gradient);

            renderer.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private static void ApplyRainTemplate(ParticleSystem ps, ParticleSystemRenderer renderer, float scale, float intensity)
        {
            var main = ps.main;
            main.duration = 1f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 1.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(10f * scale, 15f * scale);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f * scale, 0.05f * scale);
            main.startColor = new Color(0.7f, 0.8f, 0.9f, 0.6f);
            main.maxParticles = (int)(500 * intensity);
            main.gravityModifier = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 200f * intensity;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(10f * scale, 0.1f, 10f * scale);
            shape.position = new Vector3(0f, 10f * scale, 0f);

            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 3f;
        }

        private static void ApplySparksTemplate(ParticleSystem ps, ParticleSystemRenderer renderer, float scale, float intensity)
        {
            var main = ps.main;
            main.duration = 0.5f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.2f, 0.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(5f * scale, 15f * scale);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f * scale, 0.08f * scale);
            main.startColor = new Color(1f, 0.9f, 0.5f, 1f);
            main.maxParticles = (int)(300 * intensity);
            main.gravityModifier = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 80f * intensity;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.1f * scale;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 1f, 0.6f), 0f), new GradientColorKey(new Color(1f, 0.3f, 0f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) }
            );
            col.color = new ParticleSystem.MinMaxGradient(gradient);

            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 2f;
        }

        private static void ApplySnowTemplate(ParticleSystem ps, ParticleSystemRenderer renderer, float scale, float intensity)
        {
            var main = ps.main;
            main.duration = 2f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(3f, 6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f * scale, 1.5f * scale);
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f * scale, 0.1f * scale);
            main.startColor = new Color(0.95f, 0.95f, 1f, 0.8f);
            main.maxParticles = (int)(400 * intensity);
            main.gravityModifier = 0.1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 60f * intensity;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(10f * scale, 0.1f, 10f * scale);
            shape.position = new Vector3(0f, 8f * scale, 0f);

            renderer.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private static void ApplyDustTemplate(ParticleSystem ps, ParticleSystemRenderer renderer, float scale, float intensity)
        {
            var main = ps.main;
            main.duration = 5f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(3f, 8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.01f * scale, 0.04f * scale);
            main.startColor = new Color(0.8f, 0.75f, 0.65f, 0.3f);
            main.maxParticles = (int)(100 * intensity);
            main.gravityModifier = -0.02f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 10f * intensity;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(5f * scale, 3f * scale, 5f * scale);

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(0.8f, 0.75f, 0.65f), 0f), new GradientColorKey(new Color(0.8f, 0.75f, 0.65f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.3f, 0.3f), new GradientAlphaKey(0f, 1f) }
            );
            col.color = new ParticleSystem.MinMaxGradient(gradient);

            renderer.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private static void ApplyFountainTemplate(ParticleSystem ps, ParticleSystemRenderer renderer, float scale, float intensity)
        {
            var main = ps.main;
            main.duration = 1f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1f, 2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(5f * scale, 8f * scale);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f * scale, 0.15f * scale);
            main.startColor = new Color(0.5f, 0.7f, 1f, 0.7f);
            main.maxParticles = (int)(300 * intensity);
            main.gravityModifier = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 80f * intensity;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 10f;
            shape.radius = 0.1f * scale;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(0.6f, 0.8f, 1f), 0f), new GradientColorKey(new Color(0.3f, 0.5f, 0.9f), 1f) },
                new[] { new GradientAlphaKey(0.8f, 0f), new GradientAlphaKey(0f, 1f) }
            );
            col.color = new ParticleSystem.MinMaxGradient(gradient);

            renderer.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private static void ApplyFirefliesTemplate(ParticleSystem ps, ParticleSystemRenderer renderer, float scale, float intensity)
        {
            var main = ps.main;
            main.duration = 5f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(3f, 6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f * scale, 0.06f * scale);
            main.startColor = new Color(0.8f, 1f, 0.3f, 1f);
            main.maxParticles = (int)(50 * intensity);
            main.gravityModifier = 0f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 8f * intensity;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(5f * scale, 2f * scale, 5f * scale);

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(0.8f, 1f, 0.3f), 0f), new GradientColorKey(new Color(0.5f, 0.9f, 0.2f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(1f, 0.5f), new GradientAlphaKey(0f, 0.8f), new GradientAlphaKey(0.5f, 1f) }
            );
            col.color = new ParticleSystem.MinMaxGradient(gradient);

            renderer.renderMode = ParticleSystemRenderMode.Billboard;
        }

        #endregion
    }
}
