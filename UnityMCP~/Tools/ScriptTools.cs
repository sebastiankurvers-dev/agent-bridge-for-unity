using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class ScriptTools
{
    [McpServerTool(Name = "unity_get_script")]
    [Description("Read the content of a C# script file from the Unity project. Returns the full source code along with metadata like class name, namespace, and base class.")]
    public static async Task<string> GetScript(
        UnityClient client,
        [Description("The path to the script file (e.g., 'Assets/Scripts/Player.cs').")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolErrors.ValidationError("Script path is required");
        }

        return await client.GetScriptAsync(path);
    }

    [McpServerTool(Name = "unity_create_script")]
    [Description("Create a new C# script file in the Unity project. Can either provide full content or generate a template based on class name and base class.")]
    public static async Task<string> CreateScript(
        UnityClient client,
        [Description("The path where the script should be created (e.g., 'Assets/Scripts/PlayerController.cs').")] string path,
        [Description("The full content of the script. If not provided, a template will be generated.")] string? content = null,
        [Description("The class name. Defaults to the file name without extension.")] string? className = null,
        [Description("The namespace for the class (optional).")] string? namespaceName = null,
        [Description("The base class to inherit from. Defaults to 'MonoBehaviour'.")] string? baseClass = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolErrors.ValidationError("Script path is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["path"] = path
        };

        if (!string.IsNullOrEmpty(content)) request["content"] = content;
        if (!string.IsNullOrEmpty(className)) request["className"] = className;
        if (!string.IsNullOrEmpty(namespaceName)) request["namespaceName"] = namespaceName;
        if (!string.IsNullOrEmpty(baseClass)) request["baseClass"] = baseClass;

        return await client.CreateScriptAsync(request);
    }

    [McpServerTool(Name = "unity_modify_script")]
    [Description("Modify an existing C# script by replacing its entire content. The script will be tracked for checkpointing.")]
    public static async Task<string> ModifyScript(
        UnityClient client,
        [Description("The path to the script file to modify.")] string path,
        [Description("The new content for the script (replaces entire file).")] string content)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolErrors.ValidationError("Script path is required");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return ToolErrors.ValidationError("Script content is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["content"] = content
        };

        return await client.ModifyScriptAsync(request);
    }

    [McpServerTool(Name = "unity_list_scripts")]
    [Description("List C# scripts in the project with optional filtering by name, MonoBehaviour, or ScriptableObject type. Supports pagination.")]
    public static async Task<string> ListScripts(
        UnityClient client,
        [Description("Filter by script name or class name (case-insensitive substring match).")] string? name = null,
        [Description("Filter for MonoBehaviour scripts only.")] bool? isMonoBehaviour = null,
        [Description("Filter for ScriptableObject scripts only.")] bool? isScriptableObject = null,
        [Description("Number of scripts to skip for pagination (default: 0).")] int offset = 0,
        [Description("Maximum number of scripts to return (default: 50, max: 200).")] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(0, offset);
        return await client.ListScriptsAsync(name, isMonoBehaviour, isScriptableObject, offset, limit);
    }

    [McpServerTool(Name = "unity_get_script_structure")]
    [Description("Get type structure of a C# script via reflection with projection and per-section limits for token-efficient inspection. Returns totals + truncation flags so you can request more only when needed.")]
    public static async Task<string> GetScriptStructure(
        UnityClient client,
        [Description("The path to the script file (e.g., 'Assets/Scripts/PlayerController.cs').")] string path,
        [Description("Include methods section.")] bool includeMethods = true,
        [Description("Include fields section.")] bool includeFields = true,
        [Description("Include properties section.")] bool includeProperties = true,
        [Description("Include events section.")] bool includeEvents = true,
        [Description("Max methods to return (-1 = unlimited). Default 80 for token efficiency.")] int maxMethods = 80,
        [Description("Max fields to return (-1 = unlimited). Default 80 for token efficiency.")] int maxFields = 80,
        [Description("Max properties to return (-1 = unlimited). Default 80 for token efficiency.")] int maxProperties = 80,
        [Description("Max events to return (-1 = unlimited). Default 80 for token efficiency.")] int maxEvents = 80,
        [Description("Include custom attributes on class/members. Default false to reduce payload size.")] bool includeAttributes = false,
        [Description("Include method parameter details. Default false to reduce payload size.")] bool includeMethodParameters = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolErrors.ValidationError("Script path is required");
        }

        maxMethods = maxMethods < 0 ? -1 : Math.Clamp(maxMethods, 1, 2000);
        maxFields = maxFields < 0 ? -1 : Math.Clamp(maxFields, 1, 2000);
        maxProperties = maxProperties < 0 ? -1 : Math.Clamp(maxProperties, 1, 2000);
        maxEvents = maxEvents < 0 ? -1 : Math.Clamp(maxEvents, 1, 2000);

        return await client.GetScriptStructureAsync(
            path,
            includeMethods,
            includeFields,
            includeProperties,
            includeEvents,
            maxMethods,
            maxFields,
            maxProperties,
            maxEvents,
            includeAttributes,
            includeMethodParameters);
    }

    [McpServerTool(Name = "unity_get_type_schema")]
    [Description("Get a JSON schema for any C# type by name. Returns public fields, serialized private fields, "
        + "and properties with their types, descriptions, and Unity-specific shapes (Vector3, Color, etc.). "
        + "Works from type name alone — useful for configuring components or ScriptableObjects.")]
    public static async Task<string> GetTypeSchema(
        UnityClient client,
        [Description("The type name to inspect. Can be fully qualified (e.g., 'UnityEngine.Light') or short name (e.g., 'Light').")] string typeName,
        [Description("Maximum members to return (default: 100, max: 500).")] int maxMembers = 100)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return ToolErrors.ValidationError("typeName is required");

        maxMembers = Math.Clamp(maxMembers, 1, 500);
        return await client.GetTypeSchemaAsync(typeName, maxMembers);
    }
}
