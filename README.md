# Agent Bridge for Unity

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)
[![Unity 6+](https://img.shields.io/badge/Unity-6000.0%2B-black.svg)](https://unity.com)
[![.NET 10](https://img.shields.io/badge/.NET-10-purple.svg)](https://dotnet.microsoft.com)

Let AI actually operate the Unity Editor — build scenes, run Play Mode, capture screenshots, and iterate from visual feedback.

Works with [Claude Code](https://docs.anthropic.com/en/docs/claude-code), [Cursor](https://cursor.sh), [Windsurf](https://codeium.com/windsurf), VS Code, or any [MCP](https://modelcontextprotocol.io)-compatible client.

![Scene Building](Gifs/forest-4x.gif)

Not just code edits — the agent can create objects, adjust lighting, wait for compilation, enter Play Mode, take screenshots, compare against references, and keep refining until the scene is right.

---

## What Can It Do?

**Runtime Scripting** — Create C# scripts, wait for compilation, attach behaviors, enter Play Mode, and validate results.

![Runtime Scripting](Gifs/solar-system-5x.gif)

**Visual QA** — Adjust lighting and materials, capture screenshots, compare against references, refine iteratively.

![Visual QA](Gifs/demo_full.gif)

**Scene Reconstruction** — Reconstruct 3D structures from reference images using iterative visual validation.

![Scene Reconstruction](Gifs/villa-demo-5x.gif)

**Try prompts like:**

```
Create a stylized forest with 30 trees, scattered rocks, and warm lighting.

Block out a 2D platformer level with floating platforms, a player spawn, and a goal area.

Set up horror lighting: dim blue ambient, flickering point light, red emergency light, volumetric fog.

Build an obstacle course with moving platforms, a rotating blade, and a jump pad.
Then add a player controller and a win condition.

Create a solar system with orbiting planets, add the scripts, enter Play Mode, and verify it works.
```

---

## Quick Start

### Prerequisites

- **Unity 6** (6000.0+) with URP
- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**
- An MCP-compatible client

### 1. Install the Package

**Git URL** (recommended) — In Unity: **Window > Package Manager > + > Add package from git URL**:

```
https://github.com/sebastiankurvers-dev/agent-bridge-for-unity.git
```

<details>
<summary>Other install methods</summary>

**Local clone:**

```bash
cd YourUnityProject/Packages
git clone https://github.com/sebastiankurvers-dev/agent-bridge-for-unity.git com.sebastiankurvers.agent-bridge
```

**Local path reference** — add to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.sebastiankurvers.agent-bridge": "file:/path/to/agent-bridge-for-unity"
  }
}
```

</details>

### 2. Verify the Bridge

The bridge starts automatically on port 5847 when Unity compiles. Check **Window > Agent Bridge** in Unity, or:

```bash
curl http://127.0.0.1:5847/health
```

### 3. Configure Your MCP Client

Create `.mcp.json` in your Unity project root:

```json
{
  "mcpServers": {
    "unity": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "Packages/com.sebastiankurvers.agent-bridge/UnityMCP~/UnityMCP.csproj"],
      "env": {}
    }
  }
}
```

> If using a local path install, replace with the absolute path to your `UnityMCP.csproj`.

### 4. Start Using It

Open your MCP client from the Unity project directory. Tools are available immediately.

---

## How It Works

```
┌─────────────────┐       ┌──────────────────────────────┐
│   MCP Client    │       │  UnityMCP (.NET 10 Server)   │
│  (Claude Code,  │─stdio─│  Receives tool calls,        │
│  Cursor, etc.)  │       │  forwards to Unity over HTTP │
└─────────────────┘       └──────────────┬───────────────┘
                                         │
                                    HTTP :5847
                                         │
                          ┌──────────────┴───────────────┐
                          │  UnityAgentBridge (Editor)    │
                          │  Dispatches to Unity main     │
                          │  thread, executes commands    │
                          └──────────────┬───────────────┘
                                         │
                                  Unity Editor API
```

| Layer | Location | Role |
|-------|----------|------|
| **UnityMCP** | `UnityMCP~/` | .NET 10 MCP server. Receives tool calls via stdio, forwards to Unity over HTTP. |
| **UnityAgentBridge** | `Editor/UnityAgentBridge/` | Unity Editor plugin. HTTP server on `127.0.0.1:5847`, dispatches to main thread. |

> The `~` suffix on `UnityMCP~/` tells Unity to ignore the folder — it won't compile .NET 10 code as Unity scripts.

---

## Tool Coverage

248 tools across scene building, terrain, particles, visual QA, automation, editor control, assets, and scripting. You don't need to learn them — your AI agent discovers and calls them as needed.

<details>
<summary>Full tool breakdown</summary>

| Category | Tools | Description |
|----------|------:|-------------|
| Scene Builder | 30 | Spawn prefabs, primitives, path-based placement, descriptors, transactions |
| Assets | 21 | Find, search, catalog, import settings, geometry metadata |
| GameObjects | 20 | Inspect, modify, delete, reparent, group, scatter, batch operations |
| Rendering | 19 | Camera settings, render pipeline, material preview, shader scoping |
| Scene | 18 | Save, load, hierarchy queries, scene profiling |
| UI Toolkit | 14 | UXML/USS creation, visual element queries, binding |
| Components | 12 | Add, remove, inspect, modify, patch serialized properties |
| Screenshots | 12 | Capture, compare with suggestions, multi-POV, frame sequences |
| Lighting | 9 | Lights, render settings, volumes, procedural skybox, reflection probes |
| Camera | 9 | Scene view control, orbit, focus, screenshot viewpoints |
| Animation | 8 | Animator controllers, states, transitions, FBX clips |
| Shaders | 7 | Create, inspect, keywords, property management |
| Scripts | 7 | Create, modify, read structure, type schemas |
| Terrain | 6 | Create, sculpt, paint texture layers, place trees |
| Replay | 5 | Record, execute, compare — full state verification |
| Checkpoints | 5 | Save/restore scene snapshots for rollback |
| Spatial Audit | 7 | Overlap detection, grounding audit, snap-to-ground, object identification at screen points |
| Physics | 5 | Rigidbody, colliders, physics settings, spatial queries |
| Audio | 5 | Sources, mixers, listeners, spatial audio |
| Play Mode | 5 | Enter/exit, wait, event polling |
| Primitives | 4 | Built-in primitives, procedural meshes, raw mesh API, compound shapes |
| Performance | 4 | Telemetry, baselines, profiling |
| Packages | 4 | List, add, remove, search Unity packages |
| Particles | 3 | Preset templates, get/configure particle systems |
| Tests | 2 | Run tests, get results |
| Console | 2 | Read and clear Unity console logs |
| Workflow Guide | 1 | Task-specific tool recommendations |

</details>

---

## Compatibility

| Requirement | Supported |
|-------------|-----------|
| **Unity** | 6000.0+ (Unity 6) |
| **Render Pipeline** | URP |
| **.NET SDK** | 10.0+ |
| **OS** | Windows, macOS (Intel & Apple Silicon), Linux |

---

## Security

The bridge listens on `127.0.0.1` only — not accessible from other machines.

No authentication required by default. For shared machines, set `BRIDGE_AUTH_TOKEN`:

```bash
export BRIDGE_AUTH_TOKEN="your-secret-token"
```

<details>
<summary>Additional security options</summary>

- **C# execution**: Enabled by default with sandbox restrictions. Disable with `BRIDGE_DISABLE_EXECUTE=1`
- **Body size limit**: 10 MB default (`BRIDGE_MAX_REQUEST_BODY_BYTES`)
- **Concurrency limit**: 32 concurrent requests (`BRIDGE_MAX_CONCURRENT_REQUESTS`)
- **Route allowlist**: `BRIDGE_ALLOWLIST_FILE="/path/to/allowlist.json"`
- **Audit logging**: `BRIDGE_AUDIT_LOG="/path/to/audit.log"`

</details>

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| Bridge not starting | Check **Window > Agent Bridge** in Unity. Ensure port 5847 is free. |
| MCP server won't build | Verify .NET 10: `dotnet --version`. Try `dotnet build UnityMCP~/UnityMCP.csproj` |
| Tools not appearing | Ensure `.mcp.json` is in project root. Restart your MCP client. |
| Scene save dialogs | Intentional — the bridge auto-saves before write operations to prevent modal dialogs. |

---

## Support

If this project saves you time, consider [buying me a coffee](https://buymeacoffee.com/sebastian.kurvers).

## Community

[Contributing](CONTRIBUTING.md) | [Security Policy](SECURITY.md) | [Code of Conduct](CODE_OF_CONDUCT.md)

## License

[Apache 2.0](LICENSE)
