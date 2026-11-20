using System;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Labeled switch (brand: launch-orange track when on, tinted when off, as the launcher
    /// SwitchTheme). Its thumb, and a checkbox's check, take the color that reads on the track:
    /// dark on the orange, where white read at 2.6:1, and a faint grey on the tint. Checkbox()
    /// gives the boxed-check variant.
    /// </summary>
    public sealed class TopiaForgeToggle : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private const float TrackWidth = 40f;
        private const float TrackHeight = 22f;
        private const float ThumbSize = 16f;

        private readonly Toggle toggle;
        private readonly TopiaForgeControlCues cues;
        private readonly UImage track;
        private readonly UImage? thumb;
        private readonly UImage? box;
        private readonly UImage? boxRing;
        private readonly UImage? check;
        private readonly RectTransform? thumbRect;
        private readonly TMPro.TextMeshProUGUI label;
        private readonly bool checkbox;
        private bool value;
        private UiPaintRole backdrop = UiPaintRole.Surface;

        internal TopiaForgeToggle(TopiaForgeContainer parent, string text, bool initialValue, Action<bool> onChanged, bool asCheckbox)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject(asCheckbox ? "Checkbox" : "Toggle"))
        {
            checkbox = asCheckbox;
            value = initialValue;
            TopiaForgeLayout.ApplyRow(Go, TopiaForgeGap.Sm, TopiaForgeGap.None);
            Go.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset((int)UiControlPaint.CompactCueGutter, 0, 0, 0);
            this.FixedHeight(TopiaForgeTokens.ControlSmHeight);
            var hitArea = Go.AddComponent<UImage>();
            hitArea.color = Color.clear;
            hitArea.raycastTarget = true;

            var controlGo = new GameObject("Control", typeof(RectTransform));
            controlGo.transform.SetParent(Go.transform, false);
            var controlLayout = controlGo.AddComponent<LayoutElement>();

            if (checkbox)
            {
                controlLayout.minWidth = TrackHeight;
                controlLayout.preferredWidth = TrackHeight;
                controlLayout.minHeight = TrackHeight;
                controlLayout.preferredHeight = TrackHeight;

                box = controlGo.AddComponent<UImage>();
                box.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Chip);
                box.type = UImage.Type.Sliced;
                track = box;

                var ringGo = new GameObject("Ring", typeof(RectTransform));
                ringGo.transform.SetParent(controlGo.transform, false);
                boxRing = ringGo.AddComponent<UImage>();
                boxRing.sprite = TopiaForgeSprites.Ring(TopiaForgeRadius.Chip, TopiaForgeTokens.BorderStandard);
                boxRing.type = UImage.Type.Sliced;
                boxRing.raycastTarget = false;
                TopiaForgeAnchors.Stretch((RectTransform)ringGo.transform);

                var checkGo = new GameObject("Check", typeof(RectTransform));
                checkGo.transform.SetParent(controlGo.transform, false);
                check = checkGo.AddComponent<UImage>();
                check.sprite = TopiaForgeSprites.Icon(TopiaForgeIcon.Check);
                check.raycastTarget = false;
                var checkRect = (RectTransform)checkGo.transform;
                checkRect.anchorMin = new Vector2(0.5f, 0.5f);
                checkRect.anchorMax = new Vector2(0.5f, 0.5f);
                checkRect.sizeDelta = new Vector2(16f, 16f);
            }
            else
            {
                controlLayout.minWidth = TrackWidth;
                controlLayout.preferredWidth = TrackWidth;
                controlLayout.minHeight = TrackHeight;
                controlLayout.preferredHeight = TrackHeight;

                track = controlGo.AddComponent<UImage>();
                track.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Chip);
                track.type = UImage.Type.Sliced;

                var thumbGo = new GameObject("Thumb", typeof(RectTransform));
                thumbGo.transform.SetParent(controlGo.transform, false);
                thumb = thumbGo.AddComponent<UImage>();
                thumb.sprite = TopiaForgeSprites.Circle();
                thumb.raycastTarget = false;
                thumbRect = (RectTransform)thumbGo.transform;
                thumbRect.anchorMin = new Vector2(0f, 0.5f);
                thumbRect.anchorMax = new Vector2(0f, 0.5f);
                thumbRect.pivot = new Vector2(0.5f, 0.5f);
                thumbRect.sizeDelta = new Vector2(ThumbSize, ThumbSize);
            }

            label = CreateLabel(text);

            toggle = Go.AddComponent<Toggle>();
            toggle.targetGraphic = track;
            toggle.isOn = value;
            cues = TopiaForgeControlCues.Install(toggle, Rect, compact: true);
            toggle.onValueChanged.AddListener(next =>
            {
                if (value == next)
                {
                    return;
                }

                value = next;
                Repaint();
                TopiaForgeCallbacks.Player(Host.OwnerId, onChanged, next, "Toggle change");
            });

            Repaint();
        }

        /// <summary>Gets value.</summary>
        public bool Value => value;

        /// <summary>Dirty-checked programmatic update (does NOT fire onChanged).</summary>
        public void SetValue(bool next)
        {
            if (value == next)
            {
                return;
            }

            value = next;
            toggle.SetIsOnWithoutNotify(next);
            Repaint();
        }

        /// <summary>Sets whether the toggle accepts input.</summary>
        public void SetEnabled(bool enabled)
        {
            if (toggle.interactable == enabled) return;
            toggle.interactable = enabled;
            Repaint();
        }

        internal void SetBackdrop(UiPaintRole role)
        {
            if (backdrop == role) return;
            backdrop = role;
            Repaint();
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            TopiaForgeTypography.ApplyControlText(label, theme, TopiaForgeTextStyle.Body, Host.EffectiveUiScale);
            Repaint();
        }

        private void Repaint()
        {
            var theme = Theme;
            var enabled = toggle.interactable;
            var colors = toggle.colors;
            if (theme.Descriptor != null) colors.disabledColor = Color.white;
            if (checkbox)
            {
                // An unticked box draws nothing on its sunken fill, so it keeps uGUI's default tints.
                var on = theme.Fill(TopiaForgeFillRole.ControlOn);
                toggle.colors = value ? on.Tinted(colors) : DefaultTints(colors);
                track.color = value && enabled ? on.Fill : theme.SurfaceSunken;
                if (boxRing != null)
                {
                    boxRing.color = !enabled ? theme.ForegroundOn(backdrop, UiTone.Muted)
                        : value ? on.Fill : theme.Outline;
                }

                if (check != null)
                {
                    check.color = enabled ? on.Content : theme.DisabledInk;
                    check.enabled = value;
                }
            }
            else
            {
                // The thumb reads on the track in every state: the track's tints never move it toward the thumb far enough to stop it reading.
                var paint = theme.Fill(value ? TopiaForgeFillRole.ControlOn : TopiaForgeFillRole.SwitchOff);
                toggle.colors = paint.Tinted(colors);
                track.color = enabled ? paint.Fill : theme.SurfaceSunken;
                if (thumb != null && thumbRect != null)
                {
                    thumb.color = enabled ? paint.Content : theme.DisabledInk;
                    var x = value ? TrackWidth - (ThumbSize / 2f) - 3f : (ThumbSize / 2f) + 3f;
                    thumbRect.anchoredPosition = new Vector2(x, 0f);
                }
            }

            label.color = theme.ForegroundOn(backdrop, enabled ? UiTone.Neutral : UiTone.Muted);
            cues.Apply(track.color, label.color, theme.Descriptor?.Shape.Control ?? UiThemePackParser.Default.Shape.Control,
                theme.HighContrast);
        }

        private static ColorBlock DefaultTints(ColorBlock block)
        {
            var highlighted = TopiaForgeOnColors.ControlHighlightedTint;
            var pressed = TopiaForgeOnColors.ControlPressedTint;
            block.highlightedColor = new Color(highlighted, highlighted, highlighted, 1f);
            block.selectedColor = new Color(highlighted, highlighted, highlighted, 1f);
            block.pressedColor = new Color(pressed, pressed, pressed, 1f);
            return block;
        }

        private TMPro.TextMeshProUGUI CreateLabel(string text)
        {
            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(Go.transform, false);
            var tmp = TopiaForgeTmp.Create(labelGo);
            tmp.fontSize = TopiaForgeTokens.BodySize;
            tmp.alignment = TMPro.TextAlignmentOptions.Left;
            tmp.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            var font = TopiaForgeFonts.For(TopiaForgeTextStyle.Body);
            if (font != null)
            {
                tmp.font = font;
            }

            tmp.text = text;
            return tmp;
        }
    }
}
