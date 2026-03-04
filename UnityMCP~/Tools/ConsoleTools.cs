using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class ConsoleTools
{
    [McpServerTool(Name = "unity_get_console")]
    [Description("Get recent Unity console log messages including errors, warnings, and debug logs. Supports filtering by log type and text search. Stack traces are omitted by default for token efficiency and can be requested when needed.")]
    public static async Task<string> GetConsoleLogs(
        UnityClient client,
        [Description("Number of recent log entries to retrieve (default: 50, max: 500).")] int count = 50,
        [Description("Filter by log type: 'Error', 'Warning', 'Log', 'Exception', or 'Assert'.")] string? type = null,
        [Description("Filter by text substring (case-insensitive search in message).")] string? text = null,
        [Description("Include full stackTrace for each log entry. Default false to reduce token usage.")] bool includeStackTrace = false)
    {
        count = Math.Clamp(count, 1, 500);
        return await client.GetConsoleLogsAsync(count, type, text, includeStackTrace);
    }

    [McpServerTool(Name = "unity_clear_console")]
    [Description("Clear all Unity console log messages from the buffer.")]
    public static async Task<string> ClearConsoleLogs(UnityClient client)
    {
        return await client.ClearConsoleLogsAsync();
    }
}
