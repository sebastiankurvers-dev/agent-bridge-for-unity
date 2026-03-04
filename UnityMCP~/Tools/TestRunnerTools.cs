using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class TestRunnerTools
{
    [McpServerTool(Name = "unity_run_tests")]
    [Description("Run Unity EditMode or PlayMode tests and return results. Supports filtering by assembly, class, method, or category.")]
    public static async Task<string> RunTests(
        UnityClient client,
        [Description("Test mode: 'EditMode' or 'PlayMode'. Default: 'EditMode'.")] string testMode = "EditMode",
        [Description("Filter by assembly name (e.g., 'Assembly-CSharp-Editor-firstpass.Tests').")] string? assemblyName = null,
        [Description("Filter by test class name.")] string? className = null,
        [Description("Filter by test method name.")] string? methodName = null,
        [Description("Filter by test category.")] string? categoryName = null,
        [Description("Timeout in milliseconds (default: 120000, max: 600000).")] int timeoutMs = 120000)
    {
        if (testMode != "EditMode" && testMode != "PlayMode")
            return ToolErrors.ValidationError("testMode must be 'EditMode' or 'PlayMode'");

        timeoutMs = Math.Clamp(timeoutMs, 5000, 600000);

        var request = new Dictionary<string, object?>
        {
            ["testMode"] = testMode,
            ["timeoutMs"] = timeoutMs
        };

        if (!string.IsNullOrEmpty(assemblyName)) request["assemblyName"] = assemblyName;
        if (!string.IsNullOrEmpty(className)) request["className"] = className;
        if (!string.IsNullOrEmpty(methodName)) request["methodName"] = methodName;
        if (!string.IsNullOrEmpty(categoryName)) request["categoryName"] = categoryName;

        return await client.RunTestsAsync(request, timeoutMs);
    }

    [McpServerTool(Name = "unity_get_test_results")]
    [Description("Get results from the last test run. Returns pass/fail/skip counts and individual test details.")]
    public static async Task<string> GetTestResults(
        UnityClient client)
    {
        return await client.GetTestResultsAsync();
    }
}
