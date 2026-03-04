using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class EventTools
{
    [McpServerTool(Name = "unity_poll_events")]
    [Description(@"Poll for Unity editor events. Returns events that occurred since the given event ID.

Event types:
- compilation_started: Scripts began compiling. Payload: {}
- compilation_finished: Compilation completed. Payload: {errorCount}
- compilation_error: A compilation error occurred. Payload: {message, file, line, column}
- play_mode_changed: Editor play state changed. Payload: {state} (EnteredEditMode, ExitingEditMode, EnteredPlayMode, ExitingPlayMode)
- scene_changed: Active scene changed in edit mode. Payload: {oldScene, newScene, newScenePath}
- log_error: A runtime error or exception was logged. Payload: {message, type, timestamp} by default. Set includeStackTrace=true to include stackTrace.

Workflow:
1. First call: unity_poll_events(since=0) to get recent events
2. Note the 'lastId' from the response
3. Next call: unity_poll_events(since=<lastId>) to get only new events
4. Use timeout (1-10 seconds) to long-poll and wait for events (e.g. after modifying a script, poll with timeout=5 to wait for compilation results)

The timeout parameter enables long-polling: the server will wait up to that many seconds for new events before returning an empty response. Use timeout=0 for instant polling.")]
    public static async Task<string> PollEvents(
        UnityClient client,
        [Description("Return events with ID greater than this value. Use 0 for all recent events, or use lastId from a previous response to get only new events.")]
        int since = 0,
        [Description("Long-poll timeout in seconds (0-10). 0 returns immediately. Higher values wait for new events. Useful after actions that trigger compilation or play mode changes.")]
        int timeout = 0,
        [Description("Include stackTrace field in log_error event payloads. Default false to keep event polls compact.")] bool includeStackTrace = false)
    {
        return await client.PollEventsAsync(since, timeout, includeStackTrace);
    }
}
