using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class CatalogTools
{
    [McpServerTool(Name = "unity_generate_asset_catalog")]
    [Description(@"Pre-index all prefabs in an asset pack folder into a persistent JSON catalog.
Scans the given rootFolder recursively for all .prefab files and saves the catalog to
Assets/Editor/AssetCatalogs/{name}.json.

Each catalog entry includes: name, path, category (derived from subfolder name).
With includeGeometry=true: also includes boundsSize [x,y,z] and boundsCenter [x,y,z]
computed from Renderer or Collider bounds.

The catalog is saved to disk and can be retrieved later with unity_get_asset_catalog.
Returns the file path, entry count, and category breakdown (not the full catalog inline).

Timeout: 60s (geometry computation for 700+ prefabs takes time).
Without geometry: fast metadata-only scan.

Example: unity_generate_asset_catalog('Assets/Prefabs', includeGeometry=true)")]
    public static async Task<string> GenerateAssetCatalog(
        UnityClient client,
        [Description("Root folder to scan for prefabs (e.g., 'Assets/Prefabs').")] string rootFolder,
        [Description("Optional catalog name. Defaults to the folder name.")] string? name = null,
        [Description("Include bounds geometry for each prefab (default false). Slower but enables precise placement.")] bool includeGeometry = false,
        [Description("Reuse existing saved catalog if present (default true).")] bool reuseExisting = true,
        [Description("Force regeneration even if saved catalog exists (default false).")] bool forceRegenerate = false,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            return ToolErrors.ValidationError("rootFolder is required");
        }

        return await client.GenerateAssetCatalogNewAsync(new
        {
            rootFolder,
            name = name ?? "",
            includeGeometry = includeGeometry ? 1 : 0,
            reuseExisting = reuseExisting ? 1 : 0,
            forceRegenerate = forceRegenerate ? 1 : 0
        }, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_get_asset_catalog")]
    [Description(@"Retrieve a previously generated asset catalog by name.
Returns the full catalog JSON including all entries with name, path, category,
and optionally boundsSize/boundsCenter if geometry was included during generation.

Use this after unity_generate_asset_catalog to access the full indexed data.")]
    public static async Task<string> GetAssetCatalog(
        UnityClient client,
        [Description("Name of the catalog to retrieve (same name used during generation).")] string name,
        [Description("Return compact token-efficient summary instead of full catalog JSON (default true).")] bool brief = true,
        [Description("Maximum entry preview items when brief=true (1-500).")] int maxEntries = 40)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolErrors.ValidationError("Catalog name is required");
        }

        return await client.GetSavedAssetCatalogAsync(name, brief, maxEntries);
    }

    [McpServerTool(Name = "unity_pin_asset_pack_context")]
    [Description(@"Pin and cache an asset-pack context for fast scene reproduction loops.
This prepares/reuses:
- prefab catalog (required)
- optional look preset snapshot
- optional scene profile snapshot

Result is saved under Assets/Editor/AssetPins/{name}.json for quick reuse.")]
    public static async Task<string> PinAssetPackContext(
        UnityClient client,
        [Description("Asset pack root folder (e.g., 'Assets/Prefabs').")] string rootFolder,
        [Description("Pin name. Defaults to folder name.")] string? name = null,
        [Description("Optional description for this pinned context.")] string? description = null,
        [Description("Catalog name. Defaults to pin name.")] string? catalogName = null,
        [Description("Include catalog geometry bounds (default true).")] bool includeGeometry = true,
        [Description("Reuse existing artifacts if present (default true).")] bool reuseExisting = true,
        [Description("Force refresh artifacts even when cached (default false).")] bool forceRefresh = false,
        [Description("Capture current scene look preset into this pin (default false).")] bool captureLookPreset = false,
        [Description("Optional look preset name. Defaults to '{pin}_Look'.")] string? lookPresetName = null,
        [Description("Capture current scene profile into this pin (default false).")] bool captureSceneProfile = false,
        [Description("Optional scene profile name. Defaults to '{pin}_Profile'.")] string? sceneProfileName = null,
        [Description("Optional route timeout override in milliseconds.")] int timeoutMs = 0)
    {
        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            return ToolErrors.ValidationError("rootFolder is required");
        }

        return await client.PinAssetPackContextAsync(new
        {
            rootFolder,
            name = name ?? "",
            description = description ?? "",
            catalogName = catalogName ?? "",
            includeGeometry = includeGeometry ? 1 : 0,
            reuseExisting = reuseExisting ? 1 : 0,
            forceRefresh = forceRefresh ? 1 : 0,
            captureLookPreset = captureLookPreset ? 1 : 0,
            lookPresetName = lookPresetName ?? "",
            captureSceneProfile = captureSceneProfile ? 1 : 0,
            sceneProfileName = sceneProfileName ?? ""
        }, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_get_asset_pack_context_pin")]
    [Description("Retrieve the full pinned asset-pack context JSON by pin name.")]
    public static async Task<string> GetAssetPackContextPin(
        UnityClient client,
        [Description("Pin name to retrieve.")] string name,
        [Description("Return compact token-efficient summary instead of full pin JSON (default true).")] bool brief = true,
        [Description("Maximum nested preview entries when brief=true (1-500).")] int maxEntries = 40)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolErrors.ValidationError("Pin name is required");
        }

        return await client.GetAssetPackContextPinAsync(name, brief, maxEntries);
    }

    [McpServerTool(Name = "unity_list_asset_pack_context_pins")]
    [Description("List saved asset-pack context pins for quick discovery and reuse.")]
    public static async Task<string> ListAssetPackContextPins(UnityClient client)
    {
        return await client.ListAssetPackContextPinsAsync();
    }
}
