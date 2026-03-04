# Key Workflows

Step-by-step recipes for common Unity Agent Bridge operations.

## Fix a Compilation Error
1. `unity_poll_events(since=0)` - Check for compilation_error events
2. Read the error details (file, line, message)
3. `unity_get_script(path=<error_file>)` - Get the source
4. Fix the issue
5. `unity_modify_script(path=<file>, content=<fixed>)` - Apply fix
6. `unity_poll_events(since=<lastId>, timeout=5)` - Wait for recompilation result
7. Verify: compilation_finished with errorCount=0

## Force Recompile and Wait (No Poll Loop)
1. `unity_trigger_recompile(forceReimportPath="Assets/Scripts/Foo.cs", waitForCompile=true)` - trigger + wait in one call
2. For multiple files use `forceReimportPaths=["Assets/Scripts/A.cs","Assets/Scripts/B.cs"]`
3. Inspect result payload `compilationStatus` and `errorCount`

## Add a Component to a GameObject
1. `unity_find_gameobjects(name=<target>)` - Find the object
2. `unity_add_component(instanceId=<id>, componentType=<type>)` - Add it
3. `unity_get_components(instanceId=<id>)` - Verify it's there

## Create and Attach a New Script
1. `unity_create_script(path=<path>, className=<name>)` - Create the file
2. `unity_poll_events(since=<lastId>, timeout=10)` - Wait for compilation
3. `unity_add_component(instanceId=<id>, componentType=<className>)` - Attach to GameObject
4. `unity_get_components(instanceId=<id>)` - Verify

## Debug a Runtime Issue
1. `unity_play_mode(action="play")` - Enter play mode
2. `unity_poll_events(since=<lastId>, timeout=5)` - Watch for log_error events
3. `unity_get_console(type="Error")` - Get full error details
4. `unity_play_mode(action="stop")` - Stop play mode
5. Investigate and fix the issue

## Enter Play Mode with Stability Gate
1. `unity_play_mode_wait(action="play", maxWaitMs=20000, pollIntervalMs=250, requireHealthStable=true, stablePollCount=3, minStableMs=750)`
2. Verify `success=true` before runtime inspection/mutations
3. Use `unity_play_mode_wait(action="stop", ...)` to leave play mode with the same stabilization

## Build a Scene from Scratch
1. `unity_create_checkpoint(name="before-scene-build")` - Safety first
2. `unity_find_prefabs(search=<query>)` - Discover available prefabs
3. `unity_create_scene_from_descriptor(descriptor=<json>)` - Build the scene
4. `unity_screenshot(view="scene")` - Visual verification
5. If runtime visuals matter: `unity_play_mode(action="play")` -> `unity_capture_frame_sequence(frameCount=6, captureIntervalMs=200)` -> inspect first/mid/last via `unity_get_image_handle` -> `unity_play_mode(action="stop")`

## Safe Batch Patch (Review -> Approve -> Apply)
1. `unity_review_scene_patch_batch(operationsJson=<json>)` - Validate and get `reviewHash` + `riskScore`
2. Inspect `riskReasons`/`results`; adjust batch if needed
3. `unity_apply_scene_patch_batch(..., requireApproval=true, approvedReviewHash=<reviewHash>)` - Apply with explicit approval
4. Verify with compilation status + screenshot diff

## Post-Mutation Scene Quality Gate
1. `unity_run_scene_quality_checks(failOnSeverity="error")` - Validate scene integrity and setup sanity in one call
2. If `summary.failed=true`, address high-severity findings first (`missing_script`, `missing_object_reference`, UI/EventSystem errors)
3. Re-run `unity_run_scene_quality_checks` until the gate passes for your chosen threshold

## Quick Performance Telemetry
1. Enter play mode when measuring gameplay paths: `unity_play_mode(action="play")`
2. Capture metrics: `unity_get_performance_telemetry(includeHotspots=true, maxHotspots=12)`
3. If `gcAllocatedInFrameBytes` is high or unstable, inspect `scriptHotspots` and optimize the top-ranked scripts first
4. Re-run telemetry after changes and compare frame time/FPS + draw calls + GC alloc deltas

## Performance Budget Gate
1. Capture baseline before major changes: `unity_capture_performance_baseline(name="pre_optimization")`
2. Apply your scene/code changes
3. Run gate: `unity_check_performance_budget(baselineName="pre_optimization", maxFrameTimeMs=16.7, maxGcAllocBytesPerFrame=1024, maxDrawCalls=1500, maxFrameTimeDeltaMs=1.5, maxGcAllocDeltaBytesPerFrame=512, maxDrawCallsDelta=150)`
4. If `failed=true`, fix top violations and rerun until passed

## Modify a Component's Serialized Fields
1. `unity_find_gameobjects(name=<target>)` - Find the object
2. `unity_get_components(instanceId=<id>, namesOnly=false)` - See all components + their properties
3. `unity_modify_component(instanceId=<id>, componentType=<type>, properties=<json>)` - Apply changes

## Patch Runtime Fields in Play Mode (No execute_csharp)
1. Enter play mode with `unity_play_mode_wait(action="play")`
2. `unity_set_runtime_fields(instanceId=<id>, componentType="EnemyController", fieldsJson="[{\"name\":\"cameraPitch\",\"value\":74},{\"name\":\"cameraOrthographicSize\",\"value\":10.8}]")`
3. Validate with `unity_get_runtime_values(instanceId=<id>, componentType="EnemyController", fieldNames=["cameraPitch","cameraOrthographicSize"])`
4. Exit play mode with `unity_play_mode_wait(action="stop")`

## Gameplay Verification Loop (Play -> Act -> Observe -> Verify)
1. `unity_find_gameobjects(name="Player")` - Get instanceId
2. `unity_play_mode(action="play")` - Enter play mode
3. `unity_poll_events(timeout=2)` - Wait for play_mode_changed
4. `unity_invoke_method(id, "PlayerController", "Jump", screenshotBefore=true, screenshotAfter=true)` - Simulate input + capture before/after
5. `unity_poll_events(timeout=1)` - Let physics tick
6. `unity_get_runtime_values(id, "PlayerController", ["isJumping","currentLane"])` - Check state
7. `unity_play_mode(action="stop")` - Exit

## Rapid Toggle / Sequence Testing
1. `unity_find_gameobjects(name="Player")` - Get instanceId
2. `unity_play_mode(action="play")` - Enter play mode
3. `unity_invoke_sequence(id, steps=[{componentType:"PlayerController",methodName:"TogglePhase",delayMs:0},{componentType:"PlayerController",methodName:"TogglePhase",delayMs:100}], screenshotView="game")` - Multi-step with timing
4. `unity_get_runtime_values(id, "PlayerController")` - Verify final state
5. `unity_play_mode(action="stop")` - Exit

## Inspect Renderer & Validate Materials
1. `unity_find_gameobjects(name="MyObject", maxResults=1)` - Get instanceId
2. `unity_get_renderer_state(instanceId=<id>)` - Check materials, keywords, MPB overrides
3. `unity_validate_material(materialPath="Assets/Materials/MyMat.mat")` - Verify shader/pipeline compatibility
4. If `compatible=false`, use `suggestedShader` to fix via `unity_modify_material`

## Rendering Health Audit (Catch Black Objects / Invisible Meshes)
1. `unity_audit_renderers()` - Batch scan all renderers for null materials, broken shaders, camera culling issues
2. For targeted checks: `unity_audit_renderers(nameContains="Player")` or `unity_audit_renderers(layer="Environment")`
3. Scope to a subtree: `unity_audit_renderers(rootInstanceId=12345)` - Audit only children of a specific GameObject
4. `unity_get_hierarchy_renderers(instanceId=12345)` - List all renderers on a GO + children with material/emission info (raw HDR values)
5. `unity_audit_scene_lighting()` - Check if lighting is adequate (ambient, directional, post-exposure)
6. If `healthy=false`, address errors first (null materials, invalid shaders)
7. If scene is too dark (`estimatedSceneLuminance < 0.1`), increase ambient or add directional light
8. Re-run `unity_run_scene_quality_checks()` — rendering checks are now included by default

## Spatial Correctness Verification (Post-Modification)
1. After modifying walls/objects/floor: `unity_camera_visibility_audit(nameContains="Knight", view="game")` — check objects aren't hidden behind repositioned walls
2. If `fullyOccluded > 0`: inspect `occludedBy` list, reposition blocking objects or move occluded ones
3. Check decorations are still attached: `unity_camera_visibility_audit(nameContains="Corbel", checkAttachment=true, attachMaxDistance=0.5)` — catches floating decorations
4. If `detached > 0`: snap detached objects back to nearest wall/surface
5. Verify floor coverage: `unity_raycast_coverage_check(rootInstanceId=<envRoot>, surfaceNameContains="Floor", spacing=0.5)` — reveals gaps after corridor widening
6. If `gaps.length > 0`: add floor tiles at reported gap centers, then rerun coverage check
7. For wall coverage: `unity_raycast_coverage_check(rootInstanceId=<wallRoot>, direction="forward", surfaceNameContains="Wall", spacing=0.5)` — detects wall holes
8. Final gate: `unity_run_scene_quality_checks()` — rendering + structural validation

## Set Up an Animator Controller
1. `unity_get_fbx_clips(fbxPath="Assets/.../A_Sprint_F_Masc.fbx")` - Discover clip names and rig type
2. `unity_create_animator_controller(path="Assets/Animations/Player.controller", prefabPath="Assets/Prefabs/Player.prefab", applyRootMotion=false, parameters=[...])` - Create with params, attach to prefab
3. `unity_add_animation_state(controllerPath="...", stateName="Idle", setAsDefault=true, motionClipPath="Assets/.../A_Idle.fbx")` - Add states (supports .fbx)
4. `unity_add_animation_state(controllerPath="...", stateName="Run", motionClipPath="Assets/.../A_Sprint.fbx")`
5. `unity_add_animation_transition(controllerPath="...", sourceStateName="Idle", destinationStateName="Run", hasExitTime=false, conditions=[...])` - Wire transitions
6. `unity_create_animation_clip(path="Assets/Animations/Custom.anim", wrapMode="Loop", curves=[...])` - Create custom clips
7. `unity_get_animator_info(controllerPath="...")` - Verify structure

## Learn From a Demo Scene
1. Load the demo scene: `unity_load_scene(scenePath="Assets/Scenes/DemoScene.unity")`
2. `unity_pin_asset_pack_context(rootFolder="Assets/MyAssetPack/Prefabs", name="mypack", includeGeometry=true, captureLookPreset=true, lookPresetName="mypack_night", captureSceneProfile=true, sceneProfileName="mypack_demo")` - Build/reuse cached context
3. `unity_list_asset_pack_context_pins()` - Confirm pin exists
4. `unity_get_asset_pack_context_pin(name="mypack")` - Read cached paths for catalog/look/profile
5. Load your own scene, then `unity_load_look_preset(name="mypack_night")` - Apply the mood
6. Use `unity_get_scene_profile(name="mypack_demo")` and `unity_get_asset_catalog(name="mypack")` while building

## Multi-POV Verification (Post-Placement Spatial Check)
1. Place objects in scene (spawn, snap, etc.)
2. `unity_multi_pov_snapshot(targetInstanceId=<objectId>, presets="all")` - Capture front/back/top/left/right + player views
3. Review image handles via `unity_get_image_handle(handle=<handle>, includeBase64=true)` for any angle that looks wrong
4. Check for: clipping, objects hidden behind walls, incorrect alignment, floating objects
5. If issues found: fix placement, then re-run `unity_multi_pov_snapshot` to verify
6. For brief token-efficient checks: `unity_multi_pov_snapshot(targetInstanceId=<id>, presets="all", brief=true)`

## Snap-Align Building Workflow
1. Spawn first building: `unity_spawn_prefab(prefab="Assets/.../SM_Bld_Wall_01.prefab", position=[0,0,0])`
2. Spawn second building: `unity_spawn_prefab(prefab="Assets/.../SM_Bld_Wall_02.prefab")`
3. `unity_snap_objects(sourceId=<second>, targetId=<first>, alignment="right-of", gap=0)` - Align flush
4. Continue snapping more objects to build a street

## Token-Efficient Retrieval
1. `unity_get_asset_catalog(name="mypack", brief=true, maxEntries=25)` - compact catalog summary
2. `unity_get_scene_profile(name="mypack_demo", brief=true, maxEntries=20)` - compact profile summary
3. `unity_get_asset_pack_context_pin(name="mypack", brief=true)` - compact pinned context summary

## Tile Separation Tuning Loop
1. `unity_get_prefab_footprint_2d(prefabPath="Assets/Prefabs/Tiles/FloorTile.prefab", targetMinEdgeGap=0.05)` - derive lane/forward spacing baselines
2. Build/update the track with those spacing values
3. `unity_measure_tile_separation(rootInstanceId=<trackRoot>, targetMinEdgeGap=0.05, captureScreenshot=true, screenshotView="game")` - read geometric + visual merge risk
4. If merge risk remains high, apply `generationConstraints` (lane/forward/curvature widening) and rerun
5. `unity_apply_separation_safe_look(...)` - clamp bloom/exposure so dark gaps remain visible

## Screenshot-to-Scene Reconstruction
1. Analyze reference screenshot visually (LLM vision)
2. `unity_sample_screenshot_colors(samplePoints=[[0.4,0.6],[0.5,0.5]], sampleRadius=3)` - Extract exact colors with median filtering (returns hex, hsv, luminance)
3. `unity_get_visual_catalog(search="hex,tile")` - Find matching prefabs
4. `unity_get_prefab_geometry(path)` - Get tile bounds for spacing + laneSpacing
5. `unity_create_material` x N with `emissionColor` + `emissionIntensity` - Create glowing materials
6. `unity_spawn_along_path(controlPoints, prefabPaths, materialPaths, spacing, laneCount=3, lanePattern="stagger")` - Spawn entire multi-lane path in one call
7. `unity_spawn_prefab(character)` - Place character
8. `unity_set_render_settings(ambientColor:[0,0,0])` + `unity_set_volume_profile_overrides(bloom)` - Environment
9. `unity_set_scene_view_camera(...)` - Match camera angle
10. `unity_screenshot` -> `unity_compare_images` - Verify similarity
11. `unity_repro_step` - Get region-level diagnostics[] for spatial hints + proposals[] for batch fixes
12. Iterate adjustments using diagnostics to guide object placement and proposals for post-processing

## Build a Menu with UI Toolkit
1. `unity_create_panel_settings(path="Assets/UI/PanelSettings.asset", scaleMode="ScaleWithScreenSize", referenceResolution=[1080,1920], match=0.5)` - Create PanelSettings for mobile
2. `unity_create_uxml(path="Assets/UI/MainMenu.uxml", content="<ui:UXML ...>...</ui:UXML>")` - Create UXML layout
3. `unity_create_uss(path="Assets/UI/MainMenu.uss", content="...")` - Create USS stylesheet
4. `unity_create_ui_document(name="MainMenu", panelSettingsPath="Assets/UI/PanelSettings.asset", uxmlPath="Assets/UI/MainMenu.uxml")` - Create UIDocument in scene
5. `unity_get_visual_tree(instanceId=<id>)` - Verify tree structure matches UXML
6. `unity_query_visual_elements(instanceId=<id>, typeName="Button")` - Verify buttons are found
7. `unity_modify_visual_element(instanceId=<id>, elementName="play-btn", text="Play!")` - Tweak element text at runtime

## Migrate uGUI to UI Toolkit
1. `unity_find_gameobjects(component="Canvas")` - Find existing Canvas
2. `unity_get_gameobject(instanceId=<canvasId>, includeComponents=true)` - Inspect Canvas setup
3. `unity_migrate_ugui_to_uitoolkit(instanceId=<canvasId>, outputUxmlPath="Assets/UI/Migrated.uxml", outputUssPath="Assets/UI/Migrated.uss")` - Generate UXML + USS
4. `unity_read_uxml(path="Assets/UI/Migrated.uxml")` - Review generated UXML
5. `unity_read_uss(path="Assets/UI/Migrated.uss")` - Review generated USS
6. Manually refine layout using `unity_modify_uxml` and `unity_modify_uss`
7. `unity_create_panel_settings(...)` + `unity_create_ui_document(...)` - Set up UIDocument with migrated files

## Diagnose Invisible Mesh Faces

When a mesh appears as a wireframe skeleton or has missing panels in Unity.

1. **Verify geometry exists**: `unity_get_mesh_info(instanceId=X)` — check `subMeshCount`, `submeshes[].indexCount`, and `materialCorrelation[]`
2. **Cross-reference with Blender**: `blender_mesh_analysis(name="X")` — check `facesPerMaterial` counts. Compare face counts per slot between Blender and Unity submeshes.
3. **Check material slot ordering**: FBX preserves Blender slot index → Unity submesh index. If Blender slot 0 has 6 faces and Unity submesh 0 has 12 tris (= 6 quads), the mapping is correct. If the material NAMES don't match the geometry (e.g., "Mat_Frame" on large panels), the bmesh script swapped them.
4. **Check face normals**: `blender_mesh_topology(name="X")` — inspect `faces[].normal`. For enclosed geometry (tunnels, corridors), normals must point INWARD toward the camera. If normals point outward, URP backface culling hides them. Fix: `bm.faces[i].normal_flip()` in bmesh edit mode.
5. **Opaque test**: In Unity code or via bridge, temporarily set the material to opaque (`_Surface=0`) with a bright color. If faces appear → shader/transparency issue. If still invisible → geometry missing or normals wrong.
6. **Check code-side material assignment**: If your game script overrides materials at runtime, verify the slot indices match the actual submesh→material correlation from step 1. A common bug is `mats[0] = frameMat` when submesh 0 is actually glass.
7. **Fix and re-export**: After fixing in Blender (rename materials, flip normals), re-export FBX with `blender_export(selectedOnly=true)`, then `unity_trigger_recompile(forceReimportPaths=[...fbx path...])`.
