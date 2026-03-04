# Rollout Rule

<!-- Checklist for shipping new MCP endpoints. -->

Every new MCP endpoint (tool) must ship with **all four** documentation updates completed and validated before merge.

## Checklist

| # | Requirement | How to verify |
|---|-------------|---------------|
| 1 | **Tool list regenerated** | Update `docs/agent-shared/tool-list.md` so the new tool appears with the correct `unity_*` name |
| 2 | **Agent config entry added** | The tool name appears under the appropriate category (Inspection / Creation / Modification / Debugging / Safety) in your agent configuration |
| 3 | **Workflow docs updated** | If the new tool changes a workflow (new state, new safety gate, new fallback), update workflow documentation accordingly |
| 4 | **Docs cross-reference check passes** | `rg -n "scripts/sync-agent-docs\\.sh|\\.github/workflows/agent-compat\\.yml" docs` returns no matches |

## When to update workflow docs

Not every new tool requires workflow doc changes. Update them when:

- The tool introduces a **new workflow** (e.g., a new reproduction state)
- The tool **replaces** an existing tool in a documented workflow
- The tool adds a **new safety gate** or changes an existing one
- The tool changes **error handling** behavior referenced in shared contracts

If the tool is a straightforward addition to an existing category (e.g., a new `unity_find_*` variant), updating agent config and regenerating the tool list is sufficient.

## Enforcement

This rule is enforced by:
- Local docs consistency checks in PR validation
- Reviewer verification that tool list + workflows + contracts are updated together
