using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    public enum TopiaForgeButtonStyle
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
    public sealed class TopiaForgeButton : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private const float LabelInsetX = 14f;

        private readonly TopiaForgeButtonStyle style;
        private readonly Button button;
        private readonly Image? shadow;
        private readonly Image fill;
        private readonly Image? ring;
        private readonly TextMeshProUGUI? label;
        private readonly Image? iconImage;
        private readonly RectTransform body;
        private bool enabledState = true;
        private string lastText;

        internal TopiaForgeButton(TopiaForgeContainer parent, string text, Action onClick, TopiaForgeButtonStyle buttonStyle)
            : this(parent, text, null, onClick, buttonStyle)
        {
        }

        internal TopiaForgeButton(TopiaForgeContainer parent, TopiaForgeIcon icon, Action onClick, TopiaForgeButtonStyle buttonStyle)
            : this(parent, null, icon, onClick, buttonStyle)
        {
        }

        private TopiaForgeButton(TopiaForgeContainer parent, string? text, TopiaForgeIcon? icon, Action onClick, TopiaForgeButtonStyle buttonStyle)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Button"))
        {
            style = buttonStyle;
            lastText = text ?? string.Empty;

            // Shadow sits outside the body so the body can press down onto it.
            if (HasShadow)
            {
                shadow = CreateStretched(Go.transform, "Shadow", TopiaForgeSprites.Fill(TopiaForgeRadius.Control));
                var shadowRect = shadow.rectTransform;
                shadowRect.offsetMin = new Vector2(TopiaForgeTokens.ShadowSmallX, TopiaForgeTokens.ShadowSmallY);
                shadowRect.offsetMax = new Vector2(TopiaForgeTokens.ShadowSmallX, TopiaForgeTokens.ShadowSmallY);
            }

            var bodyGo = new GameObject("Body", typeof(RectTransform));
            bodyGo.transform.SetParent(Go.transform, false);
            body = (RectTransform)bodyGo.transform;
            TopiaForgeAnchors.Stretch(body);

            fill = CreateStretched(body, "Fill", TopiaForgeSprites.Fill(TopiaForgeRadius.Control));
            fill.raycastTarget = true;

            if (style == TopiaForgeButtonStyle.Outline)
            {
                ring = CreateStretched(body, "Ring", TopiaForgeSprites.Ring(TopiaForgeRadius.Control, TopiaForgeTokens.BorderStandard));
            }

            if (icon.HasValue)
            {
                var iconGo = new GameObject("Icon", typeof(RectTransform));
                iconGo.transform.SetParent(body, false);
                iconImage = iconGo.AddComponent<Image>();
                iconImage.sprite = TopiaForgeSprites.Icon(icon.Value);
                iconImage.raycastTarget = false;
                var iconRect = iconImage.rectTransform;
                iconRect.anchorMin = new Vector2(0.5f, 0.5f);
                iconRect.anchorMax = new Vector2(0.5f, 0.5f);
                iconRect.sizeDelta = new Vector2(18f, 18f);
                this.Fixed(TopiaForgeTokens.ControlHeight, TopiaForgeTokens.ControlHeight);
            }
            else
            {
                var labelGo = new GameObject("Label", typeof(RectTransform));
                labelGo.transform.SetParent(body, false);
                label = TopiaForgeTmp.Create(labelGo);
                label.fontSize = TopiaForgeTokens.LabelSize;
                label.alignment = TextAlignmentOptions.Center;
                label.textWrappingMode = TextWrappingModes.NoWrap;
                var font = TopiaForgeFonts.For(TopiaForgeTextStyle.Label);
                if (font != null)
                {
                    label.font = font;
                }

                if (TopiaForgeFonts.UseFauxBold)
                {
                    label.fontStyle = FontStyles.Bold;
                }

                label.text = lastText;
                TopiaForgeAnchors.Stretch((RectTransform)labelGo.transform, LabelInsetX, 4f, LabelInsetX, 4f);
                this.FixedHeight(TopiaForgeTokens.ControlHeight);
                FitLabelWidth();
            }

            button = Go.AddComponent<Button>();
            button.targetGraphic = fill;
            button.onClick.AddListener(() => TopiaForgeCallbacks.Invoke(onClick, "Button click"));

            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.06f, 1.06f, 1.06f, 1f);
            colors.pressedColor = new Color(0.94f, 0.94f, 0.94f, 1f);
            colors.selectedColor = new Color(1.04f, 1.04f, 1.04f, 1f);
            colors.disabledColor = Color.white; // disabled visuals are theme-driven, not multiplied
            button.colors = colors;

            var press = Go.AddComponent<TopiaForgePressEffect>();
            press.Initialize(Host, body, shadow);

            ApplyTheme(Theme);
        }

        private bool HasShadow => style == TopiaForgeButtonStyle.Filled || style == TopiaForgeButtonStyle.Danger || style == TopiaForgeButtonStyle.Outline;

        public Button Button => button;

        /// <summary>Moves keyboard/controller focus to this button.</summary>
        public void Focus()
        {
            button.Select();
        }

        /// <summary>Dirty-checked label update.</summary>
        public void SetText(string value)
        {
            if (label == null || string.Equals(lastText, value, StringComparison.Ordinal))
            {
                return;
            }

            lastText = value;
            label.text = value;
            FitLabelWidth();
        }

        /// <summary>
        /// Text buttons must report a preferred width: rows size children to preferred
        /// width (no force-expand), so without this the button collapses to zero and
        /// the no-wrap label spills over its neighbors. Columns still stretch past it.
        /// </summary>
        private void FitLabelWidth()
        {
            if (label == null)
            {
                return;
            }

            var width = label.GetPreferredValues(lastText).x + LabelInsetX * 2f;
            var layout = EnsureLayoutElement();
            layout.minWidth = width;
            layout.preferredWidth = width;
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

        public void ApplyTheme(TopiaForgeResolvedTheme theme)
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
                case TopiaForgeButtonStyle.Filled:
                    fill.color = theme.Primary;
                    SetContentColor(theme.OnPrimary);
                    break;
                case TopiaForgeButtonStyle.Danger:
                    fill.color = theme.Danger;
                    SetContentColor(theme.OnStatus);
                    break;
                case TopiaForgeButtonStyle.Outline:
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
            TopiaForgeAnchors.Stretch((RectTransform)go.transform);
            return image;
        }
    }

    /// <summary>
    /// Press micro-interaction: shifts the button body onto its shadow while held.
    /// Skipped entirely under reduced motion.
    /// </summary>
    internal sealed class TopiaForgePressEffect : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        private RectTransform? body;
        private UiHost? host;
        private Image? shadow;
        private Color shadowColor;
        private bool pressed;

        public void Initialize(UiHost owner, RectTransform bodyRect, Image? shadowImage)
        {
            host = owner;
            body = bodyRect;
            shadow = shadowImage;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (host?.EffectiveReducedMotion != false || body == null || pressed)
            {
                return;
            }

            pressed = true;
            body.anchoredPosition = new Vector2(TopiaForgeTokens.ShadowSmallX * 0.75f, TopiaForgeTokens.ShadowSmallY * 0.75f);
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
