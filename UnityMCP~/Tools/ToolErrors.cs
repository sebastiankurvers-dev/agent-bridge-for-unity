using System.Text.Json;

namespace UnityMCP.Tools;

internal static class ToolErrors
{
    internal static string ValidationError(string message) =>
        JsonSerializer.Serialize(new { success = false, error = message });
}
