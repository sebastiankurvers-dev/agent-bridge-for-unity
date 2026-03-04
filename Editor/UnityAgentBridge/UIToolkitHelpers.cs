using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using TMPro;

namespace UnityAgentBridge
{
    public static class UIToolkitHelpers
    {
        #region PanelSettings Creation

        public static PanelSettings CreatePanelSettingsAsset(
            string path,
            string scaleMode = null,
            float[] referenceResolution = null,
            float match = -1f,
            string screenMatchMode = null)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var ps = ScriptableObject.CreateInstance<PanelSettings>();

            if (!string.IsNullOrEmpty(scaleMode))
            {
                ps.scaleMode = ParseScaleMode(scaleMode);
            }
            else
            {
                ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            }

            if (referenceResolution != null && referenceResolution.Length >= 2)
            {
                ps.referenceResolution = new Vector2Int(
                    Mathf.RoundToInt(referenceResolution[0]),
                    Mathf.RoundToInt(referenceResolution[1]));
            }
            else
            {
                ps.referenceResolution = new Vector2Int(1080, 1920);
            }

            if (!string.IsNullOrEmpty(screenMatchMode))
            {
                ps.screenMatchMode = ParseScreenMatchMode(screenMatchMode);
            }
            else
            {
                ps.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            }

            if (match >= 0f)
            {
                ps.match = match;
            }
            else
            {
                ps.match = 0.5f;
            }

            AssetDatabase.CreateAsset(ps, path);
            AssetDatabase.SaveAssets();
            return ps;
        }

        #endregion

        #region UIDocument Creation

        public static GameObject CreateUIDocumentGameObject(
            string name,
            string panelSettingsPath = null,
            string uxmlPath = null,
            int sortingOrder = 0)
        {
            var go = new GameObject(name);
            var uiDoc = go.AddComponent<UIDocument>();

            if (!string.IsNullOrEmpty(panelSettingsPath))
            {
                var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelSettingsPath);
                if (ps != null)
                    uiDoc.panelSettings = ps;
            }

            if (!string.IsNullOrEmpty(uxmlPath))
            {
                var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
                if (tree != null)
                    uiDoc.visualTreeAsset = tree;
            }

            uiDoc.sortingOrder = sortingOrder;
            Undo.RegisterCreatedObjectUndo(go, "Create UIDocument");
            return go;
        }

        #endregion

        #region UXML / USS File Operations

        public static void WriteUXMLFile(string path, string content)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Strip CDATA wrappers that AI agents sometimes add — Unity's UXML parser rejects them
            content = StripCDataWrappers(content);

            File.WriteAllText(path, content, Encoding.UTF8);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        public static void WriteUSSFile(string path, string content)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, content, Encoding.UTF8);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        public static string ReadFile(string path)
        {
            if (!File.Exists(path))
                return null;
            return File.ReadAllText(path, Encoding.UTF8);
        }

        #endregion

        #region Visual Tree Serialization

        public static List<Dictionary<string, object>> SerializeVisualTree(VisualElement root, int maxDepth = -1, bool includeStyles = false)
        {
            var result = new List<Dictionary<string, object>>();
            if (root == null) return result;

            SerializeElement(root, result, 0, maxDepth, includeStyles);
            return result;
        }

        private static void SerializeElement(VisualElement element, List<Dictionary<string, object>> list, int depth, int maxDepth, bool includeStyles)
        {
            var node = new Dictionary<string, object>
            {
                ["type"] = element.GetType().Name,
                ["name"] = element.name ?? "",
                ["depth"] = depth,
                ["childCount"] = element.childCount,
                ["visible"] = element.visible
            };

            var classes = element.GetClasses().ToList();
            if (classes.Count > 0)
                node["classes"] = classes;

            if (!string.IsNullOrEmpty(element.tooltip))
                node["tooltip"] = element.tooltip;

            // Get text from known text-bearing elements
            if (element is TextElement textEl && !string.IsNullOrEmpty(textEl.text))
                node["text"] = textEl.text;

            // Resolved layout
            var layout = element.resolvedStyle;
            node["bounds"] = new Dictionary<string, object>
            {
                ["x"] = element.layout.x,
                ["y"] = element.layout.y,
                ["width"] = element.layout.width,
                ["height"] = element.layout.height
            };

            if (includeStyles)
            {
                var styleDict = new Dictionary<string, object>();
                var rs = element.resolvedStyle;
                styleDict["backgroundColor"] = ColorToHex(rs.backgroundColor);
                styleDict["color"] = ColorToHex(rs.color);
                styleDict["fontSize"] = rs.fontSize;
                styleDict["opacity"] = rs.opacity;
                styleDict["display"] = rs.display.ToString();
                styleDict["visibility"] = rs.visibility.ToString();
                styleDict["flexDirection"] = rs.flexDirection.ToString();
                styleDict["justifyContent"] = rs.justifyContent.ToString();
                styleDict["alignItems"] = rs.alignItems.ToString();
                node["resolvedStyle"] = styleDict;
            }

            list.Add(node);

            if (maxDepth >= 0 && depth >= maxDepth) return;

            foreach (var child in element.Children())
            {
                SerializeElement(child, list, depth + 1, maxDepth, includeStyles);
            }
        }

        #endregion

        #region Element Queries

        public static List<VisualElement> QueryElements(VisualElement root, string name = null, string className = null, string typeName = null)
        {
            var results = new List<VisualElement>();
            if (root == null) return results;

            // Use UI Toolkit's built-in query system
            UQueryBuilder<VisualElement> query = root.Query<VisualElement>();

            if (!string.IsNullOrEmpty(name))
                query = root.Query<VisualElement>(name: name);

            if (!string.IsNullOrEmpty(className))
                query = root.Query<VisualElement>(className: className);

            // Collect results
            var allResults = new List<VisualElement>();
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(className))
            {
                root.Query<VisualElement>(name: name, className: className).ToList(allResults);
            }
            else if (!string.IsNullOrEmpty(name))
            {
                root.Query<VisualElement>(name: name).ToList(allResults);
            }
            else if (!string.IsNullOrEmpty(className))
            {
                root.Query<VisualElement>(className: className).ToList(allResults);
            }
            else
            {
                root.Query<VisualElement>().ToList(allResults);
            }

            // Filter by type name if specified
            if (!string.IsNullOrEmpty(typeName))
            {
                allResults = allResults.Where(e =>
                    e.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return allResults;
        }

        public static VisualElement FindElement(VisualElement root, string name = null, string className = null, string typeName = null)
        {
            if (root == null) return null;

            if (!string.IsNullOrEmpty(name))
            {
                var found = root.Q(name: name);
                if (found != null) return found;
            }

            if (!string.IsNullOrEmpty(className))
            {
                var found = root.Q(className: className);
                if (found != null) return found;
            }

            if (!string.IsNullOrEmpty(typeName))
            {
                var results = new List<VisualElement>();
                root.Query<VisualElement>().ToList(results);
                return results.FirstOrDefault(e =>
                    e.GetType().Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }

        #endregion

        #region Runtime Style Modifications

        public static void ApplyStyleModifications(VisualElement element, Dictionary<string, object> styleDict)
        {
            if (element == null || styleDict == null) return;

            foreach (var kvp in styleDict)
            {
                ApplyStyleProperty(element, kvp.Key, kvp.Value);
            }
        }

        private static void ApplyStyleProperty(VisualElement element, string property, object value)
        {
            var style = element.style;
            var strValue = value?.ToString() ?? "";

            switch (property.ToLowerInvariant())
            {
                case "width":
                    style.width = ParseLength(strValue);
                    break;
                case "height":
                    style.height = ParseLength(strValue);
                    break;
                case "minwidth":
                case "min-width":
                    style.minWidth = ParseLength(strValue);
                    break;
                case "minheight":
                case "min-height":
                    style.minHeight = ParseLength(strValue);
                    break;
                case "maxwidth":
                case "max-width":
                    style.maxWidth = ParseLength(strValue);
                    break;
                case "maxheight":
                case "max-height":
                    style.maxHeight = ParseLength(strValue);
                    break;
                case "flexgrow":
                case "flex-grow":
                    if (float.TryParse(strValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fg))
                        style.flexGrow = fg;
                    break;
                case "flexshrink":
                case "flex-shrink":
                    if (float.TryParse(strValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fs))
                        style.flexShrink = fs;
                    break;
                case "flexdirection":
                case "flex-direction":
                    style.flexDirection = ParseFlexDirection(strValue);
                    break;
                case "justifycontent":
                case "justify-content":
                    style.justifyContent = ParseJustify(strValue);
                    break;
                case "alignitems":
                case "align-items":
                    style.alignItems = ParseAlign(strValue);
                    break;
                case "alignself":
                case "align-self":
                    style.alignSelf = ParseAlign(strValue);
                    break;
                case "backgroundcolor":
                case "background-color":
                    if (ColorUtility.TryParseHtmlString(strValue, out Color bgc))
                        style.backgroundColor = bgc;
                    break;
                case "color":
                    if (ColorUtility.TryParseHtmlString(strValue, out Color c))
                        style.color = c;
                    break;
                case "fontsize":
                case "font-size":
                    style.fontSize = ParseLength(strValue);
                    break;
                case "opacity":
                    if (float.TryParse(strValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float op))
                        style.opacity = op;
                    break;
                case "display":
                    style.display = strValue.ToLowerInvariant() == "none" ? DisplayStyle.None : DisplayStyle.Flex;
                    break;
                case "visibility":
                    style.visibility = strValue.ToLowerInvariant() == "hidden" ? Visibility.Hidden : Visibility.Visible;
                    break;
                case "overflow":
                    style.overflow = strValue.ToLowerInvariant() == "hidden" ? Overflow.Hidden : Overflow.Visible;
                    break;
                case "position":
                    style.position = strValue.ToLowerInvariant() == "absolute" ? Position.Absolute : Position.Relative;
                    break;
                case "top":
                    style.top = ParseLength(strValue);
                    break;
                case "bottom":
                    style.bottom = ParseLength(strValue);
                    break;
                case "left":
                    style.left = ParseLength(strValue);
                    break;
                case "right":
                    style.right = ParseLength(strValue);
                    break;
                case "margin":
                    var ml = ParseLength(strValue);
                    style.marginTop = ml; style.marginRight = ml; style.marginBottom = ml; style.marginLeft = ml;
                    break;
                case "margintop":
                case "margin-top":
                    style.marginTop = ParseLength(strValue);
                    break;
                case "marginright":
                case "margin-right":
                    style.marginRight = ParseLength(strValue);
                    break;
                case "marginbottom":
                case "margin-bottom":
                    style.marginBottom = ParseLength(strValue);
                    break;
                case "marginleft":
                case "margin-left":
                    style.marginLeft = ParseLength(strValue);
                    break;
                case "padding":
                    var pl = ParseLength(strValue);
                    style.paddingTop = pl; style.paddingRight = pl; style.paddingBottom = pl; style.paddingLeft = pl;
                    break;
                case "paddingtop":
                case "padding-top":
                    style.paddingTop = ParseLength(strValue);
                    break;
                case "paddingright":
                case "padding-right":
                    style.paddingRight = ParseLength(strValue);
                    break;
                case "paddingbottom":
                case "padding-bottom":
                    style.paddingBottom = ParseLength(strValue);
                    break;
                case "paddingleft":
                case "padding-left":
                    style.paddingLeft = ParseLength(strValue);
                    break;
                case "borderwidth":
                case "border-width":
                    var bw = ParseLength(strValue);
                    float bwPx = bw.value.value;
                    style.borderTopWidth = bwPx; style.borderRightWidth = bwPx;
                    style.borderBottomWidth = bwPx; style.borderLeftWidth = bwPx;
                    break;
                case "bordercolor":
                case "border-color":
                    if (ColorUtility.TryParseHtmlString(strValue, out Color bc))
                    {
                        style.borderTopColor = bc; style.borderRightColor = bc;
                        style.borderBottomColor = bc; style.borderLeftColor = bc;
                    }
                    break;
                case "borderradius":
                case "border-radius":
                    var br = ParseLength(strValue);
                    style.borderTopLeftRadius = br; style.borderTopRightRadius = br;
                    style.borderBottomLeftRadius = br; style.borderBottomRightRadius = br;
                    break;
                case "unityfontstyle":
                case "-unity-font-style":
                    style.unityFontStyleAndWeight = ParseFontStyle(strValue);
                    break;
                case "unitytextalign":
                case "-unity-text-align":
                    style.unityTextAlign = ParseTextAnchor(strValue);
                    break;
            }
        }

        #endregion

        #region Migration: uGUI to UI Toolkit

        public static string GenerateUXMLFromCanvas(GameObject canvasGo)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:uie=\"UnityEditor.UIElements\">");

            foreach (Transform child in canvasGo.transform)
            {
                GenerateUXMLElement(child.gameObject, sb, 1);
            }

            sb.AppendLine("</ui:UXML>");
            return sb.ToString();
        }

        private static void GenerateUXMLElement(GameObject go, StringBuilder sb, int indent)
        {
            var prefix = new string(' ', indent * 4);
            var name = SanitizeUSSName(go.name);

            // Determine element type from uGUI components
            var button = go.GetComponent<UnityEngine.UI.Button>();
            var tmpText = go.GetComponent<TextMeshProUGUI>();
            var image = go.GetComponent<UnityEngine.UI.Image>();
            var inputField = go.GetComponent<TMP_InputField>();
            var slider = go.GetComponent<UnityEngine.UI.Slider>();
            var toggle = go.GetComponent<UnityEngine.UI.Toggle>();
            var scrollRect = go.GetComponent<UnityEngine.UI.ScrollRect>();

            bool hasChildren = go.transform.childCount > 0;

            if (inputField != null)
            {
                var placeholder = inputField.placeholder as TextMeshProUGUI;
                var placeholderText = placeholder != null ? EscapeXml(placeholder.text) : "";
                sb.AppendLine($"{prefix}<ui:TextField name=\"{name}\" label=\"\" value=\"{EscapeXml(inputField.text)}\" />");
            }
            else if (button != null)
            {
                var btnText = go.GetComponentInChildren<TextMeshProUGUI>();
                var text = btnText != null ? EscapeXml(btnText.text) : "";
                if (hasChildren && go.transform.childCount > 1)
                {
                    sb.AppendLine($"{prefix}<ui:Button name=\"{name}\" text=\"{text}\">");
                    foreach (Transform child in go.transform)
                    {
                        if (child.GetComponent<TextMeshProUGUI>() != null) continue;
                        GenerateUXMLElement(child.gameObject, sb, indent + 1);
                    }
                    sb.AppendLine($"{prefix}</ui:Button>");
                }
                else
                {
                    sb.AppendLine($"{prefix}<ui:Button name=\"{name}\" text=\"{text}\" />");
                }
            }
            else if (slider != null)
            {
                sb.AppendLine($"{prefix}<ui:Slider name=\"{name}\" low-value=\"{slider.minValue}\" high-value=\"{slider.maxValue}\" value=\"{slider.value}\" />");
            }
            else if (toggle != null)
            {
                var labelTmp = go.GetComponentInChildren<TextMeshProUGUI>();
                var label = labelTmp != null ? EscapeXml(labelTmp.text) : "";
                sb.AppendLine($"{prefix}<ui:Toggle name=\"{name}\" label=\"{label}\" value=\"{toggle.isOn.ToString().ToLowerInvariant()}\" />");
            }
            else if (scrollRect != null)
            {
                sb.AppendLine($"{prefix}<ui:ScrollView name=\"{name}\">");
                var content = scrollRect.content;
                if (content != null)
                {
                    foreach (Transform child in content)
                    {
                        GenerateUXMLElement(child.gameObject, sb, indent + 1);
                    }
                }
                sb.AppendLine($"{prefix}</ui:ScrollView>");
            }
            else if (tmpText != null)
            {
                sb.AppendLine($"{prefix}<ui:Label name=\"{name}\" text=\"{EscapeXml(tmpText.text)}\" />");
            }
            else if (image != null || hasChildren)
            {
                if (hasChildren)
                {
                    sb.AppendLine($"{prefix}<ui:VisualElement name=\"{name}\">");
                    foreach (Transform child in go.transform)
                    {
                        GenerateUXMLElement(child.gameObject, sb, indent + 1);
                    }
                    sb.AppendLine($"{prefix}</ui:VisualElement>");
                }
                else
                {
                    sb.AppendLine($"{prefix}<ui:VisualElement name=\"{name}\" />");
                }
            }
            else
            {
                if (hasChildren)
                {
                    sb.AppendLine($"{prefix}<ui:VisualElement name=\"{name}\">");
                    foreach (Transform child in go.transform)
                    {
                        GenerateUXMLElement(child.gameObject, sb, indent + 1);
                    }
                    sb.AppendLine($"{prefix}</ui:VisualElement>");
                }
                else
                {
                    sb.AppendLine($"{prefix}<ui:VisualElement name=\"{name}\" />");
                }
            }
        }

        public static string GenerateUSSFromCanvas(GameObject canvasGo)
        {
            var sb = new StringBuilder();
            CollectUSSRules(canvasGo, sb);
            return sb.ToString();
        }

        private static void CollectUSSRules(GameObject go, StringBuilder sb)
        {
            var name = SanitizeUSSName(go.name);
            var rect = go.GetComponent<RectTransform>();
            var image = go.GetComponent<UnityEngine.UI.Image>();
            var tmpText = go.GetComponent<TextMeshProUGUI>();

            var rules = new List<string>();

            if (rect != null)
            {
                // Size
                if (rect.sizeDelta.x > 0)
                    rules.Add($"    width: {rect.sizeDelta.x}px;");
                if (rect.sizeDelta.y > 0)
                    rules.Add($"    height: {rect.sizeDelta.y}px;");
            }

            if (image != null)
            {
                rules.Add($"    background-color: {ColorToRGBA(image.color)};");
            }

            if (tmpText != null)
            {
                rules.Add($"    color: {ColorToRGBA(tmpText.color)};");
                rules.Add($"    font-size: {tmpText.fontSize}px;");
                rules.Add($"    -unity-text-align: {ConvertTMPAlignment(tmpText.alignment)};");
            }

            if (rules.Count > 0)
            {
                sb.AppendLine($"#{name} {{");
                foreach (var rule in rules)
                    sb.AppendLine(rule);
                sb.AppendLine("}");
                sb.AppendLine();
            }

            foreach (Transform child in go.transform)
            {
                CollectUSSRules(child.gameObject, sb);
            }
        }

        #endregion

        #region Runtime Element Creation

        public static VisualElement CreateRuntimeElement(string elementType)
        {
            return (elementType?.ToLowerInvariant()) switch
            {
                "button" => new Button(),
                "label" => new Label(),
                "textfield" or "text-field" => new TextField(),
                "toggle" => new Toggle(),
                "slider" => new Slider(),
                "sliderint" or "slider-int" => new SliderInt(),
                "minmaxslider" or "min-max-slider" => new MinMaxSlider(),
                "foldout" => new Foldout(),
                "scrollview" or "scroll-view" => new ScrollView(),
                "listview" or "list-view" => new ListView(),
                "image" => new Image(),
                "helpbox" or "help-box" => new HelpBox(),
                "progressbar" or "progress-bar" => new ProgressBar(),
                "dropdownfield" or "dropdown-field" => new DropdownField(),
                "radiobutton" or "radio-button" => new RadioButton(),
                "radiobuttongroup" or "radio-button-group" => new RadioButtonGroup(),
                "groupbox" or "group-box" => new GroupBox(),
                _ => new VisualElement()
            };
        }

        #endregion

        #region Parsers

        public static PanelScaleMode ParseScaleMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return PanelScaleMode.ScaleWithScreenSize;
            return mode.ToLowerInvariant() switch
            {
                "constantpixelsize" or "constant-pixel-size" => PanelScaleMode.ConstantPixelSize,
                "constantphysicalsize" or "constant-physical-size" => PanelScaleMode.ConstantPhysicalSize,
                _ => PanelScaleMode.ScaleWithScreenSize
            };
        }

        public static PanelScreenMatchMode ParseScreenMatchMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return PanelScreenMatchMode.MatchWidthOrHeight;
            return mode.ToLowerInvariant() switch
            {
                "expand" => PanelScreenMatchMode.Expand,
                "shrink" => PanelScreenMatchMode.Shrink,
                _ => PanelScreenMatchMode.MatchWidthOrHeight
            };
        }

        public static FlexDirection ParseFlexDirection(string value)
        {
            if (string.IsNullOrEmpty(value)) return FlexDirection.Column;
            return value.ToLowerInvariant() switch
            {
                "row" => FlexDirection.Row,
                "rowreverse" or "row-reverse" => FlexDirection.RowReverse,
                "columnreverse" or "column-reverse" => FlexDirection.ColumnReverse,
                _ => FlexDirection.Column
            };
        }

        public static Justify ParseJustify(string value)
        {
            if (string.IsNullOrEmpty(value)) return Justify.FlexStart;
            return value.ToLowerInvariant() switch
            {
                "center" => Justify.Center,
                "flexend" or "flex-end" => Justify.FlexEnd,
                "spacebetween" or "space-between" => Justify.SpaceBetween,
                "spacearound" or "space-around" => Justify.SpaceAround,
                _ => Justify.FlexStart
            };
        }

        public static Align ParseAlign(string value)
        {
            if (string.IsNullOrEmpty(value)) return Align.Auto;
            return value.ToLowerInvariant() switch
            {
                "center" => Align.Center,
                "flexstart" or "flex-start" => Align.FlexStart,
                "flexend" or "flex-end" => Align.FlexEnd,
                "stretch" => Align.Stretch,
                _ => Align.Auto
            };
        }

        private static StyleLength ParseLength(string value)
        {
            if (string.IsNullOrEmpty(value)) return new StyleLength(StyleKeyword.Auto);
            value = value.Trim();

            if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return new StyleLength(StyleKeyword.Auto);

            if (value.EndsWith("%"))
            {
                if (float.TryParse(value.TrimEnd('%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pct))
                    return new StyleLength(new Length(pct, LengthUnit.Percent));
            }

            var numStr = value.Replace("px", "").Trim();
            if (float.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px))
                return new StyleLength(px);

            return new StyleLength(StyleKeyword.Auto);
        }

        private static FontStyle ParseFontStyle(string value)
        {
            if (string.IsNullOrEmpty(value)) return FontStyle.Normal;
            return value.ToLowerInvariant() switch
            {
                "bold" => FontStyle.Bold,
                "italic" => FontStyle.Italic,
                "boldanditalic" or "bold-and-italic" => FontStyle.BoldAndItalic,
                _ => FontStyle.Normal
            };
        }

        private static TextAnchor ParseTextAnchor(string value)
        {
            if (string.IsNullOrEmpty(value)) return TextAnchor.MiddleCenter;
            return value.ToLowerInvariant() switch
            {
                "upperleft" or "upper-left" => TextAnchor.UpperLeft,
                "uppercenter" or "upper-center" => TextAnchor.UpperCenter,
                "upperright" or "upper-right" => TextAnchor.UpperRight,
                "middleleft" or "middle-left" => TextAnchor.MiddleLeft,
                "middlecenter" or "middle-center" => TextAnchor.MiddleCenter,
                "middleright" or "middle-right" => TextAnchor.MiddleRight,
                "lowerleft" or "lower-left" => TextAnchor.LowerLeft,
                "lowercenter" or "lower-center" => TextAnchor.LowerCenter,
                "lowerright" or "lower-right" => TextAnchor.LowerRight,
                _ => TextAnchor.MiddleCenter
            };
        }

        #endregion

        #region Utilities

        private static string ColorToHex(Color color)
        {
            return $"#{ColorUtility.ToHtmlStringRGBA(color)}";
        }

        private static string ColorToRGBA(Color color)
        {
            return $"rgba({Mathf.RoundToInt(color.r * 255)}, {Mathf.RoundToInt(color.g * 255)}, {Mathf.RoundToInt(color.b * 255)}, {color.a.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)})";
        }

        private static string SanitizeUSSName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "element";
            return name.Replace(" ", "-").Replace("(", "").Replace(")", "").ToLowerInvariant();
        }

        private static string StripCDataWrappers(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;
            // Remove all <![CDATA[ ... ]]> wrappers, keeping inner content
            while (content.Contains("<![CDATA["))
            {
                int start = content.IndexOf("<![CDATA[");
                int end = content.IndexOf("]]>", start);
                if (end < 0) break;
                content = content.Substring(0, start)
                    + content.Substring(start + 9, end - start - 9)
                    + content.Substring(end + 3);
            }
            return content;
        }

        private static string EscapeXml(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static string ConvertTMPAlignment(TextAlignmentOptions alignment)
        {
            return alignment switch
            {
                TextAlignmentOptions.TopLeft => "upper-left",
                TextAlignmentOptions.Top => "upper-center",
                TextAlignmentOptions.TopRight => "upper-right",
                TextAlignmentOptions.Left => "middle-left",
                TextAlignmentOptions.Center => "middle-center",
                TextAlignmentOptions.Right => "middle-right",
                TextAlignmentOptions.BottomLeft => "lower-left",
                TextAlignmentOptions.Bottom => "lower-center",
                TextAlignmentOptions.BottomRight => "lower-right",
                _ => "middle-center"
            };
        }

        #endregion
    }
}
