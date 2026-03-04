using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        // ==================== Deterministic Replay Gate ====================

        private static ReplaySessionData _activeRecording;
        private static ReplaySessionData _lastCompletedRecording;
        private static bool _replayRecordingActive;
        private static bool _replayPlaybackActive;
        private static float _replayLastCaptureTime;
        private static bool _replayTickRegistered;
        private static readonly Dictionary<string, ReplaySessionData> _storedSessions = new Dictionary<string, ReplaySessionData>();

        [Serializable]
        private class ReplaySessionData
        {
            public string matchId;
            public int roundNumber;
            public int seed;
            public float captureIntervalMs;
            public float compareIntervalMs;
            public int targetInstanceId;
            public string targetComponentType;
            public List<ReplayStateFrameData> stateFrames = new List<ReplayStateFrameData>();
            public List<ReplayInputEventData> inputEvents = new List<ReplayInputEventData>();
            public float startTime;
            public float endTime;
        }

        [Serializable]
        private class ReplayStateFrameData
        {
            public float timestamp;
            public float posX, posY, posZ;

            // Rotation (euler angles)
            public float rotX, rotY, rotZ;

            // Velocity (from Rigidbody, if present)
            public float velX, velY, velZ;
            public bool hasVelocity;

            // Animation state (from Animator, if present)
            public int animStateHash;
            public float animNormalizedTime;
            public bool hasAnimator;

            // Collision/trigger state
            public int contactCount;
            public bool isTriggerActive;
            public bool hasCollisionState;

            public string extraFieldsJson; // JSON dict of additional tracked fields
        }

        [Serializable]
        private class ReplayInputEventData
        {
            public float timestamp;
            public string action;       // e.g. "ChangeLane", "TogglePhase"
            public string[] args;
        }

        [BridgeRoute("POST", "/replay/start", Category = "replay", Description = "Start replay recording")]
        public static string ReplayStartRecording(string jsonData)
        {
            if (!EditorApplication.isPlaying)
                return JsonError("Replay recording is only allowed during play mode.");

            if (_replayRecordingActive)
                return JsonError("Recording already active. Stop it first.");

            Dictionary<string, object> req;
            try
            {
                req = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
                if (req == null) return JsonError("Invalid request JSON.");
            }
            catch (Exception ex)
            {
                return JsonError($"Failed to parse request: {ex.Message}");
            }

            _activeRecording = new ReplaySessionData
            {
                matchId = req.TryGetValue("matchId", out var mid) ? mid?.ToString() : "",
                roundNumber = req.TryGetValue("roundNumber", out var rn) ? Convert.ToInt32(rn) : 0,
                seed = req.TryGetValue("seed", out var s) ? Convert.ToInt32(s) : 0,
                captureIntervalMs = req.TryGetValue("captureIntervalMs", out var ci) ? Convert.ToSingle(ci) : 100f,
                compareIntervalMs = req.TryGetValue("compareIntervalMs", out var coi) ? Convert.ToSingle(coi) : 100f,
                targetInstanceId = req.TryGetValue("targetInstanceId", out var tid) ? Convert.ToInt32(tid) : 0,
                targetComponentType = req.TryGetValue("targetComponentType", out var tct) ? tct?.ToString() : "",
                startTime = Time.realtimeSinceStartup
            };

            _replayRecordingActive = true;
            _replayLastCaptureTime = Time.realtimeSinceStartup;
            EnsureReplayTick();

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "status", "recording" },
                { "matchId", _activeRecording.matchId },
                { "roundNumber", _activeRecording.roundNumber },
                { "captureIntervalMs", _activeRecording.captureIntervalMs }
            });
        }

        [BridgeRoute("POST", "/replay/stop", Category = "replay", Description = "Stop replay recording")]
        public static string ReplayStopRecording()
        {
            if (!_replayRecordingActive || _activeRecording == null)
                return JsonError("No active recording.");

            _replayRecordingActive = false;
            _activeRecording.endTime = Time.realtimeSinceStartup;
            _lastCompletedRecording = _activeRecording;

            // Store session with a unique ID so clients can reference it without
            // round-tripping the full frame data (which can be thousands of tokens).
            var sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            _storedSessions[sessionId] = _activeRecording;

            float duration = _activeRecording.endTime - _activeRecording.startTime;
            int frameCount = _activeRecording.stateFrames.Count;
            int eventCount = _activeRecording.inputEvents.Count;

            // Build a compact summary (first/last frame position for context)
            var summary = new Dictionary<string, object>
            {
                { "success", true },
                { "status", "stopped" },
                { "sessionId", sessionId },
                { "matchId", _activeRecording.matchId },
                { "targetInstanceId", _activeRecording.targetInstanceId },
                { "durationSeconds", Math.Round(duration, 2) },
                { "stateFrameCount", frameCount },
                { "inputEventCount", eventCount },
                { "captureIntervalMs", _activeRecording.captureIntervalMs }
            };

            if (frameCount > 0)
            {
                var first = _activeRecording.stateFrames[0];
                var last = _activeRecording.stateFrames[frameCount - 1];
                summary["firstFramePos"] = new List<object> {
                    Math.Round(first.posX, 3), Math.Round(first.posY, 3), Math.Round(first.posZ, 3)
                };
                summary["lastFramePos"] = new List<object> {
                    Math.Round(last.posX, 3), Math.Round(last.posY, 3), Math.Round(last.posZ, 3)
                };
            }

            summary["hint"] = "Use sessionId with unity_replay_execute. Full frame data is stored server-side.";

            _activeRecording = null;

            return JsonResult(summary);
        }

        [BridgeRoute("POST", "/replay/execute", Category = "replay", Description = "Execute replay session", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string ReplayExecute(string jsonData)
        {
            if (!EditorApplication.isPlaying)
                return JsonError("Replay execution is only allowed during play mode.");

            if (_replayPlaybackActive)
                return JsonError("Playback already in progress.");

            Dictionary<string, object> req;
            try
            {
                req = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
                if (req == null) return JsonError("Invalid request JSON.");
            }
            catch (Exception ex)
            {
                return JsonError($"Failed to parse request: {ex.Message}");
            }

            // Resolve session: prefer sessionId (stored server-side), fall back to inline session JSON
            int targetId;
            string targetComponent;
            float compareInterval;
            List<ReplayStateFrameData> recordedFrames;
            List<ReplayInputEventData> recordedInputs;

            if (req.TryGetValue("sessionId", out var sidObj) && sidObj is string sid && !string.IsNullOrEmpty(sid))
            {
                if (!_storedSessions.TryGetValue(sid, out var storedSession))
                    return JsonError($"Session '{sid}' not found. It may have expired after domain reload.");

                targetId = storedSession.targetInstanceId;
                targetComponent = storedSession.targetComponentType;
                compareInterval = storedSession.compareIntervalMs;
                recordedFrames = storedSession.stateFrames;
                recordedInputs = storedSession.inputEvents;
            }
            else
            {
                // Legacy path: parse inline session JSON
                var sessionObj = req.TryGetValue("session", out var sObj) ? sObj as Dictionary<string, object> : null;
                if (sessionObj == null)
                    return JsonError("Provide 'sessionId' or a 'session' object.");

                // Check if the inline session object contains a sessionId reference
                if (sessionObj.TryGetValue("sessionId", out var nestedSid) && nestedSid is string nestedSidStr
                    && !string.IsNullOrEmpty(nestedSidStr) && _storedSessions.TryGetValue(nestedSidStr, out var nestedSession))
                {
                    targetId = nestedSession.targetInstanceId;
                    targetComponent = nestedSession.targetComponentType;
                    compareInterval = nestedSession.compareIntervalMs;
                    recordedFrames = nestedSession.stateFrames;
                    recordedInputs = nestedSession.inputEvents;
                    goto resolved;
                }

                targetId = sessionObj.TryGetValue("targetInstanceId", out var tidObj) ? Convert.ToInt32(tidObj) : 0;
                targetComponent = sessionObj.TryGetValue("targetComponentType", out var tcObj) ? tcObj?.ToString() : "";
                compareInterval = sessionObj.TryGetValue("compareIntervalMs", out var ciObj) ? Convert.ToSingle(ciObj) : 100f;

                recordedFrames = new List<ReplayStateFrameData>();
                if (sessionObj.TryGetValue("stateFrames", out var framesObj) && framesObj is List<object> framesList)
                {
                    foreach (var fObj in framesList)
                    {
                        var fDict = fObj as Dictionary<string, object>;
                        if (fDict == null) continue;
                        recordedFrames.Add(new ReplayStateFrameData
                        {
                            timestamp = fDict.TryGetValue("timestamp", out var ts) ? Convert.ToSingle(ts) : 0f,
                            posX = fDict.TryGetValue("posX", out var px) ? Convert.ToSingle(px) : 0f,
                            posY = fDict.TryGetValue("posY", out var py) ? Convert.ToSingle(py) : 0f,
                            posZ = fDict.TryGetValue("posZ", out var pz) ? Convert.ToSingle(pz) : 0f,
                            rotX = fDict.TryGetValue("rotX", out var rx) ? Convert.ToSingle(rx) : 0f,
                            rotY = fDict.TryGetValue("rotY", out var ry) ? Convert.ToSingle(ry) : 0f,
                            rotZ = fDict.TryGetValue("rotZ", out var rz) ? Convert.ToSingle(rz) : 0f,
                            hasVelocity = fDict.TryGetValue("hasVelocity", out var hv) && Convert.ToBoolean(hv),
                            velX = fDict.TryGetValue("velX", out var vx) ? Convert.ToSingle(vx) : 0f,
                            velY = fDict.TryGetValue("velY", out var vy) ? Convert.ToSingle(vy) : 0f,
                            velZ = fDict.TryGetValue("velZ", out var vz) ? Convert.ToSingle(vz) : 0f,
                            hasAnimator = fDict.TryGetValue("hasAnimator", out var ha) && Convert.ToBoolean(ha),
                            animStateHash = fDict.TryGetValue("animStateHash", out var ash) ? Convert.ToInt32(ash) : 0,
                            animNormalizedTime = fDict.TryGetValue("animNormalizedTime", out var ant) ? Convert.ToSingle(ant) : 0f,
                            hasCollisionState = fDict.TryGetValue("hasCollisionState", out var hcs) && Convert.ToBoolean(hcs),
                            contactCount = fDict.TryGetValue("contactCount", out var cc) ? Convert.ToInt32(cc) : 0,
                            isTriggerActive = fDict.TryGetValue("isTriggerActive", out var ita) && Convert.ToBoolean(ita),
                            extraFieldsJson = fDict.TryGetValue("extraFieldsJson", out var ef) ? ef?.ToString() : "{}"
                        });
                    }
                }

                recordedInputs = new List<ReplayInputEventData>();
                if (sessionObj.TryGetValue("inputEvents", out var evtsObj) && evtsObj is List<object> evtsList)
                {
                    foreach (var eObj in evtsList)
                    {
                        var eDict = eObj as Dictionary<string, object>;
                        if (eDict == null) continue;
                        var inputEvent = new ReplayInputEventData
                        {
                            timestamp = eDict.TryGetValue("timestamp", out var ts) ? Convert.ToSingle(ts) : 0f,
                            action = eDict.TryGetValue("action", out var act) ? act?.ToString() : ""
                        };
                        if (eDict.TryGetValue("args", out var argsObj) && argsObj is List<object> argsList)
                            inputEvent.args = argsList.Select(a => a?.ToString()).ToArray();
                        recordedInputs.Add(inputEvent);
                    }
                }
            }

            resolved:
            if (targetId == 0)
                return JsonError("session.targetInstanceId is required.");

            var go = EditorUtility.EntityIdToObject(targetId) as GameObject;
            if (go == null)
                return JsonError($"GameObject with instanceId {targetId} not found.");

            // Execute replay: inject inputs and compare state
            _replayPlaybackActive = true;
            float startTime = Time.realtimeSinceStartup;
            int inputIndex = 0;
            int compareIndex = 0;
            var divergences = new List<object>();
            float maxPositionDelta = 0f;
            float maxRotationDelta = 0f;
            float maxVelocityDelta = 0f;
            int divergentFrameCount = 0;
            int animDivergenceCount = 0;
            int collisionDivergenceCount = 0;
            int triggerDivergenceCount = 0;

            try
            {
                float sessionDuration = recordedFrames.Count > 0
                    ? recordedFrames[recordedFrames.Count - 1].timestamp
                    : 0f;

                // Replay loop: inject inputs at their timestamps
                float elapsed = 0f;
                int maxIterations = (int)(sessionDuration / (compareInterval / 1000f)) + recordedInputs.Count + 100;
                int iteration = 0;

                while (elapsed <= sessionDuration + 0.5f && iteration < maxIterations)
                {
                    iteration++;
                    float stepMs = Math.Min(compareInterval, 50f);
                    Thread.Sleep((int)stepMs);
                    elapsed = Time.realtimeSinceStartup - startTime;

                    // Inject pending inputs
                    while (inputIndex < recordedInputs.Count && recordedInputs[inputIndex].timestamp <= elapsed)
                    {
                        var input = recordedInputs[inputIndex];
                        if (!string.IsNullOrEmpty(input.action) && !string.IsNullOrEmpty(targetComponent))
                        {
                            var (_, error) = InvokeMethodCore(go, targetComponent, input.action, input.args);
                            if (error != null)
                            {
                                divergences.Add(new Dictionary<string, object>
                                {
                                    { "type", "input_error" },
                                    { "timestamp", elapsed },
                                    { "action", input.action },
                                    { "error", error }
                                });
                            }
                        }
                        inputIndex++;
                    }

                    // Compare state at matching recorded frames
                    while (compareIndex < recordedFrames.Count && recordedFrames[compareIndex].timestamp <= elapsed)
                    {
                        var recorded = recordedFrames[compareIndex];

                        // --- Position check (threshold: 1cm) ---
                        var currentPos = go.transform.position;
                        float dx = currentPos.x - recorded.posX;
                        float dy = currentPos.y - recorded.posY;
                        float dz = currentPos.z - recorded.posZ;
                        float posDelta = Mathf.Sqrt(dx * dx + dy * dy + dz * dz);

                        if (posDelta > maxPositionDelta)
                            maxPositionDelta = posDelta;

                        if (posDelta > 0.01f)
                        {
                            divergentFrameCount++;
                            divergences.Add(new Dictionary<string, object>
                            {
                                { "type", "position_divergence" },
                                { "frameIndex", compareIndex },
                                { "timestamp", recorded.timestamp },
                                { "expectedPos", new Dictionary<string, object> { {"x", recorded.posX}, {"y", recorded.posY}, {"z", recorded.posZ} } },
                                { "actualPos", new Dictionary<string, object> { {"x", currentPos.x}, {"y", currentPos.y}, {"z", currentPos.z} } },
                                { "positionDelta", posDelta }
                            });
                        }

                        // --- Rotation check (threshold: 5 degrees) ---
                        var currentRot = go.transform.eulerAngles;
                        float rdx = Mathf.DeltaAngle(currentRot.x, recorded.rotX);
                        float rdy = Mathf.DeltaAngle(currentRot.y, recorded.rotY);
                        float rdz = Mathf.DeltaAngle(currentRot.z, recorded.rotZ);
                        float rotDelta = Mathf.Sqrt(rdx * rdx + rdy * rdy + rdz * rdz);

                        if (rotDelta > maxRotationDelta)
                            maxRotationDelta = rotDelta;

                        if (rotDelta > 5f)
                        {
                            divergentFrameCount++;
                            divergences.Add(new Dictionary<string, object>
                            {
                                { "type", "rotation_divergence" },
                                { "frameIndex", compareIndex },
                                { "timestamp", recorded.timestamp },
                                { "expectedRot", new Dictionary<string, object> { {"x", recorded.rotX}, {"y", recorded.rotY}, {"z", recorded.rotZ} } },
                                { "actualRot", new Dictionary<string, object> { {"x", currentRot.x}, {"y", currentRot.y}, {"z", currentRot.z} } },
                                { "rotationDelta", rotDelta }
                            });
                        }

                        // --- Velocity check (threshold: 0.1 m/s) ---
                        if (recorded.hasVelocity)
                        {
                            var rb = go.GetComponent<Rigidbody>();
                            if (rb != null)
                            {
                                var currentVel = rb.linearVelocity;
                                float vdx = currentVel.x - recorded.velX;
                                float vdy = currentVel.y - recorded.velY;
                                float vdz = currentVel.z - recorded.velZ;
                                float velDelta = Mathf.Sqrt(vdx * vdx + vdy * vdy + vdz * vdz);

                                if (velDelta > maxVelocityDelta)
                                    maxVelocityDelta = velDelta;

                                if (velDelta > 0.1f)
                                {
                                    divergentFrameCount++;
                                    divergences.Add(new Dictionary<string, object>
                                    {
                                        { "type", "velocity_divergence" },
                                        { "frameIndex", compareIndex },
                                        { "timestamp", recorded.timestamp },
                                        { "expectedVel", new Dictionary<string, object> { {"x", recorded.velX}, {"y", recorded.velY}, {"z", recorded.velZ} } },
                                        { "actualVel", new Dictionary<string, object> { {"x", currentVel.x}, {"y", currentVel.y}, {"z", currentVel.z} } },
                                        { "velocityDelta", velDelta }
                                    });
                                }
                            }
                        }

                        // --- Animation state check (hash mismatch) ---
                        if (recorded.hasAnimator)
                        {
                            var animator = go.GetComponent<Animator>();
                            if (animator != null && animator.runtimeAnimatorController != null && animator.layerCount > 0)
                            {
                                var currentState = animator.GetCurrentAnimatorStateInfo(0);
                                if (currentState.fullPathHash != recorded.animStateHash)
                                {
                                    animDivergenceCount++;
                                    divergentFrameCount++;
                                    divergences.Add(new Dictionary<string, object>
                                    {
                                        { "type", "animation_divergence" },
                                        { "frameIndex", compareIndex },
                                        { "timestamp", recorded.timestamp },
                                        { "expectedStateHash", recorded.animStateHash },
                                        { "actualStateHash", currentState.fullPathHash }
                                    });
                                }
                            }
                        }

                        // --- Collision/trigger state check ---
                        if (recorded.hasCollisionState)
                        {
                            var overlaps = Physics.OverlapSphere(currentPos, 0.01f);
                            int currentContacts = overlaps.Count(o => o.gameObject != go);
                            bool currentTrigger = overlaps.Any(o => o.isTrigger && o.gameObject != go);

                            if (currentContacts != recorded.contactCount)
                            {
                                collisionDivergenceCount++;
                                divergentFrameCount++;
                                divergences.Add(new Dictionary<string, object>
                                {
                                    { "type", "collision_divergence" },
                                    { "frameIndex", compareIndex },
                                    { "timestamp", recorded.timestamp },
                                    { "expectedContacts", recorded.contactCount },
                                    { "actualContacts", currentContacts }
                                });
                            }

                            if (currentTrigger != recorded.isTriggerActive)
                            {
                                triggerDivergenceCount++;
                                divergentFrameCount++;
                                divergences.Add(new Dictionary<string, object>
                                {
                                    { "type", "trigger_divergence" },
                                    { "frameIndex", compareIndex },
                                    { "timestamp", recorded.timestamp },
                                    { "expectedTrigger", recorded.isTriggerActive },
                                    { "actualTrigger", currentTrigger }
                                });
                            }
                        }

                        compareIndex++;
                    }
                }
            }
            finally
            {
                _replayPlaybackActive = false;
            }

            bool deterministic = divergentFrameCount == 0;

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "deterministic", deterministic },
                { "totalRecordedFrames", recordedFrames.Count },
                { "comparedFrames", compareIndex },
                { "divergentFrames", divergentFrameCount },
                { "maxPositionDelta", maxPositionDelta },
                { "maxRotationDelta", maxRotationDelta },
                { "maxVelocityDelta", maxVelocityDelta },
                { "animationDivergences", animDivergenceCount },
                { "collisionDivergences", collisionDivergenceCount },
                { "triggerDivergences", triggerDivergenceCount },
                { "inputsReplayed", inputIndex },
                { "totalInputs", recordedInputs.Count },
                { "divergences", divergences }
            });
        }

        [BridgeRoute("GET", "/replay/status", Category = "replay", Description = "Replay subsystem status", ReadOnly = true)]
        public static string ReplayGetStatus()
        {
            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "isRecording", _replayRecordingActive },
                { "isPlayingBack", _replayPlaybackActive },
                { "hasCompletedRecording", _lastCompletedRecording != null },
                { "activeFrameCount", _activeRecording?.stateFrames.Count ?? 0 },
                { "activeInputCount", _activeRecording?.inputEvents.Count ?? 0 }
            });
        }

        [BridgeRoute("POST", "/replay/input", Category = "replay", Description = "Record replay input event")]
        public static string ReplayRecordInput(string jsonData)
        {
            if (!_replayRecordingActive || _activeRecording == null)
                return JsonError("No active recording.");

            Dictionary<string, object> req;
            try
            {
                req = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
                if (req == null) return JsonError("Invalid request JSON.");
            }
            catch (Exception ex)
            {
                return JsonError($"Failed to parse request: {ex.Message}");
            }

            var inputEvent = new ReplayInputEventData
            {
                timestamp = Time.realtimeSinceStartup - _activeRecording.startTime,
                action = req.TryGetValue("action", out var a) ? a?.ToString() : ""
            };
            if (req.TryGetValue("args", out var argsObj) && argsObj is List<object> argsList)
                inputEvent.args = argsList.Select(x => x?.ToString()).ToArray();

            _activeRecording.inputEvents.Add(inputEvent);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "totalInputs", _activeRecording.inputEvents.Count }
            });
        }

        private static void EnsureReplayTick()
        {
            if (_replayTickRegistered) return;
            _replayTickRegistered = true;
            EditorApplication.update += ReplayCaptureTick;
            EditorApplication.playModeStateChanged += OnReplayPlayModeChanged;
        }

        private static void OnReplayPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                _replayRecordingActive = false;
                _replayPlaybackActive = false;
                _activeRecording = null;
                EditorApplication.update -= ReplayCaptureTick;
                EditorApplication.playModeStateChanged -= OnReplayPlayModeChanged;
                _replayTickRegistered = false;
            }
        }

        private static void ReplayCaptureTick()
        {
            if (!_replayRecordingActive || _activeRecording == null) return;
            if (!EditorApplication.isPlaying) return;

            float now = Time.realtimeSinceStartup;
            float intervalSec = _activeRecording.captureIntervalMs / 1000f;
            if (now - _replayLastCaptureTime < intervalSec) return;
            _replayLastCaptureTime = now;

            if (_activeRecording.targetInstanceId == 0) return;

            var go = EditorUtility.EntityIdToObject(_activeRecording.targetInstanceId) as GameObject;
            if (go == null) return;

            var pos = go.transform.position;
            var rot = go.transform.eulerAngles;
            var frame = new ReplayStateFrameData
            {
                timestamp = now - _activeRecording.startTime,
                posX = pos.x,
                posY = pos.y,
                posZ = pos.z,
                rotX = rot.x,
                rotY = rot.y,
                rotZ = rot.z
            };

            // Velocity (Rigidbody)
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                frame.hasVelocity = true;
                frame.velX = rb.linearVelocity.x;
                frame.velY = rb.linearVelocity.y;
                frame.velZ = rb.linearVelocity.z;
            }

            // Animation state (Animator)
            var animator = go.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null && animator.layerCount > 0)
            {
                frame.hasAnimator = true;
                var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
                frame.animStateHash = stateInfo.fullPathHash;
                frame.animNormalizedTime = stateInfo.normalizedTime;
            }

            // Collision/trigger state
            var col = go.GetComponent<Collider>();
            if (col != null)
            {
                frame.hasCollisionState = true;
                var overlaps = Physics.OverlapSphere(pos, 0.01f);
                frame.contactCount = overlaps.Count(o => o.gameObject != go);
                frame.isTriggerActive = overlaps.Any(o => o.isTrigger && o.gameObject != go);
            }

            // Capture extra fields from the target component if specified
            if (!string.IsNullOrEmpty(_activeRecording.targetComponentType))
            {
                var component = FindComponentOnGameObject(go, _activeRecording.targetComponentType, out var type);
                if (component != null)
                {
                    var extras = new Dictionary<string, object>();
                    var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var field in fields)
                    {
                        if (!field.IsPublic && field.GetCustomAttribute<UnityEngine.SerializeField>() == null) continue;
                        try
                        {
                            var val = field.GetValue(component);
                            if (val != null && (val is int || val is float || val is bool || val is string || val is double))
                                extras[field.Name] = val;
                        }
                        catch { }
                    }
                    if (extras.Count > 0)
                        frame.extraFieldsJson = MiniJSON.Json.Serialize(extras);
                }
            }

            _activeRecording.stateFrames.Add(frame);
        }
    }
}
