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
        #region Animation Operations

        private static AnimatorControllerParameterType ParseAnimatorParameterType(string type)
        {
            switch (type?.ToLower())
            {
                case "float": return AnimatorControllerParameterType.Float;
                case "int": return AnimatorControllerParameterType.Int;
                case "bool": return AnimatorControllerParameterType.Bool;
                case "trigger": return AnimatorControllerParameterType.Trigger;
                default: return AnimatorControllerParameterType.Float;
            }
        }

        private static AnimatorConditionMode ParseAnimatorConditionMode(string mode)
        {
            switch (mode)
            {
                case "If": return AnimatorConditionMode.If;
                case "IfNot": return AnimatorConditionMode.IfNot;
                case "Greater": return AnimatorConditionMode.Greater;
                case "Less": return AnimatorConditionMode.Less;
                case "Equals": return AnimatorConditionMode.Equals;
                case "NotEqual": return AnimatorConditionMode.NotEqual;
                default: return AnimatorConditionMode.If;
            }
        }

        [BridgeRoute("POST", "/animator/controller", Category = "animator", Description = "Create AnimatorController")]
        public static string CreateAnimatorController(string jsonData)
        {
            var request = JsonUtility.FromJson<CreateAnimatorControllerRequest>(jsonData);

            if (string.IsNullOrEmpty(request.path))
                return JsonError("path is required");

            if (ValidateAssetPath(request.path) == null)
                return JsonError("Path is outside the project directory: " + request.path);

            if (!request.path.EndsWith(".controller"))
                request.path += ".controller";

            // Ensure directory exists
            var dir = System.IO.Path.GetDirectoryName(request.path);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(request.path);
            if (controller == null)
                return JsonError("Failed to create AnimatorController at: " + request.path);

            if (!string.IsNullOrEmpty(request.name))
                controller.name = request.name;

            // Add parameters if provided
            if (!string.IsNullOrEmpty(request.parametersJson))
            {
                var paramList = JsonUtility.FromJson<AnimatorParameterDataList>("{\"items\":" + request.parametersJson + "}");
                if (paramList != null && paramList.items != null)
                {
                    foreach (var p in paramList.items)
                    {
                        var pType = ParseAnimatorParameterType(p.type);
                        controller.AddParameter(p.name, pType);
                    }

                    // Set default values (controller.parameters returns a copy)
                    var parameters = controller.parameters;
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        var matchingParam = paramList.items.Find(x => x.name == parameters[i].name);
                        if (matchingParam != null)
                        {
                            if (parameters[i].type == AnimatorControllerParameterType.Float)
                                parameters[i].defaultFloat = matchingParam.defaultFloat;
                            else if (parameters[i].type == AnimatorControllerParameterType.Int)
                                parameters[i].defaultInt = matchingParam.defaultInt;
                            else if (parameters[i].type == AnimatorControllerParameterType.Bool && matchingParam.defaultBool != -1)
                                parameters[i].defaultBool = matchingParam.defaultBool == 1;
                        }
                    }
                    controller.parameters = parameters;
                }
            }

            // Attach to scene GameObject if requested
            if (request.attachToInstanceId > 0)
            {
                var go = EditorUtility.EntityIdToObject(request.attachToInstanceId) as GameObject;
                if (go != null)
                {
                    var animator = go.GetComponent<Animator>();
                    if (animator == null)
                        animator = go.AddComponent<Animator>();
                    animator.runtimeAnimatorController = controller;
                    if (request.applyRootMotion != -1)
                        animator.applyRootMotion = request.applyRootMotion == 1;
                    EditorUtility.SetDirty(go);
                }
            }

            // Attach to prefab if requested
            string attachedPrefabPath = null;
            if (!string.IsNullOrEmpty(request.prefabPath))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(request.prefabPath);
                if (prefab != null)
                {
                    using (var scope = new PrefabUtility.EditPrefabContentsScope(request.prefabPath))
                    {
                        var root = scope.prefabContentsRoot;
                        if (root == null)
                        {
                            Debug.LogWarning($"Failed to edit prefab contents at: {request.prefabPath}");
                        }
                        else
                        {
                            // Use GetComponentInChildren for prefabs since animator may be on child armature
                            var animator = root.GetComponentInChildren<Animator>();
                            if (animator == null)
                                animator = root.AddComponent<Animator>();
                            animator.runtimeAnimatorController = controller;
                            if (request.applyRootMotion != -1)
                                animator.applyRootMotion = request.applyRootMotion == 1;
                            attachedPrefabPath = request.prefabPath;
                        }
                    }
                }
                else
                {
                    Debug.LogWarning($"Prefab not found at: {request.prefabPath}");
                }
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(request.path);

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "path", request.path },
                { "name", controller.name },
                { "parameterCount", controller.parameters.Length }
            };

            if (request.attachToInstanceId > 0)
                result["attachedToInstanceId"] = request.attachToInstanceId;
            if (attachedPrefabPath != null)
                result["attachedToPrefab"] = attachedPrefabPath;

            return JsonResult(result);
        }

        [BridgeRoute("POST", "/animator/state", Category = "animator", Description = "Add animation state")]
        public static string AddAnimationState(string jsonData)
        {
            var request = JsonUtility.FromJson<AddAnimationStateRequest>(jsonData);

            if (string.IsNullOrEmpty(request.controllerPath))
                return JsonError("controllerPath is required");
            if (string.IsNullOrEmpty(request.stateName))
                return JsonError("stateName is required");

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(request.controllerPath);
            if (controller == null)
                return JsonError("AnimatorController not found at: " + request.controllerPath);

            if (request.layerIndex < 0 || request.layerIndex >= controller.layers.Length)
                return JsonError($"layerIndex {request.layerIndex} is out of range (controller has {controller.layers.Length} layers)");

            var layers = controller.layers;
            var stateMachine = layers[request.layerIndex].stateMachine;

            // Check if state already exists
            foreach (var childState in stateMachine.states)
            {
                if (childState.state.name == request.stateName)
                    return JsonError($"State '{request.stateName}' already exists in layer {request.layerIndex}");
            }

            Undo.RecordObject(controller, "Agent Bridge: Add Animation State");

            var state = stateMachine.AddState(request.stateName);

            // Assign motion clip if provided
            if (!string.IsNullOrEmpty(request.motionClipPath))
            {
                AnimationClip clip = null;
                string clipPath = request.motionClipPath;

                if (clipPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                {
                    // FBX sub-asset: load all assets and find the AnimationClip
                    var allAssets = AssetDatabase.LoadAllAssetsAtPath(clipPath);
                    string targetName = request.motionClipName;

                    foreach (var asset in allAssets)
                    {
                        if (asset is AnimationClip c && !c.name.StartsWith("__preview__"))
                        {
                            if (!string.IsNullOrEmpty(targetName))
                            {
                                if (c.name == targetName)
                                {
                                    clip = c;
                                    break;
                                }
                            }
                            else
                            {
                                // Take first non-preview clip
                                clip = c;
                                break;
                            }
                        }
                    }

                    if (clip == null)
                        Debug.LogWarning($"No AnimationClip found in FBX: {clipPath}" +
                            (!string.IsNullOrEmpty(targetName) ? $" (looking for '{targetName}')" : ""));
                }
                else
                {
                    clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                }

                if (clip != null)
                    state.motion = clip;
                else if (!clipPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                    Debug.LogWarning($"Animation clip not found at: {clipPath}");
            }

            // Set speed
            if (request.speed >= 0f)
                state.speed = request.speed;

            // Set speed parameter
            if (!string.IsNullOrEmpty(request.speedParameterName))
            {
                state.speedParameter = request.speedParameterName;
                state.speedParameterActive = true;
            }

            // Set as default state
            if (request.setAsDefault == 1)
                stateMachine.defaultState = state;

            // Must reassign layers array since it's a copy
            controller.layers = layers;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            // Force reimport so subsequent loads see the new state immediately
            AssetDatabase.ImportAsset(request.controllerPath);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "stateName", state.name },
                { "nameHash", Animator.StringToHash(request.stateName) },
                { "isDefault", stateMachine.defaultState == state },
                { "layerIndex", request.layerIndex }
            });
        }

        [BridgeRoute("POST", "/animator/transition", Category = "animator", Description = "Add animation transition")]
        public static string AddAnimationTransition(string jsonData)
        {
            var request = JsonUtility.FromJson<AddAnimationTransitionRequest>(jsonData);

            if (string.IsNullOrEmpty(request.controllerPath))
                return JsonError("controllerPath is required");
            if (string.IsNullOrEmpty(request.sourceStateName))
                return JsonError("sourceStateName is required");
            if (string.IsNullOrEmpty(request.destinationStateName))
                return JsonError("destinationStateName is required");

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(request.controllerPath);
            if (controller == null)
                return JsonError("AnimatorController not found at: " + request.controllerPath);

            if (request.layerIndex < 0 || request.layerIndex >= controller.layers.Length)
                return JsonError($"layerIndex {request.layerIndex} is out of range");

            Undo.RecordObject(controller, "Agent Bridge: Add Animation Transition");

            var layers = controller.layers;
            var stateMachine = layers[request.layerIndex].stateMachine;

            // Find destination state
            AnimatorState destState = null;
            foreach (var childState in stateMachine.states)
            {
                if (childState.state.name == request.destinationStateName)
                {
                    destState = childState.state;
                    break;
                }
            }
            if (destState == null)
                return JsonError($"Destination state '{request.destinationStateName}' not found");

            // Parse conditions
            List<AnimatorConditionData> conditions = null;
            if (!string.IsNullOrEmpty(request.conditionsJson))
            {
                var condList = JsonUtility.FromJson<AnimatorConditionDataList>("{\"items\":" + request.conditionsJson + "}");
                if (condList != null)
                    conditions = condList.items;
            }

            string sourceName = request.sourceStateName.ToLower();
            bool isEntry = sourceName == "entry";

            if (isEntry)
            {
                // Entry transitions use AnimatorTransition (no timing properties)
                var transition = stateMachine.AddEntryTransition(destState);
                if (conditions != null)
                {
                    foreach (var c in conditions)
                        transition.AddCondition(ParseAnimatorConditionMode(c.mode), c.threshold, c.parameterName);
                }
            }
            else if (sourceName == "any" || sourceName == "anystate")
            {
                var transition = stateMachine.AddAnyStateTransition(destState);
                ApplyTransitionTiming(transition, request);
                if (conditions != null)
                {
                    foreach (var c in conditions)
                        transition.AddCondition(ParseAnimatorConditionMode(c.mode), c.threshold, c.parameterName);
                }
            }
            else
            {
                // Find source state
                AnimatorState sourceState = null;
                foreach (var childState in stateMachine.states)
                {
                    if (childState.state.name == request.sourceStateName)
                    {
                        sourceState = childState.state;
                        break;
                    }
                }
                if (sourceState == null)
                    return JsonError($"Source state '{request.sourceStateName}' not found");

                var transition = sourceState.AddTransition(destState);
                ApplyTransitionTiming(transition, request);
                if (conditions != null)
                {
                    foreach (var c in conditions)
                        transition.AddCondition(ParseAnimatorConditionMode(c.mode), c.threshold, c.parameterName);
                }
            }

            controller.layers = layers;
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(request.controllerPath);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "source", request.sourceStateName },
                { "destination", request.destinationStateName },
                { "layerIndex", request.layerIndex },
                { "conditionCount", conditions != null ? conditions.Count : 0 }
            });
        }

        private static void ApplyTransitionTiming(AnimatorStateTransition transition, AddAnimationTransitionRequest request)
        {
            if (request.hasExitTime != -1)
                transition.hasExitTime = request.hasExitTime == 1;
            if (request.exitTime >= 0f)
                transition.exitTime = request.exitTime;
            if (request.transitionDuration >= 0f)
                transition.duration = request.transitionDuration;
            if (request.transitionOffset >= 0f)
                transition.offset = request.transitionOffset;
            if (request.hasFixedDuration != -1)
                transition.hasFixedDuration = request.hasFixedDuration == 1;
            if (request.canTransitionToSelf != -1)
                transition.canTransitionToSelf = request.canTransitionToSelf == 1;
        }

        [BridgeRoute("POST", "/animator/parameter", Category = "animator", Description = "Set animation parameter")]
        public static string SetAnimationParameter(string jsonData)
        {
            var request = JsonUtility.FromJson<SetAnimationParameterRequest>(jsonData);

            if (string.IsNullOrEmpty(request.controllerPath))
                return JsonError("controllerPath is required");
            if (string.IsNullOrEmpty(request.parameterName))
                return JsonError("parameterName is required");

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(request.controllerPath);
            if (controller == null)
                return JsonError("AnimatorController not found at: " + request.controllerPath);

            Undo.RecordObject(controller, "Agent Bridge: Set Animation Parameter");

            // Remove parameter
            if (request.remove == 1)
            {
                int removeIndex = -1;
                var parameters = controller.parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].name == request.parameterName)
                    {
                        removeIndex = i;
                        break;
                    }
                }
                if (removeIndex == -1)
                    return JsonError($"Parameter '{request.parameterName}' not found");

                controller.RemoveParameter(removeIndex);
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();

                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "action", "removed" },
                    { "parameterName", request.parameterName }
                });
            }

            // Add or modify parameter
            if (string.IsNullOrEmpty(request.type))
                return JsonError("type is required when adding/modifying a parameter");

            var pType = ParseAnimatorParameterType(request.type);

            // Check if exists
            int existingIndex = -1;
            var existingParams = controller.parameters;
            for (int i = 0; i < existingParams.Length; i++)
            {
                if (existingParams[i].name == request.parameterName)
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex == -1)
            {
                controller.AddParameter(request.parameterName, pType);
            }

            // Set defaults (copy, modify, reassign)
            var allParams = controller.parameters;
            for (int i = 0; i < allParams.Length; i++)
            {
                if (allParams[i].name == request.parameterName)
                {
                    if (existingIndex >= 0)
                        allParams[i].type = pType;
                    if (pType == AnimatorControllerParameterType.Float)
                        allParams[i].defaultFloat = request.defaultFloat;
                    else if (pType == AnimatorControllerParameterType.Int)
                        allParams[i].defaultInt = request.defaultInt;
                    else if (pType == AnimatorControllerParameterType.Bool && request.defaultBool != -1)
                        allParams[i].defaultBool = request.defaultBool == 1;
                    break;
                }
            }
            controller.parameters = allParams;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "action", existingIndex >= 0 ? "modified" : "added" },
                { "parameterName", request.parameterName },
                { "type", request.type },
                { "totalParameters", controller.parameters.Length }
            });
        }

        [BridgeRoute("POST", "/animator/clip", Category = "animator", Description = "Create animation clip")]
        public static string CreateAnimationClip(string jsonData)
        {
            var request = JsonUtility.FromJson<CreateAnimationClipRequest>(jsonData);

            if (string.IsNullOrEmpty(request.path))
                return JsonError("path is required");

            if (ValidateAssetPath(request.path) == null)
                return JsonError("Path is outside the project directory: " + request.path);

            if (!request.path.EndsWith(".anim"))
                request.path += ".anim";

            var dir = System.IO.Path.GetDirectoryName(request.path);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            var clip = new AnimationClip();

            // Set frame rate
            if (request.frameRate > 0f)
                clip.frameRate = request.frameRate;

            // Set wrap mode / looping
            var clipSettings = AnimationUtility.GetAnimationClipSettings(clip);
            if (!string.IsNullOrEmpty(request.wrapMode))
            {
                switch (request.wrapMode)
                {
                    case "Loop":
                        clipSettings.loopTime = true;
                        break;
                    case "Once":
                        clipSettings.loopTime = false;
                        break;
                    case "PingPong":
                        clipSettings.loopTime = true;
                        break;
                    case "ClampForever":
                        clipSettings.loopTime = false;
                        break;
                    default:
                        break;
                }
            }
            AnimationUtility.SetAnimationClipSettings(clip, clipSettings);

            // Add curves
            if (!string.IsNullOrEmpty(request.curvesJson))
            {
                var curveList = JsonUtility.FromJson<AnimationCurveDataList>("{\"items\":" + request.curvesJson + "}");
                if (curveList != null && curveList.items != null)
                {
                    foreach (var curveData in curveList.items)
                    {
                        if (string.IsNullOrEmpty(curveData.propertyName))
                            continue;

                        // Parse keyframes
                        Keyframe[] keyframes;
                        if (!string.IsNullOrEmpty(curveData.keyframesJson))
                        {
                            var kfList = JsonUtility.FromJson<AnimationKeyframeDataList>("{\"items\":" + curveData.keyframesJson + "}");
                            keyframes = new Keyframe[kfList.items.Count];
                            for (int i = 0; i < kfList.items.Count; i++)
                            {
                                var kf = kfList.items[i];
                                keyframes[i] = new Keyframe(kf.time, kf.value, kf.inTangent, kf.outTangent);
                            }
                        }
                        else
                        {
                            keyframes = new Keyframe[0];
                        }

                        var curve = new AnimationCurve(keyframes);

                        // Resolve component type
                        Type componentType = null;
                        if (!string.IsNullOrEmpty(curveData.componentType))
                        {
                            if (curveData.componentType == "Transform")
                                componentType = typeof(Transform);
                            else if (curveData.componentType == "SpriteRenderer")
                                componentType = typeof(SpriteRenderer);
                            else if (curveData.componentType == "MeshRenderer")
                                componentType = typeof(MeshRenderer);
                            else if (curveData.componentType == "SkinnedMeshRenderer")
                                componentType = typeof(SkinnedMeshRenderer);
                            else
                                componentType = TypeResolver.FindType(curveData.componentType);
                        }

                        if (componentType != null)
                        {
                            clip.SetCurve(
                                curveData.relativePath ?? "",
                                componentType,
                                curveData.propertyName,
                                curve
                            );
                        }
                    }
                }
            }

            AssetDatabase.CreateAsset(clip, request.path);
            AssetDatabase.SaveAssets();

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "path", request.path },
                { "frameRate", clip.frameRate },
                { "length", clip.length },
                { "isLooping", clip.isLooping }
            });
        }

        public static string GetAnimatorInfo(string jsonData)
        {
            var request = JsonUtility.FromJson<GetAnimatorInfoRequest>(jsonData);

            if (string.IsNullOrEmpty(request.controllerPath))
                return JsonError("controllerPath is required");

            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(request.controllerPath);
            if (controller == null)
                return JsonError("AnimatorController not found at: " + request.controllerPath);

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "name", controller.name },
                { "path", request.controllerPath }
            };

            // Parameters
            var paramInfos = new List<Dictionary<string, object>>();
            foreach (var p in controller.parameters)
            {
                var pInfo = new Dictionary<string, object>
                {
                    { "name", p.name },
                    { "type", p.type.ToString() }
                };
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        pInfo["defaultFloat"] = p.defaultFloat;
                        break;
                    case AnimatorControllerParameterType.Int:
                        pInfo["defaultInt"] = p.defaultInt;
                        break;
                    case AnimatorControllerParameterType.Bool:
                        pInfo["defaultBool"] = p.defaultBool;
                        break;
                }
                paramInfos.Add(pInfo);
            }
            result["parameters"] = paramInfos;

            // Layers
            var layerInfos = new List<Dictionary<string, object>>();
            for (int li = 0; li < controller.layers.Length; li++)
            {
                if (request.layerIndex >= 0 && li != request.layerIndex)
                    continue;

                var layer = controller.layers[li];
                var sm = layer.stateMachine;

                var layerInfo = new Dictionary<string, object>
                {
                    { "index", li },
                    { "name", layer.name },
                    { "defaultWeight", layer.defaultWeight },
                    { "blendingMode", layer.blendingMode.ToString() },
                    { "defaultStateName", sm.defaultState != null ? sm.defaultState.name : "" }
                };

                // States
                var stateInfos = new List<Dictionary<string, object>>();
                foreach (var childState in sm.states)
                {
                    var s = childState.state;
                    var sInfo = new Dictionary<string, object>
                    {
                        { "name", s.name },
                        { "nameHash", s.nameHash },
                        { "speed", s.speed },
                        { "speedParameter", s.speedParameter ?? "" }
                    };

                    if (s.motion != null)
                    {
                        sInfo["motionName"] = s.motion.name;
                        var motionPath = AssetDatabase.GetAssetPath(s.motion);
                        if (!string.IsNullOrEmpty(motionPath))
                            sInfo["motionPath"] = motionPath;
                    }

                    // State transitions
                    var transInfos = new List<Dictionary<string, object>>();
                    foreach (var t in s.transitions)
                    {
                        transInfos.Add(BuildTransitionInfo(s.name, t));
                    }
                    sInfo["transitions"] = transInfos;

                    stateInfos.Add(sInfo);
                }
                layerInfo["states"] = stateInfos;

                // AnyState transitions
                var anyTransInfos = new List<Dictionary<string, object>>();
                foreach (var t in sm.anyStateTransitions)
                {
                    anyTransInfos.Add(BuildTransitionInfo("AnyState", t));
                }
                layerInfo["anyStateTransitions"] = anyTransInfos;

                layerInfos.Add(layerInfo);
            }
            result["layers"] = layerInfos;

            return JsonResult(result);
        }

        private static Dictionary<string, object> BuildTransitionInfo(string sourceName, AnimatorStateTransition t)
        {
            var info = new Dictionary<string, object>
            {
                { "sourceName", sourceName },
                { "destinationName", t.destinationState != null ? t.destinationState.name : "" },
                { "hasExitTime", t.hasExitTime },
                { "exitTime", t.exitTime },
                { "duration", t.duration },
                { "offset", t.offset },
                { "hasFixedDuration", t.hasFixedDuration },
                { "canTransitionToSelf", t.canTransitionToSelf }
            };

            var condInfos = new List<Dictionary<string, object>>();
            foreach (var c in t.conditions)
            {
                condInfos.Add(new Dictionary<string, object>
                {
                    { "parameterName", c.parameter },
                    { "mode", c.mode.ToString() },
                    { "threshold", c.threshold }
                });
            }
            info["conditions"] = condInfos;

            return info;
        }

        public static string GetFbxClips(string jsonData)
        {
            var request = JsonUtility.FromJson<GetFbxClipsRequest>(jsonData);

            if (string.IsNullOrEmpty(request.fbxPath))
                return JsonError("fbxPath is required");

            if (!request.fbxPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                return JsonError("fbxPath must point to an .fbx file");

            var allAssets = AssetDatabase.LoadAllAssetsAtPath(request.fbxPath);
            if (allAssets == null || allAssets.Length == 0)
                return JsonError("No assets found at: " + request.fbxPath);

            var clips = new List<Dictionary<string, object>>();
            foreach (var asset in allAssets)
            {
                if (asset is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                {
                    var clipInfo = new Dictionary<string, object>
                    {
                        { "name", clip.name },
                        { "length", clip.length },
                        { "frameRate", clip.frameRate },
                        { "isLooping", clip.isLooping },
                        { "isHumanMotion", clip.isHumanMotion },
                        { "hasRootMotion", clip.hasGenericRootTransform || clip.hasMotionCurves }
                    };

                    // Get clip settings for additional info
                    var settings = AnimationUtility.GetAnimationClipSettings(clip);
                    clipInfo["loopTime"] = settings.loopTime;
                    clipInfo["keepOriginalPositionY"] = settings.keepOriginalPositionY;

                    clips.Add(clipInfo);
                }
            }

            if (clips.Count == 0)
                Debug.LogWarning($"No AnimationClips found in FBX: {request.fbxPath}. Check import settings.");

            // Also check rig type via ModelImporter
            var importer = AssetImporter.GetAtPath(request.fbxPath) as ModelImporter;
            string rigType = "Unknown";
            if (importer != null)
                rigType = importer.animationType.ToString();

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "fbxPath", request.fbxPath },
                { "rigType", rigType },
                { "clipCount", clips.Count },
                { "clips", clips }
            });
        }

        #endregion
    }
}
