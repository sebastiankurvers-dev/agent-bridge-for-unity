# Scene Quality Checks Contract

<!-- Canonical request/response schema for unity_run_scene_quality_checks. -->

## Purpose

`unity_run_scene_quality_checks` is a post-mutation validation gate for Unity scenes.
It runs a bounded set of integrity/sanity checks and returns severity-scored findings plus pass/fail status.

## Request Contract

```json
{
  "includeInactive": 1,
  "includeInfo": 0,
  "checkRequireComponents": 1,
  "checkSerializedReferences": 1,
  "checkPhysicsSanity": 1,
  "checkUISanity": 1,
  "checkLifecycleHeuristics": 1,
  "checkRenderingHealth": 1,
  "maxIssues": 200,
  "failOnSeverity": "error"
}
```

Fields:
- `includeInactive` (`0|1`, default `1`): include inactive GameObjects in scan scope.
- `includeInfo` (`0|1`, default `0`): include informational findings in output.
- `checkRequireComponents` (`0|1`, default `1`): validate `[RequireComponent]` dependencies.
- `checkSerializedReferences` (`0|1`, default `1`): detect broken serialized object references.
- `checkPhysicsSanity` (`0|1`, default `1`): check Rigidbody/Rigidbody2D collider pairing.
- `checkUISanity` (`0|1`, default `1`): check Canvas/EventSystem/InputModule/GraphicRaycaster consistency.
- `checkLifecycleHeuristics` (`0|1`, default `1`): heuristic warning for potential event unsubscribe risks.
- `checkRenderingHealth` (`0|1`, default `1`): check renderer materials, shaders, camera culling, meshes.
- `maxIssues` (`int`, default `200`, clamped `10..5000`): cap returned findings.
- `failOnSeverity` (`"info"|"warning"|"error"`, default `"error"`): gate threshold.

## Response Contract

```json
{
  "success": true,
  "summary": {
    "sceneName": "SampleScene",
    "scenePath": "Assets/Scenes/SampleScene.unity",
    "checkedObjectCount": 558,
    "checkedComponentCount": 2189,
    "issueCount": 3,
    "errorCount": 1,
    "warningCount": 2,
    "infoCount": 0,
    "maxSeverity": "error",
    "failed": true,
    "passed": false,
    "failOnSeverity": "error",
    "issuesTruncated": false,
    "checksRun": ["missing_scripts", "invalid_tags_layers", "ui_eventsystem_sanity"],
    "categoryCounts": {
      "integrity": 1,
      "ui": 2
    }
  },
  "issues": [
    {
      "id": "missing_script",
      "severity": "error",
      "category": "integrity",
      "message": "1 missing MonoBehaviour script reference(s).",
      "suggestion": "Remove missing components or restore the deleted script assets.",
      "instanceId": 12345,
      "objectName": "NPCSpawner",
      "path": "Root/Gameplay/NPCSpawner",
      "component": "MonoBehaviour",
      "heuristic": false
    }
  ]
}
```

## Severity and Gate Semantics

Severity ordering:
- `error` (highest)
- `warning`
- `info`

Gate rule:
- `summary.failed = true` when the highest observed issue severity is **greater than or equal to** `failOnSeverity`.
- `summary.passed = !summary.failed`.

Examples:
- `failOnSeverity="error"`: warnings do not fail the gate.
- `failOnSeverity="warning"`: warnings and errors fail the gate.
- `failOnSeverity="info"`: any issue fails the gate.

## Current Check IDs

Integrity:
- `missing_script`
- `invalid_tag`
- `invalid_layer`
- `missing_required_component`

References:
- `missing_object_reference`
- `serialized_scan_failed` (info)

Physics:
- `rigidbody_without_collider`
- `rigidbody2d_without_collider2d`

UI:
- `ui_missing_eventsystem`
- `ui_multiple_eventsystems`
- `ui_missing_graphic_raycaster`
- `ui_missing_input_module`

Audio:
- `multiple_audio_listeners`

Lifecycle (heuristic):
- `event_unsubscribe_risk`

Rendering:
- `renderer_disabled` (info)
- `renderer_null_material`
- `renderer_invalid_shader`
- `renderer_builtin_shader_in_srp`
- `renderer_layer_culled`
- `skinned_renderer_no_mesh`
- `mesh_renderer_no_filter`
- `mesh_filter_no_mesh`

## Notes

- The lifecycle check is intentionally heuristic and marked via `heuristic: true` in issues.
- This endpoint is read-only and does not mutate scene state.
- This contract is shared across agents and must remain aligned with the tool list, workflow docs, and agent configuration.
