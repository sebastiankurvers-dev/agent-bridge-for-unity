using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Profiling;
using Unity.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Audio;
using TMPro;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        #region UI Operations

        [BridgeRoute("POST", "/ui/canvas", Category = "ui", Description = "Create Canvas with scaler")]
        public static string CreateCanvas(string jsonData)
        {
            var request = JsonUtility.FromJson<CanvasRequest>(jsonData);

            var renderMode = UIHelpers.ParseRenderMode(request.renderMode);
            var refRes = request.referenceResolution != null && request.referenceResolution.Length >= 2
                ? new Vector2(request.referenceResolution[0], request.referenceResolution[1])
                : new Vector2(1920, 1080);
            var createEventSystem = !request.skipEventSystem;

            var canvasGo = UIHelpers.CreateCanvas(
                request.name ?? "Canvas",
                renderMode,
                refRes,
                createEventSystem
            );

            EditorUtility.SetDirty(canvasGo);

            return JsonResult(new Dictionary<string, object> { { "success", true }, { "instanceId", canvasGo.GetInstanceID() }, { "name", canvasGo.name }, { "message", "Canvas created successfully" } });
        }

        [BridgeRoute("POST", "/ui/element", Category = "ui", Description = "Create UI element")]
        public static string CreateUIElement(string jsonData)
        {
            var request = JsonUtility.FromJson<UIElementRequest>(jsonData);

            Transform parent = null;
            if (request.parentId != 0)
            {
                var parentGo = EditorUtility.EntityIdToObject(request.parentId) as GameObject;
                if (parentGo == null)
                {
                    return JsonError("Parent GameObject not found");
                }
                parent = parentGo.transform;
            }

            if (parent == null)
            {
                // Find or create a Canvas
                var canvas = UnityEngine.Object.FindAnyObjectByType<Canvas>();
                if (canvas == null)
                {
                    var canvasGo = UIHelpers.CreateCanvas("Canvas", RenderMode.ScreenSpaceOverlay, new Vector2(1920, 1080), true);
                    parent = canvasGo.transform;
                }
                else
                {
                    parent = canvas.transform;
                }
            }

            Color? color = null;
            if (request.color != null && request.color.Length >= 3)
            {
                color = UIHelpers.ParseColor(request.color);
            }

            GameObject element = null;
            try
            {
                element = request.elementType?.ToLowerInvariant() switch
                {
                    "panel" => UIHelpers.CreatePanel(request.name ?? "Panel", parent, color),
                    "button" => UIHelpers.CreateButton(request.name ?? "Button", parent, request.text ?? "", color),
                    "image" => UIHelpers.CreateImage(request.name ?? "Image", parent, color, request.sprite),
                    "text" => UIHelpers.CreateTMPText(request.name ?? "Text", parent, request.text ?? "", request.fontSize >= 0 ? request.fontSize : 36,
                        UIHelpers.ParseAlignment(request.alignment), color),
                    "inputfield" => UIHelpers.CreateTMPInputField(request.name ?? "InputField", parent, request.placeholder ?? "Enter text...",
                        UIHelpers.ParseContentType(request.contentType), request.characterLimit >= 0 ? request.characterLimit : 0),
                    "slider" => UIHelpers.CreateSlider(request.name ?? "Slider", parent, request.minValue > -999f ? request.minValue : 0, request.maxValue > -999f ? request.maxValue : 1, request.value > -999f ? request.value : 0.5f),
                    "toggle" => UIHelpers.CreateToggle(request.name ?? "Toggle", parent, request.text ?? "", request.isOn == 1),
                    "dropdown" => UIHelpers.CreateDropdown(request.name ?? "Dropdown", parent, request.options),
                    "scrollview" => UIHelpers.CreateScrollView(request.name ?? "ScrollView", parent, request.horizontal != 0, request.vertical != 0),
                    _ => throw new ArgumentException($"Unknown element type: {request.elementType}")
                };

                EditorUtility.SetDirty(element);

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "instanceId", element.GetInstanceID() }, { "name", element.name }, { "elementType", request.elementType }, { "message", $"UI element '{element.name}' created successfully" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("PUT", "/ui/recttransform/{id}", Category = "ui", Description = "Modify RectTransform")]
        public static string ModifyRectTransform(int instanceId, string jsonData)
        {
            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            var rect = go.GetComponent<RectTransform>();
            if (rect == null)
            {
                return JsonError("GameObject does not have a RectTransform");
            }

            var request = JsonUtility.FromJson<RectTransformRequest>(jsonData);
            Undo.RecordObject(rect, "Agent Bridge Modify RectTransform");

            // Apply anchor preset first if specified
            if (!string.IsNullOrEmpty(request.anchorPreset))
            {
                UIHelpers.ApplyAnchorPreset(rect, request.anchorPreset);
            }
            else
            {
                // Apply custom anchors
                if (request.anchorMin != null)
                    rect.anchorMin = request.anchorMin.ToVector2();
                if (request.anchorMax != null)
                    rect.anchorMax = request.anchorMax.ToVector2();
            }

            // Apply position and size
            if (request.anchoredPosition != null)
                rect.anchoredPosition = request.anchoredPosition.ToVector2();
            if (request.sizeDelta != null)
                rect.sizeDelta = request.sizeDelta.ToVector2();
            if (request.pivot != null)
                rect.pivot = request.pivot.ToVector2();
            if (request.offsetMin != null)
                rect.offsetMin = request.offsetMin.ToVector2();
            if (request.offsetMax != null)
                rect.offsetMax = request.offsetMax.ToVector2();

            EditorUtility.SetDirty(go);

            return JsonResult(new Dictionary<string, object> { { "success", true }, { "instanceId", go.GetInstanceID() }, { "message", "RectTransform modified successfully" } });
        }

        [BridgeRoute("PUT", "/ui/text/{id}", Category = "ui", Description = "Modify TMP text")]
        public static string ModifyTMPText(int instanceId, string jsonData)
        {
            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            var tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
            if (tmp == null)
            {
                return JsonError("GameObject does not have a TextMeshProUGUI component");
            }

            var request = JsonUtility.FromJson<TMPTextRequest>(jsonData);
            Undo.RecordObject(tmp, "Agent Bridge Modify TMP Text");

            if (request.text != null)
                tmp.text = request.text;
            if (request.fontSize >= 0)
                tmp.fontSize = request.fontSize;
            if (!string.IsNullOrEmpty(request.alignment))
                tmp.alignment = UIHelpers.ParseAlignment(request.alignment);
            if (request.color != null && request.color.Length >= 3)
                tmp.color = UIHelpers.ParseColor(request.color);
            if (request.richText >= 0)
                tmp.richText = request.richText == 1;
            if (request.wordWrapping >= 0)
                tmp.textWrappingMode = request.wordWrapping == 1 ? TMPro.TextWrappingModes.Normal : TMPro.TextWrappingModes.NoWrap;
            if (!string.IsNullOrEmpty(request.fontAsset))
            {
                var font = AssetDatabase.LoadAssetAtPath<TMPro.TMP_FontAsset>(request.fontAsset);
                if (font != null) tmp.font = font;
            }

            EditorUtility.SetDirty(tmp);

            return JsonResult(new Dictionary<string, object> { { "success", true }, { "instanceId", go.GetInstanceID() }, { "message", "TextMeshPro text modified successfully" } });
        }

        [BridgeRoute("PUT", "/ui/color/{id}", Category = "ui", Description = "Set UI element color")]
        public static string SetUIColor(int instanceId, string jsonData)
        {
            var go = EditorUtility.EntityIdToObject(instanceId) as GameObject;
            if (go == null)
            {
                return JsonError("GameObject not found");
            }

            var request = JsonUtility.FromJson<ColorRequest>(jsonData);
            if (request.color == null || request.color.Length < 3)
            {
                return JsonError("Color array [r, g, b] or [r, g, b, a] required");
            }

            var color = UIHelpers.ParseColor(request.color);

            // Try to set color on Image
            var image = go.GetComponent<UnityEngine.UI.Image>();
            if (image != null)
            {
                Undo.RecordObject(image, "Agent Bridge Set UI Color");
                image.color = color;
                EditorUtility.SetDirty(image);
                return JsonResult(new Dictionary<string, object> { { "success", true }, { "message", "Image color set successfully" } });
            }

            // Try to set color on TMP text
            var tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
            if (tmp != null)
            {
                Undo.RecordObject(tmp, "Agent Bridge Set UI Color");
                tmp.color = color;
                EditorUtility.SetDirty(tmp);
                return JsonResult(new Dictionary<string, object> { { "success", true }, { "message", "Text color set successfully" } });
            }

            return JsonError("No Image or TextMeshProUGUI component found");
        }

        #endregion
    }
}
