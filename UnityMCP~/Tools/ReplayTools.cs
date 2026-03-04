using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class ReplayTools
{
    [McpServerTool(Name = "unity_replay_start_recording")]
    [Description("Start recording a replay session during play mode. Captures player state snapshots at regular intervals. " +
        "Use unity_replay_record_input to tag input events during recording. " +
        "Stop recording with unity_replay_stop_recording to get the full session data.")]
    public static async Task<string> StartRecording(
        UnityClient client,
        [Description("Instance ID of the GameObject to track (e.g., player).")] int targetInstanceId,
        [Description("Optional component type to capture extra serialized fields from.")] string targetComponentType = "",
        [Description("Optional match ID for seed-based deterministic replay.")] string matchId = "",
        [Description("Optional round number for seed lookup.")] int roundNumber = 0,
        [Description("Optional explicit seed value.")] int seed = 0,
        [Description("How often to capture state snapshots in milliseconds. Default 100ms.")] float captureIntervalMs = 100f)
    {
        if (targetInstanceId == 0)
        {
            return ToolErrors.ValidationError("targetInstanceId is required");
        }

        return await client.ReplayStartRecordingAsync(new
        {
            targetInstanceId,
            targetComponentType,
            matchId,
            roundNumber,
            seed,
            captureIntervalMs
        });
    }

    [McpServerTool(Name = "unity_replay_stop_recording")]
    [Description("Stop the active replay recording. Returns a compact summary with sessionId (frames stored server-side). " +
        "Pass the sessionId to unity_replay_execute — no need to send frame data back.")]
    public static async Task<string> StopRecording(UnityClient client)
    {
        return await client.ReplayStopRecordingAsync();
    }

    [McpServerTool(Name = "unity_replay_record_input")]
    [Description("Record an input event during an active replay recording. Call this right before or after unity_invoke_method " +
        "to tag the action in the replay timeline. The timestamp is auto-captured.")]
    public static async Task<string> RecordInput(
        UnityClient client,
        [Description("The action name (e.g., 'ChangeLane', 'TogglePhase', 'Jump').")] string action,
        [Description("Optional arguments for the action.")] string[]? args = null)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return ToolErrors.ValidationError("action is required");
        }

        var request = new Dictionary<string, object?> { ["action"] = action };
        if (args != null && args.Length > 0)
            request["args"] = args;

        return await client.ReplayRecordInputAsync(request);
    }

    [McpServerTool(Name = "unity_replay_execute")]
    [Description("Execute a recorded replay session and verify determinism. Replays recorded inputs at their timestamps and " +
        "compares state frames. Returns a determinism report with divergence details. " +
        "The game must be in play mode with the same seed/track for meaningful comparison.")]
    public static async Task<string> Execute(
        UnityClient client,
        [Description("Session ID returned by unity_replay_stop_recording. Preferred — avoids sending frame data.")] string? sessionId = null,
        [Description("Full session JSON (legacy). Use sessionId instead when available.")] string? session = null,
        [Description("Timeout in milliseconds. Default 30000ms to accommodate replay duration.")] int timeoutMs = 30000)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return await client.ReplayExecuteAsync(new { sessionId }, timeoutMs > 0 ? timeoutMs : null);
        }

        if (string.IsNullOrWhiteSpace(session))
        {
            return ToolErrors.ValidationError("Either sessionId or session JSON is required");
        }

        // If session looks like a plain sessionId (no braces), treat it as one
        var trimmed = session.Trim();
        if (!trimmed.StartsWith("{"))
        {
            return await client.ReplayExecuteAsync(new { sessionId = trimmed }, timeoutMs > 0 ? timeoutMs : null);
        }

        var sessionObj = System.Text.Json.JsonSerializer.Deserialize<object>(session);
        return await client.ReplayExecuteAsync(new { session = sessionObj }, timeoutMs > 0 ? timeoutMs : null);
    }

    [McpServerTool(Name = "unity_replay_status")]
    [Description("Get the current status of the replay system: whether recording or playing back, frame/input counts.")]
    public static async Task<string> GetStatus(UnityClient client)
    {
        return await client.ReplayGetStatusAsync();
    }
}
