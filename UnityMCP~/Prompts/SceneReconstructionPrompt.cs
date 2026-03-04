using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnityMCP.Prompts;

[McpServerPromptType]
public class SceneReconstructionPrompt
{
    [McpServerPrompt(Name = "scene_reconstruction_guide")]
    [Description("Get the scene reconstruction iteration workflow — state machine, tool mapping, exit criteria, and error handling. Use mode='primitive' for zero-dependency demos using built-in shapes.")]
    public static PromptMessage GetGuide(
        [Description("Workflow mode: 'full' (default, uses project prefabs/materials) or 'primitive' (uses built-in shapes + colors, no assets needed).")] string mode = "full")
    {
        var isPrimitive = string.Equals(mode, "primitive", StringComparison.OrdinalIgnoreCase);
        var guide = BuildGuide(isPrimitive);
        return new PromptMessage { Role = Role.User, Content = new Content { Type = "text", Text = guide } };
    }

    private static string BuildGuide(bool primitiveMode)
    {
        var buildTools = primitiveMode
            ? "unity_spawn_primitive (with color, position, rotation, scale), unity_apply_scene_patch_batch"
            : "unity_spawn_along_path, unity_create_scene_from_descriptor, unity_apply_scene_patch_batch, unity_spawn_prefab, unity_create_material";

        var buildNotes = primitiveMode
            ? @"
## Primitive Mode Notes

You are in PRIMITIVE MODE — use only Unity built-in shapes (Cube, Sphere, Cylinder, Capsule, Plane, Quad)
with inline colors. No prefab or material assets are needed.

- Use `unity_spawn_primitive` for each object with shape, color [r,g,b,a], position, rotation, scale
- Approximate complex shapes by combining multiple primitives
- Use color to convey material identity (e.g., red for brick, grey for concrete, green for foliage)
- Scale primitives to match proportions from the reference image
- Group related primitives under a parent using parentId"
            : "";

        return $@"# Scene Reconstruction Iteration Workflow

## State Machine

```
DISCOVER → PLAN → SAMPLE → BUILD → CAPTURE+COMPARE → DIAGNOSE → PROPOSE → APPLY → VALIDATE → (loop or DONE)
```

## States and Tools

| State | Purpose | Tools |
|-------|---------|-------|
| DISCOVER | Inspect scene + assets | unity_get_scene, unity_get_hierarchy, unity_get_visual_catalog |
| PLAN | Build reconstruction strategy | unity_plan_scene_reconstruction |
| SAMPLE | Extract color targets from reference | unity_sample_screenshot_colors |
| BUILD | Construct/mutate scene | {buildTools} |
| CAPTURE+COMPARE | Screenshot + measure similarity | unity_capture_and_compare (single call) |
| DIAGNOSE | Region-level hints | unity_repro_step (diagnostics) |
| PROPOSE | Generate applyable patches | unity_repro_step (proposals) |
| APPLY | Execute patches | unity_review_scene_patch_batch → unity_apply_scene_patch_batch |
| VALIDATE | Compile + quality gates | unity_get_compilation_status, unity_run_scene_quality_checks |

## Mandatory Preflight (before first BUILD)

1. unity_get_scene — current scene metadata
2. unity_get_hierarchy(brief=true) — scene tree
3. unity_get_compilation_status — must be clean
4. unity_create_checkpoint — safety net before mutations
5. Store reference image: unity_store_image_handle — get a handle for reuse across iterations

## Iteration Loop

For each iteration (max 5):

1. **CAPTURE+COMPARE**: Call `unity_capture_and_compare(referenceImageHandle=...)` — returns screenshot + metrics in one call
2. **Check exit**: If similarity >= 0.85 → DONE
3. **DIAGNOSE**: Call `unity_repro_step` for region-level diagnostics
4. **PROPOSE**: Call `unity_repro_step` for applyable proposals (discard confidence < 0.6)
5. **REVIEW**: Call `unity_review_scene_patch_batch` — check risk score
6. **APPLY**: Call `unity_apply_scene_patch_batch` with approved review hash
7. **VALIDATE**: Check compilation status — if errors, restore checkpoint

## Exit Criteria

| Criterion | Threshold |
|-----------|-----------|
| Similarity | >= 0.85 (use similarityAspectMatched when aspect mismatch present) |
| Max iterations | 5 CAPTURE-COMPARE-PROPOSE-APPLY cycles |
| Confidence floor | 0.6 — discard proposals below this |
| Compilation | Must be clean (0 errors) after each APPLY |

## Error Handling

| Error | Action |
|-------|--------|
| TIMEOUT during BUILD/APPLY | Retry with 2x timeout |
| BAD_REQUEST during PROPOSE/APPLY | Discard proposal, re-compare |
| INTERNAL_ERROR | Restore checkpoint, abort |
| No proposals above confidence | Exit loop, report best similarity achieved |

## Safety Rules

1. Always checkpoint before first BUILD
2. Always review before apply (unity_review_scene_patch_batch)
3. After each APPLY, verify compilation is clean
4. If loop aborts, restore pre-BUILD checkpoint
5. Use aspectMode 'crop' or 'fit_letterbox' when reference/current aspect ratios differ
{buildNotes}";
    }
}
