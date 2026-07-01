using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Robotopia.ModManager
{
    internal static class UiFactory
    {
        private static Font? cachedFont;

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

        public static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null)
            {
                return;
            }

            var eventSystem = new GameObject("RobotopiaModManagerEventSystem");
            UnityEngine.Object.DontDestroyOnLoad(eventSystem);
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<StandaloneInputModule>();
        }

        public static GameObject CreateObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        public static Text CreateText(Transform parent, string name, string text, int size, Color color, TextAnchor alignment)
        {
            var go = CreateObject(name, parent);
            var label = go.AddComponent<Text>();
            label.font = Font;
            label.text = text;
            label.fontSize = size;
            label.color = color;
            label.alignment = alignment;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = size + 10f;
            layout.preferredHeight = size + 12f;
            return label;
        }

        public static Button CreateButton(Transform parent, string name, string text, Action onClick, float width = 150f, float height = 36f)
        {
            var go = CreateObject(name, parent);
            var image = go.AddComponent<Image>();
            image.color = new Color(0.18f, 0.22f, 0.27f, 0.95f);
            var button = go.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => onClick());

            var layout = go.AddComponent<LayoutElement>();
            layout.preferredWidth = width;
            layout.minHeight = height;
            layout.preferredHeight = height;

            var label = CreateText(go.transform, "Text", text, 15, Color.white, TextAnchor.MiddleCenter);
            Stretch(label.rectTransform);
            return button;
        }

        public static InputField CreateInput(Transform parent, string name, string placeholderText, string value, Action<string> onChanged)
        {
            var go = CreateObject(name, parent);
            var image = go.AddComponent<Image>();
            image.color = new Color(0.08f, 0.1f, 0.12f, 0.98f);
            var input = go.AddComponent<InputField>();
            input.text = value;
            input.textComponent = CreateText(go.transform, "Text", value, 14, Color.white, TextAnchor.MiddleLeft);
            input.placeholder = CreateText(go.transform, "Placeholder", placeholderText, 14, new Color(0.65f, 0.68f, 0.72f), TextAnchor.MiddleLeft);
            input.onValueChanged.AddListener(v => onChanged(v));

            Stretch(input.textComponent.rectTransform, 10, 6, 10, 6);
            Stretch(((Text)input.placeholder).rectTransform, 10, 6, 10, 6);

            var layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 36f;
            layout.preferredHeight = 36f;
            return input;
        }

        public static void AddVerticalLayout(GameObject go, float spacing = 8f, int padding = 8)
        {
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
        }

        public static void AddHorizontalLayout(GameObject go, float spacing = 8f, int padding = 0)
        {
            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
        }

        public static void Stretch(RectTransform rect, float left = 0, float top = 0, float right = 0, float bottom = 0)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        public static void SetFixedHeight(GameObject go, float height)
        {
            var layout = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            layout.minHeight = height;
            layout.preferredHeight = height;
        }

        public static GameObject CreatePanel(Transform parent, string name, Color color)
        {
            var go = CreateObject(name, parent);
            var image = go.AddComponent<Image>();
            image.color = color;
            return go;
        }
    }
}
