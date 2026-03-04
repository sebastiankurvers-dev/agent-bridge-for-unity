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
        #region Audio Methods

        public static string GetAudioSource(string jsonData)
        {
            var dict = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
            if (dict == null) return JsonError("Invalid JSON");

            if (!TryReadInt(dict, "instanceId", out int instanceId))
                return JsonError("Missing or invalid instanceId");

            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null) return JsonError("GameObject not found");

            var source = go.GetComponent<AudioSource>();
            if (source == null) return JsonError("No AudioSource component on this GameObject");

            bool includeClipMeta = ReadBool(dict, "includeClipMeta", true);
            bool includeMixerInfo = ReadBool(dict, "includeMixerInfo", true);

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "instanceId", instanceId },
                { "name", go.name },
                // Playback
                { "clip", source.clip != null ? AssetDatabase.GetAssetPath(source.clip) : null },
                { "playOnAwake", source.playOnAwake },
                { "loop", source.loop },
                { "mute", source.mute },
                { "isPlaying", source.isPlaying },
                // Volume
                { "volume", source.volume },
                { "pitch", source.pitch },
                { "panStereo", source.panStereo },
                // Spatial
                { "spatialBlend", source.spatialBlend },
                { "dopplerLevel", source.dopplerLevel },
                { "spread", source.spread },
                { "minDistance", source.minDistance },
                { "maxDistance", source.maxDistance },
                { "rolloffMode", source.rolloffMode.ToString() },
                // Output
                { "priority", source.priority },
                // Bypass
                { "bypassEffects", source.bypassEffects },
                { "bypassListenerEffects", source.bypassListenerEffects },
                { "bypassReverbZones", source.bypassReverbZones },
                { "reverbZoneMix", source.reverbZoneMix }
            };

            // Optional clip metadata
            if (includeClipMeta && source.clip != null)
            {
                result["clipMeta"] = new Dictionary<string, object>
                {
                    { "length", source.clip.length },
                    { "channels", source.clip.channels },
                    { "frequency", source.clip.frequency },
                    { "samples", source.clip.samples }
                };
            }

            // Optional mixer info
            if (includeMixerInfo && source.outputAudioMixerGroup != null)
            {
                var mixerGroup = source.outputAudioMixerGroup;
                var mixerInfo = new Dictionary<string, object>
                {
                    { "groupName", mixerGroup.name }
                };
                if (mixerGroup.audioMixer != null)
                {
                    mixerInfo["mixerName"] = mixerGroup.audioMixer.name;
                    mixerInfo["mixerPath"] = AssetDatabase.GetAssetPath(mixerGroup.audioMixer);
                }
                result["outputMixerGroup"] = mixerInfo;
            }

            return JsonResult(result);
        }

        [BridgeRoute("PUT", "/audio/source", Category = "audio", Description = "Configure AudioSource")]
        public static string ConfigureAudioSource(string jsonData)
        {
            var dict = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
            if (dict == null) return JsonError("Invalid JSON");

            if (!TryReadInt(dict, "instanceId", out int instanceId))
                return JsonError("Missing or invalid instanceId");

            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null) return JsonError("GameObject not found");

            bool createIfMissing = ReadBool(dict, "createIfMissing", true);
            var source = go.GetComponent<AudioSource>();
            if (source == null)
            {
                if (!createIfMissing)
                    return JsonError("No AudioSource component and createIfMissing is false");
                source = Undo.AddComponent<AudioSource>(go);
            }
            else
            {
                Undo.RecordObject(source, "Configure AudioSource");
            }

            int fieldsApplied = 0;

            // ---- Playback ----
            var clipPath = ReadString(dict, "clip");
            if (clipPath != null)
            {
                if (ValidateAssetPath(clipPath) == null)
                    return JsonError($"Invalid asset path: {clipPath}");
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
                if (clip == null)
                    return JsonError($"AudioClip not found at path: {clipPath}");
                source.clip = clip;
                fieldsApplied++;
            }

            if (TryReadBoolField(dict, "playOnAwake", out bool playOnAwake)) { source.playOnAwake = playOnAwake; fieldsApplied++; }
            if (TryReadBoolField(dict, "loop", out bool loop)) { source.loop = loop; fieldsApplied++; }
            if (TryReadBoolField(dict, "mute", out bool mute)) { source.mute = mute; fieldsApplied++; }

            // ---- Volume ----
            if (TryReadFloatField(dict, "volume", out float volume)) { source.volume = volume; fieldsApplied++; }
            if (TryReadFloatField(dict, "pitch", out float pitch)) { source.pitch = pitch; fieldsApplied++; }
            if (TryReadFloatField(dict, "panStereo", out float panStereo)) { source.panStereo = panStereo; fieldsApplied++; }

            // ---- Spatial ----
            if (TryReadFloatField(dict, "spatialBlend", out float spatialBlend)) { source.spatialBlend = spatialBlend; fieldsApplied++; }
            if (TryReadFloatField(dict, "dopplerLevel", out float dopplerLevel)) { source.dopplerLevel = dopplerLevel; fieldsApplied++; }
            if (TryReadFloatField(dict, "spread", out float spread)) { source.spread = spread; fieldsApplied++; }
            if (TryReadFloatField(dict, "minDistance", out float minDistance)) { source.minDistance = minDistance; fieldsApplied++; }
            if (TryReadFloatField(dict, "maxDistance", out float maxDistance)) { source.maxDistance = maxDistance; fieldsApplied++; }

            // ---- Rolloff Mode ----
            var rolloffStr = ReadString(dict, "rolloffMode");
            if (rolloffStr != null)
            {
                if (Enum.TryParse<AudioRolloffMode>(rolloffStr, true, out var rolloff))
                {
                    source.rolloffMode = rolloff;
                    fieldsApplied++;
                }
                else
                {
                    return JsonError($"Invalid rolloffMode '{rolloffStr}'. Valid: Logarithmic, Linear, Custom");
                }
            }

            // ---- Output ----
            if (TryReadInt(dict, "priority", out int priority)) { source.priority = priority; fieldsApplied++; }

            // ---- Bypass ----
            if (TryReadBoolField(dict, "bypassEffects", out bool bypassEffects)) { source.bypassEffects = bypassEffects; fieldsApplied++; }
            if (TryReadBoolField(dict, "bypassListenerEffects", out bool bypassListenerEffects)) { source.bypassListenerEffects = bypassListenerEffects; fieldsApplied++; }
            if (TryReadBoolField(dict, "bypassReverbZones", out bool bypassReverbZones)) { source.bypassReverbZones = bypassReverbZones; fieldsApplied++; }
            if (TryReadFloatField(dict, "reverbZoneMix", out float reverbZoneMix)) { source.reverbZoneMix = reverbZoneMix; fieldsApplied++; }

            // ---- Mixer Group Routing ----
            var mixerPath = ReadString(dict, "outputMixerGroupMixerPath");
            var groupPath = ReadString(dict, "outputMixerGroupPath");
            var groupName = ReadString(dict, "outputMixerGroupName");
            if (mixerPath != null && (groupPath != null || groupName != null))
            {
                var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(mixerPath);
                if (mixer == null)
                    return JsonError($"AudioMixer not found at path: {mixerPath}");

                AudioMixerGroup matchedGroup = null;

                if (groupPath != null)
                {
                    // Preferred: exact path match
                    var groups = mixer.FindMatchingGroups(groupPath);
                    if (groups == null || groups.Length == 0)
                        return JsonError($"No mixer group found matching path '{groupPath}' in {mixerPath}");
                    matchedGroup = groups[0];
                }
                else if (groupName != null)
                {
                    // Fallback: match by leaf name
                    var allGroups = mixer.FindMatchingGroups("");
                    foreach (var g in allGroups)
                    {
                        if (string.Equals(g.name, groupName, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedGroup = g;
                            break;
                        }
                    }
                    if (matchedGroup == null)
                        return JsonError($"No mixer group named '{groupName}' found in {mixerPath}");
                }

                source.outputAudioMixerGroup = matchedGroup;
                fieldsApplied++;
            }

            EditorUtility.SetDirty(source);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "instanceId", instanceId },
                { "name", go.name },
                { "fieldsApplied", fieldsApplied }
            });
        }

        public static string GetAudioMixer(string jsonData)
        {
            var dict = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
            if (dict == null) return JsonError("Invalid JSON");

            var mixerPath = ReadString(dict, "mixerPath");
            if (string.IsNullOrEmpty(mixerPath))
                return JsonError("Missing mixerPath");

            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(mixerPath);
            if (mixer == null) return JsonError($"AudioMixer not found at path: {mixerPath}");

            bool brief = ReadBool(dict, "brief", true);
            TryReadInt(dict, "maxGroups", out int maxGroups); if (maxGroups <= 0) maxGroups = 50;
            TryReadInt(dict, "maxParameters", out int maxParameters); if (maxParameters <= 0) maxParameters = 50;
            TryReadInt(dict, "maxSnapshots", out int maxSnapshots); if (maxSnapshots <= 0) maxSnapshots = 20;

            // ---- Groups ----
            var allGroups = mixer.FindMatchingGroups("");
            int totalGroups = allGroups != null ? allGroups.Length : 0;
            var groupsList = new List<object>();
            if (allGroups != null)
            {
                int limit = Math.Min(totalGroups, maxGroups);
                for (int i = 0; i < limit; i++)
                {
                    groupsList.Add(allGroups[i].name);
                }
            }

            // ---- Exposed Parameters ----
            var paramsList = new List<object>();
            int totalParameters = 0;
            var serializedMixer = new SerializedObject(mixer);
            var exposedParams = serializedMixer.FindProperty("m_ExposedParameters");
            if (exposedParams != null && exposedParams.isArray)
            {
                totalParameters = exposedParams.arraySize;
                int limit = Math.Min(totalParameters, maxParameters);
                for (int i = 0; i < limit; i++)
                {
                    var element = exposedParams.GetArrayElementAtIndex(i);
                    var nameProperty = element.FindPropertyRelative("name");
                    if (nameProperty != null)
                    {
                        string paramName = nameProperty.stringValue;
                        var paramEntry = new Dictionary<string, object> { { "name", paramName } };
                        if (mixer.GetFloat(paramName, out float currentValue))
                        {
                            paramEntry["value"] = currentValue;
                        }
                        if (!brief)
                        {
                            var guidProperty = element.FindPropertyRelative("guid");
                            if (guidProperty != null)
                                paramEntry["guid"] = guidProperty.stringValue;
                        }
                        paramsList.Add(paramEntry);
                    }
                }
            }

            // ---- Snapshots ----
            var snapshotsList = new List<object>();
            int totalSnapshots = 0;
            var allAssets = AssetDatabase.LoadAllAssetsAtPath(mixerPath);
            if (allAssets != null)
            {
                var snapshots = allAssets.OfType<AudioMixerSnapshot>().ToArray();
                totalSnapshots = snapshots.Length;
                int limit = Math.Min(totalSnapshots, maxSnapshots);
                for (int i = 0; i < limit; i++)
                {
                    snapshotsList.Add(snapshots[i].name);
                }
            }

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "mixerName", mixer.name },
                { "mixerPath", mixerPath },
                { "groups", groupsList },
                { "totalGroups", totalGroups },
                { "exposedParameters", paramsList },
                { "totalParameters", totalParameters },
                { "snapshots", snapshotsList },
                { "totalSnapshots", totalSnapshots }
            });
        }

        [BridgeRoute("PUT", "/audio/mixer", Category = "audio", Description = "Configure AudioMixer")]
        public static string ConfigureAudioMixer(string jsonData)
        {
            var dict = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
            if (dict == null) return JsonError("Invalid JSON");

            var mixerPath = ReadString(dict, "mixerPath");
            if (string.IsNullOrEmpty(mixerPath))
                return JsonError("Missing mixerPath");

            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(mixerPath);
            if (mixer == null) return JsonError($"AudioMixer not found at path: {mixerPath}");

            int parametersSet = 0;
            int parametersCleared = 0;
            string snapshotTransitioned = null;

            // ---- Set Parameters ----
            if (dict.TryGetValue("parameters", out var paramsObj) && paramsObj is Dictionary<string, object> paramsDict)
            {
                foreach (var kvp in paramsDict)
                {
                    float paramValue;
                    try { paramValue = Convert.ToSingle(kvp.Value); }
                    catch { return JsonError($"Invalid value for parameter '{kvp.Key}': expected a number"); }

                    if (!mixer.SetFloat(kvp.Key, paramValue))
                        return JsonError($"Failed to set parameter '{kvp.Key}'. Is it exposed in the AudioMixer?");
                    parametersSet++;
                }
            }

            // ---- Clear Parameters ----
            var clearStr = ReadString(dict, "clearParameters");
            if (clearStr != null)
            {
                var names = clearStr.Split(',');
                foreach (var rawName in names)
                {
                    var paramName = rawName.Trim();
                    if (!string.IsNullOrEmpty(paramName))
                    {
                        mixer.ClearFloat(paramName);
                        parametersCleared++;
                    }
                }
            }

            // ---- Snapshot Transition ----
            var snapshotName = ReadString(dict, "transitionToSnapshot");
            if (snapshotName != null)
            {
                var snapshot = mixer.FindSnapshot(snapshotName);
                if (snapshot == null)
                    return JsonError($"Snapshot '{snapshotName}' not found in mixer");

                TryReadFloatField(dict, "transitionTime", out float transitionTime);
                snapshot.TransitionTo(transitionTime);
                snapshotTransitioned = snapshotName;
            }

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "parametersSet", parametersSet },
                { "parametersCleared", parametersCleared }
            };
            if (snapshotTransitioned != null)
                result["snapshotTransition"] = snapshotTransitioned;

            return JsonResult(result);
        }

        #endregion
    }
}
