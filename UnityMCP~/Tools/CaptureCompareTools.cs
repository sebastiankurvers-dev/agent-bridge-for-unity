using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

public static class ConvergenceTracker
{
    private const int MaxHistory = 10;
    private const float PlateauThreshold = 0.02f;
    private const int PlateauMinRuns = 3;

    private static readonly Dictionary<string, List<float>> _history = new();

    public static string DeriveKey(string? handle, string? base64)
    {
        if (!string.IsNullOrWhiteSpace(handle))
            return handle;
        if (!string.IsNullOrWhiteSpace(base64))
        {
            var snippet = base64.Length > 64 ? base64[..64] : base64;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(snippet));
            return Convert.ToHexString(hash)[..16];
        }
        return "__default__";
    }

    public static void Record(string key, float score)
    {
        if (!_history.TryGetValue(key, out var list))
        {
            list = new List<float>();
            _history[key] = list;
        }
        list.Add(score);
        if (list.Count > MaxHistory)
            list.RemoveAt(0);
    }

    public static Dictionary<string, object> GetMetadata(string key)
    {
        var meta = new Dictionary<string, object>();
        if (!_history.TryGetValue(key, out var list) || list.Count == 0)
        {
            meta["callCount"] = 0;
            meta["history"] = Array.Empty<float>();
            meta["plateau"] = false;
            return meta;
        }

        meta["callCount"] = list.Count;
        meta["history"] = list.ToArray();

        // Detect plateau: last PlateauMinRuns scores within threshold
        bool plateau = false;
        int plateauSince = 0;
        if (list.Count >= PlateauMinRuns)
        {
            // Find how far back the plateau extends
            float latest = list[^1];
            int run = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (MathF.Abs(list[i] - latest) <= PlateauThreshold)
                    run++;
                else
                    break;
            }
            if (run >= PlateauMinRuns)
            {
                plateau = true;
                plateauSince = run;
            }
        }

        meta["plateau"] = plateau;
        if (plateau)
        {
            meta["plateauSince"] = plateauSince;
            float avg = list.Skip(list.Count - plateauSince).Average();
            meta["suggestion"] = $"Similarity has plateaued at ~{avg:F2} for {plateauSince} calls. "
                + "Consider accepting current result or trying a different approach (camera angle, lighting setup, major object changes).";
        }

        return meta;
    }
}

[McpServerToolType]
public static class CaptureCompareTools
{
    [McpServerTool(Name = "unity_capture_and_compare")]
    [Description("Capture a screenshot and compare it against a reference image in one call. "
        + "Returns the screenshot as visual content, similarity metrics, and a 'composition' block with spatial analysis "
        + "(horizon position, visual center of mass, vertical luminance bands, edge density per third). "
        + "Also returns 'suggestions' with actionable CAMERA/FRAMING/LAYOUT/LIGHTING/COLOR guidance.\n\n"
        + "IMPORTANT: Work on structure FIRST (object placement, camera angle, major shapes) before tweaking colors/materials. "
        + "Read the suggestions — they tell you exactly what's wrong spatially (e.g., 'horizon too low', 'subject too far left', "
        + "'not enough detail in top third'). Fix camera and layout issues before color adjustments.")]
    public static async Task<IList<AIContent>> CaptureAndCompare(
        UnityClient client,
        [Description("Reference image handle from unity_screenshot/unity_store_image_handle.")] string? referenceImageHandle = null,
        [Description("Reference image base64 (raw or data URI). Use referenceImageHandle when available.")] string? referenceImageBase64 = null,
        [Description("Absolute path to a local reference image file (png/jpg). Avoids large base64 in tool params.")] string? referenceFilePath = null,
        [Description("Screenshot source: 'scene' (default) or 'game'.")] string screenshotView = "scene",
        [Description("Custom screenshot width in pixels (64-1920). Both width and height must be set.")] int screenshotWidth = 0,
        [Description("Custom screenshot height in pixels (64-1920). Both width and height must be set.")] int screenshotHeight = 0,
        [Description("Maximum analysis dimension for comparison (64-1024).")] int downsampleMaxSize = 256,
        [Description("Heatmap grid size for comparison (2-32).")] int gridSize = 8,
        [Description("Threshold for changed pixel ratio metric (0-1).")] float changedPixelThreshold = 0.12f,
        [Description("Aspect correction: 'none', 'crop', or 'fit_letterbox'.")] string aspectMode = "none",
        [Description("Include heatmap metadata in comparison response.")] bool includeHeatmap = false,
        [Description("Store reference image as handle if only base64 was provided.")] bool storeReferenceHandle = false)
    {
        // Resolve filePath to base64 if provided
        if (!string.IsNullOrWhiteSpace(referenceFilePath) && string.IsNullOrWhiteSpace(referenceImageBase64) && string.IsNullOrWhiteSpace(referenceImageHandle))
        {
            if (!File.Exists(referenceFilePath))
                return new List<AIContent> { new TextContent(ToolErrors.ValidationError($"File not found: {referenceFilePath}")) };
            try
            {
                var bytes = await File.ReadAllBytesAsync(referenceFilePath);
                referenceImageBase64 = Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                return new List<AIContent> { new TextContent(ToolErrors.ValidationError($"Failed to read file: {ex.Message}")) };
            }
        }

        if (string.IsNullOrWhiteSpace(referenceImageHandle) && string.IsNullOrWhiteSpace(referenceImageBase64))
        {
            return new List<AIContent>
            {
                new TextContent(ToolErrors.ValidationError("referenceImageHandle, referenceImageBase64, or referenceFilePath is required"))
            };
        }

        screenshotView = screenshotView?.Trim().ToLowerInvariant() ?? "scene";
        if (screenshotView != "game" && screenshotView != "scene")
            screenshotView = "scene";

        // Step 1: Capture screenshot with base64 + handle
        var screenshotJson = await client.TakeScreenshotAsync(
            screenshotView,
            includeBase64: true,
            includeHandle: true,
            screenshotWidth,
            screenshotHeight,
            payloadMode: "both");

        // Extract screenshot image content
        var imageContent = ImageContentHelper.ExtractImageContent(screenshotJson);

        // Extract the image handle from the screenshot response for use in comparison
        string? capturedHandle = null;
        try
        {
            using var doc = JsonDocument.Parse(screenshotJson);
            if (doc.RootElement.TryGetProperty("imageHandle", out var handleProp))
            {
                capturedHandle = handleProp.GetString();
            }
        }
        catch
        {
            // If we can't parse the handle, we'll fall back to captureCurrentScreenshot
        }

        // Step 2: Compare against reference using the captured handle
        var compareRequest = new Dictionary<string, object?>
        {
            ["captureCurrentScreenshot"] = 0, // We already have the screenshot
            ["screenshotView"] = screenshotView,
            ["downsampleMaxSize"] = downsampleMaxSize,
            ["gridSize"] = gridSize,
            ["changedPixelThreshold"] = changedPixelThreshold,
            ["includeHeatmap"] = includeHeatmap ? 1 : 0,
            ["storeReferenceHandle"] = storeReferenceHandle ? 1 : 0,
            ["includeImageHandles"] = 1,
            ["omitEmpty"] = 1,
            ["maxItems"] = 256
        };

        if (!string.IsNullOrWhiteSpace(aspectMode) && aspectMode != "none")
        {
            compareRequest["aspectMode"] = aspectMode;
        }

        // Set current image from captured screenshot
        if (!string.IsNullOrWhiteSpace(capturedHandle))
        {
            compareRequest["currentImageHandle"] = capturedHandle;
        }
        else
        {
            // Fallback: let compare capture its own screenshot
            compareRequest["captureCurrentScreenshot"] = 1;
            compareRequest["screenshotWidth"] = screenshotWidth;
            compareRequest["screenshotHeight"] = screenshotHeight;
        }

        // Set reference image
        if (!string.IsNullOrWhiteSpace(referenceImageHandle))
        {
            compareRequest["referenceImageHandle"] = referenceImageHandle;
        }
        if (!string.IsNullOrWhiteSpace(referenceImageBase64))
        {
            compareRequest["referenceImageBase64"] = referenceImageBase64;
        }

        var compareJson = await client.CompareImagesAsync(compareRequest);

        // Inject convergence tracking
        var convergenceKey = ConvergenceTracker.DeriveKey(referenceImageHandle, referenceImageBase64);
        try
        {
            using var parseDoc = JsonDocument.Parse(compareJson);
            if (parseDoc.RootElement.TryGetProperty("metrics", out var metricsProp) &&
                metricsProp.TryGetProperty("similarityScore", out var simProp) &&
                simProp.TryGetSingle(out var similarity))
            {
                ConvergenceTracker.Record(convergenceKey, similarity);
            }
            var responseDict = JsonSerializer.Deserialize<Dictionary<string, object>>(compareJson);
            if (responseDict != null)
            {
                responseDict["convergence"] = ConvergenceTracker.GetMetadata(convergenceKey);
                compareJson = JsonSerializer.Serialize(responseDict);
            }
        }
        catch { /* leave compareJson unmodified on parse failure */ }

        // Build combined response: image content first, then comparison metrics
        var result = new List<AIContent>();

        // Add the visual screenshot (DataContent) from imageContent
        foreach (var content in imageContent)
        {
            if (content is DataContent)
            {
                result.Add(content);
                break; // Only add the first image
            }
        }

        // Add comparison metrics as text
        result.Add(new TextContent(compareJson));

        // If no image was extracted, still return comparison metrics
        if (result.Count == 1 && result[0] is TextContent)
        {
            result.Insert(0, new TextContent("[Screenshot capture returned no image data]"));
        }

        return result;
    }
}
