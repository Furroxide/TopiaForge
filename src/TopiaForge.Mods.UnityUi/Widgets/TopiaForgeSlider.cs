using System;
using UnityEngine;
using UnityEngine.UI;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Labeled slider skinning uGUI's Slider with brand chrome: tinted rounded track,
    /// accent fill, circular handle. Shows a live value readout.
    /// </summary>
    public sealed class TopiaForgeSlider : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private readonly Slider slider;
        private readonly UImage track;
        private readonly UImage fill;
        private readonly UImage handle;
        private readonly TMPro.TextMeshProUGUI label;
        private readonly TMPro.TextMeshProUGUI valueLabel;
        private float lastShownValue = float.NaN;

        internal TopiaForgeSlider(TopiaForgeContainer parent, string text, float min, float max, float initial, Action<float> onChanged)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Slider"))
        {
            TopiaForgeLayout.ApplyRow(Go, TopiaForgeGap.Sm, TopiaForgeGap.None);
            this.FixedHeight(TopiaForgeTokens.ControlSmHeight);

            label = CreateText("Label", text, TopiaForgeTokens.BodySize);
            var labelLayout = label.gameObject.AddComponent<LayoutElement>();
            labelLayout.minWidth = 120f;
            labelLayout.preferredWidth = 120f;

            // Slider body.
            var sliderGo = new GameObject("SliderBody", typeof(RectTransform));
            sliderGo.transform.SetParent(Go.transform, false);
            var bodyLayout = sliderGo.AddComponent<LayoutElement>();
            bodyLayout.flexibleWidth = 1f;
            bodyLayout.minHeight = 20f;

            var trackGo = new GameObject("Track", typeof(RectTransform));
            trackGo.transform.SetParent(sliderGo.transform, false);
            track = trackGo.AddComponent<UImage>();
            track.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Bar);
            track.type = UImage.Type.Sliced;
            track.raycastTarget = false;
            var trackRect = (RectTransform)trackGo.transform;
            trackRect.anchorMin = new Vector2(0f, 0.5f);
            trackRect.anchorMax = new Vector2(1f, 0.5f);
            trackRect.sizeDelta = new Vector2(0f, 8f);

            var fillAreaGo = new GameObject("FillArea", typeof(RectTransform));
            fillAreaGo.transform.SetParent(sliderGo.transform, false);
            var fillArea = (RectTransform)fillAreaGo.transform;
            fillArea.anchorMin = new Vector2(0f, 0.5f);
            fillArea.anchorMax = new Vector2(1f, 0.5f);
            fillArea.offsetMin = new Vector2(6f, -4f);
            fillArea.offsetMax = new Vector2(-6f, 4f);

            var fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.transform.SetParent(fillAreaGo.transform, false);
            fill = fillGo.AddComponent<UImage>();
            fill.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Bar);
            fill.type = UImage.Type.Sliced;
            fill.raycastTarget = false;
            // Slider.UpdateVisuals drives only the fill's anchors ((0,0) → (value,1)); it never resets the
            // rect. A fresh RectTransform defaults to sizeDelta (100,100), which pads the fill 100px past its
            // anchored area in both axes — an accent-coloured block bleeding over neighbouring controls.
            var fillRect = (RectTransform)fillGo.transform;
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.sizeDelta = Vector2.zero;

            var handleAreaGo = new GameObject("HandleArea", typeof(RectTransform));
            handleAreaGo.transform.SetParent(sliderGo.transform, false);
            var handleArea = (RectTransform)handleAreaGo.transform;
            handleArea.anchorMin = new Vector2(0f, 0.5f);
            handleArea.anchorMax = new Vector2(1f, 0.5f);
            handleArea.offsetMin = new Vector2(8f, 0f);
            handleArea.offsetMax = new Vector2(-8f, 0f);

            var handleGo = new GameObject("Handle", typeof(RectTransform));
            handleGo.transform.SetParent(handleAreaGo.transform, false);
            handle = handleGo.AddComponent<UImage>();
            handle.sprite = TopiaForgeSprites.Circle();
            var handleRect = (RectTransform)handleGo.transform;
            handleRect.sizeDelta = new Vector2(16f, 16f);

            slider = sliderGo.AddComponent<Slider>();
            slider.fillRect = (RectTransform)fillGo.transform;
            slider.handleRect = handleRect;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = initial;

            valueLabel = CreateText("Value", FormatValue(initial), TopiaForgeTokens.CaptionSize);
            var valueLayout = valueLabel.gameObject.AddComponent<LayoutElement>();
            valueLayout.minWidth = 44f;
            valueLayout.preferredWidth = 44f;
            valueLabel.alignment = TMPro.TextAlignmentOptions.Right;

            slider.onValueChanged.AddListener(next =>
            {
                UpdateValueLabel(next);
                TopiaForgeCallbacks.Invoke(onChanged, next, "Slider change");
            });

            ApplyTheme(Theme);
        }

        /// <summary>Gets value.</summary>
        public float Value => slider.value;

        /// <summary>Dirty-checked programmatic update (does NOT fire onChanged).</summary>
        public void SetValue(float next)
        {
            if (Mathf.Approximately(slider.value, next))
            {
                return;
            }

            slider.SetValueWithoutNotify(next);
            UpdateValueLabel(next);
        }

        /// <summary>Sets whether the slider accepts input.</summary>
        public void SetEnabled(bool enabled)
        {
            slider.interactable = enabled;
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            track.color = theme.SurfaceSunken;
            fill.color = theme.Accent;
            handle.color = theme.OnPrimary;
            label.color = theme.Text;
            valueLabel.color = theme.TextMuted;
        }

        private void UpdateValueLabel(float next)
        {
            if (!float.IsNaN(lastShownValue) && Mathf.Abs(lastShownValue - next) < 0.005f)
            {
                return;
            }

            lastShownValue = next;
            valueLabel.text = FormatValue(next);
        }

        private static string FormatValue(float value)
        {
            return value.ToString("0.##");
        }

        private TMPro.TextMeshProUGUI CreateText(string name, string text, int size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(Go.transform, false);
            var tmp = TopiaForgeTmp.Create(go);
            tmp.fontSize = size;
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
