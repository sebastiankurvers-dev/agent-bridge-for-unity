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
        [BridgeRoute("POST", "/scene/quality-checks", Category = "scene", Description = "Run scene quality validation gate", ReadOnly = true, TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string RunSceneQualityChecks(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<SceneQualityChecksRequest>(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData)
                    ?? new SceneQualityChecksRequest();

                bool includeInactive = request.includeInactive != 0;
                bool includeInfo = request.includeInfo != 0;
                bool checkRequireComponents = request.checkRequireComponents != 0;
                bool checkSerializedReferences = request.checkSerializedReferences != 0;
                bool checkPhysicsSanity = request.checkPhysicsSanity != 0;
                bool checkUISanity = request.checkUISanity != 0;
                bool checkLifecycleHeuristics = request.checkLifecycleHeuristics != 0;
                bool checkRenderingHealth = request.checkRenderingHealth != 0;
                bool checkObjectScales = request.checkObjectScales != 0;
                int maxIssues = Mathf.Clamp(request.maxIssues <= 0 ? 200 : request.maxIssues, 10, 5000);
                string failOnSeverity = NormalizeSeverity(request.failOnSeverity, "error");
                int failThreshold = SeverityRank(failOnSeverity);

                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    return JsonError("No active loaded scene");
                }

                var allObjects = new List<GameObject>();
                var rootObjects = scene.GetRootGameObjects();
                foreach (var root in rootObjects)
                {
                    foreach (var tr in root.GetComponentsInChildren<Transform>(includeInactive))
                    {
                        if (tr != null && tr.gameObject != null)
                        {
                            allObjects.Add(tr.gameObject);
                        }
                    }
                }

                var issues = new List<SceneQualityIssue>();
                var lifecycleCache = new Dictionary<string, LifecycleScriptSignal>(StringComparer.OrdinalIgnoreCase);
                var lifecycleWarnedScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var activeCanvases = new List<Canvas>();
                var activeEventSystems = new List<EventSystem>();
                var activeAudioListeners = new List<AudioListener>();
                var checksRun = new List<object>
                {
                    "missing_scripts",
                    "invalid_tags_layers"
                };

                if (checkRequireComponents) checksRun.Add("require_component_integrity");
                if (checkSerializedReferences) checksRun.Add("broken_serialized_references");
                if (checkPhysicsSanity) checksRun.Add("physics_setup_sanity");
                if (checkUISanity) checksRun.Add("ui_eventsystem_sanity");
                if (checkLifecycleHeuristics) checksRun.Add("lifecycle_event_unsubscribe_heuristic");
                if (checkRenderingHealth) checksRun.Add("rendering_health");
                if (checkObjectScales) checksRun.Add("scale_anomaly");

                bool truncated = false;
                int checkedComponentCount = 0;

                void AddIssue(
                    string id,
                    string severity,
                    string category,
                    string message,
                    GameObject go = null,
                    string component = null,
                    string propertyPath = null,
                    string suggestion = null,
                    bool heuristic = false)
                {
                    var normalizedSeverity = NormalizeSeverity(severity, "warning");
                    if (!includeInfo && string.Equals(normalizedSeverity, "info", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (issues.Count >= maxIssues)
                    {
                        truncated = true;
                        return;
                    }

                    issues.Add(new SceneQualityIssue
                    {
                        id = id,
                        severity = normalizedSeverity,
                        category = category ?? "general",
                        message = message ?? "Issue detected",
                        suggestion = suggestion ?? string.Empty,
                        instanceId = go != null ? go.GetInstanceID() : 0,
                        objectName = go != null ? go.name : string.Empty,
                        path = go != null ? GetHierarchyPath(go.transform) : string.Empty,
                        component = component ?? string.Empty,
                        propertyPath = propertyPath ?? string.Empty,
                        heuristic = heuristic
                    });
                }

                IEnumerable<Type> EnumerateRequiredTypes(Type componentType)
                {
                    var attrs = componentType.GetCustomAttributes(typeof(RequireComponent), true);
                    foreach (var raw in attrs)
                    {
                        if (raw is not RequireComponent attr) continue;
                        if (attr.m_Type0 != null) yield return attr.m_Type0;
                        if (attr.m_Type1 != null) yield return attr.m_Type1;
                        if (attr.m_Type2 != null) yield return attr.m_Type2;
                    }
                }

                bool HasDeclaredMethod(Type type, string methodName)
                {
                    return type.GetMethod(
                        methodName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) != null;
                }

                LifecycleScriptSignal GetLifecycleSignal(MonoBehaviour monoBehaviour)
                {
                    var signal = new LifecycleScriptSignal();
                    if (monoBehaviour == null) return signal;

                    MonoScript script = null;
                    try { script = MonoScript.FromMonoBehaviour(monoBehaviour); }
                    catch { }

                    if (script == null) return signal;

                    var scriptPath = AssetDatabase.GetAssetPath(script);
                    if (string.IsNullOrWhiteSpace(scriptPath))
                    {
                        return signal;
                    }

                    if (lifecycleCache.TryGetValue(scriptPath, out var cached))
                    {
                        return cached;
                    }

                    signal.scriptPath = scriptPath;
                    var fullPath = ValidateAssetPath(scriptPath);
                    if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                    {
                        lifecycleCache[scriptPath] = signal;
                        return signal;
                    }

                    string source = string.Empty;
                    try
                    {
                        source = File.ReadAllText(fullPath);
                    }
                    catch
                    {
                        lifecycleCache[scriptPath] = signal;
                        return signal;
                    }

                    signal.containsSubscription = source.Contains("+=", StringComparison.Ordinal);
                    signal.containsUnsubscription = source.Contains("-=", StringComparison.Ordinal);
                    lifecycleCache[scriptPath] = signal;
                    return signal;
                }

                foreach (var go in allObjects)
                {
                    if (go == null) continue;
                    string scanStage = "init";
                    string renderingSubStage = "n/a";
                    try
                    {

                    scanStage = "missing_scripts";
                    int missingScripts = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                    if (missingScripts > 0)
                    {
                        AddIssue(
                            id: "missing_script",
                            severity: "error",
                            category: "integrity",
                            message: $"{missingScripts} missing MonoBehaviour script reference(s).",
                            go: go,
                            component: "MonoBehaviour",
                            suggestion: "Remove missing components or restore the deleted script assets.");
                    }

                    scanStage = "layer_name";
                    if (string.IsNullOrEmpty(LayerMask.LayerToName(go.layer)))
                    {
                        AddIssue(
                            id: "invalid_layer",
                            severity: "warning",
                            category: "integrity",
                            message: $"Layer index {go.layer} has no registered layer name.",
                            go: go,
                            suggestion: "Assign a valid layer in TagManager or move this object to a defined layer.");
                    }

                    scanStage = "tag_read";
                    try
                    {
                        _ = go.tag;
                    }
                    catch (UnityException ex)
                    {
                        AddIssue(
                            id: "invalid_tag",
                            severity: "error",
                            category: "integrity",
                            message: $"Invalid tag assignment: {ex.Message}",
                            go: go,
                            suggestion: "Reassign to an existing tag or recreate the missing tag in TagManager.");
                    }

                    scanStage = "components_collect";
                    var components = go.GetComponents<Component>();
                    checkedComponentCount += components.Length;

                    bool hasRigidbody3D = false;
                    bool hasCollider3D = false;
                    bool hasRigidbody2D = false;
                    bool hasCollider2D = false;

                    foreach (var component in components)
                    {
                        if (component == null) continue;

                        if (component is Collider) hasCollider3D = true;
                        if (component is Collider2D) hasCollider2D = true;
                        if (component is Rigidbody) hasRigidbody3D = true;
                        if (component is Rigidbody2D) hasRigidbody2D = true;

                        if (component is Canvas canvas && go.activeInHierarchy && canvas.enabled)
                        {
                            activeCanvases.Add(canvas);
                        }

                        if (component is EventSystem eventSystem && go.activeInHierarchy && eventSystem.enabled)
                        {
                            activeEventSystems.Add(eventSystem);
                        }

                        if (component is AudioListener audioListener && go.activeInHierarchy && audioListener.enabled)
                        {
                            activeAudioListeners.Add(audioListener);
                        }

                        if (checkRequireComponents)
                        {
                            scanStage = "require_component_scan";
                            foreach (var requiredType in EnumerateRequiredTypes(component.GetType()))
                            {
                                if (requiredType == null) continue;
                                if (go.GetComponent(requiredType) != null) continue;

                                AddIssue(
                                    id: "missing_required_component",
                                    severity: "error",
                                    category: "integrity",
                                    message: $"{component.GetType().Name} requires {requiredType.Name}, but it is missing.",
                                    go: go,
                                    component: component.GetType().Name,
                                    suggestion: $"Add {requiredType.Name} to satisfy [RequireComponent].");
                            }
                        }

                        if (checkSerializedReferences && component is MonoBehaviour monoBehaviour)
                        {
                            scanStage = "serialized_reference_scan";
                            try
                            {
                                var serializedObject = new SerializedObject(monoBehaviour);
                                var iterator = serializedObject.GetIterator();
                                if (iterator.NextVisible(true))
                                {
                                    do
                                    {
                                        if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                                        if (string.Equals(iterator.propertyPath, "m_Script", StringComparison.Ordinal)) continue;
                                        if (iterator.objectReferenceValue != null) continue;
                                        if (iterator.objectReferenceInstanceIDValue == 0) continue;

                                        AddIssue(
                                            id: "missing_object_reference",
                                            severity: "error",
                                            category: "references",
                                            message: $"Broken serialized object reference '{iterator.displayName}'.",
                                            go: go,
                                            component: component.GetType().Name,
                                            propertyPath: iterator.propertyPath,
                                            suggestion: "Reassign the missing reference in the Inspector.");
                                    } while (iterator.NextVisible(false));
                                }
                            }
                            catch (Exception ex)
                            {
                                AddIssue(
                                    id: "serialized_scan_failed",
                                    severity: "info",
                                    category: "diagnostics",
                                    message: $"Failed to inspect serialized references: {ex.Message}",
                                    go: go,
                                    component: component.GetType().Name);
                            }
                        }

                        if (checkLifecycleHeuristics && component is MonoBehaviour lifecycleTarget)
                        {
                            scanStage = "lifecycle_scan";
                            var lifecycleSignal = GetLifecycleSignal(lifecycleTarget);
                            if (string.IsNullOrWhiteSpace(lifecycleSignal.scriptPath)) continue;
                            if (!lifecycleSignal.containsSubscription || lifecycleSignal.containsUnsubscription) continue;
                            if (!lifecycleWarnedScripts.Add(lifecycleSignal.scriptPath)) continue;

                            var type = lifecycleTarget.GetType();
                            bool hasOnEnable = HasDeclaredMethod(type, "OnEnable");
                            bool hasOnDisable = HasDeclaredMethod(type, "OnDisable");
                            bool hasOnDestroy = HasDeclaredMethod(type, "OnDestroy");

                            if (!hasOnEnable) continue;

                            AddIssue(
                                id: "event_unsubscribe_risk",
                                severity: "warning",
                                category: "lifecycle",
                                message: $"{type.Name} appears to subscribe to events but has no explicit unsubscribe pattern in source.",
                                go: go,
                                component: type.Name,
                                suggestion: hasOnDisable || hasOnDestroy
                                    ? "Verify event unsubscription logic in OnDisable/OnDestroy."
                                    : "Add OnDisable or OnDestroy with event unsubscription for safety.",
                                heuristic: true);
                        }
                    }

                    if (checkPhysicsSanity)
                    {
                        scanStage = "physics_sanity";
                        if (hasRigidbody3D && !hasCollider3D)
                        {
                            AddIssue(
                                id: "rigidbody_without_collider",
                                severity: "warning",
                                category: "physics",
                                message: "Rigidbody found without a Collider component.",
                                go: go,
                                suggestion: "Add a Collider or remove Rigidbody if physics interaction is not needed.");
                        }

                        if (hasRigidbody2D && !hasCollider2D)
                        {
                            AddIssue(
                                id: "rigidbody2d_without_collider2d",
                                severity: "warning",
                                category: "physics",
                                message: "Rigidbody2D found without a Collider2D component.",
                                go: go,
                                suggestion: "Add Collider2D or remove Rigidbody2D if physics interaction is not needed.");
                        }
                    }

                    // ── Rendering health checks ──
                    if (checkRenderingHealth)
                    {
                        scanStage = "rendering_health";
                        renderingSubStage = "get_renderers";
                        var renderers = go.GetComponents<Renderer>();
                        foreach (var rend in renderers)
                        {
                            if (rend == null) continue;

                            // Check renderer enabled state on active objects
                            if (go.activeInHierarchy && !rend.enabled)
                            {
                                AddIssue(
                                    id: "renderer_disabled",
                                    severity: "info",
                                    category: "rendering",
                                    message: $"Renderer ({rend.GetType().Name}) is disabled on an active GameObject.",
                                    go: go,
                                    component: rend.GetType().Name,
                                    suggestion: "Enable the renderer or deactivate the GameObject if intentionally hidden.");
                            }

                            // Check for null/missing materials
                            renderingSubStage = "renderer_shared_materials";
                            var sharedMats = rend.sharedMaterials;
                            for (int mi = 0; mi < sharedMats.Length; mi++)
                            {
                                if (sharedMats[mi] == null)
                                {
                                    AddIssue(
                                        id: "renderer_null_material",
                                        severity: "error",
                                        category: "rendering",
                                        message: $"Renderer ({rend.GetType().Name}) has a null material at slot {mi}. Object will render pink/black.",
                                        go: go,
                                        component: rend.GetType().Name,
                                        suggestion: "Assign a valid material to the renderer.");
                                }
                                else
                                {
                                    var mat = sharedMats[mi];
                                    // Check for missing/invalid shader
                                    if (mat.shader == null || mat.shader.name == "Hidden/InternalErrorShader")
                                    {
                                        AddIssue(
                                            id: "renderer_invalid_shader",
                                            severity: "error",
                                            category: "rendering",
                                            message: $"Material '{mat.name}' on slot {mi} has an invalid/error shader. Object will render magenta.",
                                            go: go,
                                            component: rend.GetType().Name,
                                            suggestion: "Fix or reassign the material's shader (likely a URP/Built-in mismatch).");
                                    }

                                    // Check for Built-in shaders used in URP
                                    if (mat.shader != null)
                                    {
                                        var shaderName = mat.shader.name;
                                        if (shaderName.StartsWith("Standard", StringComparison.Ordinal)
                                            || shaderName.StartsWith("Legacy Shaders/", StringComparison.Ordinal)
                                            || shaderName.StartsWith("Mobile/", StringComparison.Ordinal))
                                        {
                                            // Verify we're actually in URP
                                            if (GraphicsSettings.currentRenderPipeline != null)
                                            {
                                                AddIssue(
                                                    id: "renderer_builtin_shader_in_srp",
                                                    severity: "warning",
                                                    category: "rendering",
                                                    message: $"Material '{mat.name}' uses Built-in shader '{shaderName}' in a Scriptable Render Pipeline project. May render incorrectly.",
                                                    go: go,
                                                    component: rend.GetType().Name,
                                                    suggestion: "Use a URP-compatible shader (Universal Render Pipeline/Lit or /Unlit).");
                                            }
                                        }
                                    }
                                }
                            }

                            // Check camera culling mask (is this object's layer visible to main camera?)
                            if (go.activeInHierarchy && rend.enabled)
                            {
                                renderingSubStage = "renderer_camera_culling";
                                var mainCam = Camera.main;
                                if (mainCam != null)
                                {
                                    int objLayerMask = 1 << go.layer;
                                    if ((mainCam.cullingMask & objLayerMask) == 0)
                                    {
                                        AddIssue(
                                            id: "renderer_layer_culled",
                                            severity: "warning",
                                            category: "rendering",
                                            message: $"Object on layer '{LayerMask.LayerToName(go.layer)}' ({go.layer}) is not in Main Camera's culling mask. It will be invisible.",
                                            go: go,
                                            component: rend.GetType().Name,
                                            suggestion: "Add the layer to the camera's culling mask, or move the object to a visible layer.");
                                    }
                                }
                            }
                        }

                        // Check for SkinnedMeshRenderer with no mesh
                        renderingSubStage = "skinned_mesh_checks";
                        var skinnedRenderers = go.GetComponents<SkinnedMeshRenderer>();
                        foreach (var smr in skinnedRenderers)
                        {
                            if (smr == null) continue;
                            if (smr.sharedMesh == null)
                            {
                                AddIssue(
                                    id: "skinned_renderer_no_mesh",
                                    severity: "error",
                                    category: "rendering",
                                    message: "SkinnedMeshRenderer has no mesh assigned.",
                                    go: go,
                                    component: nameof(SkinnedMeshRenderer),
                                    suggestion: "Assign a mesh to the SkinnedMeshRenderer or remove the component.");
                            }
                        }

                        // Check for MeshRenderer without MeshFilter (skip Camera objects — they can have MeshRenderer for gizmos)
                        renderingSubStage = "mesh_renderer_filter_check";
                        var cameraComponent = go.GetComponent<Camera>();
                        var meshRenderer = go.GetComponent<MeshRenderer>();
                        var meshFilter = go.GetComponent<MeshFilter>();

                        if (cameraComponent == null && meshRenderer != null && meshFilter == null)
                        {
                            AddIssue(
                                id: "mesh_renderer_no_filter",
                                severity: "error",
                                category: "rendering",
                                message: "MeshRenderer found without a MeshFilter component. Nothing will render.",
                                go: go,
                                component: nameof(MeshRenderer),
                                suggestion: "Add a MeshFilter with a mesh, or remove the MeshRenderer.");
                        }

                        renderingSubStage = "mesh_filter_mesh_check";
                        if (meshFilter != null && meshRenderer != null && meshFilter.sharedMesh == null)
                        {
                            AddIssue(
                                id: "mesh_filter_no_mesh",
                                severity: "error",
                                category: "rendering",
                                message: "MeshFilter has no mesh assigned. MeshRenderer will render nothing.",
                                go: go,
                                component: nameof(MeshFilter),
                                suggestion: "Assign a mesh to the MeshFilter.");
                        }
                    }

                    // ── Scale anomaly checks ──
                    if (checkObjectScales)
                    {
                        scanStage = "scale_anomaly";
                        var ls = go.transform.localScale;
                        float maxAxis = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
                        float minAxis = Mathf.Min(Mathf.Abs(ls.x), Mathf.Min(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));

                        if (maxAxis > 10f)
                        {
                            AddIssue(
                                id: "scale_anomaly_large",
                                severity: "warning",
                                category: "scale",
                                message: $"localScale ({ls.x:F2}, {ls.y:F2}, {ls.z:F2}) has axis > 10. Likely unintended (e.g. imported at wrong scale).",
                                go: go,
                                suggestion: "Set localScale to (1,1,1) and adjust the mesh import scale instead, or verify the scale is intentional.");
                        }
                        else if (minAxis > 0f && minAxis < 0.01f)
                        {
                            AddIssue(
                                id: "scale_anomaly_tiny",
                                severity: "warning",
                                category: "scale",
                                message: $"localScale ({ls.x:F4}, {ls.y:F4}, {ls.z:F4}) has axis < 0.01. Object may be invisible.",
                                go: go,
                                suggestion: "Check mesh import scale or set localScale to (1,1,1).");
                        }

                        // Check renderer bounds vs mesh bounds ratio for extreme scaling
                        var scaleRenderer = go.GetComponent<Renderer>();
                        var scaleMeshFilter = go.GetComponent<MeshFilter>();
                        if (scaleRenderer != null && scaleMeshFilter != null && scaleMeshFilter.sharedMesh != null)
                        {
                            float rendererSize = scaleRenderer.bounds.size.magnitude;
                            float meshSize = scaleMeshFilter.sharedMesh.bounds.size.magnitude;
                            if (meshSize > 0.001f)
                            {
                                float ratio = rendererSize / meshSize;
                                if (ratio > 50f)
                                {
                                    AddIssue(
                                        id: "scale_anomaly_bounds_ratio",
                                        severity: "warning",
                                        category: "scale",
                                        message: $"Renderer bounds ({rendererSize:F1}) / mesh bounds ({meshSize:F3}) ratio is {ratio:F1}x — indicates extreme scaling.",
                                        go: go,
                                        suggestion: "Reduce localScale and adjust mesh import scale to keep ratio close to 1x.");
                                }
                            }
                        }
                    }
                    }
                    catch (Exception ex)
                    {
                        var stageLabel = scanStage == "rendering_health"
                            ? $"{scanStage}:{renderingSubStage}"
                            : scanStage;
                        AddIssue(
                            id: "quality_check_object_scan_failed",
                            severity: "warning",
                            category: "diagnostics",
                            message: $"Scene quality scan skipped object at stage '{stageLabel}' due to exception: {ex.Message}",
                            go: go,
                            suggestion: "Inspect this object/components manually; this warning indicates a scanner-side compatibility edge case.");
                    }
                }

                if (checkUISanity)
                {
                    if (activeCanvases.Count > 0 && activeEventSystems.Count == 0)
                    {
                        AddIssue(
                            id: "ui_missing_eventsystem",
                            severity: "error",
                            category: "ui",
                            message: $"Found {activeCanvases.Count} active Canvas object(s) but no active EventSystem.",
                            suggestion: "Create an EventSystem (and input module) for UI interaction.");
                    }

                    if (activeEventSystems.Count > 1)
                    {
                        AddIssue(
                            id: "ui_multiple_eventsystems",
                            severity: "warning",
                            category: "ui",
                            message: $"Found {activeEventSystems.Count} active EventSystem components.",
                            go: activeEventSystems[0].gameObject,
                            component: nameof(EventSystem),
                            suggestion: "Keep a single active EventSystem to avoid duplicate UI input handling.");
                    }

                    foreach (var canvas in activeCanvases)
                    {
                        if (canvas == null || canvas.gameObject == null) continue;
                        if (canvas.GetComponent<GraphicRaycaster>() != null) continue;
                        if (canvas.GetComponentsInChildren<Selectable>(includeInactive).Length == 0) continue;

                        AddIssue(
                            id: "ui_missing_graphic_raycaster",
                            severity: "warning",
                            category: "ui",
                            message: "Canvas contains selectable UI elements but no GraphicRaycaster component.",
                            go: canvas.gameObject,
                            component: nameof(Canvas),
                            suggestion: "Add GraphicRaycaster to enable pointer interaction.");
                    }

                    foreach (var eventSystem in activeEventSystems)
                    {
                        if (eventSystem == null || eventSystem.gameObject == null) continue;
                        if (eventSystem.GetComponent<BaseInputModule>() != null) continue;

                        AddIssue(
                            id: "ui_missing_input_module",
                            severity: "error",
                            category: "ui",
                            message: "EventSystem has no BaseInputModule component.",
                            go: eventSystem.gameObject,
                            component: nameof(EventSystem),
                            suggestion: "Add StandaloneInputModule or InputSystemUIInputModule.");
                    }
                }

                if (activeAudioListeners.Count > 1)
                {
                    AddIssue(
                        id: "multiple_audio_listeners",
                        severity: "error",
                        category: "audio",
                        message: $"Found {activeAudioListeners.Count} active AudioListener components in scene.",
                        go: activeAudioListeners[0] != null ? activeAudioListeners[0].gameObject : null,
                        component: nameof(AudioListener),
                        suggestion: "Keep exactly one active AudioListener.");
                }

                issues = issues
                    .OrderByDescending(i => SeverityRank(i.severity))
                    .ThenBy(i => i.path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(i => i.id, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                int errorCount = issues.Count(i => string.Equals(i.severity, "error", StringComparison.OrdinalIgnoreCase));
                int warningCount = issues.Count(i => string.Equals(i.severity, "warning", StringComparison.OrdinalIgnoreCase));
                int infoCount = issues.Count(i => string.Equals(i.severity, "info", StringComparison.OrdinalIgnoreCase));
                int maxObservedSeverityRank = issues.Count == 0 ? 0 : issues.Max(i => SeverityRank(i.severity));
                string maxObservedSeverity = maxObservedSeverityRank <= 0 ? "none" : SeverityFromRank(maxObservedSeverityRank);
                bool failed = maxObservedSeverityRank >= failThreshold && failThreshold > 0;

                var categoryCounts = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in issues.GroupBy(i => i.category ?? "general", StringComparer.OrdinalIgnoreCase))
                {
                    categoryCounts[group.Key] = group.Count();
                }

                var response = new Dictionary<string, object>
                {
                    { "success", true },
                    { "summary", new Dictionary<string, object>
                        {
                            { "sceneName", scene.name },
                            { "scenePath", scene.path },
                            { "checkedObjectCount", allObjects.Count },
                            { "checkedComponentCount", checkedComponentCount },
                            { "issueCount", issues.Count },
                            { "errorCount", errorCount },
                            { "warningCount", warningCount },
                            { "infoCount", infoCount },
                            { "maxSeverity", maxObservedSeverity },
                            { "failed", failed },
                            { "passed", !failed },
                            { "failOnSeverity", failOnSeverity },
                            { "issuesTruncated", truncated },
                            { "checksRun", checksRun },
                            { "categoryCounts", categoryCounts }
                        }
                    },
                    { "issues", issues.Select(i => i.ToJson()).Cast<object>().ToList() }
                };

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private sealed class SceneQualityIssue
        {
            public string id;
            public string severity;
            public string category;
            public string message;
            public string suggestion;
            public int instanceId;
            public string objectName;
            public string path;
            public string component;
            public string propertyPath;
            public bool heuristic;

            public Dictionary<string, object> ToJson()
            {
                var payload = new Dictionary<string, object>
                {
                    { "id", id ?? string.Empty },
                    { "severity", severity ?? "warning" },
                    { "category", category ?? "general" },
                    { "message", message ?? "Issue detected" },
                    { "heuristic", heuristic }
                };

                if (!string.IsNullOrWhiteSpace(suggestion)) payload["suggestion"] = suggestion;
                if (instanceId != 0) payload["instanceId"] = instanceId;
                if (!string.IsNullOrWhiteSpace(objectName)) payload["objectName"] = objectName;
                if (!string.IsNullOrWhiteSpace(path)) payload["path"] = path;
                if (!string.IsNullOrWhiteSpace(component)) payload["component"] = component;
                if (!string.IsNullOrWhiteSpace(propertyPath)) payload["propertyPath"] = propertyPath;
                return payload;
            }
        }
    }
}
