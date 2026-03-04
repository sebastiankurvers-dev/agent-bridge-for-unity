# Repro Workflow Contract

<!-- Canonical state machine for scene reproduction loops. -->

## State Machine

Scene reproduction follows a fixed state machine:

```
DISCOVER -> PLAN -> SAMPLE -> BUILD -> CAPTURE -> COMPARE -> DIAGNOSE -> PROPOSE -> APPLY -> VALIDATE -> (loop or DONE)
```

Each iteration refines the scene toward a reference image or description. The loop exits when the similarity threshold is met or max iterations are exhausted.

## States and Tool Mapping

| State | Purpose | Primary Tools |
|-------|---------|---------------|
| **DISCOVER** | Inspect scene + candidate assets + geometry constraints | `unity_get_scene`, `unity_get_hierarchy`, `unity_get_scene_layout_snapshot`, `unity_find_prefabs_scoped`, `unity_find_materials_scoped`, `unity_get_prefab_footprint_2d`, `unity_get_visual_catalog` |
| **PLAN** | Build a measurable multi-pass reconstruction strategy before mutating scene state | `unity_plan_scene_reconstruction` |
| **SAMPLE** | Extract objective color targets from reference/current visuals | `unity_sample_screenshot_colors` |
| **BUILD** | Construct or mutate scene (lane/tile paths use server-side path spawning by default) | `unity_spawn_along_path`, `unity_solve_layout_constraints`, `unity_create_scene_from_descriptor`, `unity_apply_scene_patch_batch`, `unity_spawn_prefab`, `unity_snap_objects`, `unity_create_material`, `unity_modify_material` |
| **CAPTURE** | Screenshot the current scene state with explicit target aspect when comparing to reference | `unity_screenshot`, `unity_build_scene_and_screenshot` |
| **COMPARE** | Measure global similarity and tile/path separation quality with aspect-aware correction and dual metrics | `unity_compare_images` (`aspectMode`), `unity_measure_tile_separation` |
| **DIAGNOSE** | Generate region-level + structural hints (non-applyable) for object/layout gaps | `unity_repro_step` (`diagnostics[]`: `tile_spacing_anomaly`, `camera_orientation_mismatch`, `object_off_bounds`), `unity_get_scene_layout_snapshot` |
| **PROPOSE** | Generate applyable patch proposals | `unity_repro_step`, `unity_repro_step_contextual` (`proposals[]`) |
| **APPLY** | Execute approved patches | `unity_apply_scene_patch_batch`, `unity_review_scene_patch_batch` |
| **VALIDATE** | Enforce compile + quality gates before next iteration | `unity_get_compilation_status`, `unity_get_compilation_errors`, `unity_run_scene_quality_checks` |

## Mandatory Preflight

Before first BUILD in every repro task:

1. `unity_get_scene`
2. `unity_get_hierarchy(brief=true)`
3. `unity_get_compilation_status`
4. `unity_get_scene_layout_snapshot` for camera/player/tile spacing baseline
5. `unity_get_prefab_footprint_2d` for prefab-placement tasks
6. `unity_compare_images` + `unity_measure_tile_separation` for visual match tasks
7. `unity_sample_screenshot_colors` when color/emission matching is required

Do not mutate scene state until numeric constraints are documented (spacing, target gap, and any curvature widening for lane/tile paths).
For general scene layouts, use `unity_solve_layout_constraints` in dry-run mode first and require violation reduction before apply mode.

For reference-image matching, CAPTURE must use explicit screenshot dimensions aligned to reference aspect ratio whenever possible (for example portrait references use portrait capture dimensions).
When reference/current aspect differs, COMPARE must be called with `aspectMode` set (`crop` or `fit_letterbox`) and decisions must be based on corrected similarity.

## Exit Criteria

| Criterion | Default | Notes |
|-----------|---------|-------|
| Similarity threshold | 0.85 | Use `similarityAspectMatched` when `aspectMode != none`; otherwise use raw similarity (`similarityRaw`) |
| Aspect-aware compare mode | required | Use `unity_compare_images` with `aspectMode` set (`crop` or `fit_letterbox`) when reference/current aspect mismatch is present |
| Aspect mismatch reporting | required | Include `aspectInfo` (`referenceAspect`, `currentAspect`, `mismatch`, `mismatchRatio`) in iteration summary |
| Visual merge risk ceiling | 0.35 | `unity_measure_tile_separation.visualMergeRisk`; lower for neon/glow-heavy scenes |
| Overlap pairs | 0 | For tile/lane layouts, `overlapPairCount` must be zero unless explicitly waived |
| Max iterations | 5 | Full CAPTURE-COMPARE-PROPOSE-APPLY cycles after initial BUILD |
| Confidence floor | 0.6 | Discard patch proposals below this confidence score |
| Compilation clean | required | `unity_get_compilation_status` must report zero errors after each APPLY |

The loop terminates when **any** of: all quality criteria met, max iterations exhausted, or no proposals above confidence floor remain.

## Error Handling and State Transitions

Errors during the repro loop map to state transitions based on the error envelope codes (see `error-envelope.md`):

| Error Code | During State | Action |
|------------|-------------|--------|
| `TIMEOUT` | BUILD, APPLY | Retry same state with increased `timeoutMs` (up to 2x original) |
| `TIMEOUT` | CAPTURE, COMPARE | Retry once; if still failing, abort loop and report |
| `MAIN_THREAD_ERROR` | any | Retry once; if persistent, restore checkpoint and abort |
| `BAD_REQUEST` | PROPOSE, APPLY | Discard current proposal, return to COMPARE for re-proposal |
| `BAD_REQUEST` | BUILD | Abort loop — descriptor or input is malformed |
| `NOT_FOUND` | DISCOVER | Asset not available; narrow search or report gap |
| `NOT_FOUND` | APPLY | Target object missing; return to BUILD to re-create |
| `INTERNAL_ERROR` | any | Restore checkpoint if available, abort loop |
| `CANCELED` | any | Abort loop, preserve current state |

## Safety Requirements

1. **Checkpoint before BUILD**: Always `unity_create_checkpoint` before the first BUILD state.
2. **Review before APPLY**: Use `unity_review_scene_patch_batch` to validate patches before `unity_apply_scene_patch_batch`.
3. **Diagnostics vs Proposals separation**: `diagnostics[]` from `unity_repro_step` are guidance only; only `proposals[]` are applyable.
4. **Compilation gate**: After every APPLY, check `unity_get_compilation_status` + `unity_get_compilation_errors`. If errors > 0, restore checkpoint.
5. **Visual gap gate**: After every APPLY, run `unity_measure_tile_separation`; if merge risk is medium/high, apply `unity_apply_separation_safe_look` then re-measure.
6. **Aspect gate for compare**: For aspect-mismatched references, compare must report raw and corrected metrics; decisions must use corrected metric.
7. **Structural diagnostics gate**: `unity_repro_step` diagnostics must be reviewed each iteration; unresolved high-severity structural diagnostics block DONE.
8. **Rollback on failure**: If the loop aborts due to errors, restore the pre-BUILD checkpoint.

## Workflow Sequence Diagram

```
Agent                          Bridge
  |-- DISCOVER (scene + scoped asset + footprint) -->|
  |<-- scene/asset constraints ------------------------|
  |-- SAMPLE (color picks) --------------------------->|
  |<-- sampled colors ---------------------------------|
  |-- checkpoint ------------------->|
  |-- BUILD (path spawn/descriptor/batch/material) -->|
  |<-- scene created ----------------|
  |                                  |
  | loop:                            |
  |-- CAPTURE (screenshot) -------->|
  |<-- image ------------------------|
  |-- COMPARE (image + separation) ->|
  |<-- similarity + gaps + risk ------|
  |                                  |
  |  [if all criteria met] ---------> DONE
  |  [if iterations >= max] -------> DONE
  |                                  |
  |-- DIAGNOSE (repro_step) -------->|
  |<-- diagnostics -------------------|
  |-- PROPOSE (repro_step) --------->|
  |<-- proposals + confidence --------|
  |                                  |
  |  [filter by confidence floor]    |
  |                                  |
  |-- review_batch ----------------->|
  |<-- risk score + review hash -----|
  |-- APPLY (approved batch) ------>|
  |<-- results ----------------------|
  |-- VALIDATE (compile + quality) ->|
  |<-- status -----------------------|
  | end loop                         |
```
