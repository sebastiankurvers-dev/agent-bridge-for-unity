using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class CompilationTools
{
    [McpServerTool(Name = "unity_get_compilation_status")]
    [Description("Check the current compilation state of Unity scripts. Returns whether compilation is in progress, the last compilation time, and the number of errors if any.")]
    public static async Task<string> GetCompilationStatus(UnityClient client)
    {
        return await client.GetCompilationStatusAsync();
    }

    [McpServerTool(Name = "unity_get_compilation_errors")]
    [Description("Get detailed information about compilation errors including file path, line number, column, and error message. Use this after detecting errors in compilation status.")]
    public static async Task<string> GetCompilationErrors(UnityClient client)
    {
        return await client.GetCompilationErrorsAsync();
    }

    [McpServerTool(Name = "unity_trigger_recompile")]
    [Description("Force Unity to recompile scripts. Supports force-reimporting one or many paths first. Optional wait mode polls until compilation finishes and returns final status.")]
    public static async Task<string> TriggerRecompile(
        UnityClient client,
        [Description("Optional single asset path to force-reimport before recompiling (e.g. 'Assets/Scripts/MyScript.cs').")] string? forceReimportPath = null,
        [Description("Optional additional asset paths to force-reimport before recompiling.")] string[]? forceReimportPaths = null,
        [Description("Wait until compilation finishes and return final errorCount. Default true.")] bool waitForCompile = true,
        [Description("Maximum wait in milliseconds when waitForCompile=true. Default 30000.")] int maxWaitMs = 30000,
        [Description("Compilation polling interval in milliseconds when waitForCompile=true. Default 250.")] int pollIntervalMs = 250)
    {
        return await client.TriggerRecompileAsync(
            forceReimportPath,
            forceReimportPaths,
            waitForCompile,
            maxWaitMs,
            pollIntervalMs);
    }
}
