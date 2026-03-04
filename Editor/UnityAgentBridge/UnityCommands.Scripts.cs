using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Profiling;
using Unity.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Audio;
using TMPro;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        // Persistent helper registry for /execute: named code snippets that get
        // prepended to every execute_csharp call, eliminating boilerplate.
        private static readonly Dictionary<string, string> _executeHelpers = new Dictionary<string, string>();

        [BridgeRoute("POST", "/execute/register-helpers", Category = "scripts", Description = "Register reusable C# helper functions for execute_csharp. Registered helpers are auto-prepended to subsequent /execute calls.")]
        public static string RegisterExecuteHelpers(string jsonData)
        {
            try
            {
                var request = JsonUtility.FromJson<RegisterHelpersRequest>(jsonData);
                if (request == null || string.IsNullOrEmpty(request.name) || string.IsNullOrEmpty(request.code))
                {
                    return JsonError("Both 'name' and 'code' are required");
                }

                // Sandbox check the helper code too
                var violation = SandboxCheck(request.code);
                if (violation != null)
                {
                    return JsonResult(new Dictionary<string, object> { { "success", false }, { "error", violation }, { "code", "SANDBOX_BLOCKED" } });
                }

                _executeHelpers[request.name] = request.code;
                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "name", request.name },
                    { "registeredCount", _executeHelpers.Count },
                    { "message", $"Helper '{request.name}' registered. It will be available in all subsequent execute_csharp calls." }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("GET", "/execute/helpers", Category = "scripts", Description = "List registered execute_csharp helpers")]
        public static string ListExecuteHelpers(string jsonData)
        {
            var helpers = new List<Dictionary<string, object>>();
            foreach (var kvp in _executeHelpers)
            {
                helpers.Add(new Dictionary<string, object>
                {
                    { "name", kvp.Key },
                    { "codeLength", kvp.Value.Length },
                    { "preview", kvp.Value.Length > 100 ? kvp.Value.Substring(0, 100) + "..." : kvp.Value }
                });
            }
            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "count", _executeHelpers.Count },
                { "helpers", helpers }
            });
        }

        [BridgeRoute("DELETE", "/execute/helpers", Category = "scripts", Description = "Clear all or a specific registered execute_csharp helper")]
        public static string ClearExecuteHelpers(string jsonData)
        {
            try
            {
                if (!string.IsNullOrEmpty(jsonData))
                {
                    var request = JsonUtility.FromJson<ClearHelpersRequest>(jsonData);
                    if (request != null && !string.IsNullOrEmpty(request.name))
                    {
                        if (_executeHelpers.Remove(request.name))
                        {
                            return JsonResult(new Dictionary<string, object> { { "success", true }, { "removed", request.name }, { "remainingCount", _executeHelpers.Count } });
                        }
                        return JsonError($"Helper '{request.name}' not found");
                    }
                }

                int count = _executeHelpers.Count;
                _executeHelpers.Clear();
                return JsonResult(new Dictionary<string, object> { { "success", true }, { "clearedCount", count } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        // Sandbox: blocked patterns for /execute code submissions.
        // Prevents file I/O, process spawning, network access, and env var reading.
        // These protect Mac secrets when the bridge is exposed via tunnel/proxy.
        private static readonly string[] _sandboxBlockedPatterns = new[]
        {
            // Process execution — blocks shell commands
            "System.Diagnostics.Process",
            "ProcessStartInfo",
            "Process.Start",
            // File I/O — blocks reading files outside Unity APIs
            "System.IO.File",
            "System.IO.Directory",
            "System.IO.StreamReader",
            "System.IO.StreamWriter",
            "System.IO.FileStream",
            "System.IO.FileInfo",
            "System.IO.DirectoryInfo",
            // Network — blocks data exfiltration
            "System.Net.",
            "new HttpClient",
            "new WebClient",
            "HttpWebRequest",
            "new TcpClient",
            "new UdpClient",
            "new Socket(",
            // Environment secrets
            "Environment.GetEnvironmentVariable",
            "Environment.GetEnvironmentVariables",
            "Environment.SetEnvironmentVariable",
            "Environment.GetFolderPath",
        };

        // Block "using System.IO;" to prevent unqualified File/Directory access
        private static readonly string[] _sandboxBlockedUsings = new[]
        {
            "using System.IO;",
            "using System.Net;",
            "using System.Net.",
            "using System.Diagnostics;",
        };

        private static string SandboxCheck(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;

            foreach (var blocked in _sandboxBlockedUsings)
            {
                if (code.IndexOf(blocked, StringComparison.OrdinalIgnoreCase) >= 0)
                    return $"Sandbox: blocked using directive '{blocked}' — filesystem/network/process namespaces are not allowed in /execute";
            }

            foreach (var blocked in _sandboxBlockedPatterns)
            {
                if (code.IndexOf(blocked, StringComparison.OrdinalIgnoreCase) >= 0)
                    return $"Sandbox: blocked API '{blocked}' — use dedicated bridge routes instead of raw {(blocked.Contains("Process") ? "process" : blocked.Contains("Net") ? "network" : blocked.Contains("Environment") ? "environment" : "file I/O")} access";
            }

            return null; // all clear
        }

        private static bool IsExecuteEnabled()
        {
            // Enabled by default (localhost-only, sandboxed).
            // Set BRIDGE_DISABLE_EXECUTE=1 to explicitly disable.
            var raw = System.Environment.GetEnvironmentVariable("BRIDGE_DISABLE_EXECUTE");
            if (string.IsNullOrWhiteSpace(raw)) return true;
            raw = raw.Trim();
            return !(raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase));
        }

        [BridgeRoute("POST", "/execute", Category = "scripts", Description = "Execute C# code snippet in editor", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 300000)]
        public static string ExecuteCSharp(string jsonData)
        {
            if (!IsExecuteEnabled())
            {
                return JsonResult(new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", "Route /execute has been disabled. Remove BRIDGE_DISABLE_EXECUTE to re-enable." },
                    { "code", "EXECUTE_DISABLED" }
                });
            }

            var request = JsonUtility.FromJson<ExecuteRequest>(jsonData);

            if (request == null || string.IsNullOrEmpty(request.code))
            {
                return JsonError("No code provided");
            }

            // --- Sandbox gate ---
            var sandboxViolation = SandboxCheck(request.code);
            if (sandboxViolation != null)
            {
                return JsonResult(new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", sandboxViolation },
                    { "code", "SANDBOX_BLOCKED" }
                });
            }

            try
            {
                // Prepend registered helpers to user code
                var finalCode = request.code;
                if (_executeHelpers.Count > 0)
                {
                    var helperBlock = new StringBuilder();
                    helperBlock.AppendLine("// === Registered Helpers ===");
                    foreach (var kvp in _executeHelpers)
                    {
                        helperBlock.AppendLine($"// [{kvp.Key}]");
                        helperBlock.AppendLine(kvp.Value);
                        helperBlock.AppendLine();
                    }
                    helperBlock.AppendLine("// === User Code ===");
                    finalCode = helperBlock.ToString() + finalCode;
                }

                // Audit log: record code execution events for diagnostics
                UnityAgentBridgeServer.PushEvent("code_execution", finalCode.Length > 500 ? finalCode.Substring(0, 500) + "..." : finalCode);

                // Use reflection to compile and execute code
                // This is a simplified version - for production, consider Roslyn scripting
                var result = ExecuteCodeViaReflection(finalCode);
                return JsonResult(new Dictionary<string, object> { { "success", true }, { "result", result } });
            }
            catch (Exception ex)
            {
                return JsonResult(new Dictionary<string, object> { { "success", false }, { "error", ex.Message }, { "stackTrace", ex.StackTrace } });
            }
        }

        private static string _cscPath;
        private static string _dotnetPath;

        private static string FindCompiler()
        {
            if (_cscPath != null) return _cscPath;

            var editorPath = EditorApplication.applicationPath;
            string cscDll = null;
            string contentsDir = null;

            // Try multiple paths to find csc.dll
            var candidates = new[]
            {
                // macOS bundle: EditorApplication.applicationPath = ".../Unity.app"
                System.IO.Path.Combine(editorPath, "Contents", "Resources", "Scripting", "DotNetSdkRoslyn", "csc.dll"),
                // macOS executable: EditorApplication.applicationPath = ".../Contents/MacOS/Unity"
                System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(editorPath), "..", "Resources", "Scripting", "DotNetSdkRoslyn", "csc.dll")),
                // Windows: EditorApplication.applicationPath = ".../Editor/Unity.exe"
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(editorPath), "Data", "Resources", "Scripting", "DotNetSdkRoslyn", "csc.dll"),
            };

            foreach (var candidate in candidates)
            {
                if (System.IO.File.Exists(candidate))
                {
                    cscDll = candidate;
                    contentsDir = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(candidate), "..", "..", ".."));
                    break;
                }
            }

            if (cscDll == null)
            {
                Debug.LogWarning($"[AgentBridge] csc.dll not found. Editor path: {editorPath}");
                return null;
            }

            // Find dotnet executable (needed to invoke csc.dll)
            var dotnetCandidates = new[]
            {
                System.IO.Path.Combine(contentsDir, "Resources", "Scripting", "NetCoreRuntime", "dotnet"),
                System.IO.Path.Combine(contentsDir, "Resources", "Scripting", "DotNetSdk", "dotnet"),
            };
            foreach (var dc in dotnetCandidates)
            {
                if (System.IO.File.Exists(dc)) { _dotnetPath = dc; break; }
            }
            if (_dotnetPath == null)
            {
                // Fallback to system dotnet
                _dotnetPath = "dotnet";
            }

            _cscPath = cscDll;
            return _cscPath;
        }

        private static string ExecuteCodeViaReflection(string code)
        {
            var csc = FindCompiler();
            if (csc == null)
            {
                return "Error: C# compiler (csc.dll) not found in Unity installation.";
            }

            return CompileAndExecute(code, csc);
        }

        private static string CompileAndExecute(string code, string cscPath)
        {
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AgentBridge_Execute");
            System.IO.Directory.CreateDirectory(tempDir);

            var sourceFile = System.IO.Path.Combine(tempDir, "Executor.cs");
            var outputDll = System.IO.Path.Combine(tempDir, $"Executor_{Guid.NewGuid():N}.dll");

            // Extract using directives from user code and merge into template
            var extraUsings = new StringBuilder();
            var codeLines = code.Split('\n');
            var codeStart = 0;
            for (int i = 0; i < codeLines.Length; i++)
            {
                var trimmed = codeLines[i].Trim();
                if (trimmed.StartsWith("using ") && trimmed.EndsWith(";") && !trimmed.Contains("("))
                {
                    extraUsings.AppendLine(trimmed);
                    codeStart = i + 1;
                }
                else if (string.IsNullOrWhiteSpace(trimmed))
                {
                    codeStart = i + 1; // skip blank lines between usings
                }
                else
                {
                    break; // first non-using, non-blank line
                }
            }
            var processedCode = string.Join("\n", codeLines, codeStart, codeLines.Length - codeStart);

            // Replace bare 'return;' with goto — the Execute() wrapper returns string,
            // so void returns cause CS0126. The __earlyExit label is in the wrapper.
            processedCode = System.Text.RegularExpressions.Regex.Replace(
                processedCode, @"(?<=^|\n)\s*return\s*;\s*(?=\r?\n|$)", "goto __earlyExit;");

            // Wrap user code in an executable class
            var wrappedCode = @"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using Random = UnityEngine.Random;
using Debug = UnityEngine.Debug;
" + extraUsings.ToString() + @"
public static class __AgentBridgeExecutor
{
    private static StringBuilder __output = new StringBuilder();

    public static void Print(object value)
    {
        __output.AppendLine(value?.ToString() ?? ""null"");
    }

    public static string Execute()
    {
        __output.Clear();
        try
        {
            " + processedCode + @"
        }
        catch (Exception ex)
        {
            __output.AppendLine(""Exception: "" + ex.Message);
            __output.AppendLine(ex.StackTrace);
        }
        __earlyExit:
        return __output.ToString();
    }
}
";
            System.IO.File.WriteAllText(sourceFile, wrappedCode);

            try
            {
                // Build reference args from loaded assemblies
                var refArgs = new StringBuilder();
                var referencedPaths = new HashSet<string>();
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (asm.IsDynamic) continue;
                        var loc = asm.Location;
                        if (string.IsNullOrEmpty(loc) || !System.IO.File.Exists(loc)) continue;
                        if (referencedPaths.Contains(loc)) continue;
                        referencedPaths.Add(loc);
                        refArgs.Append($" -r:\"{loc}\"");
                    }
                    catch { }
                }

                // Compile with csc
                var args = $"exec \"{cscPath}\" -target:library -out:\"{outputDll}\" -nologo -nowarn:CS0105,CS1701,CS1702 -unsafe+{refArgs} \"{sourceFile}\"";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _dotnetPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = tempDir
                };

                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    var stderrTask = proc.StandardError.ReadToEndAsync();
                    var stdout = proc.StandardOutput.ReadToEnd();
                    var stderr = stderrTask.Result;

                    if (!proc.WaitForExit(15000))
                    {
                        try { proc.Kill(); } catch { }
                        return "Compilation timed out after 15 seconds";
                    }

                    if (proc.ExitCode != 0)
                    {
                        var errors = (stdout + "\n" + stderr).Trim();
                        return $"Compilation failed:\n{errors}";
                    }
                }

                // Load and execute the compiled assembly
                var bytes = System.IO.File.ReadAllBytes(outputDll);
                var assembly = Assembly.Load(bytes);
                var executorType = assembly.GetType("__AgentBridgeExecutor");
                if (executorType == null)
                    return "Error: Compiled assembly missing executor type";
                var executeMethod = executorType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                if (executeMethod == null)
                    return "Error: Executor type missing Execute method";
                var result = (string)executeMethod.Invoke(null, null);

                return string.IsNullOrEmpty(result) ? "(no output — use Print() to return values)" : result;
            }
            finally
            {
                // Cleanup temp files
                try { if (System.IO.File.Exists(sourceFile)) System.IO.File.Delete(sourceFile); } catch { }
                try { if (System.IO.File.Exists(outputDll)) System.IO.File.Delete(outputDll); } catch { }
            }
        }


        #region Script Operations

        [BridgeRoute("POST", "/script", Category = "scripts", Description = "Create new script")]
        public static string CreateScript(string jsonData)
        {
            var request = JsonUtility.FromJson<ScriptRequest>(jsonData);

            if (string.IsNullOrEmpty(request.path))
            {
                return JsonError("Script path is required");
            }

            var path = request.path;
            if (!path.StartsWith("Assets/"))
            {
                path = "Assets/" + path;
            }

            // Ensure .cs extension
            if (!path.EndsWith(".cs"))
            {
                path += ".cs";
            }

            if (ValidateAssetPath(path) == null)
            {
                return JsonError("Path is outside the project directory");
            }

            try
            {
                // Create directory if needed
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }

                // Generate content if not provided
                var content = request.content;
                if (string.IsNullOrEmpty(content))
                {
                    var className = request.className ?? System.IO.Path.GetFileNameWithoutExtension(path);
                    var ns = request.namespaceName;

                    var sb = new StringBuilder();
                    sb.AppendLine("using System;");
                    sb.AppendLine("using System.Collections.Generic;");
                    sb.AppendLine("using UnityEngine;");
                    sb.AppendLine();

                    if (!string.IsNullOrEmpty(ns))
                    {
                        sb.AppendLine($"namespace {ns}");
                        sb.AppendLine("{");
                    }

                    var baseClass = request.baseClass ?? "MonoBehaviour";
                    var indent = string.IsNullOrEmpty(ns) ? "" : "    ";

                    sb.AppendLine($"{indent}public class {className} : {baseClass}");
                    sb.AppendLine($"{indent}{{");
                    sb.AppendLine($"{indent}    void Start()");
                    sb.AppendLine($"{indent}    {{");
                    sb.AppendLine($"{indent}    }}");
                    sb.AppendLine();
                    sb.AppendLine($"{indent}    void Update()");
                    sb.AppendLine($"{indent}    {{");
                    sb.AppendLine($"{indent}    }}");
                    sb.AppendLine($"{indent}}}");

                    if (!string.IsNullOrEmpty(ns))
                    {
                        sb.AppendLine("}");
                    }

                    content = sb.ToString();
                }

                System.IO.File.WriteAllText(path, content);

                // Track for checkpointing
                CheckpointManager.TrackScript(path);

                ForceRefreshAndRecompile(path);

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "path", path }, { "message", "Script created successfully" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("PUT", "/script", Category = "scripts", Description = "Modify script source")]
        public static string ModifyScript(string jsonData)
        {
            var request = JsonUtility.FromJson<ScriptRequest>(jsonData);

            if (string.IsNullOrEmpty(request.path))
            {
                return JsonError("Script path is required");
            }

            var path = request.path;
            if (ValidateAssetPath(path) == null)
            {
                return JsonError("Path is outside the project directory");
            }

            if (!System.IO.File.Exists(path))
            {
                return JsonError($"Script not found: {path}");
            }

            try
            {
                // Track for checkpointing before modification
                CheckpointManager.TrackScript(path);

                System.IO.File.WriteAllText(path, request.content);
                ForceRefreshAndRecompile(path);

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "path", path }, { "message", "Script modified successfully" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private static void ForceRefreshAndRecompile(string changedAssetPath = null)
        {
            var refreshFlags = ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport;

            if (!string.IsNullOrWhiteSpace(changedAssetPath)
                && changedAssetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                AssetDatabase.ImportAsset(changedAssetPath, refreshFlags);
            }

            AssetDatabase.Refresh(refreshFlags);
            UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
            EditorApplication.QueuePlayerLoopUpdate();
        }

        public static string GetScript(string path)
        {
            if (ValidateAssetPath(path) == null)
            {
                return JsonError("Path is outside the project directory");
            }

            if (!System.IO.File.Exists(path))
            {
                return JsonError($"Script not found: {path}");
            }

            try
            {
                var content = System.IO.File.ReadAllText(path);
                var monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(path);

                var response = new ScriptResponse
                {
                    success = true,
                    path = path,
                    content = content,
                    lineCount = content.Split('\n').Length
                };

                if (monoScript != null)
                {
                    var scriptClass = monoScript.GetClass();
                    if (scriptClass != null)
                    {
                        response.className = scriptClass.Name;
                        response.namespaceName = scriptClass.Namespace;
                        response.baseClass = scriptClass.BaseType?.Name;
                    }
                }

                return JsonUtility.ToJson(response, false);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        #endregion
    }
}
