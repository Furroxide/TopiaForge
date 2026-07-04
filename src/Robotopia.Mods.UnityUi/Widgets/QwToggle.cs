using System;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Labeled switch (brand: launch-orange track when on, tinted when off, white
    /// thumb — the launcher SwitchTheme). Checkbox() gives the boxed-check variant.
    /// </summary>
    public sealed class QwToggle : QwWidget, IQwThemeAware
    {
        private const float TrackWidth = 40f;
        private const float TrackHeight = 22f;
        private const float ThumbSize = 16f;

        private readonly Toggle toggle;
        private readonly UImage track;
        private readonly UImage? thumb;
        private readonly UImage? box;
        private readonly UImage? boxRing;
        private readonly UImage? check;
        private readonly RectTransform? thumbRect;
        private readonly TMPro.TextMeshProUGUI label;
        private readonly bool checkbox;
        private bool value;

        internal QwToggle(QwContainer parent, string text, bool initialValue, Action<bool> onChanged, bool asCheckbox)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject(asCheckbox ? "Checkbox" : "Toggle"))
        {
            checkbox = asCheckbox;
            value = initialValue;
            QwLayout.ApplyRow(Go, QwGap.Sm, QwGap.None);
            this.FixedHeight(QwTokens.ControlSmHeight);

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
                box.sprite = QwSprites.Fill(QwRadius.Chip);
                box.type = UImage.Type.Sliced;
                track = box;

                var ringGo = new GameObject("Ring", typeof(RectTransform));
                ringGo.transform.SetParent(controlGo.transform, false);
                boxRing = ringGo.AddComponent<UImage>();
                boxRing.sprite = QwSprites.Ring(QwRadius.Chip, QwTokens.BorderStandard);
                boxRing.type = UImage.Type.Sliced;
                boxRing.raycastTarget = false;
                QwAnchors.Stretch((RectTransform)ringGo.transform);

                var checkGo = new GameObject("Check", typeof(RectTransform));
                checkGo.transform.SetParent(controlGo.transform, false);
                check = checkGo.AddComponent<UImage>();
                check.sprite = QwSprites.Icon(QwIcon.Check);
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
                track.sprite = QwSprites.Fill(QwRadius.Chip);
                track.type = UImage.Type.Sliced;

                var thumbGo = new GameObject("Thumb", typeof(RectTransform));
                thumbGo.transform.SetParent(controlGo.transform, false);
                thumb = thumbGo.AddComponent<UImage>();
                thumb.sprite = QwSprites.Circle();
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
            toggle.onValueChanged.AddListener(next =>
            {
                if (value == next)
                {
                    return;
                }

                value = next;
                Repaint();
                onChanged(next);
            });

            Repaint();
        }

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

        public void SetEnabled(bool enabled)
        {
            toggle.interactable = enabled;
        }

        public void ApplyTheme(QwResolvedTheme theme)
        {
            Repaint();
        }

        private void Repaint()
        {
            var theme = Theme;
            if (checkbox)
            {
                track.color = value ? theme.Primary : theme.SurfaceSunken;
                if (boxRing != null)
                {
                    boxRing.color = value ? theme.Primary : theme.Outline;
                }

                if (check != null)
                {
                    check.color = theme.OnPrimary;
                    check.enabled = value;
                }
            }
            else
            {
                track.color = value ? theme.Primary : theme.Tint;
                if (thumb != null && thumbRect != null)
                {
                    thumb.color = value ? theme.OnPrimary : theme.TextFaint;
                    var x = value ? TrackWidth - (ThumbSize / 2f) - 3f : (ThumbSize / 2f) + 3f;
                    thumbRect.anchoredPosition = new Vector2(x, 0f);
                }
            }

            label.color = theme.Text;
        }

        private TMPro.TextMeshProUGUI CreateLabel(string text)
        {
            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(Go.transform, false);
            var tmp = QwTmp.Create(labelGo);
            tmp.fontSize = QwTokens.BodySize;
            tmp.alignment = TMPro.TextAlignmentOptions.Left;
            tmp.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            var font = QwFonts.For(QwTextStyle.Body);
            if (font != null)
            {
                tmp.font = font;
            }

            tmp.text = text;
            return tmp;
        }
    }
}
