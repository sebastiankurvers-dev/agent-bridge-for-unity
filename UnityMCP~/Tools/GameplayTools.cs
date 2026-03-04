using System;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class GameplayTools
{
    [McpServerTool(Name = "unity_get_runtime_values")]
    [Description("Read runtime field values from a component on a GameObject. Returns structured JSON values (not .ToString()). " +
        "Useful for verifying gameplay state during play mode, e.g. checking isJumping, currentLane, forwardSpeed after invoking a method. " +
        "Works in both edit and play mode. Example: unity_get_runtime_values(instanceId=1234, componentType=\"PlayerController\", fieldNames=[\"isJumping\",\"currentLane\"])")]
    public static async Task<string> GetRuntimeValues(
        UnityClient client,
        [Description("The instance ID of the GameObject.")] int instanceId,
        [Description("The component type name (e.g., 'PlayerController', 'Rigidbody').")] string componentType,
        [Description("Optional array of specific field/property names to read. Empty or omitted returns all visible fields.")] string[]? fieldNames = null,
        [Description("Include private fields that have [SerializeField] attribute. Default true.")] bool includePrivate = true,
        [Description("Include C# properties (not just fields). Default false.")] bool includeProperties = false)
    {
        if (string.IsNullOrWhiteSpace(componentType))
        {
            return ToolErrors.ValidationError("componentType is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["componentType"] = componentType,
            ["includePrivate"] = includePrivate ? 1 : 0,
            ["includeProperties"] = includeProperties ? 1 : 0
        };

        if (fieldNames != null && fieldNames.Length > 0)
        {
            request["fieldNames"] = fieldNames;
        }

        return await client.GetRuntimeValuesAsync(request);
    }

    [McpServerTool(Name = "unity_set_runtime_fields")]
    [Description("Set runtime field/property values on a component during play mode without using execute_csharp. " +
        "Accepts a JSON array of patches: [{\"name\":\"cameraPitch\",\"value\":74},{\"name\":\"cameraOrthographicSize\",\"value\":10.8}]. " +
        "Supports private fields (allowPrivate=true) and writable properties (allowProperties=true).")]
    public static async Task<string> SetRuntimeFields(
        UnityClient client,
        [Description("The instance ID of the target GameObject. Use 0 if resolving by gameObjectName.")] int instanceId,
        [Description("Optional GameObject name when instanceId is not known.")] string gameObjectName,
        [Description("Component type name (e.g. 'EnemyController').")] string componentType,
        [Description("JSON array of runtime patches: [{\"name\":\"fieldName\",\"value\":123,\"typeHint\":\"\"}].")] string fieldsJson,
        [Description("Allow private fields to be patched. Default true.")] bool allowPrivate = true,
        [Description("Allow writable properties to be patched. Default true.")] bool allowProperties = true)
    {
        if (instanceId == 0 && string.IsNullOrWhiteSpace(gameObjectName))
        {
            return ToolErrors.ValidationError("Either instanceId or gameObjectName is required");
        }
        if (string.IsNullOrWhiteSpace(componentType))
        {
            return ToolErrors.ValidationError("componentType is required");
        }
        if (string.IsNullOrWhiteSpace(fieldsJson))
        {
            return ToolErrors.ValidationError("fieldsJson is required");
        }

        JsonElement fieldsElement;
        try
        {
            fieldsElement = JsonSerializer.Deserialize<JsonElement>(fieldsJson);
        }
        catch (Exception ex)
        {
            return ToolErrors.ValidationError($"Invalid fieldsJson: {ex.Message}");
        }

        if (fieldsElement.ValueKind != JsonValueKind.Array)
        {
            return ToolErrors.ValidationError("fieldsJson must be a JSON array");
        }

        var request = new Dictionary<string, object?>
        {
            ["componentType"] = componentType,
            ["fields"] = fieldsElement,
            ["allowPrivate"] = allowPrivate ? 1 : 0,
            ["allowProperties"] = allowProperties ? 1 : 0
        };

        if (instanceId != 0)
        {
            request["instanceId"] = instanceId;
        }
        if (!string.IsNullOrWhiteSpace(gameObjectName))
        {
            request["gameObjectName"] = gameObjectName;
        }

        return await client.SetRuntimeFieldsAsync(request);
    }

    [McpServerTool(Name = "unity_invoke_method")]
    [Description("Call a method on a component via reflection. Only works during play mode. " +
        "Use this to simulate player actions like Jump, ChangeLane, Slide, etc. " +
        "Supports optional before/after screenshots to capture visual state around the invocation. " +
        "Example: unity_invoke_method(instanceId=1234, componentType=\"PlayerController\", methodName=\"Jump\") " +
        "Example with screenshots: unity_invoke_method(instanceId=1234, componentType=\"PlayerController\", methodName=\"TogglePhase\", screenshotBefore=true, screenshotAfter=true) " +
        "If the method is not found, returns a list of available methods on the component.")]
    public static async Task<string> InvokeMethod(
        UnityClient client,
        [Description("The instance ID of the GameObject.")] int instanceId,
        [Description("The component type name (e.g., 'PlayerController').")] string componentType,
        [Description("The method name to call (e.g., 'Jump', 'ChangeLane').")] string methodName,
        [Description("Optional JSON-encoded arguments array. Each element is a string that will be parsed to the parameter type.")] string[]? args = null,
        [Description("Capture a screenshot before the method invocation. Returns beforeScreenshotHandle in the response.")] bool screenshotBefore = false,
        [Description("Capture a screenshot after the method invocation. Returns afterScreenshotHandle in the response.")] bool screenshotAfter = false,
        [Description("View to capture screenshots from: 'game' or 'scene'. Default 'game'.")] string screenshotView = "game",
        [Description("Optional screenshot width in pixels.")] int screenshotWidth = 0,
        [Description("Optional screenshot height in pixels.")] int screenshotHeight = 0)
    {
        if (string.IsNullOrWhiteSpace(componentType))
        {
            return ToolErrors.ValidationError("componentType is required");
        }

        if (string.IsNullOrWhiteSpace(methodName))
        {
            return ToolErrors.ValidationError("methodName is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["componentType"] = componentType,
            ["methodName"] = methodName
        };

        if (args != null && args.Length > 0)
        {
            request["args"] = args;
        }

        if (screenshotBefore)
            request["screenshotBefore"] = 1;
        if (screenshotAfter)
            request["screenshotAfter"] = 1;
        if (screenshotBefore || screenshotAfter)
        {
            request["screenshotView"] = screenshotView;
            if (screenshotWidth > 0) request["screenshotWidth"] = screenshotWidth;
            if (screenshotHeight > 0) request["screenshotHeight"] = screenshotHeight;
        }

        return await client.InvokeMethodAsync(request);
    }

    [McpServerTool(Name = "unity_invoke_sequence")]
    [Description("Execute multiple method invocations with timing delays in one round-trip. " +
        "Useful for rapid-toggle testing, gameplay verification sequences, and multi-step action chains. " +
        "Each step specifies a componentType, methodName, optional args, and an optional delayMs (delay BEFORE the step). " +
        "Max 20 steps, max 10000ms total delay. Only works during play mode. " +
        "Example: unity_invoke_sequence(instanceId=1234, steps='[{\"componentType\":\"PlayerController\",\"methodName\":\"TogglePhase\",\"delayMs\":0},{\"componentType\":\"PlayerController\",\"methodName\":\"TogglePhase\",\"delayMs\":100}]', screenshotView=\"game\")")]
    public static async Task<string> InvokeSequence(
        UnityClient client,
        [Description("The instance ID of the GameObject.")] int instanceId,
        [Description("JSON array of step objects. Each step: {componentType: string, methodName: string, delayMs?: number, args?: string[]}. delayMs is the delay BEFORE executing this step.")] string steps,
        [Description("Capture a screenshot after the sequence completes. Set to 'game' or 'scene', or omit for no screenshot.")] string screenshotView = "",
        [Description("Optional screenshot width in pixels.")] int screenshotWidth = 0,
        [Description("Optional screenshot height in pixels.")] int screenshotHeight = 0,
        [Description("Timeout in milliseconds. Default 15000ms to accommodate delays.")] int timeoutMs = 15000)
    {
        if (string.IsNullOrWhiteSpace(steps))
        {
            return ToolErrors.ValidationError("steps is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["stepsJson"] = steps
        };

        if (!string.IsNullOrWhiteSpace(screenshotView))
            request["screenshotView"] = screenshotView;
        if (screenshotWidth > 0) request["screenshotWidth"] = screenshotWidth;
        if (screenshotHeight > 0) request["screenshotHeight"] = screenshotHeight;

        return await client.InvokeSequenceAsync(request, timeoutMs > 0 ? timeoutMs : null);
    }
}
