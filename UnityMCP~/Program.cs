using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UnityMCP;
using UnityMCP.Prompts;
using UnityMCP.Resources;
using UnityMCP.Tools;

var builder = Host.CreateApplicationBuilder(args);

// MCP stdio transport requires stdout to be protocol-only.
// Route all logging to stderr so log lines don't contaminate the MCP frame stream.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<UnityClient>();

var toolProfile = NormalizeEnv(Environment.GetEnvironmentVariable("UNITY_MCP_TOOL_PROFILE"), "full");
var schemaMode = NormalizeEnv(Environment.GetEnvironmentVariable("UNITY_MCP_TOOL_SCHEMA_MODE"), "compact");
var discoveredToolTypes = DiscoverToolTypes(typeof(UnityClient).Assembly);
var selectedToolTypes = SelectToolTypes(discoveredToolTypes, toolProfile);

var mcpBuilder = builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithResources<SceneHierarchyResource>()
    .WithPrompts<SceneReconstructionPrompt>();

if (schemaMode == "full")
{
    mcpBuilder.WithTools(selectedToolTypes);
}
else
{
    using var schemaServices = builder.Services.BuildServiceProvider();
    var tools = BuildTools(selectedToolTypes, schemaServices, schemaMode);
    mcpBuilder.WithTools(tools);
}

var app = builder.Build();

await app.RunAsync();

static string NormalizeEnv(string? value, string fallback)
{
    return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
}

static IReadOnlyList<Type> DiscoverToolTypes(Assembly assembly)
{
    return assembly
        .GetTypes()
        .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
        .OrderBy(t => t.FullName, StringComparer.Ordinal)
        .ToArray();
}

static IReadOnlyList<Type> SelectToolTypes(IReadOnlyList<Type> allToolTypes, string profile)
{
    if (profile == "core")
    {
        var coreTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(AlignmentTools),
            nameof(AssetManagementTools),
            nameof(AssetTools),
            nameof(CaptureCompareTools),
            nameof(CatalogTools),
            nameof(CheckpointTools),
            nameof(ColorAnalysisTools),
            nameof(ComponentTools),
            nameof(CompilationTools),
            nameof(ConsoleTools),
            nameof(EventTools),
            nameof(ExecuteTools),
            nameof(GameObjectTools),
            nameof(GameplayTools),
            nameof(HierarchyTools),
            nameof(LightingTools),
            nameof(LookPresetTools),
            nameof(PerformanceTools),
            nameof(PlayModeTools),
            nameof(ProjectTools),
            nameof(RenderAuditTools),
            nameof(SceneBuilderTools),
            nameof(SceneViewTools),
            nameof(ScriptTools),
            nameof(SerializationTools),
            nameof(ShaderTools),
            nameof(UIToolkitTools),
            nameof(UITools),
            nameof(PackageManagerTools),
            nameof(PrimitiveTools),
            nameof(TestRunnerTools)
        };

        return allToolTypes
            .Where(t => coreTypeNames.Contains(t.Name))
            .ToArray();
    }

    return allToolTypes;
}

static IReadOnlyList<McpServerTool> BuildTools(
    IReadOnlyList<Type> toolTypes,
    IServiceProvider schemaServices,
    string schemaMode)
{
    var compactSchema = schemaMode is "compact" or "minimal";
    var removeMethodDescriptions = schemaMode == "minimal";
    var tools = new List<McpServerTool>();

    foreach (var toolType in toolTypes)
    {
        var methods = toolType
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .OrderBy(m => m.Name, StringComparer.Ordinal);

        foreach (var method in methods)
        {
            var toolAttribute = method.GetCustomAttribute<McpServerToolAttribute>();
            var options = new McpServerToolCreateOptions
            {
                Services = schemaServices,
                Name = string.IsNullOrWhiteSpace(toolAttribute?.Name) ? null : toolAttribute!.Name,
                Description = removeMethodDescriptions
                    ? string.Empty
                    : CompactDescription(method.GetCustomAttribute<DescriptionAttribute>()?.Description),
                SchemaCreateOptions = compactSchema
                    ? new AIJsonSchemaCreateOptions
                    {
                        IncludeSchemaKeyword = false,
                        TransformSchemaNode = static (_, node) => StripSchemaDescriptions(node)
                    }
                    : null
            };

            McpServerTool tool;
            if (method.IsStatic)
            {
                tool = McpServerTool.Create(method, target: null, options);
            }
            else
            {
                tool = McpServerTool.Create(
                    method,
                    request => ActivatorUtilities.CreateInstance(request.Services!, toolType),
                    options);
            }

            tools.Add(tool);
        }
    }

    return tools;
}

static string? CompactDescription(string? description)
{
    if (string.IsNullOrWhiteSpace(description))
    {
        return null;
    }

    var firstLine = description
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .FirstOrDefault(line => line.Length > 0);

    if (string.IsNullOrWhiteSpace(firstLine))
    {
        return null;
    }

    var sentenceEnd = firstLine.IndexOf(". ", StringComparison.Ordinal);
    var compact = sentenceEnd > 0 ? firstLine[..(sentenceEnd + 1)] : firstLine;
    return compact.Length <= 140 ? compact : compact[..140];
}

static JsonNode StripSchemaDescriptions(JsonNode node)
{
    if (node is JsonObject obj)
    {
        obj.Remove("$schema");
        obj.Remove("description");

        foreach (var property in obj.ToList())
        {
            if (property.Value != null)
            {
                StripSchemaDescriptions(property.Value);
            }
        }
    }
    else if (node is JsonArray arr)
    {
        foreach (var child in arr)
        {
            if (child != null)
            {
                StripSchemaDescriptions(child);
            }
        }
    }

    return node;
}
