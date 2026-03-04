using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class UITools
{
    [McpServerTool(Name = "unity_create_canvas")]
    [Description("Create a Canvas with EventSystem and CanvasScaler for UI elements. This is required before adding any UI elements.")]
    public static async Task<string> CreateCanvas(
        UnityClient client,
        [Description("Name for the Canvas GameObject.")] string name = "Canvas",
        [Description("Render mode: 'ScreenSpaceOverlay' (default), 'ScreenSpaceCamera', or 'WorldSpace'.")] string? renderMode = null,
        [Description("Reference resolution as [width, height]. Defaults to [1920, 1080].")] float[]? referenceResolution = null,
        [Description("Set to true to skip creating EventSystem (if one already exists).")] bool skipEventSystem = false)
    {
        var request = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["skipEventSystem"] = skipEventSystem
        };

        if (!string.IsNullOrEmpty(renderMode)) request["renderMode"] = renderMode;
        if (referenceResolution != null) request["referenceResolution"] = referenceResolution;

        return await client.CreateCanvasAsync(request);
    }

    [McpServerTool(Name = "unity_create_ui_element")]
    [Description("Create a UI element. Supported types: Panel, Button, Image, Text, InputField, Slider, Toggle, Dropdown, ScrollView.")]
    public static async Task<string> CreateUIElement(
        UnityClient client,
        [Description("Type of UI element: 'Panel', 'Button', 'Image', 'Text', 'InputField', 'Slider', 'Toggle', 'Dropdown', 'ScrollView'.")] string elementType,
        [Description("Instance ID of the parent Canvas or UI element. If not provided, uses the first Canvas found.")] int? parentId = null,
        [Description("Name for the UI element.")] string? name = null,
        [Description("Color as [r, g, b] or [r, g, b, a] (0-1 range).")] float[]? color = null,
        [Description("Text content (for Button, Text, Toggle).")] string? text = null,
        [Description("Placeholder text (for InputField).")] string? placeholder = null,
        [Description("Font size (for Text elements).")] float? fontSize = null,
        [Description("Text alignment: 'TopLeft', 'Top', 'TopRight', 'Left', 'Center', 'Right', 'BottomLeft', 'Bottom', 'BottomRight'.")] string? alignment = null,
        [Description("Sprite asset path (for Image/Button).")] string? sprite = null,
        [Description("Content type for InputField: 'Standard', 'Autocorrected', 'Integer', 'Decimal', 'Alphanumeric', 'Name', 'Email', 'Password', 'Pin'.")] string? contentType = null,
        [Description("Character limit for InputField (0 = no limit).")] int? characterLimit = null,
        [Description("Minimum value for Slider.")] float? minValue = null,
        [Description("Maximum value for Slider.")] float? maxValue = null,
        [Description("Initial value for Slider.")] float? value = null,
        [Description("Initial checked state for Toggle.")] bool? isOn = null,
        [Description("Options for Dropdown as array of strings.")] string[]? options = null,
        [Description("Enable horizontal scrolling for ScrollView.")] bool? horizontal = null,
        [Description("Enable vertical scrolling for ScrollView.")] bool? vertical = null)
    {
        if (string.IsNullOrWhiteSpace(elementType))
        {
            return ToolErrors.ValidationError("Element type is required");
        }

        var request = new Dictionary<string, object?>
        {
            ["elementType"] = elementType
        };

        if (parentId.HasValue) request["parentId"] = parentId.Value;
        if (!string.IsNullOrEmpty(name)) request["name"] = name;
        if (color != null) request["color"] = color;
        if (!string.IsNullOrEmpty(text)) request["text"] = text;
        if (!string.IsNullOrEmpty(placeholder)) request["placeholder"] = placeholder;
        if (fontSize.HasValue) request["fontSize"] = fontSize.Value;
        if (!string.IsNullOrEmpty(alignment)) request["alignment"] = alignment;
        if (!string.IsNullOrEmpty(sprite)) request["sprite"] = sprite;
        if (!string.IsNullOrEmpty(contentType)) request["contentType"] = contentType;
        if (characterLimit.HasValue) request["characterLimit"] = characterLimit.Value;
        if (minValue.HasValue) request["minValue"] = minValue.Value;
        if (maxValue.HasValue) request["maxValue"] = maxValue.Value;
        if (value.HasValue) request["value"] = value.Value;
        if (isOn.HasValue) request["isOn"] = isOn.Value;
        if (options != null) request["options"] = options;
        if (horizontal.HasValue) request["horizontal"] = horizontal.Value;
        if (vertical.HasValue) request["vertical"] = vertical.Value;

        return await client.CreateUIElementAsync(request);
    }

    [McpServerTool(Name = "unity_create_button")]
    [Description("Create a UI Button with optional text. Shortcut for creating a Button element.")]
    public static async Task<string> CreateButton(
        UnityClient client,
        [Description("Name for the button.")] string name = "Button",
        [Description("Instance ID of the parent Canvas or UI element.")] int? parentId = null,
        [Description("Button text.")] string? text = null,
        [Description("Background color as [r, g, b] or [r, g, b, a] (0-1 range).")] float[]? color = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["elementType"] = "Button",
            ["name"] = name
        };

        if (parentId.HasValue) request["parentId"] = parentId.Value;
        if (!string.IsNullOrEmpty(text)) request["text"] = text;
        if (color != null) request["color"] = color;

        return await client.CreateUIElementAsync(request);
    }

    [McpServerTool(Name = "unity_create_text")]
    [Description("Create a TextMeshPro UI text element.")]
    public static async Task<string> CreateText(
        UnityClient client,
        [Description("Text content to display.")] string text,
        [Description("Name for the text element.")] string name = "Text",
        [Description("Instance ID of the parent Canvas or UI element.")] int? parentId = null,
        [Description("Font size. Defaults to 36.")] float? fontSize = null,
        [Description("Text color as [r, g, b] or [r, g, b, a] (0-1 range).")] float[]? color = null,
        [Description("Text alignment: 'TopLeft', 'Top', 'TopRight', 'Left', 'Center', 'Right', 'BottomLeft', 'Bottom', 'BottomRight'.")] string? alignment = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["elementType"] = "Text",
            ["name"] = name,
            ["text"] = text
        };

        if (parentId.HasValue) request["parentId"] = parentId.Value;
        if (fontSize.HasValue) request["fontSize"] = fontSize.Value;
        if (color != null) request["color"] = color;
        if (!string.IsNullOrEmpty(alignment)) request["alignment"] = alignment;

        return await client.CreateUIElementAsync(request);
    }

    [McpServerTool(Name = "unity_create_image")]
    [Description("Create a UI Image element.")]
    public static async Task<string> CreateImage(
        UnityClient client,
        [Description("Name for the image element.")] string name = "Image",
        [Description("Instance ID of the parent Canvas or UI element.")] int? parentId = null,
        [Description("Image color/tint as [r, g, b] or [r, g, b, a] (0-1 range).")] float[]? color = null,
        [Description("Path to sprite asset to display.")] string? sprite = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["elementType"] = "Image",
            ["name"] = name
        };

        if (parentId.HasValue) request["parentId"] = parentId.Value;
        if (color != null) request["color"] = color;
        if (!string.IsNullOrEmpty(sprite)) request["sprite"] = sprite;

        return await client.CreateUIElementAsync(request);
    }

    [McpServerTool(Name = "unity_create_panel")]
    [Description("Create a UI Panel (container with background image).")]
    public static async Task<string> CreatePanel(
        UnityClient client,
        [Description("Name for the panel.")] string name = "Panel",
        [Description("Instance ID of the parent Canvas or UI element.")] int? parentId = null,
        [Description("Background color as [r, g, b] or [r, g, b, a] (0-1 range).")] float[]? color = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["elementType"] = "Panel",
            ["name"] = name
        };

        if (parentId.HasValue) request["parentId"] = parentId.Value;
        if (color != null) request["color"] = color;

        return await client.CreateUIElementAsync(request);
    }

    [McpServerTool(Name = "unity_create_input_field")]
    [Description("Create a TMP InputField for text input.")]
    public static async Task<string> CreateInputField(
        UnityClient client,
        [Description("Name for the input field.")] string name = "InputField",
        [Description("Instance ID of the parent Canvas or UI element.")] int? parentId = null,
        [Description("Placeholder text.")] string? placeholder = null,
        [Description("Content type: 'Standard', 'Autocorrected', 'Integer', 'Decimal', 'Alphanumeric', 'Name', 'Email', 'Password', 'Pin'.")] string? contentType = null,
        [Description("Character limit (0 = no limit).")] int? characterLimit = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["elementType"] = "InputField",
            ["name"] = name
        };

        if (parentId.HasValue) request["parentId"] = parentId.Value;
        if (!string.IsNullOrEmpty(placeholder)) request["placeholder"] = placeholder;
        if (!string.IsNullOrEmpty(contentType)) request["contentType"] = contentType;
        if (characterLimit.HasValue) request["characterLimit"] = characterLimit.Value;

        return await client.CreateUIElementAsync(request);
    }

    [McpServerTool(Name = "unity_create_slider")]
    [Description("Create a UI Slider element.")]
    public static async Task<string> CreateSlider(
        UnityClient client,
        [Description("Name for the slider.")] string name = "Slider",
        [Description("Instance ID of the parent Canvas or UI element.")] int? parentId = null,
        [Description("Minimum value. Defaults to 0.")] float? minValue = null,
        [Description("Maximum value. Defaults to 1.")] float? maxValue = null,
        [Description("Initial value. Defaults to 0.5.")] float? value = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["elementType"] = "Slider",
            ["name"] = name
        };

        if (parentId.HasValue) request["parentId"] = parentId.Value;
        if (minValue.HasValue) request["minValue"] = minValue.Value;
        if (maxValue.HasValue) request["maxValue"] = maxValue.Value;
        if (value.HasValue) request["value"] = value.Value;

        return await client.CreateUIElementAsync(request);
    }

    [McpServerTool(Name = "unity_modify_rect_transform")]
    [Description("Modify the RectTransform of a UI element to change its position, size, anchors, and pivot.")]
    public static async Task<string> ModifyRectTransform(
        UnityClient client,
        [Description("Instance ID of the UI element.")] int instanceId,
        [Description("Anchored position as [x, y].")] float[]? anchoredPosition = null,
        [Description("Size delta as [width, height].")] float[]? sizeDelta = null,
        [Description("Anchor preset: 'TopLeft', 'TopCenter', 'TopRight', 'MiddleLeft', 'Center', 'MiddleRight', 'BottomLeft', 'BottomCenter', 'BottomRight', 'StretchLeft', 'StretchCenter', 'StretchRight', 'StretchTop', 'StretchMiddle', 'StretchBottom', 'Stretch'.")] string? anchorPreset = null,
        [Description("Anchor min as [x, y] (0-1 range). Ignored if anchorPreset is set.")] float[]? anchorMin = null,
        [Description("Anchor max as [x, y] (0-1 range). Ignored if anchorPreset is set.")] float[]? anchorMax = null,
        [Description("Pivot point as [x, y] (0-1 range).")] float[]? pivot = null,
        [Description("Offset from anchor min as [left, bottom].")] float[]? offsetMin = null,
        [Description("Offset from anchor max as [right, top].")] float[]? offsetMax = null)
    {
        var request = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(anchorPreset)) request["anchorPreset"] = anchorPreset;

        if (anchoredPosition != null && anchoredPosition.Length >= 2)
            request["anchoredPosition"] = new { x = anchoredPosition[0], y = anchoredPosition[1] };
        if (sizeDelta != null && sizeDelta.Length >= 2)
            request["sizeDelta"] = new { x = sizeDelta[0], y = sizeDelta[1] };
        if (anchorMin != null && anchorMin.Length >= 2)
            request["anchorMin"] = new { x = anchorMin[0], y = anchorMin[1] };
        if (anchorMax != null && anchorMax.Length >= 2)
            request["anchorMax"] = new { x = anchorMax[0], y = anchorMax[1] };
        if (pivot != null && pivot.Length >= 2)
            request["pivot"] = new { x = pivot[0], y = pivot[1] };
        if (offsetMin != null && offsetMin.Length >= 2)
            request["offsetMin"] = new { x = offsetMin[0], y = offsetMin[1] };
        if (offsetMax != null && offsetMax.Length >= 2)
            request["offsetMax"] = new { x = offsetMax[0], y = offsetMax[1] };

        return await client.ModifyRectTransformAsync(instanceId, request);
    }

    [McpServerTool(Name = "unity_modify_tmp_text")]
    [Description("Modify the properties of a TextMeshPro UI text element.")]
    public static async Task<string> ModifyTMPText(
        UnityClient client,
        [Description("Instance ID of the text element.")] int instanceId,
        [Description("New text content.")] string? text = null,
        [Description("Font size.")] float? fontSize = null,
        [Description("Text alignment: 'TopLeft', 'Top', 'TopRight', 'Left', 'Center', 'Right', 'BottomLeft', 'Bottom', 'BottomRight'.")] string? alignment = null,
        [Description("Text color as [r, g, b] or [r, g, b, a] (0-1 range).")] float[]? color = null,
        [Description("Enable rich text parsing.")] bool? richText = null,
        [Description("Enable word wrapping.")] bool? wordWrapping = null,
        [Description("Path to TMP_FontAsset.")] string? fontAsset = null)
    {
        var request = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(text)) request["text"] = text;
        if (fontSize.HasValue) request["fontSize"] = fontSize.Value;
        if (!string.IsNullOrEmpty(alignment)) request["alignment"] = alignment;
        if (color != null) request["color"] = color;
        if (richText.HasValue) request["richText"] = richText.Value;
        if (wordWrapping.HasValue) request["wordWrapping"] = wordWrapping.Value;
        if (!string.IsNullOrEmpty(fontAsset)) request["fontAsset"] = fontAsset;

        return await client.ModifyTMPTextAsync(instanceId, request);
    }

    [McpServerTool(Name = "unity_set_ui_color")]
    [Description("Set the color of a UI element (Image or TextMeshPro).")]
    public static async Task<string> SetUIColor(
        UnityClient client,
        [Description("Instance ID of the UI element.")] int instanceId,
        [Description("Color as [r, g, b] or [r, g, b, a] (0-1 range).")] float[] color)
    {
        if (color == null || color.Length < 3)
        {
            return ToolErrors.ValidationError("Color array [r, g, b] or [r, g, b, a] is required");
        }

        return await client.SetUIColorAsync(instanceId, color);
    }
}
