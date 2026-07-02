using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Robotopia.Mods.UnityUi
{
    [Obsolete("Replaced by the QwUi kit (QwUi.For / UiHost) - see docs/UiKit.md. NeonUi will be removed once all consumers migrate.")]
    public static class NeonUi
    {
        private static Font? cachedFont;
        private static Sprite? whiteSprite;

        public static Font Font
        {
            get
            {
                if (cachedFont == null)
                {
                    cachedFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                }

                return cachedFont;
            }
        }

        public static Sprite WhiteSprite
        {
            get
            {
                if (whiteSprite == null)
                {
                    whiteSprite = Sprite.Create(
                        Texture2D.whiteTexture,
                        new Rect(0f, 0f, Texture2D.whiteTexture.width, Texture2D.whiteTexture.height),
                        new Vector2(0.5f, 0.5f),
                        100f);
                }

                return whiteSprite;
            }
        }

        public static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null)
            {
                return;
            }

            var eventSystem = new GameObject("RobotopiaEventSystem");
            UnityEngine.Object.DontDestroyOnLoad(eventSystem);
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<StandaloneInputModule>();
        }

        public static GameObject CreateOverlayCanvas(string name, int sortingOrder, bool dontDestroy)
        {
            EnsureEventSystem();
            var root = new GameObject(name, typeof(RectTransform));
            if (dontDestroy)
            {
                UnityEngine.Object.DontDestroyOnLoad(root);
            }

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            root.AddComponent<GraphicRaycaster>();
            Stretch(root.GetComponent<RectTransform>());
            return root;
        }

        public static GameObject CreateObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        public static Image CreateImage(Transform parent, string name, Color color)
        {
            var go = CreateObject(name, parent);
            var image = go.AddComponent<Image>();
            image.sprite = WhiteSprite;
            image.color = color;
            return image;
        }

        public static GameObject CreatePanel(Transform parent, string name, Color color, Color? border = null)
        {
            var image = CreateImage(parent, name, color);
            var go = image.gameObject;
            if (border.HasValue)
            {
                AddBorder(go.transform, border.Value, 2f);
            }

            return go;
        }

        public static Text CreateText(
            Transform parent,
            string name,
            string text,
            int size,
            Color color,
            TextAnchor alignment,
            FontStyle style = FontStyle.Normal)
        {
            var go = CreateObject(name, parent);
            var label = go.AddComponent<Text>();
            label.font = Font;
            label.text = text;
            label.fontSize = size;
            label.fontStyle = style;
            label.color = color;
            label.alignment = alignment;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;
            return label;
        }

        public static Button CreateButton(
            Transform parent,
            string name,
            string text,
            Action onClick,
            Vector2 size,
            Color? color = null,
            Color? accent = null)
        {
            var go = CreatePanel(parent, name, color ?? NeonTheme.PanelAlt, accent ?? NeonTheme.Line);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = size;

            var button = go.AddComponent<Button>();
            var image = go.GetComponent<Image>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => onClick());

            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
            colors.pressedColor = new Color(0.72f, 0.85f, 0.92f, 1f);
            colors.selectedColor = new Color(1.18f, 1.18f, 1.18f, 1f);
            colors.disabledColor = new Color(0.35f, 0.38f, 0.42f, 0.72f);
            button.colors = colors;

            var label = CreateText(go.transform, "Label", text, 16, NeonTheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
            Stretch(label.rectTransform, 12f, 4f, 12f, 4f);
            return button;
        }

        public static InputField CreateInput(
            Transform parent,
            string name,
            string placeholder,
            string value,
            Action<string> onChanged)
        {
            var go = CreatePanel(parent, name, new Color(0.02f, 0.035f, 0.055f, 0.96f), NeonTheme.CyanDim);
            var input = go.AddComponent<InputField>();
            input.lineType = InputField.LineType.SingleLine;

            var text = CreateText(go.transform, "Text", value, 15, NeonTheme.Text, TextAnchor.MiddleLeft);
            Stretch(text.rectTransform, 12f, 4f, 12f, 4f);
            input.textComponent = text;

            var hint = CreateText(go.transform, "Placeholder", placeholder, 15, NeonTheme.TextMuted, TextAnchor.MiddleLeft);
            Stretch(hint.rectTransform, 12f, 4f, 12f, 4f);
            input.placeholder = hint;
            input.text = value;
            input.onValueChanged.AddListener(v => onChanged(v));
            return input;
        }

        public static NeonBar CreateBar(Transform parent, string name, Color fillColor, string label = "")
        {
            var go = CreatePanel(parent, name, new Color(0f, 0f, 0f, 0.42f), NeonTheme.CyanDim);
            go.GetComponent<Image>().raycastTarget = false;
            var bar = go.AddComponent<NeonBar>();
            bar.Initialize(fillColor, label);
            return bar;
        }

        public static Text CreateBadge(Transform parent, string name, string text, Color accent, Vector2 size)
        {
            var go = CreatePanel(parent, name, NeonTheme.PanelSoft, accent);
            go.GetComponent<RectTransform>().sizeDelta = size;
            var label = CreateText(go.transform, "Label", text, 13, accent, TextAnchor.MiddleCenter, FontStyle.Bold);
            Stretch(label.rectTransform, 8f, 2f, 8f, 2f);
            return label;
        }

        public static void AddVerticalLayout(GameObject go, float spacing, int padding, bool expandWidth = true)
        {
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.childForceExpandWidth = expandWidth;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
        }

        public static void AddHorizontalLayout(GameObject go, float spacing, int padding, bool expandWidth = false)
        {
            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.childForceExpandWidth = expandWidth;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
        }

        public static LayoutElement EnsureLayout(GameObject go)
        {
            return go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        }

        public static void SetFixedSize(GameObject go, float width, float height)
        {
            var layout = EnsureLayout(go);
            layout.minWidth = width;
            layout.preferredWidth = width;
            layout.minHeight = height;
            layout.preferredHeight = height;
        }

        public static void SetFixedWidth(GameObject go, float width)
        {
            var layout = EnsureLayout(go);
            layout.minWidth = width;
            layout.preferredWidth = width;
        }

        public static void SetFixedHeight(GameObject go, float height)
        {
            var layout = EnsureLayout(go);
            layout.minHeight = height;
            layout.preferredHeight = height;
        }

        public static void SetFlexible(GameObject go, float width, float height)
        {
            var layout = EnsureLayout(go);
            layout.flexibleWidth = width;
            layout.flexibleHeight = height;
        }

        public static void Stretch(RectTransform rect, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        public static void Anchor(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPosition, Vector2 size)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
        }

        public static void AddBorder(Transform parent, Color color, float thickness)
        {
            AddEdge(parent, "BorderTop", color, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, 0f), new Vector2(0f, thickness));
            AddEdge(parent, "BorderBottom", color, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(0f, thickness));
            AddEdge(parent, "BorderLeft", color, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(thickness, 0f));
            AddEdge(parent, "BorderRight", color, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(0f, 0f), new Vector2(thickness, 0f));
        }

        public static void DestroyChildren(Transform parent)
        {
            for (var index = parent.childCount - 1; index >= 0; index--)
            {
                UnityEngine.Object.Destroy(parent.GetChild(index).gameObject);
            }
        }

        public static void SetGraphicAlpha(Graphic graphic, float alpha)
        {
            var color = graphic.color;
            color.a = alpha;
            graphic.color = color;
        }

        public static void SetRaycastRecursive(Transform root, bool enabled)
        {
            var graphics = root.GetComponentsInChildren<Graphic>(true);
            for (var index = 0; index < graphics.Length; index++)
            {
                graphics[index].raycastTarget = enabled;
            }
        }

        private static void AddEdge(
            Transform parent,
            string name,
            Color color,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 anchoredPosition,
            Vector2 size)
        {
            var image = CreateImage(parent, name, color);
            image.raycastTarget = false;
            var rect = image.rectTransform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
        }
    }
}
