using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Brand text field over TMP_InputField: rounded surface, 2px border that thickens
    /// to the focus ring on focus and turns danger on error. SyncText implements the
    /// external-model echo pattern (skip while the user is typing) that the Zombies
    /// conversation input hand-rolled. Search() adds the magnifier + clear affordance.
    /// </summary>
    public sealed class TopiaForgeInputField : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private readonly TMP_InputField input;
        private readonly UImage fill;
        private readonly UImage ring;
        private readonly UImage focusRing;
        private readonly TextMeshProUGUI textComponent;
        private readonly TextMeshProUGUI placeholderComponent;
        private UImage? searchIcon;
        private GameObject? clearButtonGo;
        private bool focused;
        private bool error;

        internal TopiaForgeInputField(TopiaForgeContainer parent, string placeholder, string value, Action<string> onChanged)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Input"))
        {
            fill = Go.AddComponent<UImage>();
            fill.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Control);
            fill.type = UImage.Type.Sliced;
            fill.raycastTarget = true;

            ring = CreateStretchedImage("Ring", TopiaForgeSprites.Ring(TopiaForgeRadius.Control, TopiaForgeTokens.BorderStandard));
            focusRing = CreateStretchedImage("FocusRing", TopiaForgeSprites.Ring(TopiaForgeRadius.Control, TopiaForgeTokens.BorderStrong));
            focusRing.enabled = false;

            // TMP_InputField needs a masked viewport containing text + placeholder.
            var areaGo = new GameObject("TextArea", typeof(RectTransform));
            areaGo.transform.SetParent(Go.transform, false);
            areaGo.AddComponent<RectMask2D>();
            var areaRect = (RectTransform)areaGo.transform;
            TopiaForgeAnchors.Stretch(areaRect, 12f, 4f, 12f, 4f);

            placeholderComponent = CreateTmp(areaGo.transform, "Placeholder", placeholder);
            placeholderComponent.fontStyle = FontStyles.Italic;
            textComponent = CreateTmp(areaGo.transform, "Text", string.Empty);

            input = Go.AddComponent<TMP_InputField>();
            input.targetGraphic = fill;
            input.textViewport = areaRect;
            input.textComponent = textComponent;
            input.placeholder = placeholderComponent;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.text = value;
            input.onValueChanged.AddListener(next => TopiaForgeCallbacks.Invoke(onChanged, next, "Input change"));
            input.onSelect.AddListener(_ =>
            {
                focused = true;
                Repaint();
            });
            input.onDeselect.AddListener(_ =>
            {
                focused = false;
                Repaint();
            });

            // A text field is a fill-the-line control: claim the row's free width (columns already force-expand).
            this.FillWidth();
            this.FixedHeight(TopiaForgeTokens.ControlHeight);
            ApplyTheme(Theme);
        }

        /// <summary>Gets input.</summary>
        public TMP_InputField Input => input;

        /// <summary>Gets text.</summary>
        public string Text => input.text;

        /// <summary>Gets whether the text field currently has input focus.</summary>
        public bool IsFocused => input.isFocused;

        /// <summary>Enter-to-submit hook (Packages path field, conversation send).</summary>
        public TopiaForgeInputField OnSubmit(Action<string> onSubmit)
        {
            input.onSubmit.AddListener(value => TopiaForgeCallbacks.Invoke(onSubmit, value, "Input submit"));
            return this;
        }

        /// <summary>Search affordance: magnifier icon and a clear (×) button when non-empty.</summary>
        public TopiaForgeInputField Search()
        {
            if (searchIcon != null)
            {
                return this;
            }

            var iconGo = new GameObject("SearchIcon", typeof(RectTransform));
            iconGo.transform.SetParent(Go.transform, false);
            searchIcon = iconGo.AddComponent<UImage>();
            searchIcon.sprite = TopiaForgeSprites.Icon(TopiaForgeIcon.Magnifier);
            searchIcon.raycastTarget = false;
            var iconRect = (RectTransform)iconGo.transform;
            iconRect.anchorMin = new Vector2(0f, 0.5f);
            iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.anchoredPosition = new Vector2(10f, 0f);
            iconRect.sizeDelta = new Vector2(16f, 16f);

            // Make room for the icon.
            var area = input.textViewport;
            area.offsetMin = new Vector2(32f, area.offsetMin.y);

            input.onValueChanged.AddListener(_ => UpdateClearVisibility());
            Repaint();
            return this;
        }

        /// <summary>
        /// External-model sync that never fights the user: applies only when the field
        /// is not focused and the value differs (the conversation echo pattern).
        /// </summary>
        public void SyncText(string modelValue)
        {
            if (input.isFocused || string.Equals(input.text, modelValue, StringComparison.Ordinal))
            {
                return;
            }

            input.SetTextWithoutNotify(modelValue);
        }

        /// <summary>Programmatic set that does not fire onChanged.</summary>
        public void SetText(string value)
        {
            if (string.Equals(input.text, value, StringComparison.Ordinal))
            {
                return;
            }

            input.SetTextWithoutNotify(value);
        }

        /// <summary>Dirty-checked interactability.</summary>
        public void SetEnabled(bool enabled)
        {
            if (input.interactable != enabled)
            {
                input.interactable = enabled;
                Repaint();
            }
        }

        /// <summary>Sets the maximum accepted character count; zero removes the limit.</summary>
        public void SetCharacterLimit(int maximumLength)
        {
            input.characterLimit = maximumLength < 0 ? 0 : maximumLength;
        }

        /// <summary>Error state: danger ring until cleared.</summary>
        public void SetError(bool hasError)
        {
            if (error == hasError)
            {
                return;
            }

            error = hasError;
            Repaint();
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            Repaint();
        }

        private void Repaint()
        {
            var theme = Theme;
            fill.color = input.interactable ? theme.SurfaceSunken : theme.Tint;
            ring.color = error ? theme.Danger : theme.Outline;
            focusRing.color = error ? theme.Danger : theme.FocusRing;
            focusRing.enabled = focused;
            ring.enabled = !focused;
            textComponent.color = theme.Text;
            placeholderComponent.color = theme.TextFaint;
            if (searchIcon != null)
            {
                searchIcon.color = theme.TextMuted;
            }
        }

        private void UpdateClearVisibility()
        {
            // Clear affordance is created lazily the first time text appears.
            if (clearButtonGo == null && input.text.Length > 0)
            {
                // The clear button is a plain image + Button to avoid TopiaForgeButton chrome.
                var clearGo = new GameObject("Clear", typeof(RectTransform));
                clearGo.transform.SetParent(Go.transform, false);
                var icon = clearGo.AddComponent<UImage>();
                icon.sprite = TopiaForgeSprites.Icon(TopiaForgeIcon.Cross);
                icon.color = Theme.TextMuted;
                var rect = (RectTransform)clearGo.transform;
                rect.anchorMin = new Vector2(1f, 0.5f);
                rect.anchorMax = new Vector2(1f, 0.5f);
                rect.pivot = new Vector2(1f, 0.5f);
                rect.anchoredPosition = new Vector2(-10f, 0f);
                rect.sizeDelta = new Vector2(14f, 14f);
                var button = clearGo.AddComponent<Button>();
                button.targetGraphic = icon;
                button.onClick.AddListener(() =>
                {
                    input.text = string.Empty;
                    clearGo.SetActive(false);
                });
                clearButtonGo = clearGo;
            }

            if (clearButtonGo != null)
            {
                clearButtonGo.SetActive(input.text.Length > 0);
            }
        }

        private UImage CreateStretchedImage(string name, Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(Go.transform, false);
            var image = go.AddComponent<UImage>();
            image.sprite = sprite;
            image.type = UImage.Type.Sliced;
            image.raycastTarget = false;
            TopiaForgeAnchors.Stretch((RectTransform)go.transform);
            return image;
        }

        private TextMeshProUGUI CreateTmp(Transform parent, string name, string text)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = TopiaForgeTmp.Create(go);
            tmp.fontSize = TopiaForgeTokens.BodySize;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Overflow;
            var font = TopiaForgeFonts.For(TopiaForgeTextStyle.Body);
            if (font != null)
            {
                tmp.font = font;
            }

            tmp.text = text;
            TopiaForgeAnchors.Stretch((RectTransform)go.transform);
            return tmp;
        }
    }
}
