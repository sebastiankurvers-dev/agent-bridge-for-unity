using System.Text.Json;
using Microsoft.Extensions.AI;

namespace UnityMCP.Tools;

/// <summary>
/// Helper to extract base64 image data from Unity JSON responses
/// and convert to AIContent blocks for proper MCP image transport.
/// </summary>
internal static class ImageContentHelper
{
    // Minimum bytes for a valid JPEG/PNG (header + minimal data)
    private const int MinImageBytes = 67;

    // JPEG: FF D8 FF
    private static readonly byte[] JpegMagic = { 0xFF, 0xD8, 0xFF };
    // PNG: 89 50 4E 47 0D 0A 1A 0A
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Validates that image bytes have a recognized format header and minimum size.
    /// Returns null if valid, or an error message if invalid.
    /// </summary>
    private static string? ValidateImageBytes(byte[] bytes, string mimeType)
    {
        if (bytes == null || bytes.Length < MinImageBytes)
        {
            return $"Image data too small ({bytes?.Length ?? 0} bytes, minimum {MinImageBytes}). Capture may have failed.";
        }

        bool isJpeg = bytes.Length >= JpegMagic.Length && bytes[0] == JpegMagic[0] && bytes[1] == JpegMagic[1] && bytes[2] == JpegMagic[2];
        bool isPng = bytes.Length >= PngMagic.Length;
        if (isPng)
        {
            for (int i = 0; i < PngMagic.Length; i++)
            {
                if (bytes[i] != PngMagic[i]) { isPng = false; break; }
            }
        }

        if (!isJpeg && !isPng)
        {
            return $"Image data has unrecognized format (first bytes: {BitConverter.ToString(bytes, 0, Math.Min(8, bytes.Length))}). Expected JPEG or PNG.";
        }

        // Verify mime type matches actual format
        if (isJpeg && mimeType == "image/png")
        {
            // Mild mismatch — data is JPEG but mime says PNG. Not fatal but worth noting.
        }
        if (isPng && mimeType == "image/jpeg")
        {
            // Same — data is PNG but mime says JPEG.
        }

        return null; // valid
    }

    /// <summary>
    /// Parses a Unity JSON response containing a base64 image and returns
    /// a list with a DataContent (image) block and an optional TextContent (metadata) block.
    /// Falls back to returning the raw JSON as TextContent if no image data is found.
    /// </summary>
    internal static IList<AIContent> ExtractImageContent(string json, string? metadataOverride = null)
    {
        var result = new List<AIContent>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check for error responses - return as text
            if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.String)
            {
                result.Add(new TextContent(json));
                return result;
            }

            // Try to extract base64 image data
            string? base64 = null;
            if (root.TryGetProperty("base64", out var b64Prop))
            {
                base64 = b64Prop.GetString();
            }

            if (string.IsNullOrEmpty(base64))
            {
                // No image data found, return raw JSON
                result.Add(new TextContent(json));
                return result;
            }

            var mimeType = root.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "image/jpeg" : "image/jpeg";

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                result.Add(new TextContent($"[Image error] Invalid base64 data (length: {base64.Length}, mime: {mimeType}). The image data from Unity was corrupted."));
                return result;
            }

            // Validate image bytes before sending to API
            var validationError = ValidateImageBytes(bytes, mimeType);
            if (validationError != null)
            {
                var width = root.TryGetProperty("width", out var w2) ? w2.GetInt32() : 0;
                var height = root.TryGetProperty("height", out var h2) ? h2.GetInt32() : 0;
                result.Add(new TextContent($"[Image validation failed] {validationError} (reported size: {width}x{height}, mime: {mimeType}, base64 length: {base64.Length})"));
                return result;
            }

            result.Add(new DataContent(bytes, mimeType));

            // Build metadata text
            var metadata = metadataOverride;
            if (metadata == null)
            {
                var width = root.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                var height = root.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                metadata = $"Screenshot captured: {width}x{height} ({mimeType})";
            }
            result.Add(new TextContent(metadata));

            return result;
        }
        catch
        {
            // If parsing fails, return raw JSON as text
            result.Add(new TextContent(json));
            return result;
        }
    }

    /// <summary>
    /// Extracts image content from a nested "screenshot" property within a larger JSON response.
    /// Returns the screenshot as DataContent plus the remaining JSON metadata as TextContent.
    /// Used for BuildSceneAndScreenshot / SceneTransaction responses.
    /// </summary>
    internal static IList<AIContent> ExtractScreenshotFromResponse(string json)
    {
        var result = new List<AIContent>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check for error
            if (root.TryGetProperty("success", out var successProp) && !successProp.GetBoolean())
            {
                result.Add(new TextContent(json));
                return result;
            }

            // Extract the screenshot sub-object
            string? base64 = null;
            string mimeType = "image/jpeg";
            int width = 0, height = 0;

            if (root.TryGetProperty("screenshot", out var screenshotProp) && screenshotProp.ValueKind == JsonValueKind.Object)
            {
                if (screenshotProp.TryGetProperty("base64", out var b64))
                    base64 = b64.GetString();
                if (screenshotProp.TryGetProperty("mimeType", out var mt))
                    mimeType = mt.GetString() ?? "image/jpeg";
                if (screenshotProp.TryGetProperty("width", out var w))
                    width = w.GetInt32();
                if (screenshotProp.TryGetProperty("height", out var h))
                    height = h.GetInt32();
            }

            // Build metadata JSON without the base64 blob
            var metadataObj = new Dictionary<string, object>();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name == "screenshot") continue;
                metadataObj[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => prop.Value.TryGetInt32(out var i) ? i : prop.Value.GetDouble(),
                    JsonValueKind.String => prop.Value.GetString()!,
                    _ => prop.Value.GetRawText()
                };
            }

            if (!string.IsNullOrEmpty(base64))
            {
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(base64);
                }
                catch (FormatException)
                {
                    metadataObj["imageError"] = $"Invalid base64 data (length: {base64.Length}, mime: {mimeType}). The image data from Unity was corrupted.";
                    result.Add(new TextContent(JsonSerializer.Serialize(metadataObj)));
                    return result;
                }

                // Validate image bytes before sending to API
                var validationError = ValidateImageBytes(bytes, mimeType);
                if (validationError != null)
                {
                    metadataObj["imageValidationError"] = $"{validationError} (reported size: {width}x{height}, mime: {mimeType})";
                }
                else
                {
                    result.Add(new DataContent(bytes, mimeType));
                }
            }

            // Add metadata as text (with screenshot dimensions but no base64)
            if (width > 0 && height > 0)
            {
                metadataObj["screenshotSize"] = $"{width}x{height}";
            }
            result.Add(new TextContent(JsonSerializer.Serialize(metadataObj)));

            return result;
        }
        catch
        {
            result.Add(new TextContent(json));
            return result;
        }
    }
}
