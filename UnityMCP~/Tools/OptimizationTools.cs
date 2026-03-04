using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class OptimizationTools
{
    [McpServerTool(Name = "unity_capture_delta_snapshot")]
    [Description("Capture a named snapshot of scene object states (transforms + components) for later delta comparison. " +
        "Max 16 snapshots with LRU eviction. Capture before making changes, then use unity_get_delta to see what changed.")]
    public static async Task<string> CaptureDeltaSnapshot(
        UnityClient client,
        [Description("Name for the snapshot (e.g., 'before_lighting_change').")] string name,
        [Description("Optional array of specific instance IDs to snapshot. If omitted, snapshots all scene objects.")] int[]? instanceIds = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolErrors.ValidationError("name is required");
        }

        var request = new Dictionary<string, object?> { ["name"] = name };
        if (instanceIds != null && instanceIds.Length > 0)
            request["instanceIds"] = instanceIds;

        return await client.CaptureDeltaSnapshotAsync(request);
    }

    [McpServerTool(Name = "unity_get_delta")]
    [Description("Compare current scene state against a named delta snapshot. Returns lists of added, removed, and modified objects " +
        "with specific field-level changes (position, rotation, scale, components, active state).")]
    public static async Task<string> GetDelta(
        UnityClient client,
        [Description("Name of the snapshot to compare against.")] string snapshotName)
    {
        if (string.IsNullOrWhiteSpace(snapshotName))
        {
            return ToolErrors.ValidationError("snapshotName is required");
        }

        return await client.GetDeltaAsync(snapshotName);
    }

    [McpServerTool(Name = "unity_list_delta_snapshots")]
    [Description("List all stored delta snapshots with their names, object counts, and capture times.")]
    public static async Task<string> ListDeltaSnapshots(UnityClient client)
    {
        return await client.ListDeltaSnapshotsAsync();
    }

    [McpServerTool(Name = "unity_delete_delta_snapshot")]
    [Description("Delete a named delta snapshot.")]
    public static async Task<string> DeleteDeltaSnapshot(
        UnityClient client,
        [Description("Name of the snapshot to delete.")] string snapshotName)
    {
        if (string.IsNullOrWhiteSpace(snapshotName))
        {
            return ToolErrors.ValidationError("snapshotName is required");
        }

        return await client.DeleteDeltaSnapshotAsync(snapshotName);
    }

    [McpServerTool(Name = "unity_batch_read")]
    [Description("Execute multiple read-only queries in a single round-trip. Max 10 requests per batch. " +
        "Each request specifies a route string (e.g., '/hierarchy?brief=true', '/gameobject/123'). " +
        "Only read routes are allowed (no mutations). Returns an array of results with statusCode and body for each.")]
    public static async Task<string> BatchRead(
        UnityClient client,
        [Description("JSON array of request objects. Each: {route: string}. Example: [{\"route\":\"/hierarchy?brief=true\"},{\"route\":\"/scene\"}]")] string requests)
    {
        if (string.IsNullOrWhiteSpace(requests))
        {
            return ToolErrors.ValidationError("requests JSON array is required");
        }

        var requestsObj = System.Text.Json.JsonSerializer.Deserialize<object[]>(requests);
        return await client.BatchReadAsync(new { requests = requestsObj });
    }
}
