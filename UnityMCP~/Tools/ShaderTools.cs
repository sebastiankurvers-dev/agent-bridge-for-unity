using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class ShaderTools
{
    [McpServerTool(Name = "unity_create_shader")]
    [Description("Create a new shader file from a template or custom content. Templates include Unlit, Surface, URP, and transparent variants.")]
    public static async Task<string> CreateShader(
        UnityClient client,
        [Description("Path for the shader file (e.g., 'Assets/Shaders/MyShader.shader').")] string path,
        [Description("Shader name as it appears in material dropdown (e.g., 'Custom/MyShader'). Defaults to filename.")] string? name = null,
        [Description("Shader type for template generation: 'Unlit', 'UnlitTransparent', 'Surface', 'SurfaceNormal', 'SurfaceEmission', 'SurfaceFull', 'URP', 'URPTransparent'. If content is provided, this is ignored.")] string? shaderType = null,
        [Description("Full shader source code. If not provided, generates from shaderType template.")] string? content = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolErrors.ValidationError("Shader path is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["path"] = path
        };

        if (!string.IsNullOrEmpty(name)) request["name"] = name;
        if (!string.IsNullOrEmpty(shaderType)) request["shaderType"] = shaderType;
        if (!string.IsNullOrEmpty(content)) request["content"] = content;

        return await client.CreateShaderAsync(request);
    }

    [McpServerTool(Name = "unity_modify_shader")]
    [Description("Modify an existing shader file with new content.")]
    public static async Task<string> ModifyShader(
        UnityClient client,
        [Description("Path to the shader file to modify.")] string path,
        [Description("New shader source code.")] string content)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolErrors.ValidationError("Shader path is required");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return ToolErrors.ValidationError("Shader content is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["content"] = content
        };

        return await client.ModifyShaderAsync(request);
    }

    [McpServerTool(Name = "unity_get_shader")]
    [Description("Read the content of a shader file.")]
    public static async Task<string> GetShader(
        UnityClient client,
        [Description("Path to the shader file.")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolErrors.ValidationError("Shader path is required");
        }

        return await client.GetShaderAsync(path);
    }

    [McpServerTool(Name = "unity_find_shaders")]
    [Description("Search for shaders in the project by name or path.")]
    public static async Task<string> FindShaders(
        UnityClient client,
        [Description("Optional search query to filter shaders by name or path.")] string search = "",
        [Description("Maximum results to return (1-5000, default 100).")] int maxResults = 100)
    {
        maxResults = Math.Clamp(maxResults, 1, 5000);
        return await client.FindShadersAsync(search, maxResults);
    }

    [McpServerTool(Name = "unity_find_shaders_scoped")]
    [Description("Search shaders with folder scope filters. Use includeRoots to keep shader discovery in a specific package/folder.")]
    public static async Task<string> FindShadersScoped(
        UnityClient client,
        [Description("Optional search query. Empty returns all shaders in scope (limited by maxResults).")] string search = "",
        [Description("Only include assets under these roots (e.g., ['Assets/Shaders']). Empty means project-wide.")] string[]? includeRoots = null,
        [Description("Exclude assets under these roots.")] string[]? excludeRoots = null,
        [Description("If true, includes subfolders recursively. If false, only direct children of the include root directories are considered.")] bool includeSubfolders = true,
        [Description("Maximum results to return (1-5000).")] int maxResults = 200,
        [Description("Search mode: 'contains', 'exact', or 'fuzzy'.")] string matchMode = "contains")
    {
        return await client.FindShadersScopedAsync(
            search,
            includeRoots,
            excludeRoots,
            includeSubfolders,
            maxResults,
            matchMode);
    }

    [McpServerTool(Name = "unity_get_shader_properties")]
    [Description("Get the properties defined in a shader (e.g., _MainTex, _Color, _Metallic).")]
    public static async Task<string> GetShaderProperties(
        UnityClient client,
        [Description("Path to the shader file, or shader name (e.g., 'Standard', 'Unlit/Color').")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolErrors.ValidationError("Shader path or name is required");
        }

        return await client.GetShaderPropertiesAsync(path);
    }

    [McpServerTool(Name = "unity_set_shader_keyword")]
    [Description("Enable or disable a shader keyword on a material. Keywords control shader variants (e.g., _ALPHABLEND_ON, _NORMALMAP).")]
    public static async Task<string> SetShaderKeyword(
        UnityClient client,
        [Description("Path to the material asset (e.g., 'Assets/Materials/Glass.mat').")] string materialPath,
        [Description("Shader keyword to toggle (e.g., '_ALPHABLEND_ON', '_NORMALMAP', '_EMISSION').")] string keyword,
        [Description("Whether to enable (true) or disable (false) the keyword.")] bool enabled)
    {
        if (string.IsNullOrWhiteSpace(materialPath))
        {
            return ToolErrors.ValidationError("Material path is required");
        }

        if (string.IsNullOrWhiteSpace(keyword))
        {
            return ToolErrors.ValidationError("Keyword is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["materialPath"] = materialPath,
            ["keyword"] = keyword,
            ["enabled"] = enabled
        };

        return await client.SetShaderKeywordAsync(request);
    }
}
