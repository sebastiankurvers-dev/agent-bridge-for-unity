using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityAgentBridge
{
    /// <summary>
    /// Manages checkpoints for code and scene state, enabling restore and diff operations.
    /// </summary>
    public static class CheckpointManager
    {
        private const int DefaultMaxRecentScripts = 50;
        private const int CheckpointSampleFileLimit = 12;
        private static List<Checkpoint> _checkpoints = new List<Checkpoint>();
        private static readonly ConcurrentDictionary<string, byte> _trackedScripts = new ConcurrentDictionary<string, byte>();
        private static string _checkpointDir;

        static CheckpointManager()
        {
            // Prefer Unity's historical checkpoint folder if it exists, otherwise use a stable temp folder.
            var defaultDir = Path.Combine(Path.GetTempPath(), "AgentBridgeCheckpoints");
            var projectFolderName = new DirectoryInfo(Directory.GetCurrentDirectory()).Name;
            var legacyUnityDir = Path.Combine(Path.GetTempPath(), "DefaultCompany", projectFolderName, "AgentBridgeCheckpoints");

            _checkpointDir = Directory.Exists(legacyUnityDir) ? legacyUnityDir : defaultDir;
            if (!Directory.Exists(_checkpointDir))
            {
                Directory.CreateDirectory(_checkpointDir);
            }
            LoadCheckpointIndex();
        }

        /// <summary>
        /// Create a new checkpoint with the current state.
        /// </summary>
        public static string CreateCheckpoint(string name, bool includeRecentScripts = false, int maxRecentScripts = DefaultMaxRecentScripts)
        {
            var checkpoint = new Checkpoint
            {
                id = Guid.NewGuid().ToString("N").Substring(0, 8),
                name = string.IsNullOrEmpty(name) ? $"Checkpoint {_checkpoints.Count + 1}" : name,
                timestamp = DateTime.Now,
                scenePath = SceneManager.GetActiveScene().path,
                sceneName = SceneManager.GetActiveScene().name,
                modifiedFiles = new List<FileBackup>()
            };

            var checkpointPath = Path.Combine(_checkpointDir, checkpoint.id);
            Directory.CreateDirectory(checkpointPath);

            try
            {
                // Save current scene state
                var currentScene = SceneManager.GetActiveScene();
                if (!string.IsNullOrEmpty(currentScene.path))
                {
                    var sceneCopy = Path.Combine(checkpointPath, "scene.unity");
                    EditorSceneManager.SaveScene(currentScene, sceneCopy, true);
                    checkpoint.sceneBackupPath = sceneCopy;
                }

                // Backup all tracked scripts plus recently modified scripts
                var scriptsToBackup = new HashSet<string>(_trackedScripts.Keys);

                // Recent script scanning is expensive on large projects, keep it opt-in.
                if (includeRecentScripts)
                {
                    var recentlyModified = GetRecentlyModifiedScripts(TimeSpan.FromHours(1), Math.Clamp(maxRecentScripts, 1, 500));
                    foreach (var script in recentlyModified)
                    {
                        scriptsToBackup.Add(script);
                    }
                }

                foreach (var scriptPath in scriptsToBackup)
                {
                    if (File.Exists(scriptPath))
                    {
                        var content = File.ReadAllText(scriptPath);
                        var backupPath = Path.Combine(checkpointPath, scriptPath.Replace("/", "_").Replace("\\", "_") + "." + checkpoint.id + ".bak");

                        var backup = new FileBackup
                        {
                            originalPath = scriptPath,
                            backupPath = backupPath
                        };
                        File.WriteAllText(backup.backupPath, content);
                        checkpoint.modifiedFiles.Add(backup);
                    }
                }

                _checkpoints.Add(checkpoint);
                SaveCheckpointIndex();

                return UnityCommands.JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "checkpoint", BuildCheckpointSummary(checkpoint, includeSampleFiles: true, sampleFileLimit: CheckpointSampleFileLimit, includeBackupPath: true) },
                    { "options", new Dictionary<string, object>
                        {
                            { "includeRecentScripts", includeRecentScripts },
                            { "maxRecentScripts", Math.Clamp(maxRecentScripts, 1, 500) }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                // Clean up on failure
                if (Directory.Exists(checkpointPath))
                {
                    Directory.Delete(checkpointPath, true);
                }
                return UnityCommands.JsonError(ex.Message);
            }
        }

        /// <summary>
        /// List all available checkpoints.
        /// </summary>
        public static string ListCheckpoints()
        {
            var summaries = _checkpoints
                .OrderByDescending(c => c.timestamp)
                .Select(c => BuildCheckpointSummary(c, includeSampleFiles: false, includeBackupPath: false))
                .ToList();

            return UnityCommands.JsonResult(new Dictionary<string, object>
            {
                { "checkpoints", summaries },
                { "count", summaries.Count }
            });
        }

        /// <summary>
        /// Restore to a specific checkpoint.
        /// </summary>
        public static string RestoreCheckpoint(string checkpointId)
        {
            var checkpoint = _checkpoints.Find(c => c.id == checkpointId);
            if (checkpoint == null)
            {
                return UnityCommands.JsonError($"Checkpoint not found: {checkpointId}");
            }

            try
            {
                var restoredFiles = new List<string>();

                // Restore files
                foreach (var backup in checkpoint.modifiedFiles)
                {
                    if (File.Exists(backup.backupPath))
                    {
                        var validPath = UnityCommands.ValidateAssetPath(backup.originalPath);
                        if (validPath == null)
                        {
                            continue; // Skip files with invalid paths
                        }

                        // Create backup of current state before restoring
                        var currentContent = File.Exists(backup.originalPath) ? File.ReadAllText(backup.originalPath) : "";

                        File.Copy(backup.backupPath, backup.originalPath, overwrite: true);
                        restoredFiles.Add(backup.originalPath);
                    }
                }

                // Restore scene if it was backed up
                if (!string.IsNullOrEmpty(checkpoint.sceneBackupPath) && File.Exists(checkpoint.sceneBackupPath)
                    && !string.IsNullOrEmpty(checkpoint.scenePath))
                {
                    // Save then unload the current scene so the file isn't "loaded" when we overwrite it.
                    // This prevents the "modified externally" modal dialog from appearing.
                    EditorSceneManager.SaveOpenScenes();
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                    // Overwrite scene file while no scene references it
                    File.Copy(checkpoint.sceneBackupPath, checkpoint.scenePath, overwrite: true);

                    // Now open the restored scene — Unity sees a fresh load, no "external modification"
                    EditorSceneManager.OpenScene(checkpoint.scenePath);
                }

                AssetDatabase.Refresh();

                return UnityCommands.JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "message", $"Restored to checkpoint: {checkpoint.name}" },
                    { "restoredFiles", restoredFiles },
                    { "checkpointId", checkpoint.id }
                });
            }
            catch (Exception ex)
            {
                return UnityCommands.JsonError(ex.Message);
            }
        }

        /// <summary>
        /// Delete a checkpoint.
        /// </summary>
        public static string DeleteCheckpoint(string checkpointId)
        {
            var checkpoint = _checkpoints.Find(c => c.id == checkpointId);
            if (checkpoint == null)
            {
                return UnityCommands.JsonError($"Checkpoint not found: {checkpointId}");
            }

            try
            {
                var checkpointPath = Path.Combine(_checkpointDir, checkpoint.id);
                if (Directory.Exists(checkpointPath))
                {
                    Directory.Delete(checkpointPath, true);
                }

                _checkpoints.Remove(checkpoint);
                SaveCheckpointIndex();

                return UnityCommands.JsonResult(new Dictionary<string, object> { { "success", true }, { "message", $"Deleted checkpoint: {checkpoint.name}" } });
            }
            catch (Exception ex)
            {
                return UnityCommands.JsonError(ex.Message);
            }
        }

        /// <summary>
        /// Get diff between current file state and a checkpoint.
        /// </summary>
        public static string GetDiff(string filePath, string checkpointId = null)
        {
            if (!File.Exists(filePath))
            {
                return UnityCommands.JsonError($"File not found: {filePath}");
            }

            var validPath = UnityCommands.ValidateAssetPath(filePath);
            if (validPath == null)
            {
                return UnityCommands.JsonError($"Invalid file path: {filePath}");
            }

            var currentContent = File.ReadAllText(filePath);
            string previousContent = "";
            string compareSource = "empty";

            if (!string.IsNullOrEmpty(checkpointId))
            {
                var checkpoint = _checkpoints.Find(c => c.id == checkpointId);
                if (checkpoint == null)
                {
                    return UnityCommands.JsonError($"Checkpoint not found: {checkpointId}");
                }

                var backup = checkpoint.modifiedFiles.Find(f => f.originalPath == filePath);
                if (backup != null && File.Exists(backup.backupPath))
                {
                    previousContent = File.ReadAllText(backup.backupPath);
                    compareSource = checkpoint.name;
                }
            }
            else
            {
                // Use most recent checkpoint that has this file
                var recentCheckpoint = _checkpoints
                    .OrderByDescending(c => c.timestamp)
                    .FirstOrDefault(c => c.modifiedFiles.Any(f => f.originalPath == filePath));

                if (recentCheckpoint != null)
                {
                    var backup = recentCheckpoint.modifiedFiles.Find(f => f.originalPath == filePath);
                    if (backup != null && File.Exists(backup.backupPath))
                    {
                        previousContent = File.ReadAllText(backup.backupPath);
                        compareSource = recentCheckpoint.name;
                    }
                }
            }

            var diff = ComputeUnifiedDiff(previousContent, currentContent, filePath, compareSource);

            const int MaxDiffChars = 8000;
            int totalLength = diff.Length;
            bool truncated = totalLength > MaxDiffChars;
            if (truncated)
            {
                diff = diff.Substring(0, MaxDiffChars) + "\n... [truncated]";
            }

            return UnityCommands.JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "filePath", filePath },
                { "compareSource", compareSource },
                { "hasChanges", previousContent != currentContent },
                { "diff", diff },
                { "truncated", truncated },
                { "totalDiffLength", totalLength }
            });
        }

        /// <summary>
        /// Track a script file for checkpointing.
        /// </summary>
        public static void TrackScript(string path)
        {
            _trackedScripts.TryAdd(path, 0);
        }

        /// <summary>
        /// Clear all checkpoints.
        /// </summary>
        public static string ClearAllCheckpoints()
        {
            try
            {
                foreach (var checkpoint in _checkpoints.ToList())
                {
                    var checkpointPath = Path.Combine(_checkpointDir, checkpoint.id);
                    if (Directory.Exists(checkpointPath))
                    {
                        Directory.Delete(checkpointPath, true);
                    }
                }

                _checkpoints.Clear();
                SaveCheckpointIndex();

                return UnityCommands.JsonResult(new Dictionary<string, object> { { "success", true }, { "message", "All checkpoints cleared" } });
            }
            catch (Exception ex)
            {
                return UnityCommands.JsonError(ex.Message);
            }
        }

        private static List<string> GetRecentlyModifiedScripts(TimeSpan timeSpan, int maxScripts)
        {
            var scripts = new List<string>();
            var cutoff = DateTime.Now - timeSpan;

            var guids = AssetDatabase.FindAssets("t:Script");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".cs") && File.Exists(path))
                {
                    var lastWrite = File.GetLastWriteTime(path);
                    if (lastWrite > cutoff)
                    {
                        scripts.Add(path);
                        if (scripts.Count >= maxScripts)
                        {
                            break;
                        }
                    }
                }
            }

            return scripts;
        }

        private static string ComputeUnifiedDiff(string oldContent, string newContent, string fileName, string oldLabel)
        {
            var oldLines = oldContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var newLines = newContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            var sb = new StringBuilder();
            sb.AppendLine($"--- {fileName} ({oldLabel})");
            sb.AppendLine($"+++ {fileName} (current)");

            // Simple line-by-line diff
            int oldIndex = 0;
            int newIndex = 0;
            int contextLines = 3;
            var hunks = new List<DiffHunk>();
            DiffHunk currentHunk = null;

            while (oldIndex < oldLines.Length || newIndex < newLines.Length)
            {
                if (oldIndex < oldLines.Length && newIndex < newLines.Length && oldLines[oldIndex] == newLines[newIndex])
                {
                    // Lines match
                    if (currentHunk != null)
                    {
                        currentHunk.lines.Add(new DiffLine { type = ' ', content = oldLines[oldIndex] });
                        currentHunk.contextAfter++;
                        if (currentHunk.contextAfter >= contextLines)
                        {
                            hunks.Add(currentHunk);
                            currentHunk = null;
                        }
                    }
                    oldIndex++;
                    newIndex++;
                }
                else
                {
                    // Lines differ
                    if (currentHunk == null)
                    {
                        currentHunk = new DiffHunk
                        {
                            oldStart = Math.Max(1, oldIndex - contextLines + 1),
                            newStart = Math.Max(1, newIndex - contextLines + 1),
                            lines = new List<DiffLine>()
                        };

                        // Add context before
                        for (int i = Math.Max(0, oldIndex - contextLines); i < oldIndex; i++)
                        {
                            currentHunk.lines.Add(new DiffLine { type = ' ', content = oldLines[i] });
                        }
                    }

                    currentHunk.contextAfter = 0;

                    // Find matching line ahead
                    int oldMatch = -1;
                    int newMatch = -1;

                    for (int i = 1; i <= 5 && (oldIndex + i < oldLines.Length || newIndex + i < newLines.Length); i++)
                    {
                        if (oldIndex + i < oldLines.Length && newIndex < newLines.Length && oldLines[oldIndex + i] == newLines[newIndex])
                        {
                            oldMatch = i;
                            break;
                        }
                        if (newIndex + i < newLines.Length && oldIndex < oldLines.Length && newLines[newIndex + i] == oldLines[oldIndex])
                        {
                            newMatch = i;
                            break;
                        }
                    }

                    if (oldMatch >= 0)
                    {
                        // Lines were removed
                        for (int i = 0; i < oldMatch; i++)
                        {
                            currentHunk.lines.Add(new DiffLine { type = '-', content = oldLines[oldIndex++] });
                        }
                    }
                    else if (newMatch >= 0)
                    {
                        // Lines were added
                        for (int i = 0; i < newMatch; i++)
                        {
                            currentHunk.lines.Add(new DiffLine { type = '+', content = newLines[newIndex++] });
                        }
                    }
                    else
                    {
                        // Line was changed
                        if (oldIndex < oldLines.Length)
                        {
                            currentHunk.lines.Add(new DiffLine { type = '-', content = oldLines[oldIndex++] });
                        }
                        if (newIndex < newLines.Length)
                        {
                            currentHunk.lines.Add(new DiffLine { type = '+', content = newLines[newIndex++] });
                        }
                    }
                }
            }

            if (currentHunk != null)
            {
                hunks.Add(currentHunk);
            }

            // Output hunks
            foreach (var hunk in hunks)
            {
                int oldCount = hunk.lines.Count(l => l.type == ' ' || l.type == '-');
                int newCount = hunk.lines.Count(l => l.type == ' ' || l.type == '+');
                sb.AppendLine($"@@ -{hunk.oldStart},{oldCount} +{hunk.newStart},{newCount} @@");

                foreach (var line in hunk.lines)
                {
                    sb.AppendLine($"{line.type}{line.content}");
                }
            }

            return sb.ToString();
        }

        private static void SaveCheckpointIndex()
        {
            var indexPath = Path.Combine(_checkpointDir, "index.json");
            var index = new CheckpointIndex { checkpoints = _checkpoints };
            File.WriteAllText(indexPath, JsonUtility.ToJson(index, true));
        }

        private static void LoadCheckpointIndex()
        {
            var indexPath = Path.Combine(_checkpointDir, "index.json");
            if (File.Exists(indexPath))
            {
                try
                {
                    var json = File.ReadAllText(indexPath);
                    var index = JsonUtility.FromJson<CheckpointIndex>(json);
                    _checkpoints = index.checkpoints ?? new List<Checkpoint>();

                    // Migrate legacy index entries that still hold serialized backup contents.
                    bool shouldRewriteIndex = false;
                    foreach (var checkpoint in _checkpoints)
                    {
                        if (checkpoint.modifiedFiles == null)
                        {
                            checkpoint.modifiedFiles = new List<FileBackup>();
                            shouldRewriteIndex = true;
                            continue;
                        }

                        foreach (var backup in checkpoint.modifiedFiles)
                        {
                            if (!string.IsNullOrEmpty(backup.content))
                            {
                                backup.content = null;
                                shouldRewriteIndex = true;
                            }
                        }
                    }

                    // Validate checkpoints still exist
                    _checkpoints = _checkpoints.Where(c =>
                        Directory.Exists(Path.Combine(_checkpointDir, c.id))).ToList();

                    if (shouldRewriteIndex)
                    {
                        SaveCheckpointIndex();
                    }
                }
                catch
                {
                    _checkpoints = new List<Checkpoint>();
                }
            }
        }

        #region Data Classes

        [Serializable]
        public class Checkpoint
        {
            public string id;
            public string name;
            public string timestampStr;
            public string scenePath;
            public string sceneName;
            public string sceneBackupPath;
            public List<FileBackup> modifiedFiles;

            [NonSerialized]
            private DateTime _timestamp;
            
            public DateTime timestamp
            {
                get
                {
                    if (_timestamp == DateTime.MinValue && !string.IsNullOrEmpty(timestampStr))
                    {
                        DateTime.TryParse(timestampStr, out _timestamp);
                    }
                    return _timestamp;
                }
                set
                {
                    _timestamp = value;
                    timestampStr = value.ToString("o");
                }
            }

            public Checkpoint()
            {
                modifiedFiles = new List<FileBackup>();
            }
        }

        [Serializable]
        public class FileBackup
        {
            public string originalPath;
            public string backupPath;
            // Legacy field kept for backward compatibility. Not persisted in new indices/responses.
            [NonSerialized]
            public string content;
        }

        [Serializable]
        public class CheckpointResponse
        {
            public bool success;
            public Checkpoint checkpoint;
        }

        [Serializable]
        public class CheckpointListResponse
        {
            public List<Checkpoint> checkpoints;
        }

        [Serializable]
        private class CheckpointIndex
        {
            public List<Checkpoint> checkpoints;
        }

        private class DiffHunk
        {
            public int oldStart;
            public int newStart;
            public int contextAfter;
            public List<DiffLine> lines;
        }

        private class DiffLine
        {
            public char type;
            public string content;
        }

        private static Dictionary<string, object> BuildCheckpointSummary(Checkpoint checkpoint, bool includeSampleFiles, int sampleFileLimit = 0, bool includeBackupPath = false)
        {
            var summary = new Dictionary<string, object>
            {
                { "id", checkpoint.id },
                { "name", checkpoint.name },
                { "timestampStr", checkpoint.timestampStr },
                { "scenePath", checkpoint.scenePath },
                { "sceneName", checkpoint.sceneName },
                { "modifiedFileCount", checkpoint.modifiedFiles?.Count ?? 0 }
            };

            if (includeBackupPath)
            {
                summary["sceneBackupPath"] = checkpoint.sceneBackupPath;
            }

            if (includeSampleFiles && checkpoint.modifiedFiles != null && checkpoint.modifiedFiles.Count > 0)
            {
                var limit = sampleFileLimit > 0 ? sampleFileLimit : CheckpointSampleFileLimit;
                var sampleFiles = checkpoint.modifiedFiles
                    .Take(limit)
                    .Select(f => f.originalPath)
                    .ToList();
                summary["modifiedFileSample"] = sampleFiles;
                summary["modifiedFileSampleTruncated"] = checkpoint.modifiedFiles.Count > sampleFiles.Count;
            }

            return summary;
        }

        #endregion
    }
}
