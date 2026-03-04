using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class SemanticCompareTools
{
    [McpServerTool(Name = "unity_compare_images_semantic")]
    [Description("Compare semantic regions between a reference image and current image/screenshot. Returns per-region luminance/saturation/color-shift metrics and suggestions.")]
    public static async Task<string> CompareImagesSemantic(
        UnityClient client,
        [Description("JSON array of normalized regions: [{\"name\":\"road\",\"x\":0.1,\"y\":0.5,\"w\":0.8,\"h\":0.3}]")] string regionsJson,
        [Description("Optional reference image handle.")] string? referenceImageHandle = null,
        [Description("Optional reference image base64. Used when handle is not provided.")] string? referenceImageBase64 = null,
        [Description("Absolute path to a local reference image file (png/jpg). Avoids large base64 in tool params.")] string? referenceFilePath = null,
        [Description("Optional current image handle.")] string? currentImageHandle = null,
        [Description("Optional current image base64. Used when handle is not provided.")] string? currentImageBase64 = null,
        [Description("Capture current screenshot automatically if current image isn't provided.")] bool captureCurrentScreenshot = true,
        [Description("Screenshot view when captureCurrentScreenshot=true: game or scene.")] string screenshotView = "scene",
        [Description("Optional screenshot width.")] int width = 0,
        [Description("Optional screenshot height.")] int height = 0,
        [Description("Include resolved image handles in response.")] bool includeImageHandles = true,
        [Description("Timeout for semantic compare route (ms). Default 20000.")] int timeoutMs = 20000)
    {
        if (string.IsNullOrWhiteSpace(regionsJson))
        {
            return ToolErrors.ValidationError("regionsJson is required");
        }

        try
        {
            var regionsElement = JsonSerializer.Deserialize<JsonElement>(regionsJson);
            if (regionsElement.ValueKind != JsonValueKind.Array)
            {
                return ToolErrors.ValidationError("regionsJson must be a JSON array");
            }

            var request = new Dictionary<string, object?>
            {
                ["regions"] = regionsElement,
                ["captureCurrentScreenshot"] = captureCurrentScreenshot ? 1 : 0,
                ["screenshotView"] = screenshotView,
                ["screenshotWidth"] = width,
                ["screenshotHeight"] = height,
                ["includeImageHandles"] = includeImageHandles ? 1 : 0
            };

            // Resolve filePath to base64 if provided
            if (!string.IsNullOrWhiteSpace(referenceFilePath) && string.IsNullOrWhiteSpace(referenceImageBase64) && string.IsNullOrWhiteSpace(referenceImageHandle))
            {
                if (!File.Exists(referenceFilePath))
                    return ToolErrors.ValidationError($"File not found: {referenceFilePath}");
                try
                {
                    var bytes = await File.ReadAllBytesAsync(referenceFilePath);
                    referenceImageBase64 = Convert.ToBase64String(bytes);
                }
                catch (Exception ex)
                {
                    return ToolErrors.ValidationError($"Failed to read file: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(referenceImageHandle)) request["referenceImageHandle"] = referenceImageHandle;
            if (!string.IsNullOrWhiteSpace(referenceImageBase64)) request["referenceImageBase64"] = referenceImageBase64;
            if (!string.IsNullOrWhiteSpace(currentImageHandle)) request["currentImageHandle"] = currentImageHandle;
            if (!string.IsNullOrWhiteSpace(currentImageBase64)) request["currentImageBase64"] = currentImageBase64;

            return await client.CompareImagesSemanticAsync(request, timeoutMs);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = $"Invalid regionsJson: {ex.Message}" });
        }
    }
}
