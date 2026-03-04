using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class ParameterSweepTools
{
    [McpServerTool(Name = "unity_parameter_sweep")]
    [Description("Sweep a single numeric parameter across N steps and capture a screenshot handle per step. Target format: material:<path>:<property>, volume:<component>:<property>, rendersettings:<scope>:<property>.")]
    public static async Task<string> ParameterSweep(
        UnityClient client,
        [Description("Target spec. Examples: material:Assets/Mats/Road.mat:_Smoothness, volume:Bloom:intensity, rendersettings:global:fogDensity")] string target,
        [Description("Minimum sweep value.")] float min,
        [Description("Maximum sweep value.")] float max,
        [Description("Number of sweep steps (2-24).")] int steps = 6,
        [Description("Screenshot view: game or scene.")] string viewType = "scene",
        [Description("Screenshot width (0=auto).")] int width = 0,
        [Description("Screenshot height (0=auto).")] int height = 0,
        [Description("Screenshot format: jpeg or png.")] string format = "jpeg",
        [Description("Source prefix stored on image handles.")] string source = "parameter-sweep",
        [Description("Optional volume profile path (for volume targets).")] string? profilePath = null,
        [Description("Optional target volume instance ID (for volume targets).")] int? volumeInstanceId = null,
        [Description("Optional timeout override in ms. Defaults to steps*8000+10000.")] int? timeoutMs = null)
    {
        steps = Math.Clamp(steps, 2, 24);
        int computedTimeout = Math.Clamp(steps * 8000 + 10000, 20000, 180000);

        var request = new Dictionary<string, object?>
        {
            ["target"] = target,
            ["min"] = min,
            ["max"] = max,
            ["steps"] = steps,
            ["viewType"] = viewType,
            ["width"] = width,
            ["height"] = height,
            ["format"] = format,
            ["source"] = source
        };

        if (!string.IsNullOrWhiteSpace(profilePath)) request["profilePath"] = profilePath;
        if (volumeInstanceId.HasValue && volumeInstanceId.Value != 0) request["volumeInstanceId"] = volumeInstanceId.Value;

        return await client.ParameterSweepAsync(request, timeoutMs ?? computedTimeout);
    }
}
