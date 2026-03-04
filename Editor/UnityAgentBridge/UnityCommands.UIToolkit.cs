using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        #region UI Toolkit - Asset Management

        private static bool TryNormalizeAssetPath(
            string rawPath,
            string requiredExtension,
            out string assetPath,
            out string fullPath,
            out string error)
        {
            assetPath = null;
            fullPath = null;
            error = null;

            var normalized = (rawPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                error = "Path is required";
                return false;
            }

            normalized = normalized.Replace('\\', '/');

            if (Path.IsPathRooted(normalized))
            {
                error = "Absolute paths are not allowed. Use project-relative paths under Assets/.";
                return false;
            }

            if (!string.IsNullOrEmpty(requiredExtension)
                && !normalized.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
            {
                normalized += requiredExtension;
            }

            normalized = normalized.TrimStart('/');
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "Assets/" + normalized;
            }

            var validated = ValidateAssetPath(normalized);
            if (validated == null)
            {
                error = "Path is outside the project directory";
                return false;
            }

            assetPath = normalized;
            fullPath = validated;
            return true;
        }

        [BridgeRoute("POST", "/uitoolkit/document", Category = "uitoolkit", Description = "Create UIDocument")]
        public static string CreateUIDocument(string jsonData)
        {
            var request = JsonUtility.FromJson<UIDocumentRequest>(jsonData) ?? new UIDocumentRequest();
            var name = request.name ?? "UIDocument";
            string panelSettingsPath = null;
            string uxmlPath = null;

            if (!string.IsNullOrWhiteSpace(request.panelSettingsPath))
            {
                if (!TryNormalizeAssetPath(request.panelSettingsPath, ".asset", out panelSettingsPath, out _, out var pathError))
                    return JsonError($"Invalid panelSettingsPath: {pathError}");
            }

            if (!string.IsNullOrWhiteSpace(request.uxmlPath))
            {
                if (!TryNormalizeAssetPath(request.uxmlPath, ".uxml", out uxmlPath, out _, out var pathError))
                    return JsonError($"Invalid uxmlPath: {pathError}");
            }

            var go = UIToolkitHelpers.CreateUIDocumentGameObject(
                name,
                panelSettingsPath,
                uxmlPath,
                request.sortingOrder);

            EditorUtility.SetDirty(go);

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "instanceId", go.GetInstanceID() },
                { "name", go.name },
                { "message", "UIDocument created successfully" }
            };

            var uiDoc = go.GetComponent<UIDocument>();
            if (uiDoc.panelSettings != null)
                result["panelSettings"] = AssetDatabase.GetAssetPath(uiDoc.panelSettings);
            if (uiDoc.visualTreeAsset != null)
                result["visualTreeAsset"] = AssetDatabase.GetAssetPath(uiDoc.visualTreeAsset);

            return JsonResult(result);
        }

        [BridgeRoute("POST", "/uitoolkit/panelsettings", Category = "uitoolkit", Description = "Create PanelSettings asset")]
        public static string CreatePanelSettings(string jsonData)
        {
            var request = JsonUtility.FromJson<PanelSettingsRequest>(jsonData);
            if (request == null) return JsonError("Invalid PanelSettings request body");

            if (string.IsNullOrEmpty(request.path))
                return JsonError("PanelSettings path is required");

            if (!TryNormalizeAssetPath(request.path, ".asset", out var panelPath, out _, out var pathError))
                return JsonError(pathError);
            request.path = panelPath;

            var ps = UIToolkitHelpers.CreatePanelSettingsAsset(
                request.path,
                request.scaleMode,
                request.referenceResolution,
                request.match,
                request.screenMatchMode);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "path", request.path },
                { "scaleMode", ps.scaleMode.ToString() },
                { "referenceResolution", new float[] { ps.referenceResolution.x, ps.referenceResolution.y } },
                { "screenMatchMode", ps.screenMatchMode.ToString() },
                { "match", ps.match },
                { "message", "PanelSettings created successfully" }
            });
        }

        public static string GetPanelSettings(string path)
        {
            if (string.IsNullOrEmpty(path))
                return JsonError("PanelSettings path is required");

            if (!TryNormalizeAssetPath(path, ".asset", out var panelPath, out _, out var pathError))
                return JsonError(pathError);

            var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelPath);
            if (ps == null)
                return JsonError($"PanelSettings not found at '{panelPath}'");

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "path", panelPath },
                { "scaleMode", ps.scaleMode.ToString() },
                { "referenceResolution", new float[] { ps.referenceResolution.x, ps.referenceResolution.y } },
                { "screenMatchMode", ps.screenMatchMode.ToString() },
                { "match", ps.match },
                { "sortingOrder", ps.sortingOrder }
            });
        }

        [BridgeRoute("POST", "/uitoolkit/uxml", Category = "uitoolkit", Description = "Create UXML file")]
        public static string CreateUXML(string jsonData)
        {
            var request = JsonUtility.FromJson<UXMLRequest>(jsonData);
            if (request == null) return JsonError("Invalid UXML request body");

            if (string.IsNullOrEmpty(request.path))
                return JsonError("UXML path is required");
            if (string.IsNullOrEmpty(request.content))
                return JsonError("UXML content is required");

            if (!TryNormalizeAssetPath(request.path, ".uxml", out var uxmlPath, out _, out var pathError))
                return JsonError(pathError);
            request.path = uxmlPath;

            UIToolkitHelpers.WriteUXMLFile(request.path, request.content);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "path", request.path },
                { "size", request.content.Length },
                { "message", "UXML file created successfully" }
            });
        }

        [BridgeRoute("PUT", "/uitoolkit/uxml", Category = "uitoolkit", Description = "Modify UXML file")]
        public static string ModifyUXML(string jsonData)
        {
            var request = JsonUtility.FromJson<UXMLRequest>(jsonData);
            if (request == null) return JsonError("Invalid UXML request body");

            if (string.IsNullOrEmpty(request.path))
                return JsonError("UXML path is required");
            if (string.IsNullOrEmpty(request.content))
                return JsonError("UXML content is required");
            if (!TryNormalizeAssetPath(request.path, ".uxml", out var uxmlPath, out var fullPath, out var pathError))
                return JsonError(pathError);
            if (!File.Exists(fullPath))
                return JsonError($"UXML file not found at '{uxmlPath}'");
            request.path = uxmlPath;

            UIToolkitHelpers.WriteUXMLFile(request.path, request.content);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "path", request.path },
                { "size", request.content.Length },
                { "message", "UXML file modified successfully" }
            });
        }

        public static string ReadUXML(string path)
        {
            if (string.IsNullOrEmpty(path))
                return JsonError("UXML path is required");

            if (!TryNormalizeAssetPath(path, ".uxml", out var uxmlPath, out _, out var pathError))
                return JsonError(pathError);

            var content = UIToolkitHelpers.ReadFile(uxmlPath);
            if (content == null)
                return JsonError($"UXML file not found at '{uxmlPath}'");

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "path", uxmlPath },
                { "content", content },
                { "size", content.Length }
            });
        }

        [BridgeRoute("POST", "/uitoolkit/uss", Category = "uitoolkit", Description = "Create USS file")]
        public static string CreateUSS(string jsonData)
        {
            var request = JsonUtility.FromJson<USSRequest>(jsonData);
            if (request == null) return JsonError("Invalid USS request body");

            if (string.IsNullOrEmpty(request.path))
                return JsonError("USS path is required");
            if (string.IsNullOrEmpty(request.content))
                return JsonError("USS content is required");

            if (!TryNormalizeAssetPath(request.path, ".uss", out var ussPath, out _, out var pathError))
                return JsonError(pathError);
            request.path = ussPath;

            UIToolkitHelpers.WriteUSSFile(request.path, request.content);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "path", request.path },
                { "size", request.content.Length },
                { "message", "USS file created successfully" }
            });
        }

        [BridgeRoute("PUT", "/uitoolkit/uss", Category = "uitoolkit", Description = "Modify USS file")]
        public static string ModifyUSS(string jsonData)
        {
            var request = JsonUtility.FromJson<USSRequest>(jsonData);
            if (request == null) return JsonError("Invalid USS request body");

            if (string.IsNullOrEmpty(request.path))
                return JsonError("USS path is required");
            if (string.IsNullOrEmpty(request.content))
                return JsonError("USS content is required");
            if (!TryNormalizeAssetPath(request.path, ".uss", out var ussPath, out var fullPath, out var pathError))
                return JsonError(pathError);
            if (!File.Exists(fullPath))
                return JsonError($"USS file not found at '{ussPath}'");
            request.path = ussPath;

            UIToolkitHelpers.WriteUSSFile(request.path, request.content);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "path", request.path },
                { "size", request.content.Length },
                { "message", "USS file modified successfully" }
            });
        }

        public static string ReadUSS(string path)
        {
            if (string.IsNullOrEmpty(path))
                return JsonError("USS path is required");

            if (!TryNormalizeAssetPath(path, ".uss", out var ussPath, out _, out var pathError))
                return JsonError(pathError);

            var content = UIToolkitHelpers.ReadFile(ussPath);
            if (content == null)
                return JsonError($"USS file not found at '{ussPath}'");

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "path", ussPath },
                { "content", content },
                { "size", content.Length }
            });
        }

        #endregion

        #region UI Toolkit - Runtime Visual Tree

        public static string GetVisualTree(int instanceId, string queryJson = null)
        {
            if (!Application.isPlaying)
                return JsonError("Visual tree operations require play mode. UIDocument.rootVisualElement is only available at runtime. Use unity_play_mode first.");

            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null)
                return JsonError("GameObject not found");

            var uiDoc = go.GetComponent<UIDocument>();
            if (uiDoc == null)
                return JsonError("GameObject does not have a UIDocument component");

            var root = uiDoc.rootVisualElement;
            if (root == null)
                return JsonError("UIDocument has no root visual element (is the document loaded?)");

            int maxDepth = -1;
            bool includeStyles = false;
            int offset = 0;
            int limit = -1;
            bool compact = false;
            bool includeBounds = true;
            bool includeClasses = true;
            bool includeText = true;

            if (!string.IsNullOrEmpty(queryJson))
            {
                var queryReq = JsonUtility.FromJson<VisualTreeQueryRequest>(queryJson);
                if (queryReq != null)
                {
                    if (queryReq.maxDepth >= 0) maxDepth = queryReq.maxDepth;
                    includeStyles = queryReq.includeStyles == 1;
                    if (queryReq.offset >= 0) offset = queryReq.offset;
                    if (queryReq.limit >= 0) limit = queryReq.limit;
                    compact = queryReq.compact == 1;
                    includeBounds = queryReq.includeBounds >= 0 ? queryReq.includeBounds == 1 : !compact;
                    includeClasses = queryReq.includeClasses >= 0 ? queryReq.includeClasses == 1 : !compact;
                    includeText = queryReq.includeText >= 0 ? queryReq.includeText == 1 : !compact;
                }
            }

            if (compact) includeStyles = false;
            offset = Mathf.Max(0, offset);
            if (limit >= 0) limit = Mathf.Clamp(limit, 1, 5000);

            var tree = UIToolkitHelpers.SerializeVisualTree(root, maxDepth, includeStyles);
            var projectedTree = ProjectVisualTreeNodes(tree, compact, includeBounds, includeClasses, includeText, includeStyles);
            int totalCount = projectedTree.Count;
            var page = ApplyPagination(projectedTree, offset, limit);
            bool truncated = offset + page.Count < totalCount;

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "instanceId", instanceId },
                { "name", go.name },
                { "elementCount", totalCount },
                { "returnedCount", page.Count },
                { "offset", offset },
                { "limit", limit },
                { "compact", compact },
                { "truncated", truncated },
                { "elements", page }
            });
        }

        public static string QueryVisualElements(int instanceId, string jsonData)
        {
            if (!Application.isPlaying)
                return JsonError("Visual tree operations require play mode. UIDocument.rootVisualElement is only available at runtime. Use unity_play_mode first.");

            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null)
                return JsonError("GameObject not found");

            var uiDoc = go.GetComponent<UIDocument>();
            if (uiDoc == null)
                return JsonError("GameObject does not have a UIDocument component");

            var root = uiDoc.rootVisualElement;
            if (root == null)
                return JsonError("UIDocument has no root visual element");

            var request = JsonUtility.FromJson<VisualTreeQueryRequest>(string.IsNullOrWhiteSpace(jsonData) ? "{}" : jsonData) ?? new VisualTreeQueryRequest();

            var elements = UIToolkitHelpers.QueryElements(root, request.name, request.className, request.typeName);

            bool compact = request.compact == 1;
            bool includeStyles = request.includeStyles == 1;
            if (compact) includeStyles = false;

            bool includeBounds = request.includeBounds >= 0 ? request.includeBounds == 1 : !compact;
            bool includeClasses = request.includeClasses >= 0 ? request.includeClasses == 1 : !compact;
            bool includeText = request.includeText >= 0 ? request.includeText == 1 : !compact;
            int offset = Mathf.Max(0, request.offset);
            int limit = request.limit >= 0 ? Mathf.Clamp(request.limit, 1, 5000) : -1;

            int totalMatchCount = elements.Count;
            IEnumerable<VisualElement> pageElements = elements.Skip(offset);
            if (limit >= 0)
                pageElements = pageElements.Take(limit);

            var serialized = new List<Dictionary<string, object>>();
            foreach (var el in pageElements)
            {
                var node = new Dictionary<string, object>
                {
                    ["type"] = el.GetType().Name,
                    ["name"] = el.name ?? "",
                    ["visible"] = el.visible,
                    ["childCount"] = el.childCount
                };

                if (includeClasses)
                {
                    var classes = el.GetClasses().ToList();
                    if (classes.Count > 0) node["classes"] = classes;
                }
                if (includeText && el is TextElement te && !string.IsNullOrEmpty(te.text))
                    node["text"] = te.text;
                if (includeBounds)
                {
                    node["bounds"] = new Dictionary<string, object>
                    {
                        ["x"] = el.layout.x,
                        ["y"] = el.layout.y,
                        ["width"] = el.layout.width,
                        ["height"] = el.layout.height
                    };
                }

                if (includeStyles)
                {
                    var rs = el.resolvedStyle;
                    node["resolvedStyle"] = new Dictionary<string, object>
                    {
                        ["backgroundColor"] = $"#{ColorUtility.ToHtmlStringRGBA(rs.backgroundColor)}",
                        ["color"] = $"#{ColorUtility.ToHtmlStringRGBA(rs.color)}",
                        ["fontSize"] = rs.fontSize,
                        ["display"] = rs.display.ToString()
                    };
                }

                serialized.Add(node);
            }

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "instanceId", instanceId },
                { "matchCount", serialized.Count },
                { "totalMatchCount", totalMatchCount },
                { "offset", offset },
                { "limit", limit },
                { "compact", compact },
                { "truncated", offset + serialized.Count < totalMatchCount },
                { "elements", serialized }
            });
        }

        private static List<Dictionary<string, object>> ProjectVisualTreeNodes(
            List<Dictionary<string, object>> source,
            bool compact,
            bool includeBounds,
            bool includeClasses,
            bool includeText,
            bool includeStyles)
        {
            var projected = new List<Dictionary<string, object>>(source.Count);
            foreach (var node in source)
            {
                var output = new Dictionary<string, object>
                {
                    { "type", node.TryGetValue("type", out var type) ? type : string.Empty },
                    { "name", node.TryGetValue("name", out var name) ? name : string.Empty },
                    { "depth", node.TryGetValue("depth", out var depth) ? depth : 0 },
                    { "childCount", node.TryGetValue("childCount", out var childCount) ? childCount : 0 },
                    { "visible", node.TryGetValue("visible", out var visible) && visible is bool b && b }
                };

                if (!compact && node.TryGetValue("tooltip", out var tooltip))
                    output["tooltip"] = tooltip;
                if (includeClasses && node.TryGetValue("classes", out var classes))
                    output["classes"] = classes;
                if (includeText && node.TryGetValue("text", out var text))
                    output["text"] = text;
                if (includeBounds && node.TryGetValue("bounds", out var bounds))
                    output["bounds"] = bounds;
                if (includeStyles && node.TryGetValue("resolvedStyle", out var styles))
                    output["resolvedStyle"] = styles;

                projected.Add(output);
            }

            return projected;
        }

        private static List<Dictionary<string, object>> ApplyPagination(List<Dictionary<string, object>> source, int offset, int limit)
        {
            IEnumerable<Dictionary<string, object>> page = source.Skip(Mathf.Max(0, offset));
            if (limit >= 0)
                page = page.Take(limit);
            return page.ToList();
        }

        [BridgeRoute("PUT", "/uitoolkit/element/{id}", Category = "uitoolkit", Description = "Modify visual element")]
        public static string ModifyVisualElement(int instanceId, string jsonData)
        {
            if (!Application.isPlaying)
                return JsonError("Visual tree operations require play mode. UIDocument.rootVisualElement is only available at runtime. Use unity_play_mode first.");

            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null)
                return JsonError("GameObject not found");

            var uiDoc = go.GetComponent<UIDocument>();
            if (uiDoc == null)
                return JsonError("GameObject does not have a UIDocument component");

            var root = uiDoc.rootVisualElement;
            if (root == null)
                return JsonError("UIDocument has no root visual element");

            var request = JsonUtility.FromJson<VisualElementModifyRequest>(jsonData);

            // Find the target element
            var element = UIToolkitHelpers.FindElement(root, request.elementName, request.className, request.typeName);
            if (element == null)
                return JsonError("Visual element not found matching the given criteria");

            // Apply modifications
            if (request.text != null && element is TextElement textEl)
                textEl.text = request.text;

            if (request.tooltip != null)
                element.tooltip = request.tooltip;

            if (request.visible >= 0)
                element.visible = request.visible == 1;

            if (request.addClasses != null)
            {
                foreach (var cls in request.addClasses)
                {
                    if (!string.IsNullOrEmpty(cls))
                        element.AddToClassList(cls);
                }
            }

            if (request.removeClasses != null)
            {
                foreach (var cls in request.removeClasses)
                {
                    if (!string.IsNullOrEmpty(cls))
                        element.RemoveFromClassList(cls);
                }
            }

            // Apply style modifications
            if (!string.IsNullOrEmpty(request.styleJson))
            {
                var styleDict = MiniJSON.Json.Deserialize(request.styleJson) as Dictionary<string, object>;
                if (styleDict != null)
                    UIToolkitHelpers.ApplyStyleModifications(element, styleDict);
            }

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "instanceId", instanceId },
                { "elementName", element.name ?? "" },
                { "elementType", element.GetType().Name },
                { "message", "Visual element modified successfully" }
            });
        }

        [BridgeRoute("POST", "/uitoolkit/element/{id}", Category = "uitoolkit", Description = "Create visual element")]
        public static string CreateVisualElement(int instanceId, string jsonData)
        {
            if (!Application.isPlaying)
                return JsonError("Visual tree operations require play mode. UIDocument.rootVisualElement is only available at runtime. Use unity_play_mode first.");

            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null)
                return JsonError("GameObject not found");

            var uiDoc = go.GetComponent<UIDocument>();
            if (uiDoc == null)
                return JsonError("GameObject does not have a UIDocument component");

            var root = uiDoc.rootVisualElement;
            if (root == null)
                return JsonError("UIDocument has no root visual element");

            var request = JsonUtility.FromJson<VisualElementCreateRequest>(jsonData);

            // Find parent element
            VisualElement parent = root;
            if (!string.IsNullOrEmpty(request.parentSelector))
            {
                parent = root.Q(request.parentSelector);
                if (parent == null)
                    parent = root.Q(name: request.parentSelector);
                if (parent == null)
                    parent = root.Q(className: request.parentSelector);
                if (parent == null)
                    return JsonError($"Parent element not found for selector '{request.parentSelector}'");
            }

            // Create the element
            var element = UIToolkitHelpers.CreateRuntimeElement(request.elementType);

            if (!string.IsNullOrEmpty(request.name))
                element.name = request.name;

            if (request.classes != null)
            {
                foreach (var cls in request.classes)
                {
                    if (!string.IsNullOrEmpty(cls))
                        element.AddToClassList(cls);
                }
            }

            if (request.text != null && element is TextElement te)
                te.text = request.text;
            else if (request.text != null && element is Button btn)
                btn.text = request.text;

            // Apply styles
            if (!string.IsNullOrEmpty(request.styleJson))
            {
                var styleDict = MiniJSON.Json.Deserialize(request.styleJson) as Dictionary<string, object>;
                if (styleDict != null)
                    UIToolkitHelpers.ApplyStyleModifications(element, styleDict);
            }

            // Insert at specified index or append
            if (request.insertIndex >= 0 && request.insertIndex < parent.childCount)
                parent.Insert(request.insertIndex, element);
            else
                parent.Add(element);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "instanceId", instanceId },
                { "elementName", element.name ?? "" },
                { "elementType", element.GetType().Name },
                { "parentName", parent.name ?? "" },
                { "message", "Visual element created successfully" }
            });
        }

        #endregion

        #region UI Toolkit - Migration

        [BridgeRoute("POST", "/uitoolkit/migrate/{id}", Category = "uitoolkit", Description = "Migrate uGUI to UI Toolkit")]
        public static string MigrateUGUIToUIToolkit(int instanceId, string jsonData)
        {
            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null)
                return JsonError("GameObject not found");

            var canvas = go.GetComponent<Canvas>();
            if (canvas == null)
                return JsonError("GameObject does not have a Canvas component (must be a uGUI root)");

            var request = !string.IsNullOrEmpty(jsonData) ? JsonUtility.FromJson<MigrateUIRequest>(jsonData) : new MigrateUIRequest();
            request ??= new MigrateUIRequest();

            var uxmlPath = request.outputUxmlPath;
            var ussPath = request.outputUssPath;

            // Default paths based on canvas name
            if (string.IsNullOrEmpty(uxmlPath))
                uxmlPath = $"Assets/UI/{go.name}.uxml";
            if (string.IsNullOrEmpty(ussPath))
                ussPath = $"Assets/UI/{go.name}.uss";

            if (!TryNormalizeAssetPath(uxmlPath, ".uxml", out var normalizedUxmlPath, out _, out var uxmlPathError))
                return JsonError($"Invalid outputUxmlPath: {uxmlPathError}");
            if (!TryNormalizeAssetPath(ussPath, ".uss", out var normalizedUssPath, out _, out var ussPathError))
                return JsonError($"Invalid outputUssPath: {ussPathError}");
            uxmlPath = normalizedUxmlPath;
            ussPath = normalizedUssPath;

            // Generate UXML
            var uxmlContent = UIToolkitHelpers.GenerateUXMLFromCanvas(go);
            UIToolkitHelpers.WriteUXMLFile(uxmlPath, uxmlContent);

            // Generate USS
            var ussContent = UIToolkitHelpers.GenerateUSSFromCanvas(go);
            UIToolkitHelpers.WriteUSSFile(ussPath, ussContent);

            // Count elements for report
            int elementCount = CountUIElements(go.transform);

            return JsonResult(new Dictionary<string, object>
            {
                { "success", true },
                { "uxmlPath", uxmlPath },
                { "ussPath", ussPath },
                { "uxmlSize", uxmlContent.Length },
                { "ussSize", ussContent.Length },
                { "elementCount", elementCount },
                { "message", $"Migration complete: {elementCount} elements converted to UXML + USS" }
            });
        }

        private static int CountUIElements(Transform parent)
        {
            int count = 1;
            foreach (Transform child in parent)
                count += CountUIElements(child);
            return count;
        }

        #endregion
    }
}
