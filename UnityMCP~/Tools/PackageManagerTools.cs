using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class PackageManagerTools
{
    [McpServerTool(Name = "unity_list_packages")]
    [Description("List all installed Unity packages with name, version, source, and description.")]
    public static async Task<string> ListPackages(
        UnityClient client)
    {
        return await client.ListPackagesAsync();
    }

    [McpServerTool(Name = "unity_add_package")]
    [Description("Install a Unity package by identifier. Accepts package name (e.g., 'com.unity.textmeshpro'), "
        + "name@version (e.g., 'com.unity.textmeshpro@3.0.6'), or a git URL.")]
    public static async Task<string> AddPackage(
        UnityClient client,
        [Description("Package identifier: name, name@version, or git URL.")] string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            return ToolErrors.ValidationError("identifier is required");

        return await client.AddPackageAsync(identifier);
    }

    [McpServerTool(Name = "unity_remove_package")]
    [Description("Uninstall a Unity package by name (e.g., 'com.unity.textmeshpro').")]
    public static async Task<string> RemovePackage(
        UnityClient client,
        [Description("The package name to remove (e.g., 'com.unity.textmeshpro').")] string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolErrors.ValidationError("name is required");

        return await client.RemovePackageAsync(name);
    }

    [McpServerTool(Name = "unity_search_packages")]
    [Description("Search the Unity Package Registry for available packages.")]
    public static async Task<string> SearchPackages(
        UnityClient client,
        [Description("Search query (e.g., 'animation', 'ui', 'textmesh'). Empty string lists all.")] string query = "",
        [Description("Maximum results to return (default: 50, max: 200).")] int maxResults = 50)
    {
        maxResults = Math.Clamp(maxResults, 1, 200);
        return await client.SearchPackagesAsync(query, maxResults);
    }
}
