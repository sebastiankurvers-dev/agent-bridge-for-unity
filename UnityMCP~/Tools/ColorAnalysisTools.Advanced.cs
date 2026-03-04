using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

public static partial class ColorAnalysisTools
{
    private static string BuildSemanticScript(
        float[][] samplePoints,
        string nameContains,
        string tag,
        string layerName,
        bool includeInactive,
        string viewType)
    {
        var pointsLiteral = BuildVector2ListLiteral(samplePoints);

        return $$"""
string __nameContains = {{ToCSharpString(nameContains)}};
string __tagFilter = {{ToCSharpString(tag)}};
string __layerFilter = {{ToCSharpString(layerName)}};
bool __includeInactive = {{(includeInactive ? "true" : "false")}};
string __viewType = {{ToCSharpString(viewType)}};
var __points = {{pointsLiteral}};

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

var __matches = new List<Dictionary<string, object>>();

foreach (var __point in __points)
{
    float __x = __point.x;
    float __y = __point.y;

    if (__x < 0f || __x > 1f || __y < 0f || __y > 1f)
    {
        __matches.Add(new Dictionary<string, object>
        {
            ["matched"] = false,
            ["matchReason"] = "out_of_bounds"
        });
        continue;
    }

    float __vy = 1f - __y;
    var __ray = __camera.ViewportPointToRay(new Vector3(__x, __vy, 0f));

    Renderer __best = null;
    float __bestDistance = float.MaxValue;

    foreach (var __renderer in __filtered)
    {
        if (__renderer.bounds.IntersectRay(__ray, out var __distance) && __distance >= 0f)
        {
            if (__distance < __bestDistance)
            {
                __bestDistance = __distance;
                __best = __renderer;
            }
        }
    }

    if (__best == null)
    {
        __matches.Add(new Dictionary<string, object>
        {
            ["matched"] = false,
            ["matchReason"] = "no_semantic_object_intersection"
        });
        continue;
    }

    var __go = __best.gameObject;
    var __mat = __best.sharedMaterial;
    __matches.Add(new Dictionary<string, object>
    {
        ["matched"] = true,
        ["matchReason"] = "ray_bounds_intersection",
        ["instanceId"] = __go.GetInstanceID(),
        ["name"] = __go.name,
        ["path"] = __PathOf(__go.transform),
        ["materialName"] = __mat != null ? __mat.name : string.Empty,
        ["materialPath"] = __mat != null ? UnityEditor.AssetDatabase.GetAssetPath(__mat) : string.Empty,
        ["distance"] = __bestDistance
    });
}

Print(UnityAgentBridge.MiniJSON.Json.Serialize(new Dictionary<string, object>
{
    ["success"] = true,
    ["cameraName"] = __camera.name ?? string.Empty,
    ["points"] = __matches
}));
""";
    }

    private static string BuildVector2ListLiteral(float[][] points)
    {
        var sb = new StringBuilder();
        sb.Append("new List<Vector2> { ");

        for (var i = 0; i < points.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            var x = points[i][0].ToString("0.########", CultureInfo.InvariantCulture);
            var y = points[i][1].ToString("0.########", CultureInfo.InvariantCulture);
            sb.Append($"new Vector2({x}f, {y}f)");
        }

        sb.Append(" }");
        return sb.ToString();
    }

    private static string ToCSharpString(string value)
    {
        // JSON string literals are valid C# string literals for escaped content.
        return JsonSerializer.Serialize(value ?? string.Empty);
    }

    private static Dictionary<string, object?> BuildFilterDict(string? nameContains, string? tag, string? layerName, bool includeInactive)
    {
        return new Dictionary<string, object?>
        {
            ["nameContains"] = nameContains ?? string.Empty,
            ["tag"] = tag ?? string.Empty,
            ["layerName"] = layerName ?? string.Empty,
            ["includeInactive"] = includeInactive
        };
    }

    private static bool TryExtractImageHandle(
        string screenshotJson,
        out string imageHandle,
        out int width,
        out int height,
        out string error)
    {
        imageHandle = string.Empty;
        width = 0;
        height = 0;
        error = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(screenshotJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("success", out var successEl) && successEl.ValueKind == JsonValueKind.False)
            {
                error = ReadString(root, "error");
                return false;
            }

            if (!root.TryGetProperty("imageHandle", out var handleEl) || handleEl.ValueKind != JsonValueKind.String)
            {
                error = "Screenshot did not return imageHandle";
                return false;
            }

            imageHandle = handleEl.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(imageHandle))
            {
                error = "Screenshot imageHandle was empty";
                return false;
            }

            if (root.TryGetProperty("width", out var widthEl) && widthEl.TryGetInt32(out var w)) width = w;
            if (root.TryGetProperty("height", out var heightEl) && heightEl.TryGetInt32(out var h)) height = h;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to parse screenshot response: {ex.Message}";
            return false;
        }
    }

    private static bool TryExtractExecuteResultJson(string executeJson, out string resultJson, out string error)
    {
        resultJson = string.Empty;
        error = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(executeJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("success", out var successEl) && successEl.ValueKind == JsonValueKind.False)
            {
                error = ReadString(root, "error");
                return false;
            }

            if (!root.TryGetProperty("result", out var resultEl) || resultEl.ValueKind != JsonValueKind.String)
            {
                error = "Execute response missing string result";
                return false;
            }

            var raw = resultEl.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "Execute result was empty";
                return false;
            }

            using var payloadDoc = JsonDocument.Parse(raw);
            resultJson = payloadDoc.RootElement.GetRawText();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to parse execute response: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseProjection(string json, out ProjectionResult projection, out string error)
    {
        projection = new ProjectionResult();
        error = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var successEl) && successEl.ValueKind == JsonValueKind.False)
            {
                error = ReadString(root, "error");
                return false;
            }

            projection.CameraName = ReadString(root, "cameraName");

            if (!root.TryGetProperty("objects", out var objectsEl) || objectsEl.ValueKind != JsonValueKind.Array)
            {
                return true;
            }

            foreach (var objectEl in objectsEl.EnumerateArray())
            {
                var obj = new ProjectionObject
                {
                    InstanceId = ReadInt(objectEl, "instanceId"),
                    Name = ReadString(objectEl, "name"),
                    Path = ReadString(objectEl, "path"),
                    LayerName = ReadString(objectEl, "layerName"),
                    Tag = ReadString(objectEl, "tag"),
                    MaterialName = ReadString(objectEl, "materialName"),
                    MaterialPath = ReadString(objectEl, "materialPath"),
                    DistanceToCamera = ReadDouble(objectEl, "distanceToCamera")
                };

                if (objectEl.TryGetProperty("screenRect", out var rectEl) && rectEl.ValueKind == JsonValueKind.Array)
                {
                    obj.RectLeft = ReadArrayDouble(rectEl, 0);
                    obj.RectTop = ReadArrayDouble(rectEl, 1);
                    obj.RectWidth = ReadArrayDouble(rectEl, 2);
                    obj.RectHeight = ReadArrayDouble(rectEl, 3);
                }

                if (objectEl.TryGetProperty("samplePoints", out var pointsEl) && pointsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pointEl in pointsEl.EnumerateArray())
                    {
                        if (pointEl.ValueKind != JsonValueKind.Array) continue;
                        obj.SamplePoints.Add(new Point2
                        {
                            X = ReadArrayDouble(pointEl, 0),
                            Y = ReadArrayDouble(pointEl, 1)
                        });
                    }
                }

                if (obj.SamplePoints.Count > 0)
                {
                    projection.Objects.Add(obj);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to parse projection JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseSemanticProjection(string json, out SemanticProjection projection, out string error)
    {
        projection = new SemanticProjection();
        error = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var successEl) && successEl.ValueKind == JsonValueKind.False)
            {
                error = ReadString(root, "error");
                return false;
            }

            projection.CameraName = ReadString(root, "cameraName");
            if (!root.TryGetProperty("points", out var pointsEl) || pointsEl.ValueKind != JsonValueKind.Array)
            {
                return true;
            }

            foreach (var pointEl in pointsEl.EnumerateArray())
            {
                projection.Points.Add(new SemanticPoint
                {
                    Matched = ReadBool(pointEl, "matched"),
                    MatchReason = ReadString(pointEl, "matchReason"),
                    InstanceId = ReadInt(pointEl, "instanceId"),
                    Name = ReadString(pointEl, "name"),
                    Path = ReadString(pointEl, "path"),
                    MaterialName = ReadString(pointEl, "materialName"),
                    MaterialPath = ReadString(pointEl, "materialPath"),
                    Distance = ReadDouble(pointEl, "distance")
                });
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to parse semantic projection JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseSampleColors(string json, out List<SampleColor> colors, out string error)
    {
        colors = new List<SampleColor>();
        error = string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var successEl) && successEl.ValueKind == JsonValueKind.False)
            {
                error = ReadString(root, "error");
                return false;
            }

            if (!root.TryGetProperty("samples", out var samplesEl) || samplesEl.ValueKind != JsonValueKind.Array)
            {
                error = "samples[] missing in sample response";
                return false;
            }

            foreach (var sampleEl in samplesEl.EnumerateArray())
            {
                colors.Add(new SampleColor
                {
                    R = ReadDouble(sampleEl, "r"),
                    G = ReadDouble(sampleEl, "g"),
                    B = ReadDouble(sampleEl, "b"),
                    Luminance = ReadDouble(sampleEl, "luminance"),
                    Hex = ReadString(sampleEl, "hex"),
                    H = ReadArrayDouble(sampleEl, "hsv", 0),
                    S = ReadArrayDouble(sampleEl, "hsv", 1),
                    V = ReadArrayDouble(sampleEl, "hsv", 2)
                });
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to parse sample response: {ex.Message}";
            return false;
        }
    }

    private static Stats ComputeStats(List<SampleColor> colors)
    {
        if (colors.Count == 0)
        {
            return new Stats();
        }

        double r = 0;
        double g = 0;
        double b = 0;
        double h = 0;
        double s = 0;
        double lum = 0;

        foreach (var color in colors)
        {
            r += color.R;
            g += color.G;
            b += color.B;
            h += color.H;
            s += color.S;
            lum += color.Luminance;
        }

        var count = colors.Count;
        var avgR = r / count;
        var avgG = g / count;
        var avgB = b / count;

        return new Stats
        {
            AvgR = avgR,
            AvgG = avgG,
            AvgB = avgB,
            AvgHue = h / count,
            AvgSaturation = s / count,
            AvgLuminance = lum / count,
            AvgHex = ToHex(avgR, avgG, avgB)
        };
    }

    private static string DetermineColorCast(double r, double g, double b, double hueDeg, double sat)
    {
        if (sat < 0.1)
        {
            return "neutral";
        }

        if (b > r * 1.15 && b > g * 1.08) return "blue";
        if (g > r * 1.15 && g > b * 1.08) return "green";
        if (r > g * 1.15 && r > b * 1.08) return "red";

        if (hueDeg >= 20 && hueDeg < 70) return "yellow";
        if (hueDeg >= 70 && hueDeg < 165) return "green";
        if (hueDeg >= 165 && hueDeg < 210) return "cyan";
        if (hueDeg >= 210 && hueDeg < 270) return "blue";
        if (hueDeg >= 270 && hueDeg < 330) return "magenta";
        return "red";
    }

    private static string ToHex(double r, double g, double b)
    {
        var rr = (int)Math.Round(Math.Clamp(r, 0, 1) * 255);
        var gg = (int)Math.Round(Math.Clamp(g, 0, 1) * 255);
        var bb = (int)Math.Round(Math.Clamp(b, 0, 1) * 255);
        return $"#{rr:X2}{gg:X2}{bb:X2}FF";
    }

    private static string NormalizeViewType(string viewType)
    {
        return string.Equals(viewType, "scene", StringComparison.OrdinalIgnoreCase)
            ? "scene"
            : "game";
    }

    private static int NormalizeDimension(int value, int fallback)
    {
        return value <= 0 ? fallback : Math.Clamp(value, 64, 1920);
    }

    private static string ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String
            ? child.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var child)) return 0;
        if (child.ValueKind == JsonValueKind.Number && child.TryGetInt32(out var value)) return value;
        if (child.ValueKind == JsonValueKind.String && int.TryParse(child.GetString(), out var parsed)) return parsed;
        return 0;
    }

    private static bool ReadBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var child)) return false;
        if (child.ValueKind == JsonValueKind.True) return true;
        if (child.ValueKind == JsonValueKind.False) return false;
        if (child.ValueKind == JsonValueKind.Number && child.TryGetInt32(out var number)) return number != 0;
        if (child.ValueKind == JsonValueKind.String && bool.TryParse(child.GetString(), out var parsed)) return parsed;
        return false;
    }

    private static double ReadDouble(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var child) ? ReadDouble(child) : 0d;
    }

    private static double ReadDouble(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0d;
    }

    private static double ReadArrayDouble(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array) return 0d;
        var i = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (i == index) return ReadDouble(item);
            i++;
        }
        return 0d;
    }

    private static double ReadArrayDouble(JsonElement element, string property, int index)
    {
        if (!element.TryGetProperty(property, out var array)) return 0d;
        return ReadArrayDouble(array, index);
    }

    private sealed class ProjectionResult
    {
        public string CameraName { get; set; } = string.Empty;
        public List<ProjectionObject> Objects { get; } = new();
    }

    private sealed class ProjectionObject
    {
        public int InstanceId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string LayerName { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string MaterialName { get; set; } = string.Empty;
        public string MaterialPath { get; set; } = string.Empty;
        public double DistanceToCamera { get; set; }
        public double RectLeft { get; set; }
        public double RectTop { get; set; }
        public double RectWidth { get; set; }
        public double RectHeight { get; set; }
        public List<Point2> SamplePoints { get; } = new();
    }

    private sealed class SemanticProjection
    {
        public string CameraName { get; set; } = string.Empty;
        public List<SemanticPoint> Points { get; } = new();
    }

    private sealed class SemanticPoint
    {
        public bool Matched { get; set; }
        public string MatchReason { get; set; } = string.Empty;
        public int InstanceId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string MaterialName { get; set; } = string.Empty;
        public string MaterialPath { get; set; } = string.Empty;
        public double Distance { get; set; }
    }

    private sealed class Point2
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    private sealed class SampleColor
    {
        public double R { get; set; }
        public double G { get; set; }
        public double B { get; set; }
        public double H { get; set; }
        public double S { get; set; }
        public double V { get; set; }
        public double Luminance { get; set; }
        public string Hex { get; set; } = string.Empty;
    }

    private sealed class Stats
    {
        public double AvgR { get; set; }
        public double AvgG { get; set; }
        public double AvgB { get; set; }
        public double AvgHue { get; set; }
        public double AvgSaturation { get; set; }
        public double AvgLuminance { get; set; }
        public string AvgHex { get; set; } = "#000000FF";
    }
}
