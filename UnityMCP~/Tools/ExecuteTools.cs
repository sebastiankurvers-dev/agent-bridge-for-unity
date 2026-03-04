using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class ExecuteTools
{
    [McpServerTool(Name = "unity_get_routes")]
    [Description("Get a catalog of available bridge routes with method, path, category, and description. Supports optional filtering and compact mode for token-efficient discovery.")]
    public static async Task<string> GetRoutes(
        UnityClient client,
        [Description("Optional category filter (e.g., 'scene', 'runtime', 'profiles').")] string? category = null,
        [Description("Optional case-insensitive search across method/path/category/description.")] string? search = null,
        [Description("Optional max routes to return (1-500). Default 0 returns all matches.")] int max = 0,
        [Description("If true, omit route descriptions and return compact entries (method/path/category).")] bool compact = false)
    {
        if (max < 0) max = 0;
        return await client.GetRoutesAsync(category, search, max, compact);
    }

    [McpServerTool(Name = "unity_execute_csharp")]
    [Description("Execute a C# code snippet in the Unity Editor context. "
        + "TIP: If you plan 3+ execute_csharp calls, call unity_register_execute_helpers FIRST to define reusable factory/utility functions — they auto-prepend to every subsequent call, eliminating boilerplate. "
        + "IMPORTANT: Use renderer.sharedMaterial (not renderer.material) to avoid material leaks in edit mode. "
        + "Use Print() to return values.\n\n"
        + "SCENE RECONSTRUCTION: If building from a reference image, call unity_capture_and_compare after every major build step. "
        + "Don't just eyeball screenshots — the compare tool returns spatial suggestions telling you what to fix next.")]
    public static async Task<string> ExecuteCSharp(
        UnityClient client,
        [Description("The C# code to execute. Registered helpers are automatically available — no need to redefine them.")] string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return ToolErrors.ValidationError("No code provided");
        }

        // Auto-rewrite renderer.material to renderer.sharedMaterial to prevent material leaks in edit mode
        string? materialWarning = null;
        if (System.Text.RegularExpressions.Regex.IsMatch(code, @"(?<!shared)\.material(?!s)\b"))
        {
            code = System.Text.RegularExpressions.Regex.Replace(code, @"(?<!shared)\.material(?!s)\b", ".sharedMaterial");
            materialWarning = "Auto-replaced .material with .sharedMaterial to prevent material leaks in edit mode.";
        }

        var result = await client.ExecuteCSharpAsync(code);

        if (materialWarning != null)
        {
            // Prepend warning to result
            result = $"[WARNING] {materialWarning}\n{result}";
        }

        return result;
    }

    [McpServerTool(Name = "unity_register_execute_helpers")]
    [Description("Register reusable C# helper functions that persist across execute_csharp calls. "
        + "Registered helpers are auto-prepended to every subsequent unity_execute_csharp call, eliminating boilerplate. "
        + "Example: register Box/Cyl/Sphere factory functions once, then use them in all later calls without re-defining.")]
    public static async Task<string> RegisterExecuteHelpers(
        UnityClient client,
        [Description("Unique name for this helper set (e.g., 'scene_factories', 'material_helpers'). Re-registering the same name overwrites.")] string name,
        [Description("C# code defining helper functions. These become available as local functions in all subsequent execute_csharp calls. "
            + "Example: 'GameObject Box(string n, Vector3 p, Vector3 s, Color c) { var go = GameObject.CreatePrimitive(PrimitiveType.Cube); ... return go; }'")] string code)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolErrors.ValidationError("name is required");
        if (string.IsNullOrWhiteSpace(code))
            return ToolErrors.ValidationError("code is required");

        return await client.RegisterExecuteHelpersAsync(name, code);
    }

    [McpServerTool(Name = "unity_list_execute_helpers")]
    [Description("List all registered execute_csharp helper sets with names and code previews.")]
    public static async Task<string> ListExecuteHelpers(UnityClient client)
    {
        return await client.ListExecuteHelpersAsync();
    }

    [McpServerTool(Name = "unity_clear_execute_helpers")]
    [Description("Clear all registered execute_csharp helpers. Use when starting a fresh build session.")]
    public static async Task<string> ClearExecuteHelpers(UnityClient client)
    {
        return await client.ClearExecuteHelpersAsync();
    }

    [McpServerTool(Name = "unity_screenshot")]
    [Description("Capture a screenshot of the Unity Game or Scene view. Returns the image directly as visual content. Supports custom resolution for aspect-matched comparisons. "
        + "NOTE: Scene view screenshots do NOT include post-processing effects (bloom, color grading, etc.) — use viewType='game' for post-processing-accurate captures. "
        + "Post-processing Volume effects are only visible in Game view.\n\n"
        + "SCENE RECONSTRUCTION: When working from a reference image, prefer unity_capture_and_compare instead — it captures AND compares in one call, "
        + "returning spatial suggestions (camera angle, object placement, lighting direction) that guide your next action.")]
    public static async Task<IList<AIContent>> TakeScreenshot(
        UnityClient client,
        [Description("Which view to capture: 'game' for Game view, 'scene' for Scene view. Default: 'game'")] string viewType = "game",
        [Description("If true, includes the base64 image payload. Default false returns metadata + optional handle only.")] bool includeImage = false,
        [Description("If true, stores screenshot in bridge cache and returns an imageHandle for reuse (default true).")] bool includeHandle = true,
        [Description("Payload mode: 'auto' (derive from includeImage/includeHandle), 'handle', 'base64', or 'both'.")] string payloadMode = "auto",
        [Description("Custom render width in pixels (64-1920). Both width and height must be set. Bypasses Game View size and 800px cap.")] int width = 0,
        [Description("Custom render height in pixels (64-1920). Both width and height must be set. Bypasses Game View size and 800px cap.")] int height = 0)
    {
        viewType = viewType?.Trim().ToLowerInvariant() ?? "scene";
        if (viewType != "game" && viewType != "scene")
        {
            viewType = "scene";
        }

        payloadMode = string.IsNullOrWhiteSpace(payloadMode) ? "auto" : payloadMode.Trim().ToLowerInvariant();
        if (payloadMode is not ("auto" or "handle" or "base64" or "both"))
        {
            return new List<AIContent> { new TextContent(ToolErrors.ValidationError("payloadMode must be one of: auto, handle, base64, both")) };
        }

        var json = await client.TakeScreenshotAsync(viewType, includeImage, includeHandle, width, height, payloadMode);

        if (!includeImage)
        {
            return new List<AIContent> { new TextContent(json) };
        }

        return ImageContentHelper.ExtractImageContent(json);
    }
}
