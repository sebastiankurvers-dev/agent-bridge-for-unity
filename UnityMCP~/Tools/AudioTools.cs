using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class AudioTools
{
    [McpServerTool(Name = "unity_get_audio_source")]
    [Description(@"Read AudioSource component configuration from a GameObject.

Returns structured JSON with playback, volume, spatial, output routing, and bypass settings.
Use includeClipMeta/includeMixerInfo to control response size.

Example:
  instanceId: 12345
  instanceId: 12345, includeClipMeta: false, includeMixerInfo: false")]
    public static async Task<string> GetAudioSource(
        UnityClient client,
        [Description("Instance ID of the GameObject with AudioSource.")] int instanceId,
        [Description("Include clip metadata (length, channels, frequency). Default false for token efficiency.")] bool includeClipMeta = false,
        [Description("Include mixer output routing info. Default false for token efficiency.")] bool includeMixerInfo = false)
    {
        return await client.GetAudioSourceAsync(instanceId, includeClipMeta, includeMixerInfo);
    }

    [McpServerTool(Name = "unity_configure_audio_source")]
    [Description(@"Configure AudioSource component on a GameObject. Adds AudioSource if createIfMissing is true (default).
Only specified properties are changed.

Mixer group routing requires outputMixerGroupMixerPath plus either outputMixerGroupPath (preferred, exact path like ""Master/SFX"")
or outputMixerGroupName (fallback, matches first group with that leaf name).

rolloffMode: Logarithmic, Linear, Custom (Custom requires AnimationCurve via execute_csharp).

Example - 3D ambient:
  instanceId: 12345, volume: 0.5, spatialBlend: 1, loop: true, minDistance: 5, maxDistance: 50

Example - UI sound:
  instanceId: 12345, clip: ""Assets/Audio/click.wav"", spatialBlend: 0, playOnAwake: false")]
    public static async Task<string> ConfigureAudioSource(
        UnityClient client,
        [Description("Instance ID of the GameObject.")] int instanceId,
        [Description("Add AudioSource if missing (default true).")] bool? createIfMissing = null,
        // ---- Playback ----
        [Description("Audio clip asset path (e.g. 'Assets/Audio/clip.wav').")] string? clip = null,
        [Description("Play automatically on awake.")] bool? playOnAwake = null,
        [Description("Loop audio clip.")] bool? loop = null,
        [Description("Mute audio.")] bool? mute = null,
        // ---- Volume ----
        [Description("Volume (0-1).")] float? volume = null,
        [Description("Pitch (-3 to 3, 1 = normal).")] float? pitch = null,
        [Description("Stereo pan (-1 = left, 0 = center, 1 = right).")] float? panStereo = null,
        // ---- Spatial ----
        [Description("Spatial blend (0 = 2D, 1 = 3D).")] float? spatialBlend = null,
        [Description("Doppler level (0-5).")] float? dopplerLevel = null,
        [Description("Spread angle (0-360 degrees).")] float? spread = null,
        [Description("Min distance for 3D attenuation.")] float? minDistance = null,
        [Description("Max distance for 3D attenuation.")] float? maxDistance = null,
        [Description("Rolloff mode: Logarithmic, Linear, Custom.")] string? rolloffMode = null,
        // ---- Output ----
        [Description("Priority (0 = highest, 256 = lowest).")] int? priority = null,
        [Description("Mixer asset path for output routing (e.g. 'Assets/Audio/Main.mixer').")] string? outputMixerGroupMixerPath = null,
        [Description("Exact mixer group path (preferred, e.g. 'Master/SFX/Ambience').")] string? outputMixerGroupPath = null,
        [Description("Mixer group name fallback (matches first group with this leaf name).")] string? outputMixerGroupName = null,
        // ---- Bypass ----
        [Description("Bypass effects on AudioSource.")] bool? bypassEffects = null,
        [Description("Bypass listener effects.")] bool? bypassListenerEffects = null,
        [Description("Bypass reverb zones.")] bool? bypassReverbZones = null,
        [Description("Reverb zone mix (0-1.1).")] float? reverbZoneMix = null)
    {
        var data = new Dictionary<string, object?>();
        data["instanceId"] = instanceId;
        if (createIfMissing.HasValue) data["createIfMissing"] = createIfMissing.Value ? 1 : 0;

        // Playback
        if (clip != null) data["clip"] = clip;
        if (playOnAwake.HasValue) data["playOnAwake"] = playOnAwake.Value ? 1 : 0;
        if (loop.HasValue) data["loop"] = loop.Value ? 1 : 0;
        if (mute.HasValue) data["mute"] = mute.Value ? 1 : 0;

        // Volume
        if (volume.HasValue) data["volume"] = volume.Value;
        if (pitch.HasValue) data["pitch"] = pitch.Value;
        if (panStereo.HasValue) data["panStereo"] = panStereo.Value;

        // Spatial
        if (spatialBlend.HasValue) data["spatialBlend"] = spatialBlend.Value;
        if (dopplerLevel.HasValue) data["dopplerLevel"] = dopplerLevel.Value;
        if (spread.HasValue) data["spread"] = spread.Value;
        if (minDistance.HasValue) data["minDistance"] = minDistance.Value;
        if (maxDistance.HasValue) data["maxDistance"] = maxDistance.Value;
        if (rolloffMode != null) data["rolloffMode"] = rolloffMode;

        // Output
        if (priority.HasValue) data["priority"] = priority.Value;
        if (outputMixerGroupMixerPath != null) data["outputMixerGroupMixerPath"] = outputMixerGroupMixerPath;
        if (outputMixerGroupPath != null) data["outputMixerGroupPath"] = outputMixerGroupPath;
        if (outputMixerGroupName != null) data["outputMixerGroupName"] = outputMixerGroupName;

        // Bypass
        if (bypassEffects.HasValue) data["bypassEffects"] = bypassEffects.Value ? 1 : 0;
        if (bypassListenerEffects.HasValue) data["bypassListenerEffects"] = bypassListenerEffects.Value ? 1 : 0;
        if (bypassReverbZones.HasValue) data["bypassReverbZones"] = bypassReverbZones.Value ? 1 : 0;
        if (reverbZoneMix.HasValue) data["reverbZoneMix"] = reverbZoneMix.Value;

        return await client.ConfigureAudioSourceAsync(data);
    }

    [McpServerTool(Name = "unity_get_audio_mixer")]
    [Description(@"Read AudioMixer structure: groups, exposed parameters, and snapshots.
Unity does not support creating AudioMixers programmatically — use the Unity Editor to create them.

Token-safe defaults: brief=true, maxGroups=50, maxParameters=50, maxSnapshots=20.
Response includes total counts so you know if results were clamped.

Example:
  mixerPath: ""Assets/Audio/MainMixer.mixer""
  mixerPath: ""Assets/Audio/MainMixer.mixer"", brief: false, maxGroups: 100")]
    public static async Task<string> GetAudioMixer(
        UnityClient client,
        [Description("Asset path of the AudioMixer (e.g. 'Assets/Audio/MainMixer.mixer').")] string mixerPath,
        [Description("Compact output (default true). Set false for full details.")] bool brief = true,
        [Description("Max groups to return (default 50).")] int maxGroups = 50,
        [Description("Max exposed parameters to return (default 50).")] int maxParameters = 50,
        [Description("Max snapshots to return (default 20).")] int maxSnapshots = 20)
    {
        return await client.GetAudioMixerAsync(mixerPath, brief, maxGroups, maxParameters, maxSnapshots);
    }

    [McpServerTool(Name = "unity_configure_audio_mixer")]
    [Description(@"Set exposed AudioMixer parameters and transition snapshots.
Only exposed parameters can be set — use the Unity Editor to expose parameters first.

parametersJson is a JSON object of {paramName: floatValue} pairs, e.g. '{""Volume"": -10, ""SFXPitch"": 1.2}'.
clearParameters is a comma-separated list of param names to reset to defaults.
Use transitionToSnapshot to smoothly transition to a named snapshot.

Example - Set params:
  mixerPath: ""Assets/Audio/Main.mixer"", parametersJson: '{""MasterVolume"": -5, ""SFXVolume"": -10}'

Example - Snapshot transition:
  mixerPath: ""Assets/Audio/Main.mixer"", transitionToSnapshot: ""Muted"", transitionTime: 0.5")]
    public static async Task<string> ConfigureAudioMixer(
        UnityClient client,
        [Description("Asset path of the AudioMixer.")] string mixerPath,
        [Description("JSON object of {paramName: floatValue} pairs to set.")] string? parametersJson = null,
        [Description("Comma-separated parameter names to reset to defaults.")] string? clearParameters = null,
        [Description("Snapshot name to transition to.")] string? transitionToSnapshot = null,
        [Description("Transition duration in seconds (default 0 = instant).")] float? transitionTime = null)
    {
        var data = new Dictionary<string, object?>();
        data["mixerPath"] = mixerPath;

        // Parse parametersJson into a native dict for the handler
        if (!string.IsNullOrEmpty(parametersJson))
        {
            try
            {
                var jsonElement = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(parametersJson);
                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    var parameters = new Dictionary<string, object>();
                    foreach (var prop in jsonElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                            parameters[prop.Name] = prop.Value.GetSingle();
                    }
                    if (parameters.Count > 0) data["parameters"] = parameters;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                return "{\"success\":false,\"error\":\"Invalid parametersJson format. Expected JSON object like {\\\"Volume\\\": -10}\"}";
            }
        }

        if (clearParameters != null) data["clearParameters"] = clearParameters;
        if (transitionToSnapshot != null) data["transitionToSnapshot"] = transitionToSnapshot;
        if (transitionTime.HasValue) data["transitionTime"] = transitionTime.Value;

        return await client.ConfigureAudioMixerAsync(data);
    }
}
