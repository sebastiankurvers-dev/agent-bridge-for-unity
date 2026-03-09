using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class WorkflowGuideTools
{
    [McpServerTool(Name = "unity_workflow_guide")]
    [Description(@"Get guided workflow recommendations for Unity tasks. Returns the exact tool sequence, required parameters, common pitfalls, and examples.

Call this FIRST when you're unsure which tools to use for a task. It covers all 225 tools organized into practical workflows.

Query modes:
  - task=""build a scene"" → returns step-by-step tool sequence for scene building
  - task=""list"" → returns all available workflow categories
  - task=""materials"" → returns all material-related workflows

Examples:
  unity_workflow_guide(task=""create a UI with buttons and text"")
  unity_workflow_guide(task=""set up physics on an object"")
  unity_workflow_guide(task=""debug why an object is invisible"")
  unity_workflow_guide(task=""list"")")]
    public static Task<string> GetWorkflowGuide(
        [Description("Describe what you want to accomplish, or 'list' to see all workflow categories.")] string task)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return Task.FromResult(JsonSerializer.Serialize(new { error = "Provide a task description or 'list' for categories." }));
        }

        var normalized = task.Trim().ToLowerInvariant();

        if (normalized == "list" || normalized == "categories" || normalized == "help")
        {
            return Task.FromResult(BuildCategoryList());
        }

        var matches = FindMatchingWorkflows(normalized);
        if (matches.Count == 0)
        {
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                message = "No specific workflow found. Try broader terms or 'list' for categories.",
                suggestions = new[]
                {
                    "scene building", "materials", "lighting", "physics", "animation",
                    "ui", "prefabs", "scripts", "debugging", "performance", "play mode"
                }
            }));
        }

        return Task.FromResult(BuildWorkflowResponse(matches));
    }

    private static string BuildCategoryList()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Available workflow categories:");
        sb.AppendLine();
        foreach (var cat in s_workflows)
        {
            sb.AppendLine($"  {cat.Category} — {cat.Summary}");
        }
        sb.AppendLine();
        sb.AppendLine("Query any category by name, e.g. unity_workflow_guide(task=\"scene building\")");
        sb.AppendLine("Or describe your task, e.g. unity_workflow_guide(task=\"make an object glow\")");
        return sb.ToString();
    }

    private static List<Workflow> FindMatchingWorkflows(string query)
    {
        var results = new List<(Workflow wf, int score)>();

        foreach (var wf in s_workflows)
        {
            int score = 0;
            var fields = new[] { wf.Category, wf.Summary, string.Join(" ", wf.Keywords) };
            foreach (var field in fields)
            {
                if (field.Contains(query, StringComparison.OrdinalIgnoreCase))
                    score += 10;
            }
            // Check individual query words against keywords (both directions)
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                if (word.Length < 3) continue;
                // Check if any keyword contains this word or this word contains a keyword
                foreach (var kw in wf.Keywords)
                {
                    if (kw.Contains(word, StringComparison.OrdinalIgnoreCase)
                        || word.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        score += 5;
                }
                foreach (var field in fields)
                {
                    if (field.Contains(word, StringComparison.OrdinalIgnoreCase))
                        score += 2;
                }
                foreach (var step in wf.Steps)
                {
                    if (step.Contains(word, StringComparison.OrdinalIgnoreCase))
                        score += 1;
                }
            }
            if (score > 0) results.Add((wf, score));
        }

        results.Sort((a, b) => b.score.CompareTo(a.score));
        // Require minimum score to avoid false matches on nonsense queries
        return results.Where(r => r.score >= 4).Take(3).Select(r => r.wf).ToList();
    }

    private static string BuildWorkflowResponse(List<Workflow> workflows)
    {
        var sb = new StringBuilder();
        foreach (var wf in workflows)
        {
            sb.AppendLine($"## {wf.Category}");
            sb.AppendLine(wf.Summary);
            sb.AppendLine();

            sb.AppendLine("### Steps:");
            for (int i = 0; i < wf.Steps.Length; i++)
            {
                sb.AppendLine($"  {i + 1}. {wf.Steps[i]}");
            }
            sb.AppendLine();

            if (wf.Pitfalls.Length > 0)
            {
                sb.AppendLine("### Common pitfalls:");
                foreach (var p in wf.Pitfalls)
                {
                    sb.AppendLine($"  - {p}");
                }
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(wf.Example))
            {
                sb.AppendLine("### Example:");
                sb.AppendLine(wf.Example);
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ======================== WORKFLOW DATABASE ========================

    private record Workflow(
        string Category,
        string Summary,
        string[] Keywords,
        string[] Steps,
        string[] Pitfalls,
        string Example);

    private static readonly Workflow[] s_workflows = new[]
    {
        // ---- SCENE BUILDING ----
        new Workflow(
            "Scene Building",
            "Create and modify scenes with objects, lights, and spatial layout.",
            new[] { "scene", "build", "create", "layout", "place", "spawn", "object", "primitive", "cube", "sphere", "compound", "tree", "lantern", "fence", "steps", "building", "rock", "floating", "grounding", "sunk", "snap" },
            new[]
            {
                "unity_create_compound_shape(preset, ...) — One-call compound objects: tree, lantern, steps, fence, rock_cluster, simple_building (PREFERRED for scene reconstruction)",
                "unity_spawn_primitive(type, name, position) — Create basic shapes (Cube, Sphere, Cylinder, Capsule, Plane, Quad)",
                "unity_create_procedural_mesh(meshType, ...) — Create advanced shapes: cone, wedge (ramp), arch, torus, prism",
                "unity_create_raw_mesh(vertices, triangles, ...) — Create any mesh from raw vertex/triangle data",
                "unity_spawn_prefab(prefabPath, name, position, rotation, scale) — Instantiate prefabs",
                "unity_spawn_batch(items) — Spawn multiple objects in one call (more efficient)",
                "unity_scatter_objects(sourceInstanceId/prefabPath, count, boundsCenter, boundsSize) — Scatter copies with random transforms",
                "unity_create_scene_from_descriptor(descriptor) — Build a full scene from a JSON descriptor",
                "unity_scene_transaction(ops, checkpoint) — Atomic batch of create/modify/delete with auto-rollback",
                "unity_modify_gameobject(instanceId, ...) — Change name, tag, layer, static, position, rotation, scale",
                "unity_group_objects(instanceIds, name) — Group multiple objects under a new parent",
                "unity_snap_objects(sourceId, targetId, snapMode) — Snap objects together",
                "unity_audit_overlaps(rootInstanceId) — Detect mesh/bounds intersections after placement",
                "unity_resolve_overlaps(rootInstanceId) — Auto-nudge overlapping objects apart",
                "unity_audit_grounding(rootInstanceId) — Detect floating/sunk objects after placement",
                "unity_snap_to_ground(rootInstanceId) — Snap floating objects to terrain/ground",
                "unity_screenshot(viewType) — Verify results visually"
            },
            new[]
            {
                "BUILD ORDER: Structure first (terrain, major objects, camera) → then materials/lighting → then fine details. Do NOT tweak colors until composition matches.",
                "Use unity_create_compound_shape for trees, lanterns, steps, fences, rocks, buildings — much better than manually composing primitives",
                "Use unity_spawn_batch for 3+ objects — much faster than individual spawns",
                "OVERLAP CHECK: After placing multiple objects, run unity_audit_overlaps(rootInstanceId) to detect clipping. Then unity_resolve_overlaps to auto-fix, or manually adjust positions.",
                "GROUNDING CHECK: After placement, run unity_audit_grounding(rootInstanceId) to catch floating or sunk objects. Use unity_snap_to_ground to fix. Run AFTER overlap resolve to avoid re-floating.",
                "ROOF CLIPPING: When placing buildings close together, set roofOverhang=0 to prevent roofs from clipping through neighboring walls. Default overhang (0.02) is small but still clips in tight village layouts.",
                "ROOF CONSTRUCTION: When building gable roofs manually with two angled cubes, ALWAYS add a ridge beam at the peak — the panel thickness creates a V-gap where they meet. Place a small cube at (0, wallHeight+roofHeight, 0) spanning the full depth.",
                "For complex procedural scenes, use unity_execute_csharp with unity_register_execute_helpers to define reusable factory functions",
                "Always verify placement with unity_screenshot after building",
                "Negative instanceIds are valid — Unity uses them for new unsaved objects"
            },
            "  # Build a simple scene\n  unity_spawn_primitive(type=\"Plane\", name=\"Floor\", position=[0,0,0], scale=[10,1,10])\n  unity_spawn_primitive(type=\"Cube\", name=\"Wall\", position=[0,1,5])\n  unity_create_light(type=\"Directional\", rotation=[50,-30,0], shadows=\"Soft\")\n  unity_screenshot(viewType=\"scene\")"
        ),

        // ---- MATERIALS ----
        new Workflow(
            "Materials & Shaders",
            "Create, find, modify, and validate materials and shaders.",
            new[] { "material", "shader", "color", "texture", "render", "urp", "lit", "unlit", "emission", "glow", "transparent" },
            new[]
            {
                "unity_find_materials_scoped(folder) — Find materials in a folder (preferred over unity_find_materials)",
                "unity_create_material(path, shader) — Create new material with a specific shader",
                "unity_create_procedural_texture(textureType, ...) — Generate noise/gradient/checkerboard/bricks/stripes textures as PNG assets",
                "unity_modify_material(path, properties) — Set material properties (_BaseColor, _Metallic, _Smoothness, etc.)",
                "unity_validate_material(path) — Check material/shader compatibility with current render pipeline",
                "unity_set_renderer_materials(instanceId, materials) — Assign materials to scene objects",
                "unity_set_shader_keyword(materialPath, keyword, enabled) — Toggle shader features like _EMISSION",
                "unity_get_material_preview(path) — Get a visual thumbnail of the material",
                "unity_get_shader_properties(shaderName) — Discover what properties a shader exposes"
            },
            new[]
            {
                "Use unity_find_materials_scoped (not unity_find_materials) for broad searches",
                "Material property names are shader-specific — use unity_get_shader_properties to discover them",
                "For URP/Lit: _BaseColor, _Metallic, _Smoothness, _EmissionColor are the key properties",
                "Enable _EMISSION keyword before setting _EmissionColor, otherwise emission won't render",
                "Use unity_validate_material to catch pipeline mismatches (e.g., Built-in shader in URP project)"
            },
            "  # Create a glowing red material\n  unity_create_material(path=\"Assets/Materials/GlowRed.mat\", shader=\"Universal Render Pipeline/Lit\")\n  unity_set_shader_keyword(materialPath=\"Assets/Materials/GlowRed.mat\", keyword=\"_EMISSION\", enabled=true)\n  unity_modify_material(path=\"Assets/Materials/GlowRed.mat\", properties={\"_BaseColor\":[1,0,0,1], \"_EmissionColor\":[2,0,0,1]})"
        ),

        // ---- LIGHTING ----
        new Workflow(
            "Lighting & Rendering",
            "Set up lights, render settings, volumes, and visual look.",
            new[] { "light", "lighting", "directional", "point", "spot", "shadow", "ambient", "fog", "bloom", "volume", "exposure", "render", "skybox", "hdr" },
            new[]
            {
                "unity_create_light(type, color, intensity, ...) — Create Directional/Point/Spot/Area lights",
                "unity_modify_light(instanceId, ...) — Change light properties",
                "unity_get_render_settings() — Read ambient, fog, skybox settings",
                "unity_set_render_settings(properties) — Set ambient, fog, skybox",
                "unity_create_procedural_skybox(skyTint, groundColor, exposure, ...) — Create and apply a procedural skybox in one call",
                "unity_get_volume_profile() — Read URP volume overrides (bloom, color grading, etc.)",
                "unity_set_volume_profile_overrides(overrides) — Set bloom, exposure, color adjustments, vignette, DOF",
                "unity_get_camera_rendering() / unity_set_camera_rendering() — Camera HDR, post-processing, background",
                "unity_save_look_preset(name) — Save entire visual look as a reusable preset",
                "unity_load_look_preset(name) — Apply a saved look to any scene",
                "unity_audit_scene_lighting() — Check for lighting health issues"
            },
            new[]
            {
                "Always create at least one Directional light (sun) for basic scene visibility",
                "Use unity_save_look_preset to capture a polished demo scene's look for reuse",
                "Volume overrides require an active Volume component in the scene",
                "unity_apply_separation_safe_look is a quick fix for over-bloomed neon scenes"
            },
            "  # Set up outdoor lighting\n  unity_create_light(type=\"Directional\", name=\"Sun\", color=[1,0.95,0.8], intensity=1.5, rotation=[50,-30,0], shadows=\"Soft\")\n  unity_set_render_settings(ambientMode=\"Flat\", ambientColor=[0.4,0.5,0.6])\n  unity_set_volume_profile_overrides(overrides={\"bloom\":{\"intensity\":0.5,\"threshold\":1.2}})"
        ),

        // ---- PHYSICS ----
        new Workflow(
            "Physics Setup",
            "Configure rigidbodies, colliders, physics settings, and raycasting.",
            new[] { "physics", "rigidbody", "collider", "gravity", "collision", "trigger", "raycast", "force", "mass", "kinematic" },
            new[]
            {
                "unity_configure_rigidbody(instanceId, mass, useGravity, constraints, ...) — Add/configure Rigidbody",
                "unity_configure_collider(instanceId, type, ...) — Add/configure Box/Sphere/Capsule/Mesh collider",
                "unity_get_physics_settings() — Read gravity, solver iterations, etc.",
                "unity_set_physics_settings(gravity, ...) — Change physics configuration",
                "unity_raycast(origin, direction) — Cast a ray and get hit info",
                "unity_raycast_coverage_check(rootInstanceId) — Grid raycast for gap detection",
                "unity_audit_overlaps(rootInstanceId) — Detect mesh/bounds intersections between objects",
                "unity_resolve_overlaps(rootInstanceId, keepOnTerrain) — Auto-nudge overlapping objects apart",
                "unity_audit_grounding(rootInstanceId) — Detect floating/sunk objects",
                "unity_snap_to_ground(rootInstanceId) — Snap floating objects to terrain/ground"
            },
            new[]
            {
                "Always add a Collider AND a Rigidbody for physics-driven objects",
                "Use isKinematic=true for objects moved by code (not physics forces)",
                "Constraints use comma-separated names: \"FreezeRotationX,FreezeRotationZ\"",
                "unity_raycast requires a specific origin+direction; use unity_raycast_coverage_check for broad testing",
                "unity_audit_overlaps uses Bounds.Intersects (no colliders needed) + Physics.ComputePenetration (more accurate, needs colliders)",
                "GROUNDING: After resolving overlaps, run unity_audit_grounding to catch objects that were pushed into the air. Use unity_snap_to_ground to fix."
            },
            "  # Set up a physics object\n  unity_configure_rigidbody(instanceId=123, mass=2, useGravity=true, constraints=\"FreezeRotationX,FreezeRotationZ\")\n  unity_configure_collider(instanceId=123, type=\"Box\")"
        ),

        // ---- ANIMATION ----
        new Workflow(
            "Animation",
            "Create animator controllers, states, transitions, and animation clips.",
            new[] { "animation", "animator", "state", "transition", "clip", "parameter", "blend", "controller", "fbx" },
            new[]
            {
                "unity_create_animator_controller(path, parameters) — Create .controller with initial parameters",
                "unity_add_animation_state(controllerPath, stateName, layerIndex) — Add states to the controller",
                "unity_add_animation_transition(controllerPath, source, dest, conditions) — Connect states with conditions",
                "unity_add_animator_parameter(controllerPath, name, type) — Add Float/Int/Bool/Trigger parameters",
                "unity_get_animator_info(controllerPath) — Inspect states, transitions, parameters",
                "unity_create_animation_clip(path) — Create a new .anim clip",
                "unity_get_fbx_clips(fbxPath) — List animation clips embedded in FBX files"
            },
            new[]
            {
                "Condition JSON uses 'parameterName' (not 'parameter'): [{\"parameterName\":\"Speed\",\"mode\":\"Greater\",\"threshold\":0.5}]",
                "First state added to a layer becomes the default state",
                "Use 'any' as sourceStateName for AnyState transitions, 'entry' for Entry transitions",
                "Bool condition modes: 'If' (true) and 'IfNot' (false), threshold is ignored"
            },
            "  # Create a simple Idle→Run controller\n  unity_create_animator_controller(path=\"Assets/Anim/Player.controller\", parameters='[{\"name\":\"Speed\",\"type\":\"Float\"}]')\n  unity_add_animation_state(controllerPath=\"Assets/Anim/Player.controller\", stateName=\"Idle\", layerIndex=0)\n  unity_add_animation_state(controllerPath=\"Assets/Anim/Player.controller\", stateName=\"Run\", layerIndex=0)\n  unity_add_animation_transition(controllerPath=\"Assets/Anim/Player.controller\", sourceStateName=\"Idle\", destinationStateName=\"Run\", conditions='[{\"parameterName\":\"Speed\",\"mode\":\"Greater\",\"threshold\":0.5}]')"
        ),

        // ---- UI ----
        new Workflow(
            "UI (uGUI & UI Toolkit)",
            "Build user interfaces with either legacy uGUI (Canvas) or modern UI Toolkit.",
            new[] { "ui", "canvas", "button", "text", "label", "panel", "slider", "input", "toggle", "ugui", "uitoolkit", "uxml", "uss", "uidocument" },
            new[]
            {
                "--- uGUI (Canvas-based, legacy but widely used) ---",
                "unity_create_canvas(renderMode) — Create Canvas with CanvasScaler",
                "unity_create_button(parentId, text, width, height) — Create Button with TMP text",
                "unity_create_text(parentId, text, fontSize, color) — Create TMP text element",
                "unity_create_image / unity_create_panel / unity_create_slider / unity_create_input_field — Other UI elements",
                "unity_create_ui_element(parentId, type) — Generic: Toggle, Dropdown, Scrollbar, ScrollView",
                "unity_modify_rect_transform(instanceId, ...) — Position/size UI elements",
                "unity_modify_tmp_text(instanceId, text, ...) — Change text content/style",
                "unity_set_ui_color(instanceId, color) — Set element color",
                "",
                "--- UI Toolkit (modern, recommended for tools/editor UI) ---",
                "unity_create_panel_settings(path) — Create PanelSettings asset",
                "unity_create_uxml(path, content) / unity_create_uss(path, content) — Create UXML/USS files",
                "unity_create_ui_document(name, panelSettingsPath, uxmlPath) — Create UIDocument in scene",
                "unity_get_visual_tree(instanceId) — Inspect live visual tree (play mode only)",
                "unity_query_visual_elements(instanceId, query) — Find elements by USS selector",
                "unity_modify_visual_element / unity_create_visual_element — Modify/add elements at runtime",
                "unity_migrate_ugui_to_uitoolkit(canvasInstanceId) — Convert existing uGUI to UXML/USS"
            },
            new[]
            {
                "uGUI elements must be children of a Canvas — create Canvas first",
                "UI Toolkit visual tree is only available in play mode",
                "Use unity_migrate_ugui_to_uitoolkit to convert legacy UI to modern UI Toolkit",
                "TMP (TextMeshPro) is the default text renderer — use unity_modify_tmp_text, not unity_modify_component"
            },
            "  # Quick uGUI HUD\n  unity_create_canvas(renderMode=\"ScreenSpaceOverlay\")\n  unity_create_text(parentId=<canvasId>, text=\"Score: 0\", fontSize=32, color=[1,1,1,1])\n  unity_create_button(parentId=<canvasId>, text=\"Restart\", width=200, height=60)"
        ),

        // ---- PREFABS ----
        new Workflow(
            "Prefabs & Assets",
            "Create, find, modify, and manage prefabs and project assets.",
            new[] { "prefab", "asset", "variant", "instance", "fbx", "import", "folder", "duplicate", "move", "delete" },
            new[]
            {
                "unity_find_prefabs(query) — Search prefabs by name",
                "unity_find_prefabs_scoped(folder) — Find prefabs in a specific folder",
                "unity_create_prefab(instanceId, path) — Save scene object as prefab asset",
                "unity_spawn_prefab(prefabPath, name, position) — Instantiate prefab in scene",
                "unity_modify_prefab(path, ops) — Modify prefab asset (add components, change properties)",
                "unity_create_prefab_variant(basePrefabPath, variantPath) — Create a prefab variant",
                "unity_apply_prefab_overrides(instanceId) — Push scene changes back to prefab",
                "unity_get_prefab_geometry(path) — Get mesh/bounds data",
                "unity_get_prefab_footprint_2d(path) — Get 2D footprint for layout planning",
                "unity_duplicate_asset / unity_move_asset / unity_delete_asset — Asset file operations",
                "unity_create_folder(path) — Create project folder",
                "unity_get_asset_info(path) — Get GUID, type, size, dependencies"
            },
            new[]
            {
                "unity_find_prefabs uses name substring matching, not glob patterns",
                "Use unity_find_prefabs_scoped for folder-scoped searches",
                "After modifying a prefab instance in scene, use unity_apply_prefab_overrides to save changes back",
                "Prefab variants inherit from a base — changes to base propagate to variants"
            },
            ""
        ),

        // ---- SCRIPTS ----
        new Workflow(
            "Scripts & Code",
            "Create, read, and modify C# scripts. Execute arbitrary C# in the editor.",
            new[] { "script", "code", "csharp", "monobehaviour", "execute", "compile", "class", "component", "helper", "helpers", "factory", "boilerplate" },
            new[]
            {
                "unity_list_scripts() — List all scripts in the project",
                "unity_get_script(path) — Read script source code",
                "unity_get_script_structure(path) — Get class/method/field overview without full source",
                "unity_create_script(path, content) — Create a new .cs file",
                "unity_modify_script(path, content) — Replace script content",
                "unity_execute_csharp(code) — Run arbitrary C# in editor context (registered helpers are auto-prepended)",
                "unity_register_execute_helpers(name, code) — Register reusable helper functions that persist across execute_csharp calls",
                "unity_list_execute_helpers() — List registered helper sets",
                "unity_clear_execute_helpers() — Clear all helpers (use at session start)",
                "unity_get_type_schema(typeName) — Get JSON schema for any C# type",
                "unity_get_derived_types(baseTypeName) — Find all types implementing an interface/base",
                "unity_get_compilation_status() — Check if scripts are compiling",
                "unity_get_compilation_errors() — Get compilation errors",
                "unity_trigger_recompile() — Force script recompilation"
            },
            new[]
            {
                "After creating/modifying scripts, wait for compilation: unity_poll_events(timeout=5) to catch compilation_finished",
                "unity_execute_csharp wraps code in a method — use Print() to return values",
                "IMPORTANT: Use renderer.sharedMaterial (not renderer.material) to avoid material leaks in edit mode",
                "For multi-step building (3+ execute_csharp calls), register helper functions FIRST with unity_register_execute_helpers to avoid repeating factory/utility code",
                "Call unity_clear_execute_helpers() at the start of a new session to avoid stale helpers from previous sessions",
                "unity_get_type_schema is useful for discovering serialized fields before using unity_patch_serialized_properties"
            },
            "  # Multi-step scene building with helpers\n  unity_clear_execute_helpers()  # Fresh start\n  unity_register_execute_helpers(name=\"factories\", code=\"GameObject Box(string n, Vector3 p, Vector3 s, Color c) { var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = n; go.transform.position = p; go.transform.localScale = s; go.GetComponent<Renderer>().sharedMaterial.color = c; return go; }\")\n  unity_execute_csharp(code=\"Box(\\\"Wall\\\", new Vector3(0,1,5), new Vector3(10,2,0.2f), Color.white);\")  # helpers auto-available\n\n  # Create and verify a script\n  unity_create_script(path=\"Assets/Scripts/MyComponent.cs\", content=\"using UnityEngine;\\npublic class MyComponent : MonoBehaviour { public float speed = 5f; }\")\n  unity_poll_events(timeout=5)  # Wait for compilation\n  unity_get_compilation_errors()  # Verify no errors"
        ),

        // ---- COMPONENTS ----
        new Workflow(
            "Components & Properties",
            "Add, inspect, modify, and remove components on GameObjects.",
            new[] { "component", "property", "serialized", "inspector", "mesh", "renderer", "transform", "modify" },
            new[]
            {
                "unity_get_components(instanceId, namesOnly) — List components on an object",
                "unity_add_component(instanceId, componentType) — Add a component",
                "unity_remove_component(instanceId, componentType) — Remove a component",
                "unity_patch_serialized_properties(instanceId, patches) — Set serialized fields by property path (PREFERRED)",
                "unity_modify_component(instanceId, componentType, properties) — Set fields via JsonUtility (limited)",
                "unity_get_renderer_state(instanceId) — Get detailed renderer/material state",
                "unity_set_renderer_materials(instanceId, materials) — Assign materials to renderer"
            },
            new[]
            {
                "PREFER unity_patch_serialized_properties over unity_modify_component for built-in Unity types (Transform, Renderer, Light, etc.)",
                "unity_modify_component uses JsonUtility which CANNOT modify engine types — it will suggest using patch_serialized_properties",
                "Property paths use Unity's serialized format: 'm_CastShadows', 'm_ReceiveShadows', 'm_LocalPosition.x'",
                "Use unity_get_components with namesOnly=false to discover available serialized property paths"
            },
            "  # Modify a MeshRenderer's shadow settings\n  unity_patch_serialized_properties(instanceId=123, patches=[{\"propertyPath\":\"m_CastShadows\",\"value\":0},{\"propertyPath\":\"m_ReceiveShadows\",\"value\":false}])"
        ),

        // ---- DEBUGGING ----
        new Workflow(
            "Debugging & Diagnostics",
            "Find and fix rendering issues, missing objects, and scene problems.",
            new[] { "debug", "diagnose", "invisible", "missing", "black", "magenta", "error", "console", "audit", "broken", "floating", "grounding", "sunk" },
            new[]
            {
                "unity_get_console() — Read Unity console errors/warnings",
                "unity_clear_console() — Clear console",
                "unity_audit_renderers() — Detect null materials, invalid shaders, missing meshes",
                "unity_get_hierarchy_renderers(instanceId) — Check material assignments on a hierarchy",
                "unity_audit_scene_lighting() — Verify lighting health (too dark, over-exposed, etc.)",
                "unity_camera_visibility_audit() — Check which objects are visible/occluded from camera",
                "unity_audit_overlaps(rootInstanceId) — Detect mesh/bounds intersections between objects",
                "unity_resolve_overlaps(rootInstanceId) — Auto-nudge overlapping objects apart",
                "unity_audit_grounding(rootInstanceId) — Detect floating/sunk objects",
                "unity_snap_to_ground(rootInstanceId) — Snap floating objects to terrain/ground",
                "unity_identify_objects_at_points(points) — Identify objects at screen positions (from compare hotspots)",
                "unity_run_scene_quality_checks() — Comprehensive scene health validation",
                "unity_get_compilation_errors() — Check for script errors",
                "unity_screenshot(viewType=\"scene\") — Visual verification",
                "unity_multi_pov_snapshot(presets=\"all\") — Check object from multiple angles"
            },
            new[]
            {
                "Object invisible? Check: (1) unity_audit_renderers for null materials, (2) unity_camera_visibility_audit for frustum/occlusion, (3) unity_get_components to verify renderer exists and is enabled",
                "Magenta/pink = missing shader. Use unity_validate_material to check pipeline compatibility",
                "Black object = no lights or normals flipped. Use unity_audit_scene_lighting",
                "Use unity_multi_pov_snapshot to verify objects aren't clipping or hidden behind other geometry",
                "Objects clipping through each other? Use unity_audit_overlaps to detect, then unity_resolve_overlaps to auto-fix",
                "Objects floating or sunk into ground? Use unity_audit_grounding to detect, then unity_snap_to_ground to fix. Common after scatter/batch operations.",
                "Compare hotspot shows a problem region? Use unity_identify_objects_at_points with the hotspot coordinates to find which objects need fixing"
            },
            ""
        ),

        // ---- PERFORMANCE ----
        new Workflow(
            "Performance & Optimization",
            "Profile performance, set budgets, and track scene changes.",
            new[] { "performance", "profile", "fps", "framerate", "frame rate", "60fps", "draw calls", "memory", "optimize", "budget", "telemetry", "hotspot", "delta", "snapshot", "slow", "lag" },
            new[]
            {
                "unity_get_performance_telemetry(includeHotspots) — Quick FPS/drawcall/memory snapshot",
                "unity_capture_performance_baseline(name) — Save baseline for comparison",
                "unity_check_performance_budget(maxDrawCalls, maxTriangles, ...) — Validate against limits",
                "unity_get_script_hotspots() — Find expensive MonoBehaviours",
                "unity_capture_delta_snapshot(name) — Snapshot scene state before changes",
                "unity_get_delta(snapshotName) — See what changed since snapshot",
                "unity_get_mesh_info(instanceId) — Check vertex/triangle counts on objects"
            },
            new[]
            {
                "Capture a baseline BEFORE making changes, then check budget AFTER",
                "Delta snapshots are lost on domain reload (script recompilation)",
                "Performance telemetry is most meaningful during play mode"
            },
            "  # Performance check workflow\n  unity_capture_performance_baseline(name=\"before_changes\")\n  # ... make scene changes ...\n  unity_check_performance_budget(maxDrawCalls=200, maxTriangles=100000)"
        ),

        // ---- PLAY MODE & TESTING ----
        new Workflow(
            "Play Mode & Testing",
            "Control play mode, inspect runtime state, run tests, and verify gameplay.",
            new[] { "play", "runtime", "test", "invoke", "field", "contract", "event", "replay", "record", "gameplay" },
            new[]
            {
                "unity_play_mode(action) — Start/stop/pause/step play mode",
                "unity_play_mode_wait(action) — Start with stability wait (use before runtime calls)",
                "unity_get_runtime_values(instanceId, componentType, fieldNames) — Read live field values",
                "unity_set_runtime_fields(instanceId, componentType, fieldsJson) — Set field values at runtime",
                "unity_invoke_method(instanceId, componentType, methodName) — Call methods on components",
                "unity_invoke_sequence(instanceId, steps) — Call multiple methods in sequence",
                "unity_register_contracts(contracts) — Set up runtime invariant checks",
                "unity_query_contracts() — Check if invariants held",
                "unity_poll_events(since, timeout) — Listen for play mode / compilation events",
                "unity_replay_start_recording(targetInstanceId) — Record gameplay for replay",
                "unity_replay_stop_recording() — Stop and save session",
                "unity_replay_execute(sessionId) — Replay and verify determinism",
                "unity_run_tests(testMode) — Run EditMode/PlayMode tests",
                "unity_get_test_results() — Poll for test completion"
            },
            new[]
            {
                "Contracts only work in play mode — register after entering play mode",
                "Instance IDs can change between edit/play mode (Unity recreates objects)",
                "Use unity_play_mode_wait instead of unity_play_mode for more reliable transitions",
                "After unity_run_tests, poll unity_get_test_results — tests run asynchronously",
                "set_runtime_fields uses JSON array format: [{\"name\":\"field\",\"value\":123}]"
            },
            "  # Test a gameplay mechanic\n  unity_play_mode_wait(action=\"play\")\n  unity_invoke_method(instanceId=123, componentType=\"PlayerController\", methodName=\"Jump\")\n  unity_get_runtime_values(instanceId=123, componentType=\"PlayerController\", fieldNames=[\"isGrounded\",\"velocity\"])\n  unity_play_mode(action=\"stop\")"
        ),

        // ---- VISUAL COMPARISON ----
        new Workflow(
            "Visual Comparison & Capture",
            "Take screenshots, compare images, capture sequences, and sweep parameters.",
            new[] { "screenshot", "compare", "image", "capture", "reference", "similarity", "frame", "sequence", "sweep", "pov", "angle" },
            new[]
            {
                "unity_screenshot(viewType) — Single screenshot (scene or game view)",
                "unity_multi_pov_snapshot(targetInstanceId, presets) — Screenshots from multiple angles",
                "unity_capture_and_compare(referenceImageHandle) — Screenshot + similarity check in one call",
                "unity_compare_images(handle1, handle2) — Pixel-level comparison metrics",
                "unity_compare_images_semantic(handle1, handle2) — Region-based semantic comparison",
                "unity_capture_frame_sequence(frameCount, captureIntervalMs) — Burst capture during play mode",
                "unity_parameter_sweep(target, min, max, steps) — Sweep a value and capture per step",
                "unity_sample_screenshot_colors(points) — Sample pixel colors from a screenshot",
                "unity_store_image_handle / unity_get_image_handle / unity_delete_image_handle — Manage stored images"
            },
            new[]
            {
                "STRUCTURE FIRST: If structuralSimilarity is low (<0.6), fix camera angle and object placement BEFORE tweaking colors/materials. Low structural score means composition is wrong.",
                "If similarityScore plateaus but structuralSimilarity is still low, the camera position or major objects are wrong — no amount of color tweaking will fix it.",
                "Image handles are lost on domain reload (script recompilation)",
                "unity_capture_and_compare is more efficient than separate screenshot + compare calls",
                "Use unity_multi_pov_snapshot with presets='all' for thorough spatial verification",
                "Parameter sweep target format: 'material:<path>:<prop>', 'volume:<component>:<prop>', 'rendersettings:global:<prop>'"
            },
            "  # Verify a scene matches a reference\n  unity_screenshot(viewType=\"scene\")  # Get reference handle\n  # ... make changes ...\n  unity_capture_and_compare(referenceImageHandle=\"img_abc123\")"
        ),

        // ---- AUDIO ----
        new Workflow(
            "Audio",
            "Configure audio sources, spatial audio, and mixer routing.",
            new[] { "audio", "sound", "music", "clip", "source", "listener", "spatial", "3d", "mixer", "volume" },
            new[]
            {
                "unity_configure_audio_source(instanceId, createIfMissing, volume, spatialBlend, ...) — Add/configure AudioSource",
                "unity_get_audio_source(instanceId) — Read AudioSource configuration",
                "unity_get_audio_mixer() — Get mixer info (if mixer exists in project)",
                "unity_configure_audio_mixer() — Modify mixer settings"
            },
            new[]
            {
                "Set spatialBlend=1 for 3D audio, spatialBlend=0 for 2D (UI sounds)",
                "createIfMissing=true (default) auto-adds AudioSource if not present",
                "Mixer routing requires an AudioMixer asset in the project"
            },
            "  # 3D ambient sound\n  unity_configure_audio_source(instanceId=123, volume=0.5, spatialBlend=1, loop=true, minDistance=5, maxDistance=50, createIfMissing=true)"
        ),

        // ---- TERRAIN ----
        new Workflow(
            "Terrain",
            "Create and sculpt Unity terrains with heightmaps, texture layers, and vegetation.",
            new[] { "terrain", "heightmap", "landscape", "ground", "hill", "mountain", "grass", "rock", "tree", "vegetation", "sculpt", "paint" },
            new[]
            {
                "unity_create_terrain(name, terrainWidth, terrainLength, terrainHeight) — Create a new terrain with saved TerrainData asset",
                "unity_get_terrain_info(instanceId) — Read terrain size, layers, tree counts, heightmap resolution",
                "unity_set_terrain_heights(instanceId, mode) — Shape terrain: flat, noise, slope, plateau, or raw heightmap data",
                "unity_add_terrain_layer(instanceId, diffusePath) — Add a texture layer (grass, rock, sand, etc.)",
                "unity_paint_terrain(instanceId, layerIndex) — Paint texture layers with brush or fill",
                "unity_place_terrain_trees(instanceId, prefabPath, count) — Place trees with altitude/slope constraints"
            },
            new[]
            {
                "Heightmap resolution must be 2^n+1 (33, 65, 129, 257, 513, 1025) — 257 is a good default",
                "Add at least one terrain layer before painting — painting with no layers will fail",
                "Height values are 0-1 normalized; multiply by terrainHeight for world units",
                "Use unity_set_terrain_heights mode='noise' for quick natural landscapes",
                "Tree placement respects altitude/slope constraints — use maxSlope=30 to avoid cliffs",
                "For complex heightmaps, use mode='noise' first, then layer plateau or slope on top via unity_execute_csharp"
            },
            "  # Create a natural island\n  unity_create_terrain(name=\"Island\", terrainWidth=200, terrainLength=200, terrainHeight=60)\n  unity_set_terrain_heights(instanceId=<id>, mode=\"noise\", noiseScale=0.02, noiseAmplitude=0.35, noiseOctaves=4)\n  unity_add_terrain_layer(instanceId=<id>, diffusePath=\"Assets/Textures/Grass.png\", tileSizeX=5, tileSizeY=5)\n  unity_add_terrain_layer(instanceId=<id>, diffusePath=\"Assets/Textures/Rock.png\", tileSizeX=3, tileSizeY=3)\n  unity_paint_terrain(instanceId=<id>, layerIndex=0, fill=true)  # Base grass\n  unity_paint_terrain(instanceId=<id>, layerIndex=1, centerX=0.5, centerY=0.5, radius=0.2)  # Rock center\n  unity_place_terrain_trees(instanceId=<id>, prefabPath=\"Assets/Prefabs/Tree.prefab\", count=100, maxSlope=25)"
        ),

        // ---- PARTICLES & VFX ----
        new Workflow(
            "Particles & VFX",
            "Create particle effects from templates or configure systems manually.",
            new[] { "particle", "vfx", "effect", "fire", "smoke", "rain", "sparks", "snow", "dust", "fountain", "fireflies", "emitter" },
            new[]
            {
                "unity_create_particle_template(template, ...) — One-call preset effects: fire, smoke, rain, sparks, snow, dust, fountain, fireflies",
                "unity_get_particle_system(instanceId, modules) — Read ParticleSystem module configuration",
                "unity_configure_particle_system(instanceId, ...) — Fine-tune individual module properties (main, emission, shape, renderer, color/size over lifetime)",
            },
            new[]
            {
                "Start with unity_create_particle_template for quick effects, then fine-tune with unity_configure_particle_system",
                "Use 'scale' to resize the entire effect proportionally, 'intensity' to adjust density",
                "Color override on templates replaces the template's default start color but preserves gradients",
                "MinMaxCurve properties accept constant (startSpeed: 10) or range (startSpeedRange: [3, 7])"
            },
            "  # Quick fire effect\n  unity_create_particle_template(template=\"fire\", position=[0,0,0], scale=2, intensity=1.5)\n\n  # Fine-tune existing particle system\n  unity_configure_particle_system(instanceId=123, startSpeed=8, shapeType=\"Cone\", shapeAngle=30, emissionRateOverTime=100)"
        ),

        // ---- CHECKPOINTS ----
        new Workflow(
            "Checkpoints & Undo",
            "Create scene checkpoints, compare diffs, and restore previous states.",
            new[] { "checkpoint", "undo", "restore", "diff", "rollback", "backup", "save", "revert" },
            new[]
            {
                "unity_create_checkpoint(name) — Save current scene state as a named checkpoint",
                "unity_list_checkpoints() — List all saved checkpoints",
                "unity_get_diff(checkpointName) — See what changed since a checkpoint",
                "unity_restore_checkpoint(checkpointName) — Restore scene to checkpoint state",
                "unity_delete_checkpoint(checkpointName) — Clean up old checkpoints"
            },
            new[]
            {
                "Checkpoints save the entire scene file — create them before risky operations",
                "unity_get_diff output is capped at 8000 chars to avoid token bloat",
                "unity_scene_transaction auto-creates a checkpoint and rolls back on failure"
            },
            "  # Safe scene modification\n  unity_create_checkpoint(name=\"before_restructure\")\n  # ... make changes ...\n  unity_get_diff(checkpointName=\"before_restructure\")  # Review changes\n  # If bad: unity_restore_checkpoint(checkpointName=\"before_restructure\")"
        ),

        // ---- PACKAGES ----
        new Workflow(
            "Package Manager",
            "List, install, remove, and search Unity packages.",
            new[] { "package", "install", "uninstall", "registry", "dependency" },
            new[]
            {
                "unity_list_packages() — List all installed packages",
                "unity_search_packages(query) — Search Unity Package Registry",
                "unity_add_package(name) — Install a package (triggers domain reload)",
                "unity_remove_package(name) — Uninstall a package (triggers domain reload)"
            },
            new[]
            {
                "Adding/removing packages triggers a domain reload — bridge reconnects after ~10s",
                "Image handles and delta snapshots are lost during domain reloads",
                "Use the full package name: 'com.unity.cinemachine', not just 'cinemachine'"
            },
            ""
        ),

        // ---- 2D GAME SETUP ----
        new Workflow(
            "2D Game Setup",
            "Set up a 2D game scene with proper 2D physics, colliders, and camera.",
            new[] { "2d", "platformer", "2d game", "side-scroller", "top-down", "2d physics" },
            new[]
            {
                "1. Create parent GameObjects to organize hierarchy: 'Level', 'Platforms', 'Player', 'Environment'",
                "2. Spawn primitives with parentId to keep hierarchy clean",
                "3. For EVERY spawned primitive: remove the 3D collider, add the 2D equivalent:",
                "   unity_execute_csharp: DestroyImmediate(obj.GetComponent<Collider>()); obj.AddComponent<BoxCollider2D>();",
                "   Or use unity_add_component(instanceId, componentType=\"BoxCollider2D\")",
                "4. Add Rigidbody2D to dynamic objects (player, enemies, moving platforms)",
                "5. Set camera to orthographic: unity_set_scene_view_camera(..., orthographic=true)",
                "6. For the game camera: set projection to orthographic, size ~5-10 depending on level scale",
                "7. Constrain all Z positions to 0 — 2D games should stay on the XY plane"
            },
            new[]
            {
                "Unity primitives spawn with 3D colliders (BoxCollider, etc.) — these do NOT work with 2D physics. Always swap to 2D colliders.",
                "Rigidbody2D and Rigidbody are incompatible — never mix 2D and 3D physics on the same object",
                "Set Rigidbody2D bodyType to 'Kinematic' for moving platforms, 'Static' for ground/walls",
                "Keep all objects at z=0 for 2D — depth sorting issues occur when objects are at different Z positions"
            },
            ""
        ),

        // ---- SCENE RECONSTRUCTION ----
        new Workflow(
            "Scene Reconstruction",
            "Reconstruct a scene from a reference image. Follow strict pass ordering: structure first, then materials, then details.",
            new[] { "reconstruct", "recreation", "reference", "reproduce", "match", "replica", "copy", "rebuild" },
            new[]
            {
                "PASS 1 — LAYOUT: Set up terrain/ground, skybox, camera position. Get the broad composition right first.",
                "PASS 2 — MAJOR SHAPES: Place main objects using unity_create_compound_shape (trees, buildings, lanterns, steps, fences, rocks). Match positions and sizes to reference.",
                "PASS 2.5 — OVERLAP CHECK: Run unity_audit_overlaps(rootInstanceId) to detect clipping between placed objects. Use unity_resolve_overlaps to auto-fix before camera matching.",
                "PASS 2.7 — GROUNDING CHECK: Run unity_audit_grounding(rootInstanceId) to catch floating or sunk objects. Use unity_snap_to_ground to fix. Run AFTER overlap resolve.",
                "PASS 3 — CAMERA: Match camera angle, FOV, and framing with unity_set_scene_view_camera. Take screenshot and compare — structuralSimilarity should be >0.5 before proceeding.",
                "PASS 4 — LIGHTING: Add directional light (sun), point/spot lights. Match the reference's light direction and color temperature.",
                "PASS 5 — MATERIALS: Adjust colors, metallic, smoothness. Create procedural textures for terrain. Only now tweak visual appearance.",
                "PASS 6 — DETAILS: Add particle effects (fire, smoke), scatter decorations, fine-tune positions. Compare frequently.",
                "COMPARE: Use unity_capture_and_compare after each pass. Check structuralSimilarity — if <0.5, go back to passes 1-3 before touching materials."
            },
            new[]
            {
                "NEVER tweak colors/materials when structuralSimilarity is below 0.5 — fix composition first",
                "Use compound shapes (tree, lantern, rock_cluster, etc.) instead of manual primitive composition",
                "ROOF GAP: When building gable roofs with two angled cubes, always add a ridge beam — panel thickness creates a visible V-gap at the peak",
                "ROOF CLIPPING in villages: Use roofOverhang=0 when buildings are close together to prevent roofs from cutting through neighboring walls",
                "After placing objects, run unity_audit_overlaps to catch roof-wall clipping, tree-building intersections, etc. before wasting iterations on color tweaks",
                "FLOATING OBJECTS: After scatter or batch placement, run unity_audit_grounding — objects often land at wrong Y. Use unity_snap_to_ground(rootInstanceId) to fix all at once.",
                "When compare hotspots show problem regions, use unity_identify_objects_at_points with those coordinates to find the offending objects",
                "If similarity plateaus for 3+ iterations, you're likely optimizing the wrong thing — check structuralSimilarity",
                "Create a checkpoint before each major pass so you can rollback if needed",
                "Camera position has the biggest single impact on similarity — match it early",
                "Read the 'suggestions' array from compare results — it gives spatial guidance (CAMERA/FRAMING/LAYOUT) not just color feedback"
            },
            "  # Reconstruct from reference\n  unity_screenshot(viewType=\"scene\", includeHandle=true)  # Store reference\n  # Pass 1: Ground + sky\n  unity_create_terrain(name=\"Ground\", ...)\n  unity_create_procedural_skybox(skyTint=[0.7,0.5,0.8], ...)\n  # Pass 2: Major shapes\n  unity_create_compound_shape(preset=\"simple_building\", ...)\n  unity_create_compound_shape(preset=\"tree\", ...) (x several)\n  unity_create_compound_shape(preset=\"lantern\", chainLength=0.5, ...)\n  unity_create_compound_shape(preset=\"steps\", ...)\n  # Pass 3: Camera\n  unity_set_scene_view_camera(pivot=[...], rotation=[...], size=...)\n  unity_capture_and_compare(referenceImageHandle=...)  # Check structural match\n  # Pass 4-6: Lighting, materials, details"
        ),

        // ---- SCENE VIEW ----
        new Workflow(
            "Scene View Camera",
            "Control the editor's scene view camera for positioning, framing, and picking.",
            new[] { "camera", "scene view", "orbit", "pan", "zoom", "frame", "look", "pick", "view" },
            new[]
            {
                "unity_get_scene_view_camera() — Read current camera state",
                "unity_set_scene_view_camera(pivot, rotation, size) — Set camera transform",
                "unity_frame_object(instanceId) — Frame an object (like pressing F in editor)",
                "unity_look_at_point(point) — Point camera at a world position",
                "unity_orbit_camera(yaw, pitch) — Orbit around pivot",
                "unity_pan_camera(right, up) — Pan in camera's local plane",
                "unity_zoom_camera(delta) — Zoom in/out (negative = zoom in)",
                "unity_pick_at_screen(x, y) — Raycast from screen coordinates to find objects"
            },
            new[]
            {
                "unity_frame_object is the easiest way to center on an object",
                "Camera size controls zoom level in orthographic, orbit distance in perspective",
                "unity_pick_at_screen uses scene view coordinates, not game view"
            },
            ""
        ),
    };
}
