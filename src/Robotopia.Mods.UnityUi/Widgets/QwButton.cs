using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Robotopia.Mods.UnityUi
{
    public enum QwButtonStyle
    {
        /// <summary>Brand-orange primary action.</summary>
        Filled,

        /// <summary>Surface with strong border — secondary action.</summary>
        Outline,

        /// <summary>No chrome until hover — tertiary/inline action.</summary>
        Ghost,

        /// <summary>Filled with the danger role — destructive action.</summary>
        Danger,
    }

    /// <summary>
    /// Brand button. The press micro-interaction pushes the body down-left onto its
    /// hard shadow (the "press the sticker" motion) — shadow shrink and body offset,
    /// no color flash needed. Hover/pressed tinting rides uGUI's ColorBlock multiplier.
    /// </summary>
    public sealed class QwButton : QwWidget, IQwThemeAware
    {
        private readonly QwButtonStyle style;
        private readonly Button button;
        private readonly Image? shadow;
        private readonly Image fill;
        private readonly Image? ring;
        private readonly TextMeshProUGUI? label;
        private readonly Image? iconImage;
        private readonly RectTransform body;
        private bool enabledState = true;
        private string lastText;

        internal QwButton(QwContainer parent, string text, Action onClick, QwButtonStyle buttonStyle)
            : this(parent, text, null, onClick, buttonStyle)
        {
        }

        internal QwButton(QwContainer parent, QwIcon icon, Action onClick, QwButtonStyle buttonStyle)
            : this(parent, null, icon, onClick, buttonStyle)
        {
        }

        private QwButton(QwContainer parent, string? text, QwIcon? icon, Action onClick, QwButtonStyle buttonStyle)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Button"))
        {
            style = buttonStyle;
            lastText = text ?? string.Empty;

            // Shadow sits outside the body so the body can press down onto it.
            if (HasShadow)
            {
                shadow = CreateStretched(Go.transform, "Shadow", QwSprites.Fill(QwRadius.Control));
                var shadowRect = shadow.rectTransform;
                shadowRect.offsetMin = new Vector2(QwTokens.ShadowSmallX, QwTokens.ShadowSmallY);
                shadowRect.offsetMax = new Vector2(QwTokens.ShadowSmallX, QwTokens.ShadowSmallY);
            }

            var bodyGo = new GameObject("Body", typeof(RectTransform));
            bodyGo.transform.SetParent(Go.transform, false);
            body = (RectTransform)bodyGo.transform;
            QwAnchors.Stretch(body);

            fill = CreateStretched(body, "Fill", QwSprites.Fill(QwRadius.Control));
            fill.raycastTarget = true;

            if (style == QwButtonStyle.Outline)
            {
                ring = CreateStretched(body, "Ring", QwSprites.Ring(QwRadius.Control, QwTokens.BorderStandard));
            }

            if (icon.HasValue)
            {
                var iconGo = new GameObject("Icon", typeof(RectTransform));
                iconGo.transform.SetParent(body, false);
                iconImage = iconGo.AddComponent<Image>();
                iconImage.sprite = QwSprites.Icon(icon.Value);
                iconImage.raycastTarget = false;
                var iconRect = iconImage.rectTransform;
                iconRect.anchorMin = new Vector2(0.5f, 0.5f);
                iconRect.anchorMax = new Vector2(0.5f, 0.5f);
                iconRect.sizeDelta = new Vector2(18f, 18f);
                this.Fixed(QwTokens.ControlHeight, QwTokens.ControlHeight);
            }
            else
            {
                var labelGo = new GameObject("Label", typeof(RectTransform));
                labelGo.transform.SetParent(body, false);
                label = labelGo.AddComponent<TextMeshProUGUI>();
                label.raycastTarget = false;
                label.fontSize = QwTokens.LabelSize;
                label.alignment = TextAlignmentOptions.Center;
                label.textWrappingMode = TextWrappingModes.NoWrap;
                var font = QwFonts.For(QwTextStyle.Label);
                if (font != null)
                {
                    label.font = font;
                }

                if (QwFonts.UseFauxBold)
                {
                    label.fontStyle = FontStyles.Bold;
                }

                label.text = lastText;
                QwAnchors.Stretch((RectTransform)labelGo.transform, 14f, 4f, 14f, 4f);
                this.FixedHeight(QwTokens.ControlHeight);
            }

            button = Go.AddComponent<Button>();
            button.targetGraphic = fill;
            button.onClick.AddListener(() => onClick());

            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.06f, 1.06f, 1.06f, 1f);
            colors.pressedColor = new Color(0.94f, 0.94f, 0.94f, 1f);
            colors.selectedColor = new Color(1.04f, 1.04f, 1.04f, 1f);
            colors.disabledColor = Color.white; // disabled visuals are theme-driven, not multiplied
            button.colors = colors;

            var press = Go.AddComponent<QwPressEffect>();
            press.Initialize(body, shadow);

            ApplyTheme(Theme);
        }

        private bool HasShadow => style == QwButtonStyle.Filled || style == QwButtonStyle.Danger || style == QwButtonStyle.Outline;

        public Button Button => button;

        /// <summary>Dirty-checked label update.</summary>
        public void SetText(string value)
        {
            if (label == null || string.Equals(lastText, value, StringComparison.Ordinal))
            {
                return;
            }

            lastText = value;
            label.text = value;
        }

        /// <summary>Dirty-checked interactability + disabled visuals.</summary>
        public void SetEnabled(bool value)
        {
            if (enabledState == value)
            {
                return;
            }

            enabledState = value;
            button.interactable = value;
            ApplyTheme(Theme);
        }

        public void ApplyTheme(QwResolvedTheme theme)
        {
            if (!enabledState)
            {
                fill.color = theme.Tint;
                if (ring != null)
                {
                    ring.color = theme.Tint;
                }

                if (label != null)
                {
                    label.color = theme.TextFaint;
                }

                if (iconImage != null)
                {
                    iconImage.color = theme.TextFaint;
                }

                if (shadow != null)
                {
                    shadow.color = Color.clear;
                }

                return;
            }

            switch (style)
            {
                case QwButtonStyle.Filled:
                    fill.color = theme.Primary;
                    SetContentColor(theme.OnPrimary);
                    break;
                case QwButtonStyle.Danger:
                    fill.color = theme.Danger;
                    SetContentColor(theme.OnStatus);
                    break;
                case QwButtonStyle.Outline:
                    fill.color = theme.Surface;
                    if (ring != null)
                    {
                        ring.color = theme.OutlineStrong;
                    }

                    SetContentColor(theme.Text);
                    break;
                default:
                    fill.color = Color.clear;
                    SetContentColor(theme.Primary);
                    break;
            }

            if (shadow != null)
            {
                shadow.color = theme.Shadow;
            }
        }

        private void SetContentColor(Color color)
        {
            if (label != null)
            {
                label.color = color;
            }

            if (iconImage != null)
            {
                iconImage.color = color;
            }
        }

        private static Image CreateStretched(Transform parent, string name, Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.raycastTarget = false;
            QwAnchors.Stretch((RectTransform)go.transform);
            return image;
        }
    }

    /// <summary>
    /// Press micro-interaction: shifts the button body onto its shadow while held.
    /// Skipped entirely under reduced motion.
    /// </summary>
    internal sealed class QwPressEffect : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        private RectTransform? body;
        private Image? shadow;
        private Color shadowColor;
        private bool pressed;

        public void Initialize(RectTransform bodyRect, Image? shadowImage)
        {
            body = bodyRect;
            shadow = shadowImage;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (QwTheme.ReducedMotion || body == null || pressed)
            {
                return;
            }

            pressed = true;
            body.anchoredPosition = new Vector2(QwTokens.ShadowSmallX * 0.75f, QwTokens.ShadowSmallY * 0.75f);
            if (shadow != null)
            {
                shadowColor = shadow.color;
                shadow.color = new Color(shadowColor.r, shadowColor.g, shadowColor.b, shadowColor.a * 0.25f);
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            ReleasePress();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            ReleasePress();
        }

        private void ReleasePress()
        {
            if (!pressed || body == null)
            {
                return;
            }

            pressed = false;
            body.anchoredPosition = Vector2.zero;
            if (shadow != null)
            {
                shadow.color = shadowColor;
            }
        }
    }
}
