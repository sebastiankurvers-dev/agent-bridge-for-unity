using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class CheckpointTools
{
    [McpServerTool(Name = "unity_create_checkpoint")]
    [Description("Create a checkpoint to save the current state. By default this is scene + tracked scripts only for speed; optionally include a capped recent-script scan.")]
    public static async Task<string> CreateCheckpoint(
        UnityClient client,
        [Description("A descriptive name for the checkpoint (e.g., 'Before player refactor', 'Working movement system').")] string name,
        [Description("If true, also scans and backs up recently modified scripts. Slower on large projects.")] bool includeRecentScripts = false,
        [Description("Maximum number of recent scripts to include when includeRecentScripts=true (1-500).")] int maxRecentScripts = 50)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Checkpoint {DateTime.Now:yyyy-MM-dd HH:mm}";
        }

        maxRecentScripts = Math.Clamp(maxRecentScripts, 1, 500);
        return await client.CreateCheckpointAsync(name, includeRecentScripts, maxRecentScripts);
    }

    [McpServerTool(Name = "unity_list_checkpoints")]
    [Description("List all available checkpoints, ordered by most recent first.")]
    public static async Task<string> ListCheckpoints(UnityClient client)
    {
        return await client.ListCheckpointsAsync();
    }

    [McpServerTool(Name = "unity_restore_checkpoint")]
    [Description("Restore the project to a previous checkpoint. This will revert modified scripts and the scene to the state at checkpoint creation. Use unity_list_checkpoints first to get checkpoint IDs.")]
    public static async Task<string> RestoreCheckpoint(
        UnityClient client,
        [Description("The ID of the checkpoint to restore (get this from unity_list_checkpoints).")] string checkpointId)
    {
        if (string.IsNullOrWhiteSpace(checkpointId))
        {
            return ToolErrors.ValidationError("Checkpoint ID is required");
        }

        return await client.RestoreCheckpointAsync(checkpointId);
    }

    [McpServerTool(Name = "unity_delete_checkpoint")]
    [Description("Delete a checkpoint. This frees up disk space used by the checkpoint's backups.")]
    public static async Task<string> DeleteCheckpoint(
        UnityClient client,
        [Description("The ID of the checkpoint to delete.")] string checkpointId)
    {
        if (string.IsNullOrWhiteSpace(checkpointId))
        {
            return ToolErrors.ValidationError("Checkpoint ID is required");
        }

        return await client.DeleteCheckpointAsync(checkpointId);
    }

    [McpServerTool(Name = "unity_get_diff")]
    [Description("Get a diff showing changes made to a file since the last checkpoint. Useful for reviewing what changed before committing or restoring.")]
    public static async Task<string> GetDiff(
        UnityClient client,
        [Description("The path to the file to diff (e.g., 'Assets/Scripts/Player.cs').")] string filePath,
        [Description("Optional checkpoint ID to compare against. If not specified, uses the most recent checkpoint.")] string? checkpointId = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return ToolErrors.ValidationError("File path is required");
        }

        return await client.GetDiffAsync(filePath, checkpointId);
    }
}
