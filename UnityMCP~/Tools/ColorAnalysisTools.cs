using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static partial class ColorAnalysisTools
{
    [McpServerTool(Name = "unity_object_color_audit_in_view")]
    [Description("Audit visible object colors in the active camera view by semantic filter (name/tag/layer). Returns per-object luminance/saturation/color-cast plus summary.")]
    public static async Task<string> ObjectColorAuditInView(
        UnityClient client,
        [Description("Name substring filter (case-insensitive). Empty = all rendered objects.")] string? nameContains = null,
        [Description("Optional tag filter.")] string? tag = null,
        [Description("Optional layer name filter.")] string? layerName = null,
        [Description("Include inactive objects.")] bool includeInactive = false,
        [Description("Maximum objects to audit (1-250).")]
        int maxObjects = 40,
        [Description("Sample points per object (1-9).")]
        int samplesPerObject = 5,
        [Description("Capture view: game or scene.")] string viewType = "game",
        [Description("Screenshot width in pixels (64-1920).")]
        int screenshotWidth = 1600,
        [Description("Screenshot height in pixels (64-1920).")]
        int screenshotHeight = 900,
        [Description("Include individual point samples in each object result.")] bool includePerObjectSamples = false)
    {
        viewType = NormalizeViewType(viewType);
        maxObjects = Math.Clamp(maxObjects, 1, 250);
        samplesPerObject = Math.Clamp(samplesPerObject, 1, 9);
        screenshotWidth = NormalizeDimension(screenshotWidth, 1600);
        screenshotHeight = NormalizeDimension(screenshotHeight, 900);

        var screenshotJson = await client.TakeScreenshotAsync(
            viewType,
            includeBase64: false,
            includeHandle: true,
            width: screenshotWidth,
            height: screenshotHeight);

        if (!TryExtractImageHandle(screenshotJson, out var imageHandle, out var imageWidth, out var imageHeight, out var screenshotError))
        {
            return ToolErrors.ValidationError($"Failed to capture screenshot: {screenshotError}");
        }

        var projectionScript = BuildObjectProjectionScript(
            nameContains ?? string.Empty,
            tag ?? string.Empty,
            layerName ?? string.Empty,
            includeInactive,
            maxObjects,
            samplesPerObject,
            viewType);

        var executeJson = await client.ExecuteCSharpAsync(projectionScript);
        if (!TryExtractExecuteResultJson(executeJson, out var projectionJson, out var executeError))
        {
            return ToolErrors.ValidationError($"Projection pass failed: {executeError}");
        }

        if (!TryParseProjection(projectionJson, out var projection, out var projectionError))
        {
            return ToolErrors.ValidationError($"Projection parse failed: {projectionError}");
        }

        if (projection.Objects.Count == 0)
        {
            var empty = new Dictionary<string, object?>
            {
                ["success"] = true,
                ["viewType"] = viewType,
                ["imageHandle"] = imageHandle,
                ["imageSize"] = new[] { imageWidth, imageHeight },
                ["filter"] = BuildFilterDict(nameContains, tag, layerName, includeInactive),
                ["summary"] = new Dictionary<string, object?>
                {
                    ["objectCount"] = 0,
                    ["samplePointCount"] = 0,
                    ["dominantCast"] = "none"
                },
                ["objects"] = Array.Empty<object>()
            };
            return JsonSerializer.Serialize(empty);
        }

        var flattenedPoints = new List<double[]>();
        var reverseMap = new List<(int objectIndex, int pointIndex)>();

        for (var i = 0; i < projection.Objects.Count; i++)
        {
            for (var j = 0; j < projection.Objects[i].SamplePoints.Count; j++)
            {
                flattenedPoints.Add(new[]
                {
                    projection.Objects[i].SamplePoints[j].X,
                    projection.Objects[i].SamplePoints[j].Y
                });
                reverseMap.Add((i, j));
            }
        }

        var samplePayload = new Dictionary<string, object?>
        {
            ["imageHandle"] = imageHandle,
            ["samplePoints"] = flattenedPoints,
            ["sampleRadius"] = 3
        };

        var sampledJson = await client.SampleScreenshotColorsAsync(samplePayload);
        if (!TryParseSampleColors(sampledJson, out var sampledColors, out var sampleError))
        {
            return ToolErrors.ValidationError($"Color sampling failed: {sampleError}");
        }

        if (sampledColors.Count != reverseMap.Count)
        {
            return ToolErrors.ValidationError($"Sampling mismatch: expected {reverseMap.Count} points, got {sampledColors.Count}");
        }

        var groupedSamples = new List<List<SampleColor>>(projection.Objects.Count);
        for (var i = 0; i < projection.Objects.Count; i++)
        {
            groupedSamples.Add(new List<SampleColor>());
        }

        for (var i = 0; i < reverseMap.Count; i++)
        {
            var (objectIndex, _) = reverseMap[i];
            groupedSamples[objectIndex].Add(sampledColors[i]);
        }

        var objectsOut = new List<Dictionary<string, object?>>(projection.Objects.Count);
        var castHistogram = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        double globalLuminance = 0;
        double globalSaturation = 0;
        int blueOrCyanCount = 0;

        for (var i = 0; i < projection.Objects.Count; i++)
        {
            var obj = projection.Objects[i];
            var stats = ComputeStats(groupedSamples[i]);
            var cast = DetermineColorCast(stats.AvgR, stats.AvgG, stats.AvgB, stats.AvgHue, stats.AvgSaturation);

            castHistogram.TryGetValue(cast, out var castCount);
            castHistogram[cast] = castCount + 1;
            if (cast.Equals("blue", StringComparison.OrdinalIgnoreCase) || cast.Equals("cyan", StringComparison.OrdinalIgnoreCase))
            {
                blueOrCyanCount++;
            }

            globalLuminance += stats.AvgLuminance;
            globalSaturation += stats.AvgSaturation;

            var item = new Dictionary<string, object?>
            {
                ["instanceId"] = obj.InstanceId,
                ["name"] = obj.Name,
                ["path"] = obj.Path,
                ["layerName"] = obj.LayerName,
                ["tag"] = obj.Tag,
                ["materialName"] = obj.MaterialName,
                ["materialPath"] = obj.MaterialPath,
                ["distanceToCamera"] = Math.Round(obj.DistanceToCamera, 4),
                ["screenRectTopLeftNorm"] = new[]
                {
                    Math.Round(obj.RectLeft, 4),
                    Math.Round(obj.RectTop, 4),
                    Math.Round(obj.RectWidth, 4),
                    Math.Round(obj.RectHeight, 4)
                },
                ["sampleCount"] = groupedSamples[i].Count,
                ["avgColor"] = new[]
                {
                    Math.Round(stats.AvgR, 4),
                    Math.Round(stats.AvgG, 4),
                    Math.Round(stats.AvgB, 4),
                    1.0
                },
                ["avgColorHex"] = stats.AvgHex,
                ["avgLuminance"] = Math.Round(stats.AvgLuminance, 4),
                ["avgSaturation"] = Math.Round(stats.AvgSaturation, 4),
                ["avgHueDeg"] = Math.Round(stats.AvgHue, 2),
                ["colorCast"] = cast
            };

            if (includePerObjectSamples)
            {
                var perPoint = new List<Dictionary<string, object?>>(groupedSamples[i].Count);
                for (var p = 0; p < groupedSamples[i].Count; p++)
                {
                    var sample = groupedSamples[i][p];
                    var point = obj.SamplePoints[p];
                    perPoint.Add(new Dictionary<string, object?>
                    {
                        ["x"] = Math.Round(point.X, 4),
                        ["y"] = Math.Round(point.Y, 4),
                        ["hex"] = sample.Hex,
                        ["r"] = Math.Round(sample.R, 4),
                        ["g"] = Math.Round(sample.G, 4),
                        ["b"] = Math.Round(sample.B, 4),
                        ["luminance"] = Math.Round(sample.Luminance, 4),
                        ["hsv"] = new[]
                        {
                            Math.Round(sample.H, 2),
                            Math.Round(sample.S, 4),
                            Math.Round(sample.V, 4)
                        }
                    });
                }
                item["samples"] = perPoint;
            }

            objectsOut.Add(item);
        }

        var summary = new Dictionary<string, object?>
        {
            ["objectCount"] = projection.Objects.Count,
            ["samplePointCount"] = sampledColors.Count,
            ["avgLuminance"] = Math.Round(globalLuminance / projection.Objects.Count, 4),
            ["avgSaturation"] = Math.Round(globalSaturation / projection.Objects.Count, 4),
            ["blueOrCyanObjectCount"] = blueOrCyanCount,
            ["dominantCast"] = castHistogram.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? "none",
            ["castHistogram"] = castHistogram
        };

        var response = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["viewType"] = viewType,
            ["cameraName"] = projection.CameraName,
            ["imageHandle"] = imageHandle,
            ["imageSize"] = new[] { imageWidth, imageHeight },
            ["filter"] = BuildFilterDict(nameContains, tag, layerName, includeInactive),
            ["summary"] = summary,
            ["objects"] = objectsOut
        };

        return JsonSerializer.Serialize(response);
    }

    [McpServerTool(Name = "unity_semantic_sample_colors")]
    [Description("Sample colors at points, but report semantic hits only when the ray intersects filtered objects (name/tag/layer).")]
    public static async Task<string> SemanticSampleColors(
        UnityClient client,
        [Description("Sample points as [[x,y], ...] in normalized top-left coordinates (0..1).")]
        float[][] samplePoints,
        [Description("Name substring filter (case-insensitive). Empty = all rendered objects.")] string? nameContains = null,
        [Description("Optional tag filter.")] string? tag = null,
        [Description("Optional layer name filter.")] string? layerName = null,
        [Description("Include inactive objects.")] bool includeInactive = false,
        [Description("Capture view: game or scene.")] string viewType = "game",
        [Description("Screenshot width in pixels (64-1920).")]
        int screenshotWidth = 1600,
        [Description("Screenshot height in pixels (64-1920).")]
        int screenshotHeight = 900,
        [Description("Include unmatched points in response.")] bool includeUnmatched = true)
    {
        if (samplePoints == null || samplePoints.Length == 0)
        {
            return ToolErrors.ValidationError("samplePoints must have at least 1 point");
        }

        foreach (var point in samplePoints)
        {
            if (point == null || point.Length != 2)
            {
                return ToolErrors.ValidationError("Each sample point must be [x,y]");
            }
        }

        viewType = NormalizeViewType(viewType);
        screenshotWidth = NormalizeDimension(screenshotWidth, 1600);
        screenshotHeight = NormalizeDimension(screenshotHeight, 900);

        var screenshotJson = await client.TakeScreenshotAsync(
            viewType,
            includeBase64: false,
            includeHandle: true,
            width: screenshotWidth,
            height: screenshotHeight);

        if (!TryExtractImageHandle(screenshotJson, out var imageHandle, out var imageWidth, out var imageHeight, out var screenshotError))
        {
            return ToolErrors.ValidationError($"Failed to capture screenshot: {screenshotError}");
        }

        var semanticScript = BuildSemanticScript(
            samplePoints,
            nameContains ?? string.Empty,
            tag ?? string.Empty,
            layerName ?? string.Empty,
            includeInactive,
            viewType);

        var executeJson = await client.ExecuteCSharpAsync(semanticScript);
        if (!TryExtractExecuteResultJson(executeJson, out var semanticJson, out var executeError))
        {
            return ToolErrors.ValidationError($"Semantic projection failed: {executeError}");
        }

        if (!TryParseSemanticProjection(semanticJson, out var projection, out var parseError))
        {
            return ToolErrors.ValidationError($"Semantic projection parse failed: {parseError}");
        }

        if (projection.Points.Count != samplePoints.Length)
        {
            return ToolErrors.ValidationError($"Semantic projection mismatch: expected {samplePoints.Length}, got {projection.Points.Count}");
        }

        var samplePayload = new Dictionary<string, object?>
        {
            ["imageHandle"] = imageHandle,
            ["samplePoints"] = samplePoints,
            ["sampleRadius"] = 3
        };

        var sampledJson = await client.SampleScreenshotColorsAsync(samplePayload);
        if (!TryParseSampleColors(sampledJson, out var sampledColors, out var sampleError))
        {
            return ToolErrors.ValidationError($"Color sampling failed: {sampleError}");
        }

        if (sampledColors.Count != samplePoints.Length)
        {
            return ToolErrors.ValidationError($"Sampling mismatch: expected {samplePoints.Length}, got {sampledColors.Count}");
        }

        var pointsOut = new List<Dictionary<string, object?>>();
        var objectHits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int matched = 0;

        for (var i = 0; i < samplePoints.Length; i++)
        {
            var point = projection.Points[i];
            var color = sampledColors[i];

            var item = new Dictionary<string, object?>
            {
                ["x"] = Math.Round(samplePoints[i][0], 4),
                ["y"] = Math.Round(samplePoints[i][1], 4),
                ["matched"] = point.Matched,
                ["matchReason"] = point.MatchReason,
                ["color"] = new Dictionary<string, object?>
                {
                    ["hex"] = color.Hex,
                    ["r"] = Math.Round(color.R, 4),
                    ["g"] = Math.Round(color.G, 4),
                    ["b"] = Math.Round(color.B, 4),
                    ["luminance"] = Math.Round(color.Luminance, 4),
                    ["hsv"] = new[]
                    {
                        Math.Round(color.H, 2),
                        Math.Round(color.S, 4),
                        Math.Round(color.V, 4)
                    }
                }
            };

            if (point.Matched)
            {
                matched++;
                item["instanceId"] = point.InstanceId;
                item["name"] = point.Name;
                item["path"] = point.Path;
                item["materialName"] = point.MaterialName;
                item["materialPath"] = point.MaterialPath;
                item["distance"] = Math.Round(point.Distance, 4);

                var key = string.IsNullOrWhiteSpace(point.Path) ? point.Name : point.Path;
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = $"instance:{point.InstanceId}";
                }

                objectHits.TryGetValue(key, out var count);
                objectHits[key] = count + 1;
            }

            if (includeUnmatched || point.Matched)
            {
                pointsOut.Add(item);
            }
        }

        var response = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["viewType"] = viewType,
            ["cameraName"] = projection.CameraName,
            ["imageHandle"] = imageHandle,
            ["imageSize"] = new[] { imageWidth, imageHeight },
            ["filter"] = BuildFilterDict(nameContains, tag, layerName, includeInactive),
            ["summary"] = new Dictionary<string, object?>
            {
                ["pointCount"] = samplePoints.Length,
                ["matchedPointCount"] = matched,
                ["unmatchedPointCount"] = samplePoints.Length - matched,
                ["uniqueMatchedObjectCount"] = objectHits.Count,
                ["topMatchedObjects"] = objectHits
                    .OrderByDescending(kv => kv.Value)
                    .Take(8)
                    .Select(kv => new Dictionary<string, object?>
                    {
                        ["objectKey"] = kv.Key,
                        ["hitCount"] = kv.Value
                    })
                    .ToList()
            },
            ["points"] = pointsOut
        };

        return JsonSerializer.Serialize(response);
    }

    private static string BuildObjectProjectionScript(
        string nameContains,
        string tag,
        string layerName,
        bool includeInactive,
        int maxObjects,
        int samplesPerObject,
        string viewType)
    {
        return $$"""
string __nameContains = {{ToCSharpString(nameContains)}};
string __tagFilter = {{ToCSharpString(tag)}};
string __layerFilter = {{ToCSharpString(layerName)}};
bool __includeInactive = {{(includeInactive ? "true" : "false")}};
int __maxObjects = {{maxObjects}};
int __samplesPerObject = {{samplesPerObject}};
string __viewType = {{ToCSharpString(viewType)}};

Camera __camera = null;
if (string.Equals(__viewType, "scene", StringComparison.OrdinalIgnoreCase))
{
    var __sceneView = UnityEditor.SceneView.lastActiveSceneView;
    if (__sceneView != null) __camera = __sceneView.camera;
}
if (__camera == null) __camera = Camera.main;
if (__camera == null)
{
    var __allCameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
    if (__allCameras != null && __allCameras.Length > 0) __camera = __allCameras[0];
}

if (__camera == null)
{
    Print(UnityAgentBridge.MiniJSON.Json.Serialize(new Dictionary<string, object>
    {
        ["success"] = false,
        ["error"] = "No camera available"
    }));
    return;
}

string __PathOf(Transform __t)
{
    if (__t == null) return string.Empty;
    var __parts = new Stack<string>();
    var __cursor = __t;
    while (__cursor != null)
    {
        __parts.Push(__cursor.name);
        __cursor = __cursor.parent;
    }
    return string.Join("/", __parts);
}

bool __NamePass(string __value, string __needle)
{
    if (string.IsNullOrWhiteSpace(__needle)) return true;
    if (string.IsNullOrWhiteSpace(__value)) return false;
    return __value.IndexOf(__needle, StringComparison.OrdinalIgnoreCase) >= 0;
}

bool __TagPass(GameObject __go, string __tag)
{
    if (string.IsNullOrWhiteSpace(__tag)) return true;
    if (__go == null) return false;
    try { return __go.CompareTag(__tag); }
    catch { return false; }
}

bool __LayerPass(GameObject __go, string __layer)
{
    if (string.IsNullOrWhiteSpace(__layer)) return true;
    if (__go == null) return false;
    var __layerName = LayerMask.LayerToName(__go.layer) ?? string.Empty;
    return string.Equals(__layerName, __layer, StringComparison.OrdinalIgnoreCase);
}

var __renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
var __filtered = new List<Renderer>();
var __camPos = __camera.transform.position;

foreach (var __renderer in __renderers)
{
    if (__renderer == null) continue;
    var __go = __renderer.gameObject;
    if (__go == null) continue;
    if (!__includeInactive && !__go.activeInHierarchy) continue;
    if (!__renderer.enabled) continue;
    if (!__NamePass(__go.name, __nameContains)) continue;
    if (!__TagPass(__go, __tagFilter)) continue;
    if (!__LayerPass(__go, __layerFilter)) continue;
    __filtered.Add(__renderer);
}

__filtered = __filtered
    .OrderBy(__r => Vector3.SqrMagnitude(__r.bounds.center - __camPos))
    .Take(__maxObjects)
    .ToList();

var __objects = new List<Dictionary<string, object>>();

foreach (var __renderer in __filtered)
{
    var __go = __renderer.gameObject;
    if (__go == null) continue;

    var __b = __renderer.bounds;
    var __min = __b.min;
    var __max = __b.max;

    var __corners = new[]
    {
        new Vector3(__min.x, __min.y, __min.z),
        new Vector3(__min.x, __min.y, __max.z),
        new Vector3(__min.x, __max.y, __min.z),
        new Vector3(__min.x, __max.y, __max.z),
        new Vector3(__max.x, __min.y, __min.z),
        new Vector3(__max.x, __min.y, __max.z),
        new Vector3(__max.x, __max.y, __min.z),
        new Vector3(__max.x, __max.y, __max.z),
        __b.center
    };

    bool __any = false;
    float __minX = 1f;
    float __maxX = 0f;
    float __minY = 1f;
    float __maxY = 0f;

    foreach (var __corner in __corners)
    {
        var __vp = __camera.WorldToViewportPoint(__corner);
        if (__vp.z <= 0f) continue;
        __any = true;
        __minX = Mathf.Min(__minX, __vp.x);
        __maxX = Mathf.Max(__maxX, __vp.x);
        __minY = Mathf.Min(__minY, __vp.y);
        __maxY = Mathf.Max(__maxY, __vp.y);
    }

    if (!__any) continue;

    __minX = Mathf.Clamp01(__minX);
    __maxX = Mathf.Clamp01(__maxX);
    __minY = Mathf.Clamp01(__minY);
    __maxY = Mathf.Clamp01(__maxY);

    var __width = __maxX - __minX;
    var __height = __maxY - __minY;
    if (__width < 0.001f || __height < 0.001f) continue;

    float __left = __minX;
    float __right = __maxX;
    float __top = 1f - __maxY;
    float __bottom = 1f - __minY;
    float __centerX = (__left + __right) * 0.5f;
    float __centerY = (__top + __bottom) * 0.5f;
    float __padX = Mathf.Min(0.02f, (__right - __left) * 0.2f);
    float __padY = Mathf.Min(0.02f, (__bottom - __top) * 0.2f);

    var __points = new List<float[]>
    {
        new[] { Mathf.Clamp01(__centerX), Mathf.Clamp01(__centerY) }
    };

    if (__samplesPerObject > 1)
    {
        __points.Add(new[] { Mathf.Clamp01(__left + __padX), Mathf.Clamp01(__top + __padY) });
        __points.Add(new[] { Mathf.Clamp01(__right - __padX), Mathf.Clamp01(__top + __padY) });
        __points.Add(new[] { Mathf.Clamp01(__left + __padX), Mathf.Clamp01(__bottom - __padY) });
        __points.Add(new[] { Mathf.Clamp01(__right - __padX), Mathf.Clamp01(__bottom - __padY) });
    }

    if (__samplesPerObject > 5)
    {
        __points.Add(new[] { Mathf.Clamp01(__centerX), Mathf.Clamp01(__top + __padY) });
        __points.Add(new[] { Mathf.Clamp01(__centerX), Mathf.Clamp01(__bottom - __padY) });
        __points.Add(new[] { Mathf.Clamp01(__left + __padX), Mathf.Clamp01(__centerY) });
        __points.Add(new[] { Mathf.Clamp01(__right - __padX), Mathf.Clamp01(__centerY) });
    }

    while (__points.Count > __samplesPerObject) __points.RemoveAt(__points.Count - 1);

    var __mat = __renderer.sharedMaterial;
    __objects.Add(new Dictionary<string, object>
    {
        ["instanceId"] = __go.GetInstanceID(),
        ["name"] = __go.name,
        ["path"] = __PathOf(__go.transform),
        ["layerName"] = LayerMask.LayerToName(__go.layer) ?? string.Empty,
        ["tag"] = __go.tag ?? string.Empty,
        ["materialName"] = __mat != null ? __mat.name : string.Empty,
        ["materialPath"] = __mat != null ? UnityEditor.AssetDatabase.GetAssetPath(__mat) : string.Empty,
        ["distanceToCamera"] = Vector3.Distance(__camPos, __b.center),
        ["screenRect"] = new[] { __left, __top, (__right - __left), (__bottom - __top) },
        ["samplePoints"] = __points
    });
}

Print(UnityAgentBridge.MiniJSON.Json.Serialize(new Dictionary<string, object>
{
    ["success"] = true,
    ["cameraName"] = __camera.name ?? string.Empty,
    ["objects"] = __objects
}));
""";
    }
}
