using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class AnimationTools
{
    [McpServerTool(Name = "unity_create_animator_controller")]
    [Description("Create an AnimatorController (.controller) asset. Optionally attach it to a scene GameObject or prefab asset, and add initial parameters. " +
        "Supports both scene objects (attachToInstanceId) and prefab assets (prefabPath) for attachment. " +
        "Example: unity_create_animator_controller(path=\"Assets/Animations/Player.controller\", " +
        "prefabPath=\"Assets/Prefabs/Player.prefab\", applyRootMotion=false, " +
        "parameters=\"[{\\\"name\\\":\\\"Speed\\\",\\\"type\\\":\\\"Float\\\"},{\\\"name\\\":\\\"IsJumping\\\",\\\"type\\\":\\\"Bool\\\"}]\")")]
    public static async Task<string> CreateAnimatorController(
        UnityClient client,
        [Description("Asset path for the controller (e.g., 'Assets/Animations/Player.controller').")] string path,
        [Description("Optional display name for the controller.")] string? name = null,
        [Description("Optional instance ID of a scene GameObject to attach the Animator + controller to.")] int attachToInstanceId = 0,
        [Description("Optional path to a prefab asset to attach the controller to (uses GetComponentInChildren<Animator>).")] string? prefabPath = null,
        [Description("Optional: set applyRootMotion on the Animator. False for code-driven movement (Rigidbody.MovePosition).")] bool? applyRootMotion = null,
        [Description("Optional JSON array of parameters to add. Each element: {\"name\":\"Speed\",\"type\":\"Float|Int|Bool|Trigger\",\"defaultFloat\":0,\"defaultInt\":0,\"defaultBool\":-1}. " +
            "Bool uses int sentinel: -1=unset, 0=false, 1=true.")] string? parameters = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolErrors.ValidationError("path is required");

        var request = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["attachToInstanceId"] = attachToInstanceId
        };

        if (!string.IsNullOrWhiteSpace(name))
            request["name"] = name;

        if (!string.IsNullOrWhiteSpace(prefabPath))
            request["prefabPath"] = prefabPath;

        if (applyRootMotion.HasValue)
            request["applyRootMotion"] = applyRootMotion.Value ? 1 : 0;

        if (!string.IsNullOrWhiteSpace(parameters))
            request["parametersJson"] = parameters;

        return await client.CreateAnimatorControllerAsync(request);
    }

    [McpServerTool(Name = "unity_add_animation_state")]
    [Description("Add a state to an AnimatorController layer. Optionally assign a motion clip and set as default state. " +
        "Supports both .anim clips and .fbx sub-assets (auto-resolves embedded AnimationClips from FBX files). " +
        "The first state added to a layer automatically becomes the default. " +
        "Example with FBX: unity_add_animation_state(controllerPath=\"...\", stateName=\"Sprint\", " +
        "motionClipPath=\"Assets/Synty/.../A_Sprint_F_Masc.fbx\")")]
    public static async Task<string> AddAnimationState(
        UnityClient client,
        [Description("Path to the .controller asset.")] string controllerPath,
        [Description("Name for the new state.")] string stateName,
        [Description("Layer index (0-based, default 0).")] int layerIndex = 0,
        [Description("Set this state as the default state for the layer.")] bool? setAsDefault = null,
        [Description("Path to a motion clip. Supports .anim files and .fbx files (auto-resolves embedded clips).")] string? motionClipPath = null,
        [Description("When motionClipPath is an .fbx with multiple clips, specify which clip by name. If omitted, uses the first non-preview clip.")] string? motionClipName = null,
        [Description("Optional playback speed (default 1.0).")] float speed = -1f,
        [Description("Optional parameter name to drive speed.")] string? speedParameterName = null)
    {
        if (string.IsNullOrWhiteSpace(controllerPath))
            return ToolErrors.ValidationError("controllerPath is required");
        if (string.IsNullOrWhiteSpace(stateName))
            return ToolErrors.ValidationError("stateName is required");

        var request = new Dictionary<string, object?>
        {
            ["controllerPath"] = controllerPath,
            ["stateName"] = stateName,
            ["layerIndex"] = layerIndex,
            ["speed"] = speed
        };

        if (setAsDefault.HasValue)
            request["setAsDefault"] = setAsDefault.Value ? 1 : 0;

        if (!string.IsNullOrWhiteSpace(motionClipPath))
            request["motionClipPath"] = motionClipPath;

        if (!string.IsNullOrWhiteSpace(motionClipName))
            request["motionClipName"] = motionClipName;

        if (!string.IsNullOrWhiteSpace(speedParameterName))
            request["speedParameterName"] = speedParameterName;

        return await client.AddAnimationStateAsync(request);
    }

    [McpServerTool(Name = "unity_add_animation_transition")]
    [Description("Add a transition between states in an AnimatorController. Supports state-to-state, AnyState, and Entry transitions. " +
        "Entry transitions have NO timing properties (hasExitTime, exitTime, duration are ignored). " +
        "Example: unity_add_animation_transition(controllerPath=\"...\", sourceStateName=\"any\", destinationStateName=\"Jump\", " +
        "hasExitTime=false, conditions=\"[{\\\"parameterName\\\":\\\"IsJumping\\\",\\\"mode\\\":\\\"If\\\",\\\"threshold\\\":0}]\")")]
    public static async Task<string> AddAnimationTransition(
        UnityClient client,
        [Description("Path to the .controller asset.")] string controllerPath,
        [Description("Source state name. Use 'any' for AnyState transitions, 'entry' for Entry transitions.")] string sourceStateName,
        [Description("Destination state name.")] string destinationStateName,
        [Description("Layer index (0-based, default 0).")] int layerIndex = 0,
        [Description("Whether to use exit time. Ignored for entry transitions.")] bool? hasExitTime = null,
        [Description("Exit time value (0-1). Ignored for entry transitions.")] float exitTime = -1f,
        [Description("Transition duration in seconds. Ignored for entry transitions.")] float transitionDuration = -1f,
        [Description("Transition offset. Ignored for entry transitions.")] float transitionOffset = -1f,
        [Description("Whether duration is in fixed time. Ignored for entry transitions.")] bool? hasFixedDuration = null,
        [Description("Whether can transition to self (for AnyState). Ignored for entry transitions.")] bool? canTransitionToSelf = null,
        [Description("Optional JSON array of conditions. Each: {\"parameterName\":\"Speed\",\"mode\":\"Greater|Less|Equals|NotEqual|If|IfNot\",\"threshold\":0.1}.")] string? conditions = null)
    {
        if (string.IsNullOrWhiteSpace(controllerPath))
            return ToolErrors.ValidationError("controllerPath is required");
        if (string.IsNullOrWhiteSpace(sourceStateName))
            return ToolErrors.ValidationError("sourceStateName is required");
        if (string.IsNullOrWhiteSpace(destinationStateName))
            return ToolErrors.ValidationError("destinationStateName is required");

        var request = new Dictionary<string, object?>
        {
            ["controllerPath"] = controllerPath,
            ["sourceStateName"] = sourceStateName,
            ["destinationStateName"] = destinationStateName,
            ["layerIndex"] = layerIndex,
            ["exitTime"] = exitTime,
            ["transitionDuration"] = transitionDuration,
            ["transitionOffset"] = transitionOffset
        };

        if (hasExitTime.HasValue)
            request["hasExitTime"] = hasExitTime.Value ? 1 : 0;
        if (hasFixedDuration.HasValue)
            request["hasFixedDuration"] = hasFixedDuration.Value ? 1 : 0;
        if (canTransitionToSelf.HasValue)
            request["canTransitionToSelf"] = canTransitionToSelf.Value ? 1 : 0;

        if (!string.IsNullOrWhiteSpace(conditions))
            request["conditionsJson"] = conditions;

        return await client.AddAnimationTransitionAsync(request);
    }

    [McpServerTool(Name = "unity_set_animation_parameter")]
    [Description("Add, modify, or remove a parameter on an AnimatorController. " +
        "To remove a parameter, set remove=true. " +
        "Example: unity_set_animation_parameter(controllerPath=\"...\", parameterName=\"Speed\", type=\"Float\", defaultFloat=0.0)")]
    public static async Task<string> SetAnimationParameter(
        UnityClient client,
        [Description("Path to the .controller asset.")] string controllerPath,
        [Description("Name of the parameter.")] string parameterName,
        [Description("Parameter type: 'Bool', 'Float', 'Int', or 'Trigger'. Required when adding/modifying.")] string? type = null,
        [Description("Default float value (for Float type).")] float defaultFloat = 0f,
        [Description("Default int value (for Int type).")] int defaultInt = 0,
        [Description("Default bool value (for Bool type).")] bool? defaultBool = null,
        [Description("Set to true to remove the parameter instead of adding/modifying.")] bool remove = false)
    {
        if (string.IsNullOrWhiteSpace(controllerPath))
            return ToolErrors.ValidationError("controllerPath is required");
        if (string.IsNullOrWhiteSpace(parameterName))
            return ToolErrors.ValidationError("parameterName is required");

        var request = new Dictionary<string, object?>
        {
            ["controllerPath"] = controllerPath,
            ["parameterName"] = parameterName,
            ["defaultFloat"] = defaultFloat,
            ["defaultInt"] = defaultInt,
            ["remove"] = remove ? 1 : 0
        };

        if (!string.IsNullOrWhiteSpace(type))
            request["type"] = type;

        if (defaultBool.HasValue)
            request["defaultBool"] = defaultBool.Value ? 1 : 0;

        return await client.SetAnimationParameterAsync(request);
    }

    [McpServerTool(Name = "unity_create_animation_clip")]
    [Description("Create an AnimationClip (.anim) asset with optional keyframe curves. " +
        "Example: unity_create_animation_clip(path=\"Assets/Animations/Jump.anim\", wrapMode=\"Once\", " +
        "curves=\"[{\\\"relativePath\\\":\\\"\\\",\\\"componentType\\\":\\\"Transform\\\",\\\"propertyName\\\":\\\"localPosition.y\\\"," +
        "\\\"keyframesJson\\\":\\\"[{\\\\\\\"time\\\\\\\":0,\\\\\\\"value\\\\\\\":0},{\\\\\\\"time\\\\\\\":0.5,\\\\\\\"value\\\\\\\":2}]\\\"}]\")")]
    public static async Task<string> CreateAnimationClip(
        UnityClient client,
        [Description("Asset path for the clip (e.g., 'Assets/Animations/Jump.anim').")] string path,
        [Description("Wrap mode: 'Default', 'Once', 'Loop', 'PingPong', or 'ClampForever'.")] string? wrapMode = null,
        [Description("Frame rate (default 60).")] float frameRate = -1f,
        [Description("Optional JSON array of curves. Each: {\"relativePath\":\"\",\"componentType\":\"Transform\",\"propertyName\":\"localPosition.y\"," +
            "\"keyframesJson\":\"[{\\\"time\\\":0,\\\"value\\\":0,\\\"inTangent\\\":0,\\\"outTangent\\\":0}]\"}")] string? curves = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolErrors.ValidationError("path is required");

        var request = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["frameRate"] = frameRate
        };

        if (!string.IsNullOrWhiteSpace(wrapMode))
            request["wrapMode"] = wrapMode;

        if (!string.IsNullOrWhiteSpace(curves))
            request["curvesJson"] = curves;

        return await client.CreateAnimationClipAsync(request);
    }

    [McpServerTool(Name = "unity_get_animator_info")]
    [Description("Inspect an AnimatorController's structure: layers, states, transitions, and parameters. " +
        "Example: unity_get_animator_info(controllerPath=\"Assets/Animations/Player.controller\")")]
    public static async Task<string> GetAnimatorInfo(
        UnityClient client,
        [Description("Path to the .controller asset.")] string controllerPath,
        [Description("Optional layer index to inspect (-1 for all layers, default -1).")] int layerIndex = -1)
    {
        if (string.IsNullOrWhiteSpace(controllerPath))
            return ToolErrors.ValidationError("controllerPath is required");

        return await client.GetAnimatorInfoAsync(controllerPath, layerIndex);
    }

    [McpServerTool(Name = "unity_get_fbx_clips")]
    [Description("List all AnimationClips embedded in an FBX file with metadata (name, length, frameRate, isLooping, isHumanMotion, hasRootMotion). " +
        "Also reports the FBX rig type (Generic/Humanoid). Use this to discover clip names before assigning them via unity_add_animation_state. " +
        "Example: unity_get_fbx_clips(fbxPath=\"Assets/Synty/AnimationBaseLocomotion/.../A_Sprint_F_Masc.fbx\")")]
    public static async Task<string> GetFbxClips(
        UnityClient client,
        [Description("Path to the .fbx file.")] string fbxPath)
    {
        if (string.IsNullOrWhiteSpace(fbxPath))
            return ToolErrors.ValidationError("fbxPath is required");

        return await client.GetFbxClipsAsync(fbxPath);
    }
}
