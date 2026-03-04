# UnityMCP - Unity Editor MCP Server

MCP server that gives AI agents control over the Unity Editor.

## Prerequisites

1. **.NET 8 SDK** - Install from https://dotnet.microsoft.com/download/dotnet/8.0
   ```bash
   # macOS with Homebrew
   brew install dotnet-sdk
   ```

2. **Unity Editor** with the Agent Bridge plugin (in `Editor/UnityAgentBridge/`)

## Setup

1. Open Unity and let it compile the Agent Bridge scripts
2. Open **Window > Agent Bridge** to see the server status
3. The HTTP server starts automatically on port 5847

## Testing

Test the Unity bridge directly:
```bash
curl http://127.0.0.1:5847/health
curl http://127.0.0.1:5847/hierarchy
```

Long operations can override route timeout with `timeoutMs` (query parameter on bridge routes).
Example:
```bash
curl -X POST "http://127.0.0.1:5847/catalog/generate?timeoutMs=120000" \
  -H "Content-Type: application/json" \
  -d '{"rootFolder":"Assets/MyPrefabs","name":"MyAssetPack","includeGeometry":1}'
```

## MCP Configuration

Add to your MCP client configuration (e.g., `.mcp.json`):
```json
{
  "mcpServers": {
    "unity": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "./UnityMCP/UnityMCP.csproj"]
    }
  }
}
```

Token-efficiency controls:
- `UNITY_MCP_TOOL_PROFILE=full|core` (default `full`)
- `UNITY_MCP_TOOL_SCHEMA_MODE=full|compact|minimal` (default `compact`)

Security controls:
- `BRIDGE_AUTH_TOKEN=<token>` (recommended): client sends `Authorization: Bearer <token>`
- `BRIDGE_ALLOW_UNAUTHENTICATED=1` (dev-only): disables fail-closed auth requirement
- `BRIDGE_ENABLE_EXECUTE=1` (explicit opt-in): enables `/execute`
- `BRIDGE_MAX_REQUEST_BODY_BYTES=<bytes>` (default `1048576`)
- `BRIDGE_MAX_CONCURRENT_REQUESTS=<count>` (default `32`)

`compact` trims tool/schema descriptions while keeping all tool parameters and behavior unchanged.

## Available Tools

| Tool | Description |
|------|-------------|
| `unity_get_hierarchy` | Get scene hierarchy tree |
| `unity_get_gameobject` | Inspect a GameObject |
| `unity_get_components` | Get component details |
| `unity_modify_gameobject` | Change transform, name, active state |
| `unity_patch_serialized_properties` | Patch exact serialized properties by property path |
| `unity_set_renderer_materials` | Set renderer materials with optional slot indices |
| `unity_get_camera_rendering` | Get URP camera rendering settings |
| `unity_set_camera_rendering` | Patch URP camera rendering settings |
| `unity_get_volume_profile` | Get URP volume profile overrides |
| `unity_set_volume_profile_overrides` | Patch URP volume profile overrides |
| `unity_spawn_prefab` | Instantiate a prefab |
| `unity_create_gameobject` | Create empty GameObject |
| `unity_delete_gameobject` | Delete a GameObject |
| `unity_find_prefabs` | Search for prefabs |
| `unity_get_prefab_geometry` | Get prefab bounds/collider/connectors metadata for accurate placement |
| `unity_find_prefabs_scoped` | Search prefabs within specific folders (include/exclude roots) |
| `unity_find_materials_scoped` | Search materials within specific folders (include/exclude roots) |
| `unity_find_shaders_scoped` | Search shaders within specific folders (include/exclude roots) |
| `unity_get_console` | Read console logs |
| `unity_execute_csharp` | Run C# code |
| `unity_screenshot` | Capture Game/Scene view |
| `unity_play_mode` | Start/stop/pause Play mode |
| `unity_get_scene` | Get current scene info |
| `unity_load_scene` | Load a scene |
| `unity_get_status` | Get Unity status |
| `unity_review_scene_patch_batch` | Review/validate batch ops without mutation; returns risk score + review hash |
| `unity_apply_scene_patch_batch` | Apply batch ops with review-hash approval, dry-run/atomic/rollback support |
| `unity_generate_asset_catalog` | Generate/reuse prefab catalog JSON for an asset-pack folder |
| `unity_get_asset_catalog` | Retrieve saved asset catalog JSON by name |
| `unity_pin_asset_pack_context` | Pin cached asset-pack context (catalog + optional look/profile) |
| `unity_get_asset_pack_context_pin` | Retrieve pinned asset-pack context by name |
| `unity_list_asset_pack_context_pins` | List pinned asset-pack contexts |
| `unity_compare_images` | Compare two images and return similarity metrics + heatmap metadata |
| `unity_repro_step` | Generate confidence-scored patch proposals from reference/current images |
| `unity_repro_step_contextual` | Run repro step with pin/profile/catalog context hydration in one call |
