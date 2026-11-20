using System;
using TMPro;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Defines the supported button style values.</summary>
    public enum TopiaForgeButtonStyle
    {
        /// <summary>Brand-orange primary action.</summary>
        Filled,

        /// <summary>Surface with strong border â€” secondary action.</summary>
        Outline,

        /// <summary>No chrome until hover â€” tertiary/inline action.</summary>
        Ghost,

        /// <summary>Filled with the danger role â€” destructive action.</summary>
        Danger,
    }

    /// <summary>
    /// Brand button. The press micro-interaction pushes the body down-left onto its
    /// hard shadow (the "press the sticker" motion) â€” shadow shrink and body offset,
    /// no color flash needed. Hover/pressed tinting rides uGUI's ColorBlock multiplier.
    /// </summary>
    public sealed partial class TopiaForgeButton : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private const float LabelInsetX = 14f;

        private readonly TopiaForgeButtonStyle style;
        private readonly TopiaForgeCueButton button;
        private readonly TopiaForgeRoundedImage? shadow;
        private readonly TopiaForgeRoundedImage fill;
        private readonly TopiaForgeRoundedImage? ring;
        private readonly TextMeshProUGUI? label;
        private readonly Image? iconImage;
        private readonly RectTransform body;
        private bool enabledState = true;
        private string lastText;
        private UiPaintRole backdrop = UiPaintRole.Surface;

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

            // The painted body moves on press. The raycast surface stays inside its layout slot.
            var hitArea = Go.AddComponent<Image>();
            hitArea.color = Color.clear;
            hitArea.raycastTarget = true;

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

            button = Go.AddComponent<TopiaForgeCueButton>();
            button.ObserveCues(ShowCues);
            button.targetGraphic = fill;
            button.onClick.AddListener(() => TopiaForgeCallbacks.Player(Host.OwnerId, onClick, "Button click"));

            // ApplyTheme sets the hovered, selected and pressed tints, which keep the label readable on the fill.
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.disabledColor = Color.white; // disabled visuals are theme-driven, not multiplied
            button.colors = colors;

            if (shadow != null)
            {
                var press = Go.AddComponent<TopiaForgePressEffect>();
                press.Initialize(Host, button, body, shadow);
            }

            ApplyTheme(Theme);
        }

        private bool HasShadow => style == TopiaForgeButtonStyle.Filled || style == TopiaForgeButtonStyle.Danger || style == TopiaForgeButtonStyle.Outline;

        internal void SetBackdrop(UiPaintRole role)
        {
            if (backdrop == role) return;
            backdrop = role;
            ApplyTheme(Theme);
        }

        /// <summary>Gets button.</summary>
        public Button Button => button;

        /// <summary>
        /// Moves keyboard/controller focus to this button. A dialog focuses its default button in the frame it appears, and
        /// Unity drops a selection made while no event system is active, so one is made active first.
        /// </summary>
        public void Focus()
        {
            TopiaForgeEventSystems.EnsureEventSystem();
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

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            if (label != null)
            {
                TopiaForgeTypography.ApplyControlText(label, theme, TopiaForgeTextStyle.Label, Host.EffectiveUiScale);
                FitLabelWidth();
            }
            // Every opaque button uses the state tints calculated for the actual fill and label pair.
            var radius = theme.Descriptor?.Shape.Control;
            fill.SetShape(radius);
            shadow?.SetShape(radius);
            ring?.SetShape(radius, theme.HighContrast ? TopiaForgeTokens.BorderStrong : TopiaForgeTokens.BorderStandard);
            var paint = enabledState && style == TopiaForgeButtonStyle.Filled ? theme.Fill(TopiaForgeFillRole.FilledButton)
                : enabledState && style == TopiaForgeButtonStyle.Danger ? theme.Fill(TopiaForgeFillRole.DangerButton)
                : enabledState && style == TopiaForgeButtonStyle.Outline
                    ? theme.FieldPaint(UiPaintRole.Surface, UiTone.Neutral, enabled: true)
                : (TopiaForgeFillPaint?)null;
            var colors = button.colors;
            colors.highlightedColor = Tint(TopiaForgeOnColors.ButtonHoveredTint);
            colors.selectedColor = Tint(TopiaForgeOnColors.ButtonSelectedTint);
            colors.pressedColor = Tint(TopiaForgeOnColors.ButtonPressedTint);
            button.colors = paint.HasValue ? paint.Value.Tinted(colors) : colors;

            if (!enabledState)
            {
                button.ConfigureCues(UiButtonCue.None);
                fill.color = theme.Descriptor == null ? theme.Tint : theme.SurfaceSunken;
                if (ring != null)
                {
                    ring.color = theme.Descriptor == null ? theme.Tint : theme.BoundaryOn(backdrop);
                }

                if (label != null)
                {
                    label.color = theme.DisabledInk;
                }

                if (iconImage != null)
                {
                    iconImage.color = theme.DisabledInk;
                }

                if (shadow != null)
                {
                    shadow.color = Color.clear;
                }

                return;
            }

            switch (style)
            {
                // A label on a fill takes the color that reads on it, dark on the brand orange where white read at 2.6:1,
                // and the fill's state tints never move it toward that color far enough to stop it reading.
                case TopiaForgeButtonStyle.Filled:
                case TopiaForgeButtonStyle.Danger:
                    var resolved = paint.GetValueOrDefault();
                    fill.color = resolved.Fill;
                    SetContentColor(resolved.Content);
                    break;
                case TopiaForgeButtonStyle.Outline:
                    fill.color = theme.Surface;
                    if (ring != null)
                    {
                        ring.color = theme.BoundaryOn(backdrop);
                    }

                    SetContentColor(paint?.Content ?? theme.Text);
                    break;
                default:
                    // No fill: the label sits on the surface itself, so it takes the Primary tone's readable text color. On
                    // Paper the brand orange reads at only 2.0 to 2.6:1, and labels in that tone are darkened the same way.
                    fill.color = Color.clear;
                    SetContentColor(theme.ForegroundOn(backdrop, UiTone.Primary));
                    break;
            }

            if (shadow != null)
            {
                shadow.color = theme.Shadow;
            }

            ConfigureCues(theme, radius);
        }

        private static Color Tint(float value)
        {
            return new Color(value, value, value, 1f);
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

        private static TopiaForgeRoundedImage CreateStretched(Transform parent, string name, Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<TopiaForgeRoundedImage>();
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
        private Button? button;
        private Image? shadow;
        private Color shadowColor;
        private bool pressed;

        public void Initialize(UiHost owner, Button selectable, RectTransform bodyRect, Image shadowImage)
        {
            host = owner;
            button = selectable;
            body = bodyRect;
            shadow = shadowImage;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (host == null || host.EffectiveReducedMotion || host.EffectiveMotion <= 0f ||
                button == null || !button.IsInteractable() || shadow == null || shadow.color.a == 0f ||
                body == null || pressed || eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            pressed = true;
            var strength = Mathf.Min(1f, host.EffectiveMotion);
            body.anchoredPosition = new Vector2(TopiaForgeTokens.ShadowSmallX * strength, TopiaForgeTokens.ShadowSmallY * strength);
            shadowColor = shadow.color;
            shadow.color = Color.clear;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            ReleasePress();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            ReleasePress();
        }

        private void OnDisable() => ReleasePress();

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
