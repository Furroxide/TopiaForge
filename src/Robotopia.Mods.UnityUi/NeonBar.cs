using System;
using UnityEngine;
using UnityEngine.UI;

namespace Robotopia.Mods.UnityUi
{
    [Obsolete("Replaced by QwStatBar/QwProgressBar - see docs/UiKit.md. NeonBar will be removed once all consumers migrate.")]
    public sealed class NeonBar : MonoBehaviour
    {
        private Image? fill;
        private Text? label;
        private Color fillColor;
        private float lastFraction = -1f;
        private float lastWidth = -1f;
        private string lastLabel = string.Empty;
        private Color lastColor;

        public void Initialize(Color color, string labelText)
        {
            fillColor = color;
            lastColor = color;
            fill = NeonUi.CreateImage(transform, "Fill", color);
            fill.raycastTarget = false;
            var fillRect = fill.rectTransform;
            fillRect.anchorMin = new Vector2(0f, 0f);
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.anchoredPosition = Vector2.zero;
            fillRect.sizeDelta = Vector2.zero;

            label = NeonUi.CreateText(transform, "Label", labelText, 12, NeonTheme.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
            label.raycastTarget = false;
            NeonUi.Stretch(label.rectTransform, 4f, 0f, 4f, 0f);
        }

        public void Set(float fraction, string? labelText = null, Color? color = null)
        {
            fraction = Mathf.Clamp01(fraction);
            var rect = GetComponent<RectTransform>();
            var width = rect.rect.width;
            if (fill != null)
            {
                if (Mathf.Abs(lastFraction - fraction) > 0.001f || Mathf.Abs(lastWidth - width) > 0.1f)
                {
                    fill.rectTransform.sizeDelta = new Vector2(width * fraction, 0f);
                    lastFraction = fraction;
                    lastWidth = width;
                }

                var nextColor = color ?? fillColor;
                if (fill.color != nextColor)
                {
                    fill.color = nextColor;
                    lastColor = nextColor;
                }
            }

            if (label != null && labelText != null && lastLabel != labelText)
            {
                label.text = labelText;
                lastLabel = labelText;
            }
        }
    }
}
