using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UnityMCP.Resources;

[McpServerResourceType]
public class SceneHierarchyResource
{
    [McpServerResource(
        Name = "Scene Hierarchy",
        UriTemplate = "unity://scene/hierarchy",
        MimeType = "application/json")]
    [Description("Browse the current Unity scene hierarchy as a tree of GameObjects with names, instance IDs, and child counts.")]
    public static async Task<ReadResourceResult> GetHierarchy(
        UnityClient client,
        RequestContext<ReadResourceRequestParams> context)
    {
        var json = await client.GetHierarchyAsync(depth: 3, brief: true);

        return new ReadResourceResult
        {
            Contents = new List<ResourceContents>
            {
                new TextResourceContents
                {
                    Text = json,
                    Uri = "unity://scene/hierarchy",
                    MimeType = "application/json"
                }
            }
        };
    }

    [McpServerResource(
        Name = "Scene Info",
        UriTemplate = "unity://scene/info",
        MimeType = "application/json")]
    [Description("Get current scene metadata including name, path, build index, and dirty state.")]
    public static async Task<ReadResourceResult> GetSceneInfo(
        UnityClient client,
        RequestContext<ReadResourceRequestParams> context)
    {
        var json = await client.GetSceneAsync();

        return new ReadResourceResult
        {
            Contents = new List<ResourceContents>
            {
                new TextResourceContents
                {
                    Text = json,
                    Uri = "unity://scene/info",
                    MimeType = "application/json"
                }
            }
        };
    }

    [McpServerResource(
        Name = "Installed Packages",
        UriTemplate = "unity://packages",
        MimeType = "application/json")]
    [Description("List all installed Unity packages with versions and sources.")]
    public static async Task<ReadResourceResult> GetPackages(
        UnityClient client,
        RequestContext<ReadResourceRequestParams> context)
    {
        var json = await client.ListPackagesAsync();

        return new ReadResourceResult
        {
            Contents = new List<ResourceContents>
            {
                new TextResourceContents
                {
                    Text = json,
                    Uri = "unity://packages",
                    MimeType = "application/json"
                }
            }
        };
    }

    [McpServerResource(
        Name = "Console Logs",
        UriTemplate = "unity://console",
        MimeType = "application/json")]
    [Description("Get recent Unity Editor console logs (errors, warnings, and messages).")]
    public static async Task<ReadResourceResult> GetConsoleLogs(
        UnityClient client,
        RequestContext<ReadResourceRequestParams> context)
    {
        var json = await client.GetConsoleLogsAsync(count: 100);

        return new ReadResourceResult
        {
            Contents = new List<ResourceContents>
            {
                new TextResourceContents
                {
                    Text = json,
                    Uri = "unity://console",
                    MimeType = "application/json"
                }
            }
        };
    }
}
