using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class UIToolkitTools
{
    // ==================== Asset Management ====================

    [McpServerTool(Name = "unity_create_ui_document")]
    [Description("Create a GameObject with UIDocument component for UI Toolkit. Optionally link PanelSettings and UXML source asset.")]
    public static async Task<string> CreateUIDocument(
        UnityClient client,
        [Description("Name for the UIDocument GameObject.")] string name = "UIDocument",
        [Description("Path to existing PanelSettings asset (e.g. 'Assets/UI/PanelSettings.asset').")] string? panelSettingsPath = null,
        [Description("Path to UXML file to load as visual tree (e.g. 'Assets/UI/MainMenu.uxml').")] string? uxmlPath = null,
        [Description("Sorting order for the UIDocument (higher = renders on top).")] int sortingOrder = 0)
    {
        var request = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["sortingOrder"] = sortingOrder
        };

        if (!string.IsNullOrEmpty(panelSettingsPath)) request["panelSettingsPath"] = panelSettingsPath;
        if (!string.IsNullOrEmpty(uxmlPath)) request["uxmlPath"] = uxmlPath;

        return await client.CreateUIDocumentAsync(request);
    }

    [McpServerTool(Name = "unity_create_panel_settings")]
    [Description("Create a PanelSettings asset for UI Toolkit screen scaling. Required before creating UIDocuments for runtime UI.")]
    public static async Task<string> CreatePanelSettings(
        UnityClient client,
        [Description("Asset path for the PanelSettings (e.g. 'Assets/UI/PanelSettings.asset').")] string path,
        [Description("Scale mode: 'ConstantPixelSize', 'ConstantPhysicalSize', or 'ScaleWithScreenSize' (default).")] string? scaleMode = null,
        [Description("Reference resolution as [width, height]. Defaults to [1080, 1920] (portrait mobile).")] float[]? referenceResolution = null,
        [Description("Screen match mode: 'MatchWidthOrHeight' (default), 'Expand', or 'Shrink'.")] string? screenMatchMode = null,
        [Description("Width/height match factor (0-1). 0=match width, 1=match height, 0.5=balanced.")] float? match = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolErrors.ValidationError("PanelSettings path is required");

        var request = new Dictionary<string, object?>
        {
            ["path"] = path
        };

        if (!string.IsNullOrEmpty(scaleMode)) request["scaleMode"] = scaleMode;
        if (referenceResolution != null) request["referenceResolution"] = referenceResolution;
        if (!string.IsNullOrEmpty(screenMatchMode)) request["screenMatchMode"] = screenMatchMode;
        if (match.HasValue) request["match"] = match.Value;

        return await client.CreateUIToolkitPanelSettingsAsync(request);
    }

    [McpServerTool(Name = "unity_get_panel_settings")]
    [Description("Inspect an existing PanelSettings asset configuration (scale mode, reference resolution, match factor).")]
    public static async Task<string> GetPanelSettings(
        UnityClient client,
        [Description("Path to the PanelSettings asset.")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolErrors.ValidationError("PanelSettings path is required");

        return await client.GetUIToolkitPanelSettingsAsync(path);
    }

    [McpServerTool(Name = "unity_create_uxml")]
    [Description("Create a UXML layout file for UI Toolkit. UXML defines the structure and hierarchy of UI elements using XML syntax.")]
    public static async Task<string> CreateUXML(
        UnityClient client,
        [Description("File path for the UXML file (e.g. 'Assets/UI/MainMenu.uxml').")] string path,
        [Description("UXML content as XML string. Must include <ui:UXML> root element with xmlns declarations.")] string content)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolErrors.ValidationError("UXML path is required");
        if (string.IsNullOrWhiteSpace(content))
            return ToolErrors.ValidationError("UXML content is required");

        return await client.CreateUXMLAsync(new { path, content });
    }

    [McpServerTool(Name = "unity_create_uss")]
    [Description("Create a USS stylesheet file for UI Toolkit. USS is similar to CSS and controls the visual appearance of UXML elements.")]
    public static async Task<string> CreateUSS(
        UnityClient client,
        [Description("File path for the USS file (e.g. 'Assets/UI/MainMenu.uss').")] string path,
        [Description("USS content as CSS-like string. Uses selectors like #name, .class, Type to target elements.")] string content)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolErrors.ValidationError("USS path is required");
        if (string.IsNullOrWhiteSpace(content))
            return ToolErrors.ValidationError("USS content is required");

        return await client.CreateUSSAsync(new { path, content });
    }

    [McpServerTool(Name = "unity_modify_uxml")]
    [Description("Replace the content of an existing UXML layout file.")]
    public static async Task<string> ModifyUXML(
        UnityClient client,
        [Description("Path to the existing UXML file.")] string path,
        [Description("New UXML content to replace the file with.")] string content)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolErrors.ValidationError("UXML path is required");
        if (string.IsNullOrWhiteSpace(content))
            return ToolErrors.ValidationError("UXML content is required");

        return await client.ModifyUXMLAsync(new { path, content });
    }

    [McpServerTool(Name = "unity_modify_uss")]
    [Description("Replace the content of an existing USS stylesheet file.")]
    public static async Task<string> ModifyUSS(
        UnityClient client,
        [Description("Path to the existing USS file.")] string path,
        [Description("New USS content to replace the file with.")] string content)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolErrors.ValidationError("USS path is required");
        if (string.IsNullOrWhiteSpace(content))
            return ToolErrors.ValidationError("USS content is required");

        return await client.ModifyUSSAsync(new { path, content });
    }

    [McpServerTool(Name = "unity_read_uxml")]
    [Description("Read the contents of a UXML layout file.")]
    public static async Task<string> ReadUXML(
        UnityClient client,
        [Description("Path to the UXML file.")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolErrors.ValidationError("UXML path is required");

        return await client.ReadUXMLAsync(path);
    }

    [McpServerTool(Name = "unity_read_uss")]
    [Description("Read the contents of a USS stylesheet file.")]
    public static async Task<string> ReadUSS(
        UnityClient client,
        [Description("Path to the USS file.")] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ToolErrors.ValidationError("USS path is required");

        return await client.ReadUSSAsync(path);
    }

    // ==================== Runtime Visual Tree ====================

    [McpServerTool(Name = "unity_get_visual_tree")]
    [Description("Inspect the visual element hierarchy of a UIDocument with optional pagination and compact projection for token-efficient inspection.")]
    public static async Task<string> GetVisualTree(
        UnityClient client,
        [Description("Instance ID of the UIDocument GameObject.")] int instanceId,
        [Description("Maximum depth to traverse (-1 = unlimited).")] int maxDepth = -1,
        [Description("Include resolved style information for each element.")] bool includeStyles = false,
        [Description("Skip this many elements from the start (pagination).")] int offset = 0,
        [Description("Maximum elements to return (-1 = unlimited). Default 200 for token efficiency.")] int limit = 200,
        [Description("If true, return compact element projection (type/name/depth/childCount/visible only).")] bool compact = true,
        [Description("Override bounds inclusion (null = auto based on compact).")] bool? includeBounds = null,
        [Description("Override USS classes inclusion (null = auto based on compact).")] bool? includeClasses = null,
        [Description("Override text inclusion (null = auto based on compact).")] bool? includeText = null)
    {
        offset = Math.Max(0, offset);
        limit = limit < 0 ? -1 : Math.Clamp(limit, 1, 5000);
        return await client.GetVisualTreeAsync(instanceId, maxDepth, includeStyles, offset, limit, compact, includeBounds, includeClasses, includeText);
    }

    [McpServerTool(Name = "unity_query_visual_elements")]
    [Description("Query visual elements in a UIDocument by name, USS class, or type name, with optional pagination and compact projection.")]
    public static async Task<string> QueryVisualElements(
        UnityClient client,
        [Description("Instance ID of the UIDocument GameObject.")] int instanceId,
        [Description("Element name to search for (matches element.name).")] string? name = null,
        [Description("USS class to search for (matches .className).")] string? className = null,
        [Description("Type name to filter by (e.g. 'Button', 'Label', 'TextField').")] string? typeName = null,
        [Description("Include resolved style information for each matched element.")] bool includeStyles = false,
        [Description("Skip this many matched elements from the start (pagination).")] int offset = 0,
        [Description("Maximum matched elements to return (-1 = unlimited). Default 200 for token efficiency.")] int limit = 200,
        [Description("If true, return compact element projection (type/name/childCount/visible only).")] bool compact = true,
        [Description("Override bounds inclusion (null = auto based on compact).")] bool? includeBounds = null,
        [Description("Override USS classes inclusion (null = auto based on compact).")] bool? includeClasses = null,
        [Description("Override text inclusion (null = auto based on compact).")] bool? includeText = null)
    {
        offset = Math.Max(0, offset);
        limit = limit < 0 ? -1 : Math.Clamp(limit, 1, 5000);
        return await client.QueryVisualElementsAsync(instanceId, name, className, typeName, includeStyles, offset, limit, compact, includeBounds, includeClasses, includeText);
    }

    [McpServerTool(Name = "unity_modify_visual_element")]
    [Description("Modify properties of a visual element at runtime: text, tooltip, visibility, USS classes, and inline styles.")]
    public static async Task<string> ModifyVisualElement(
        UnityClient client,
        [Description("Instance ID of the UIDocument GameObject.")] int instanceId,
        [Description("Name of the element to find and modify.")] string? elementName = null,
        [Description("USS class of the element to find.")] string? className = null,
        [Description("Type name of the element to find (e.g. 'Button').")] string? typeName = null,
        [Description("New text content (for Label, Button, etc.).")] string? text = null,
        [Description("New tooltip text.")] string? tooltip = null,
        [Description("Set element visibility (true/false).")] bool? visible = null,
        [Description("USS classes to add to the element.")] string[]? addClasses = null,
        [Description("USS classes to remove from the element.")] string[]? removeClasses = null,
        [Description("Inline style properties as JSON dict (e.g. {\"background-color\":\"#FF0000\",\"width\":\"200px\"}).")] string? styleJson = null)
    {
        var request = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(elementName)) request["elementName"] = elementName;
        if (!string.IsNullOrEmpty(className)) request["className"] = className;
        if (!string.IsNullOrEmpty(typeName)) request["typeName"] = typeName;
        if (text != null) request["text"] = text;
        if (tooltip != null) request["tooltip"] = tooltip;
        if (visible.HasValue) request["visible"] = visible.Value ? 1 : 0;
        if (addClasses != null) request["addClasses"] = addClasses;
        if (removeClasses != null) request["removeClasses"] = removeClasses;
        if (!string.IsNullOrEmpty(styleJson)) request["styleJson"] = styleJson;

        return await client.ModifyVisualElementAsync(instanceId, request);
    }

    [McpServerTool(Name = "unity_create_visual_element")]
    [Description("Add a new visual element to the UIDocument's visual tree at runtime. Supports all standard UI Toolkit element types.")]
    public static async Task<string> CreateVisualElement(
        UnityClient client,
        [Description("Instance ID of the UIDocument GameObject.")] int instanceId,
        [Description("Type of element to create: 'VisualElement', 'Button', 'Label', 'TextField', 'Toggle', 'Slider', 'ScrollView', 'Image', 'DropdownField', 'Foldout', 'ProgressBar', 'RadioButton', 'GroupBox'.")] string elementType = "VisualElement",
        [Description("Name/selector for the parent element to add to. Searches by name, then class. Empty = add to root.")] string? parentSelector = null,
        [Description("Name for the new element.")] string? name = null,
        [Description("USS classes to add to the new element.")] string[]? classes = null,
        [Description("Text content (for Label, Button, etc.).")] string? text = null,
        [Description("Inline style properties as JSON dict.")] string? styleJson = null,
        [Description("Insert index within parent (-1 = append at end).")] int insertIndex = -1)
    {
        var request = new Dictionary<string, object?>
        {
            ["elementType"] = elementType,
            ["insertIndex"] = insertIndex
        };

        if (!string.IsNullOrEmpty(parentSelector)) request["parentSelector"] = parentSelector;
        if (!string.IsNullOrEmpty(name)) request["name"] = name;
        if (classes != null) request["classes"] = classes;
        if (text != null) request["text"] = text;
        if (!string.IsNullOrEmpty(styleJson)) request["styleJson"] = styleJson;

        return await client.CreateVisualElementAsync(instanceId, request);
    }

    // ==================== Migration ====================

    [McpServerTool(Name = "unity_migrate_ugui_to_uitoolkit")]
    [Description("Convert a uGUI Canvas hierarchy to UI Toolkit UXML + USS files. Analyzes the Canvas and generates equivalent UI Toolkit markup and styles.")]
    public static async Task<string> MigrateUGUIToUIToolkit(
        UnityClient client,
        [Description("Instance ID of the Canvas GameObject to migrate.")] int instanceId,
        [Description("Output path for the UXML file (default: Assets/UI/{CanvasName}.uxml).")] string? outputUxmlPath = null,
        [Description("Output path for the USS file (default: Assets/UI/{CanvasName}.uss).")] string? outputUssPath = null)
    {
        var request = new Dictionary<string, object?>();

        if (!string.IsNullOrEmpty(outputUxmlPath)) request["outputUxmlPath"] = outputUxmlPath;
        if (!string.IsNullOrEmpty(outputUssPath)) request["outputUssPath"] = outputUssPath;

        return await client.MigrateUGUIToUIToolkitAsync(instanceId, request);
    }
}
