using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UnityEditor;
using TMPro;

namespace UnityAgentBridge
{
    /// <summary>
    /// Helper methods for creating and configuring Unity UI elements.
    /// </summary>
    public static class UIHelpers
    {
        #region Canvas Creation

        /// <summary>
        /// Creates a Canvas with CanvasScaler, GraphicRaycaster, and optional EventSystem.
        /// </summary>
        public static GameObject CreateCanvas(string name, RenderMode renderMode, Vector2 referenceResolution, bool createEventSystem)
        {
            var canvasGo = new GameObject(name);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = renderMode;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = referenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            if (createEventSystem && UnityEngine.Object.FindAnyObjectByType<EventSystem>() == null)
            {
                var eventSystemGo = new GameObject("EventSystem");
                eventSystemGo.AddComponent<EventSystem>();
                eventSystemGo.AddComponent<StandaloneInputModule>();
                Undo.RegisterCreatedObjectUndo(eventSystemGo, "Create EventSystem");
            }

            Undo.RegisterCreatedObjectUndo(canvasGo, "Create Canvas");
            return canvasGo;
        }

        #endregion

        #region UI Element Creation

        /// <summary>
        /// Creates a Panel (Image with RectTransform).
        /// </summary>
        public static GameObject CreatePanel(string name, Transform parent, Color? color = null)
        {
            var panelGo = new GameObject(name);
            panelGo.transform.SetParent(parent, false);

            var rectTransform = panelGo.AddComponent<RectTransform>();
            SetDefaultRectTransform(rectTransform);

            var image = panelGo.AddComponent<Image>();
            image.color = color ?? new Color(1, 1, 1, 0.4f);

            Undo.RegisterCreatedObjectUndo(panelGo, "Create Panel");
            return panelGo;
        }

        /// <summary>
        /// Creates a Button with optional text child.
        /// </summary>
        public static GameObject CreateButton(string name, Transform parent, string text = "", Color? color = null)
        {
            var buttonGo = new GameObject(name);
            buttonGo.transform.SetParent(parent, false);

            var rectTransform = buttonGo.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(160, 30);

            var image = buttonGo.AddComponent<Image>();
            image.color = color ?? Color.white;

            var button = buttonGo.AddComponent<Button>();
            button.targetGraphic = image;

            // Create text child
            if (!string.IsNullOrEmpty(text))
            {
                var textGo = new GameObject("Text");
                textGo.transform.SetParent(buttonGo.transform, false);

                var textRect = textGo.AddComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.sizeDelta = Vector2.zero;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;

                var tmp = textGo.AddComponent<TextMeshProUGUI>();
                tmp.text = text;
                tmp.fontSize = 24;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color = Color.black;
            }

            Undo.RegisterCreatedObjectUndo(buttonGo, "Create Button");
            return buttonGo;
        }

        /// <summary>
        /// Creates an Image element.
        /// </summary>
        public static GameObject CreateImage(string name, Transform parent, Color? color = null, string spritePath = null)
        {
            var imageGo = new GameObject(name);
            imageGo.transform.SetParent(parent, false);

            var rectTransform = imageGo.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(100, 100);

            var image = imageGo.AddComponent<Image>();
            image.color = color ?? Color.white;

            if (!string.IsNullOrEmpty(spritePath))
            {
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                if (sprite != null)
                {
                    image.sprite = sprite;
                }
            }

            Undo.RegisterCreatedObjectUndo(imageGo, "Create Image");
            return imageGo;
        }

        /// <summary>
        /// Creates a TextMeshPro text element.
        /// </summary>
        public static GameObject CreateTMPText(string name, Transform parent, string text = "", float fontSize = 36,
            TextAlignmentOptions alignment = TextAlignmentOptions.Center, Color? color = null)
        {
            var textGo = new GameObject(name);
            textGo.transform.SetParent(parent, false);

            var rectTransform = textGo.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(200, 50);

            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.alignment = alignment;
            tmp.color = color ?? Color.white;
            tmp.textWrappingMode = TMPro.TextWrappingModes.Normal;
            tmp.richText = true;

            Undo.RegisterCreatedObjectUndo(textGo, "Create TMP Text");
            return textGo;
        }

        /// <summary>
        /// Creates a TMP InputField.
        /// </summary>
        public static GameObject CreateTMPInputField(string name, Transform parent, string placeholder = "Enter text...",
            TMP_InputField.ContentType contentType = TMP_InputField.ContentType.Standard, int characterLimit = 0)
        {
            var inputFieldGo = new GameObject(name);
            inputFieldGo.transform.SetParent(parent, false);

            var rectTransform = inputFieldGo.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(200, 50);

            var image = inputFieldGo.AddComponent<Image>();
            image.color = new Color(0.2f, 0.2f, 0.2f, 1f);

            // Text Area
            var textAreaGo = new GameObject("Text Area");
            textAreaGo.transform.SetParent(inputFieldGo.transform, false);
            var textAreaRect = textAreaGo.AddComponent<RectTransform>();
            textAreaRect.anchorMin = Vector2.zero;
            textAreaRect.anchorMax = Vector2.one;
            textAreaRect.offsetMin = new Vector2(10, 6);
            textAreaRect.offsetMax = new Vector2(-10, -7);
            textAreaGo.AddComponent<RectMask2D>();

            // Placeholder
            var placeholderGo = new GameObject("Placeholder");
            placeholderGo.transform.SetParent(textAreaGo.transform, false);
            var placeholderRect = placeholderGo.AddComponent<RectTransform>();
            placeholderRect.anchorMin = Vector2.zero;
            placeholderRect.anchorMax = Vector2.one;
            placeholderRect.sizeDelta = Vector2.zero;
            placeholderRect.offsetMin = Vector2.zero;
            placeholderRect.offsetMax = Vector2.zero;
            var placeholderTmp = placeholderGo.AddComponent<TextMeshProUGUI>();
            placeholderTmp.text = placeholder;
            placeholderTmp.fontSize = 24;
            placeholderTmp.fontStyle = FontStyles.Italic;
            placeholderTmp.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            placeholderTmp.alignment = TextAlignmentOptions.Left;

            // Text
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(textAreaGo.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            var textTmp = textGo.AddComponent<TextMeshProUGUI>();
            textTmp.fontSize = 24;
            textTmp.color = Color.white;
            textTmp.alignment = TextAlignmentOptions.Left;

            // Configure InputField
            var inputField = inputFieldGo.AddComponent<TMP_InputField>();
            inputField.textViewport = textAreaRect;
            inputField.textComponent = textTmp;
            inputField.placeholder = placeholderTmp;
            inputField.contentType = contentType;
            inputField.characterLimit = characterLimit;

            Undo.RegisterCreatedObjectUndo(inputFieldGo, "Create TMP InputField");
            return inputFieldGo;
        }

        /// <summary>
        /// Creates a Slider element.
        /// </summary>
        public static GameObject CreateSlider(string name, Transform parent, float minValue = 0, float maxValue = 1, float value = 0.5f)
        {
            var sliderGo = new GameObject(name);
            sliderGo.transform.SetParent(parent, false);

            var rectTransform = sliderGo.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(160, 20);

            // Background
            var bgGo = new GameObject("Background");
            bgGo.transform.SetParent(sliderGo.transform, false);
            var bgRect = bgGo.AddComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 0.25f);
            bgRect.anchorMax = new Vector2(1, 0.75f);
            bgRect.sizeDelta = Vector2.zero;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            var bgImage = bgGo.AddComponent<Image>();
            bgImage.color = new Color(0.2f, 0.2f, 0.2f, 1f);

            // Fill Area
            var fillAreaGo = new GameObject("Fill Area");
            fillAreaGo.transform.SetParent(sliderGo.transform, false);
            var fillAreaRect = fillAreaGo.AddComponent<RectTransform>();
            fillAreaRect.anchorMin = new Vector2(0, 0.25f);
            fillAreaRect.anchorMax = new Vector2(1, 0.75f);
            fillAreaRect.offsetMin = new Vector2(5, 0);
            fillAreaRect.offsetMax = new Vector2(-15, 0);

            // Fill
            var fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(fillAreaGo.transform, false);
            var fillRect = fillGo.AddComponent<RectTransform>();
            fillRect.sizeDelta = new Vector2(10, 0);
            var fillImage = fillGo.AddComponent<Image>();
            fillImage.color = new Color(0.3f, 0.6f, 0.9f, 1f);

            // Handle Slide Area
            var handleAreaGo = new GameObject("Handle Slide Area");
            handleAreaGo.transform.SetParent(sliderGo.transform, false);
            var handleAreaRect = handleAreaGo.AddComponent<RectTransform>();
            handleAreaRect.anchorMin = Vector2.zero;
            handleAreaRect.anchorMax = Vector2.one;
            handleAreaRect.offsetMin = new Vector2(10, 0);
            handleAreaRect.offsetMax = new Vector2(-10, 0);

            // Handle
            var handleGo = new GameObject("Handle");
            handleGo.transform.SetParent(handleAreaGo.transform, false);
            var handleRect = handleGo.AddComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(20, 0);
            var handleImage = handleGo.AddComponent<Image>();
            handleImage.color = Color.white;

            // Configure Slider
            var slider = sliderGo.AddComponent<Slider>();
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handleImage;
            slider.minValue = minValue;
            slider.maxValue = maxValue;
            slider.value = value;

            Undo.RegisterCreatedObjectUndo(sliderGo, "Create Slider");
            return sliderGo;
        }

        /// <summary>
        /// Creates a Toggle (checkbox) element.
        /// </summary>
        public static GameObject CreateToggle(string name, Transform parent, string label = "", bool isOn = false)
        {
            var toggleGo = new GameObject(name);
            toggleGo.transform.SetParent(parent, false);

            var rectTransform = toggleGo.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(160, 20);

            // Background
            var bgGo = new GameObject("Background");
            bgGo.transform.SetParent(toggleGo.transform, false);
            var bgRect = bgGo.AddComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 0.5f);
            bgRect.anchorMax = new Vector2(0, 0.5f);
            bgRect.sizeDelta = new Vector2(20, 20);
            bgRect.pivot = new Vector2(0, 0.5f);
            bgRect.anchoredPosition = Vector2.zero;
            var bgImage = bgGo.AddComponent<Image>();
            bgImage.color = Color.white;

            // Checkmark
            var checkmarkGo = new GameObject("Checkmark");
            checkmarkGo.transform.SetParent(bgGo.transform, false);
            var checkmarkRect = checkmarkGo.AddComponent<RectTransform>();
            checkmarkRect.anchorMin = new Vector2(0.5f, 0.5f);
            checkmarkRect.anchorMax = new Vector2(0.5f, 0.5f);
            checkmarkRect.sizeDelta = new Vector2(16, 16);
            checkmarkRect.anchoredPosition = Vector2.zero;
            var checkmarkImage = checkmarkGo.AddComponent<Image>();
            checkmarkImage.color = new Color(0.1f, 0.6f, 0.1f, 1f);

            // Label
            if (!string.IsNullOrEmpty(label))
            {
                var labelGo = new GameObject("Label");
                labelGo.transform.SetParent(toggleGo.transform, false);
                var labelRect = labelGo.AddComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = new Vector2(25, 0);
                labelRect.offsetMax = Vector2.zero;
                var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
                labelTmp.text = label;
                labelTmp.fontSize = 18;
                labelTmp.color = Color.white;
                labelTmp.alignment = TextAlignmentOptions.Left;
            }

            // Configure Toggle
            var toggle = toggleGo.AddComponent<Toggle>();
            toggle.targetGraphic = bgImage;
            toggle.graphic = checkmarkImage;
            toggle.isOn = isOn;

            Undo.RegisterCreatedObjectUndo(toggleGo, "Create Toggle");
            return toggleGo;
        }

        /// <summary>
        /// Creates a TMP Dropdown element.
        /// </summary>
        public static GameObject CreateDropdown(string name, Transform parent, string[] options = null)
        {
            var dropdownGo = new GameObject(name);
            dropdownGo.transform.SetParent(parent, false);

            var rectTransform = dropdownGo.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(160, 30);

            var image = dropdownGo.AddComponent<Image>();
            image.color = Color.white;

            // Label
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(dropdownGo.transform, false);
            var labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(10, 6);
            labelRect.offsetMax = new Vector2(-25, -7);
            var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
            labelTmp.text = options != null && options.Length > 0 ? options[0] : "Option A";
            labelTmp.fontSize = 18;
            labelTmp.color = Color.black;
            labelTmp.alignment = TextAlignmentOptions.Left;

            // Arrow
            var arrowGo = new GameObject("Arrow");
            arrowGo.transform.SetParent(dropdownGo.transform, false);
            var arrowRect = arrowGo.AddComponent<RectTransform>();
            arrowRect.anchorMin = new Vector2(1, 0.5f);
            arrowRect.anchorMax = new Vector2(1, 0.5f);
            arrowRect.sizeDelta = new Vector2(20, 20);
            arrowRect.anchoredPosition = new Vector2(-15, 0);
            var arrowImage = arrowGo.AddComponent<Image>();
            arrowImage.color = Color.gray;

            // Template (simple version)
            var templateGo = new GameObject("Template");
            templateGo.transform.SetParent(dropdownGo.transform, false);
            templateGo.SetActive(false);
            var templateRect = templateGo.AddComponent<RectTransform>();
            templateRect.anchorMin = new Vector2(0, 0);
            templateRect.anchorMax = new Vector2(1, 0);
            templateRect.pivot = new Vector2(0.5f, 1);
            templateRect.sizeDelta = new Vector2(0, 150);
            templateGo.AddComponent<Image>();
            var scrollRect = templateGo.AddComponent<ScrollRect>();

            // Viewport
            var viewportGo = new GameObject("Viewport");
            viewportGo.transform.SetParent(templateGo.transform, false);
            var viewportRect = viewportGo.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.sizeDelta = Vector2.zero;
            viewportRect.pivot = new Vector2(0, 1);
            viewportGo.AddComponent<Mask>().showMaskGraphic = false;
            viewportGo.AddComponent<Image>();

            // Content
            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(viewportGo.transform, false);
            var contentRect = contentGo.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1);
            contentRect.sizeDelta = new Vector2(0, 28);

            // Item
            var itemGo = new GameObject("Item");
            itemGo.transform.SetParent(contentGo.transform, false);
            var itemRect = itemGo.AddComponent<RectTransform>();
            itemRect.anchorMin = new Vector2(0, 0.5f);
            itemRect.anchorMax = new Vector2(1, 0.5f);
            itemRect.sizeDelta = new Vector2(0, 28);
            var itemToggle = itemGo.AddComponent<Toggle>();

            // Item Background
            var itemBgGo = new GameObject("Item Background");
            itemBgGo.transform.SetParent(itemGo.transform, false);
            var itemBgRect = itemBgGo.AddComponent<RectTransform>();
            itemBgRect.anchorMin = Vector2.zero;
            itemBgRect.anchorMax = Vector2.one;
            itemBgRect.sizeDelta = Vector2.zero;
            var itemBgImage = itemBgGo.AddComponent<Image>();
            itemBgImage.color = new Color(0.9f, 0.9f, 0.9f, 1f);

            // Item Checkmark
            var itemCheckGo = new GameObject("Item Checkmark");
            itemCheckGo.transform.SetParent(itemGo.transform, false);
            var itemCheckRect = itemCheckGo.AddComponent<RectTransform>();
            itemCheckRect.anchorMin = new Vector2(0, 0.5f);
            itemCheckRect.anchorMax = new Vector2(0, 0.5f);
            itemCheckRect.sizeDelta = new Vector2(20, 20);
            itemCheckRect.anchoredPosition = new Vector2(10, 0);
            var itemCheckImage = itemCheckGo.AddComponent<Image>();
            itemCheckImage.color = Color.black;

            // Item Label
            var itemLabelGo = new GameObject("Item Label");
            itemLabelGo.transform.SetParent(itemGo.transform, false);
            var itemLabelRect = itemLabelGo.AddComponent<RectTransform>();
            itemLabelRect.anchorMin = Vector2.zero;
            itemLabelRect.anchorMax = Vector2.one;
            itemLabelRect.offsetMin = new Vector2(20, 0);
            itemLabelRect.offsetMax = new Vector2(-10, 0);
            var itemLabelTmp = itemLabelGo.AddComponent<TextMeshProUGUI>();
            itemLabelTmp.text = "Option";
            itemLabelTmp.fontSize = 18;
            itemLabelTmp.color = Color.black;
            itemLabelTmp.alignment = TextAlignmentOptions.Left;

            itemToggle.targetGraphic = itemBgImage;
            itemToggle.graphic = itemCheckImage;

            scrollRect.content = contentRect;
            scrollRect.viewport = viewportRect;

            // Configure Dropdown
            var dropdown = dropdownGo.AddComponent<TMP_Dropdown>();
            dropdown.template = templateRect;
            dropdown.captionText = labelTmp;
            dropdown.itemText = itemLabelTmp;
            dropdown.targetGraphic = image;

            // Add options
            if (options != null && options.Length > 0)
            {
                dropdown.ClearOptions();
                dropdown.AddOptions(new System.Collections.Generic.List<string>(options));
            }

            Undo.RegisterCreatedObjectUndo(dropdownGo, "Create Dropdown");
            return dropdownGo;
        }

        /// <summary>
        /// Creates a ScrollView element.
        /// </summary>
        public static GameObject CreateScrollView(string name, Transform parent, bool horizontal = true, bool vertical = true)
        {
            var scrollViewGo = new GameObject(name);
            scrollViewGo.transform.SetParent(parent, false);

            var rectTransform = scrollViewGo.AddComponent<RectTransform>();
            rectTransform.sizeDelta = new Vector2(200, 200);

            var scrollViewImage = scrollViewGo.AddComponent<Image>();
            scrollViewImage.color = new Color(0.1f, 0.1f, 0.1f, 1f);

            // Viewport
            var viewportGo = new GameObject("Viewport");
            viewportGo.transform.SetParent(scrollViewGo.transform, false);
            var viewportRect = viewportGo.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.sizeDelta = Vector2.zero;
            viewportRect.pivot = new Vector2(0, 1);
            viewportGo.AddComponent<Mask>().showMaskGraphic = false;
            viewportGo.AddComponent<Image>();

            // Content
            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(viewportGo.transform, false);
            var contentRect = contentGo.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0, 1);
            contentRect.sizeDelta = new Vector2(0, 300);

            // Configure ScrollRect
            var scrollRect = scrollViewGo.AddComponent<ScrollRect>();
            scrollRect.content = contentRect;
            scrollRect.viewport = viewportRect;
            scrollRect.horizontal = horizontal;
            scrollRect.vertical = vertical;

            Undo.RegisterCreatedObjectUndo(scrollViewGo, "Create ScrollView");
            return scrollViewGo;
        }

        #endregion

        #region RectTransform Helpers

        /// <summary>
        /// Sets default RectTransform to stretch fill parent.
        /// </summary>
        public static void SetDefaultRectTransform(RectTransform rectTransform)
        {
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.sizeDelta = Vector2.zero;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Applies an anchor preset to a RectTransform.
        /// </summary>
        public static void ApplyAnchorPreset(RectTransform rect, string preset)
        {
            switch (preset?.ToLowerInvariant())
            {
                case "topleft":
                    rect.anchorMin = new Vector2(0, 1);
                    rect.anchorMax = new Vector2(0, 1);
                    rect.pivot = new Vector2(0, 1);
                    break;
                case "topcenter":
                    rect.anchorMin = new Vector2(0.5f, 1);
                    rect.anchorMax = new Vector2(0.5f, 1);
                    rect.pivot = new Vector2(0.5f, 1);
                    break;
                case "topright":
                    rect.anchorMin = new Vector2(1, 1);
                    rect.anchorMax = new Vector2(1, 1);
                    rect.pivot = new Vector2(1, 1);
                    break;
                case "middleleft":
                    rect.anchorMin = new Vector2(0, 0.5f);
                    rect.anchorMax = new Vector2(0, 0.5f);
                    rect.pivot = new Vector2(0, 0.5f);
                    break;
                case "center":
                case "middlecenter":
                    rect.anchorMin = new Vector2(0.5f, 0.5f);
                    rect.anchorMax = new Vector2(0.5f, 0.5f);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    break;
                case "middleright":
                    rect.anchorMin = new Vector2(1, 0.5f);
                    rect.anchorMax = new Vector2(1, 0.5f);
                    rect.pivot = new Vector2(1, 0.5f);
                    break;
                case "bottomleft":
                    rect.anchorMin = new Vector2(0, 0);
                    rect.anchorMax = new Vector2(0, 0);
                    rect.pivot = new Vector2(0, 0);
                    break;
                case "bottomcenter":
                    rect.anchorMin = new Vector2(0.5f, 0);
                    rect.anchorMax = new Vector2(0.5f, 0);
                    rect.pivot = new Vector2(0.5f, 0);
                    break;
                case "bottomright":
                    rect.anchorMin = new Vector2(1, 0);
                    rect.anchorMax = new Vector2(1, 0);
                    rect.pivot = new Vector2(1, 0);
                    break;
                case "stretchleft":
                    rect.anchorMin = new Vector2(0, 0);
                    rect.anchorMax = new Vector2(0, 1);
                    rect.pivot = new Vector2(0, 0.5f);
                    break;
                case "stretchcenter":
                    rect.anchorMin = new Vector2(0.5f, 0);
                    rect.anchorMax = new Vector2(0.5f, 1);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    break;
                case "stretchright":
                    rect.anchorMin = new Vector2(1, 0);
                    rect.anchorMax = new Vector2(1, 1);
                    rect.pivot = new Vector2(1, 0.5f);
                    break;
                case "stretchtop":
                    rect.anchorMin = new Vector2(0, 1);
                    rect.anchorMax = new Vector2(1, 1);
                    rect.pivot = new Vector2(0.5f, 1);
                    break;
                case "stretchmiddle":
                    rect.anchorMin = new Vector2(0, 0.5f);
                    rect.anchorMax = new Vector2(1, 0.5f);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    break;
                case "stretchbottom":
                    rect.anchorMin = new Vector2(0, 0);
                    rect.anchorMax = new Vector2(1, 0);
                    rect.pivot = new Vector2(0.5f, 0);
                    break;
                case "stretch":
                case "stretchall":
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    break;
            }
        }

        /// <summary>
        /// Parse TextMeshPro alignment from string.
        /// </summary>
        public static TextAlignmentOptions ParseAlignment(string alignment)
        {
            if (string.IsNullOrEmpty(alignment))
                return TextAlignmentOptions.Center;

            return alignment.ToLowerInvariant() switch
            {
                "topleft" => TextAlignmentOptions.TopLeft,
                "top" or "topcenter" => TextAlignmentOptions.Top,
                "topright" => TextAlignmentOptions.TopRight,
                "left" or "middleleft" => TextAlignmentOptions.Left,
                "center" or "middlecenter" => TextAlignmentOptions.Center,
                "right" or "middleright" => TextAlignmentOptions.Right,
                "bottomleft" => TextAlignmentOptions.BottomLeft,
                "bottom" or "bottomcenter" => TextAlignmentOptions.Bottom,
                "bottomright" => TextAlignmentOptions.BottomRight,
                _ => TextAlignmentOptions.Center
            };
        }

        /// <summary>
        /// Parse RenderMode from string.
        /// </summary>
        public static RenderMode ParseRenderMode(string mode)
        {
            if (string.IsNullOrEmpty(mode))
                return RenderMode.ScreenSpaceOverlay;

            return mode.ToLowerInvariant() switch
            {
                "screenspacecamera" or "camera" => RenderMode.ScreenSpaceCamera,
                "worldspace" or "world" => RenderMode.WorldSpace,
                _ => RenderMode.ScreenSpaceOverlay
            };
        }

        /// <summary>
        /// Parse TMP_InputField.ContentType from string.
        /// </summary>
        public static TMP_InputField.ContentType ParseContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType))
                return TMP_InputField.ContentType.Standard;

            return contentType.ToLowerInvariant() switch
            {
                "autocorrected" => TMP_InputField.ContentType.Autocorrected,
                "integer" or "integernumber" => TMP_InputField.ContentType.IntegerNumber,
                "decimal" or "decimalnumber" => TMP_InputField.ContentType.DecimalNumber,
                "alphanumeric" => TMP_InputField.ContentType.Alphanumeric,
                "name" => TMP_InputField.ContentType.Name,
                "email" or "emailaddress" => TMP_InputField.ContentType.EmailAddress,
                "password" => TMP_InputField.ContentType.Password,
                "pin" => TMP_InputField.ContentType.Pin,
                "custom" => TMP_InputField.ContentType.Custom,
                _ => TMP_InputField.ContentType.Standard
            };
        }

        /// <summary>
        /// Parse TMP_InputField.LineType from string.
        /// </summary>
        public static TMP_InputField.LineType ParseLineType(string lineType)
        {
            if (string.IsNullOrEmpty(lineType))
                return TMP_InputField.LineType.SingleLine;

            return lineType.ToLowerInvariant() switch
            {
                "multilinesubmit" => TMP_InputField.LineType.MultiLineSubmit,
                "multiline" or "multilinenewtine" => TMP_InputField.LineType.MultiLineNewline,
                _ => TMP_InputField.LineType.SingleLine
            };
        }

        /// <summary>
        /// Parse Color from float array [r, g, b] or [r, g, b, a].
        /// </summary>
        public static Color ParseColor(float[] colorArray)
        {
            if (colorArray == null || colorArray.Length < 3)
                return Color.white;

            float r = colorArray[0];
            float g = colorArray[1];
            float b = colorArray[2];
            float a = colorArray.Length >= 4 ? colorArray[3] : 1f;

            return new Color(r, g, b, a);
        }

        #endregion
    }
}
