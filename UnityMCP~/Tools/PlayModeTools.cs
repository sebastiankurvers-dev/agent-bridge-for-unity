using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class PlayModeTools
{
    [McpServerTool(Name = "unity_play_mode")]
    [Description("Control Unity's Play mode. Start, stop, pause, or step through gameplay.")]
    public static async Task<string> ControlPlayMode(
        UnityClient client,
        [Description("The action to perform: 'play' (start), 'stop', 'pause', 'resume'/'unpause', or 'step' (advance one frame).")] string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return ToolErrors.ValidationError("action is required");
        }

        var validActions = new[] { "play", "stop", "pause", "resume", "unpause", "step" };
        if (!validActions.Contains(action.ToLowerInvariant()))
        {
            return ToolErrors.ValidationError($"Invalid action. Must be one of: {string.Join(", ", validActions)}");
        }

        return await client.ControlPlayModeAsync(action);
    }

    [McpServerTool(Name = "unity_play_mode_wait")]
    [Description("Control Unity play mode and wait for editor health to stabilize after the transition. " +
        "Use this before immediate follow-up runtime calls to avoid play-mode transition race conditions.")]
    public static async Task<string> ControlPlayModeAndWait(
        UnityClient client,
        [Description("The action to perform: 'play', 'stop', 'pause', 'resume'/'unpause', or 'step'.")] string action,
        [Description("Maximum time to wait for transition stabilization, in milliseconds. Default 15000.")] int maxWaitMs = 15000,
        [Description("Polling interval in milliseconds while waiting. Default 250.")] int pollIntervalMs = 250,
        [Description("Require a stable health window before returning success. Default true.")] bool requireHealthStable = true,
        [Description("Number of consecutive healthy polls required when requireHealthStable=true. Default 3.")] int stablePollCount = 3,
        [Description("Minimum stable duration in milliseconds when requireHealthStable=true. Default 750.")] int minStableMs = 750)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return ToolErrors.ValidationError("action is required");
        }

        var validActions = new[] { "play", "stop", "pause", "resume", "unpause", "step" };
        if (!validActions.Contains(action.ToLowerInvariant()))
        {
            return ToolErrors.ValidationError($"Invalid action. Must be one of: {string.Join(", ", validActions)}");
        }

        return await client.ControlPlayModeAndWaitAsync(
            action,
            maxWaitMs,
            pollIntervalMs,
            requireHealthStable,
            stablePollCount,
            minStableMs);
    }

    [McpServerTool(Name = "unity_get_status")]
    [Description("Get the current status of the Unity Editor including whether it's playing, paused, and project information.\n\n"
        + "TIP: Call unity_workflow_guide(task=\"your goal\") before starting complex tasks — it returns the exact tool sequence, "
        + "parameters, and pitfalls for 20+ workflows (scene building, 2D games, lighting, physics, UI, materials, etc.).")]
    public static async Task<string> GetStatus(UnityClient client)
    {
        var json = await client.GetHealthAsync();
        // Inject workflow hint into response so agents see it in the data, not just the description
        try
        {
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (dict != null)
            {
                dict["_tip"] = "Call unity_workflow_guide(task=\"your goal\") BEFORE building. It returns the correct tool sequence, required parameters, and common pitfalls. Especially important for 2D games, scene reconstruction, and lighting setups.";
                return System.Text.Json.JsonSerializer.Serialize(dict);
            }
        }
        catch { }
        return json;
    }

    [McpServerTool(Name = "unity_get_editor_runtime")]
    [Description("Get runtime/editor state for resiliency checks (compile/update/play state, queue pressure, domain reload count).")]
    public static async Task<string> GetEditorRuntime(UnityClient client)
    {
        return await client.GetEditorRuntimeAsync();
    }
}
