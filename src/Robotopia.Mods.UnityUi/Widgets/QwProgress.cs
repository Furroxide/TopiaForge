using TMPro;
using UnityEngine;
using UImage = UnityEngine.UI.Image;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// Brand progress bar: sunken rounded track with a rounded fill. QwStatBar adds an
    /// inner label and threshold-driven auto-toning (the Zombies integrity-bar pattern:
    /// success above warn, warning above crit, danger below). Set() dirty-checks
    /// fraction, width, label, and color so per-frame HUD updates are free when idle.
    /// </summary>
    public class QwProgressBar : QwWidget, IQwThemeAware
    {
        private readonly UImage track;
        private readonly UImage fill;
        private readonly RectTransform fillRect;
        private QwTone tone = QwTone.Accent;
        private float warnThreshold = -1f;
        private float critThreshold = -1f;
        private bool vertical;
        private float lastFraction = -1f;
        private float lastExtent = -1f;
        private QwTone lastAppliedTone;

        internal QwProgressBar(QwContainer parent, string name)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject(name))
        {
            track = Go.AddComponent<UImage>();
            track.sprite = QwSprites.Fill(QwRadius.Bar);
            track.type = UImage.Type.Sliced;
            track.raycastTarget = false;

            var fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.transform.SetParent(Go.transform, false);
            fill = fillGo.AddComponent<UImage>();
            fill.sprite = QwSprites.Fill(QwRadius.Bar);
            fill.type = UImage.Type.Sliced;
            fill.raycastTarget = false;
            fillRect = fill.rectTransform;
            SetFillAnchors();

            this.FixedHeight(18f);
            ApplyTheme(Theme);
        }

        /// <summary>Fixed color role for the fill (default Accent).</summary>
        public QwProgressBar Tone(QwTone value)
        {
            tone = value;
            warnThreshold = -1f;
            critThreshold = -1f;
            ApplyFillColor(value);
            return this;
        }

        /// <summary>
        /// Auto-tone by value: success above warn, warning above crit, danger below —
        /// replaces the hand-rolled threshold color switches in HUD code.
        /// </summary>
        public QwProgressBar Thresholds(float warn, float crit)
        {
            warnThreshold = warn;
            critThreshold = crit;
            return this;
        }

        /// <summary>Vertical fill (bottom-up) for meters like the combo tier bar.</summary>
        public QwProgressBar Vertical()
        {
            vertical = true;
            SetFillAnchors();
            return this;
        }

        /// <summary>
        /// Dirty-checked runtime tone switch (the conversation-timer danger-under-25%
        /// pattern). Clears any Thresholds() auto-toning.
        /// </summary>
        public void SetTone(QwTone value)
        {
            if (warnThreshold < 0f && value == tone && value == lastAppliedTone)
            {
                return;
            }

            tone = value;
            warnThreshold = -1f;
            critThreshold = -1f;
            ApplyFillColor(value);
        }

        /// <summary>Dirty-checked fraction update.</summary>
        public void SetFraction(float fraction)
        {
            fraction = Mathf.Clamp01(fraction);

            if (warnThreshold >= 0f)
            {
                var autoTone = fraction < critThreshold ? QwTone.Danger
                    : fraction < warnThreshold ? QwTone.Warning
                    : QwTone.Success;
                if (autoTone != lastAppliedTone)
                {
                    ApplyFillColor(autoTone);
                }
            }

            var extent = vertical ? Rect.rect.height : Rect.rect.width;
            if (Mathf.Abs(lastFraction - fraction) <= 0.001f && Mathf.Abs(lastExtent - extent) <= 0.1f)
            {
                return;
            }

            lastFraction = fraction;
            lastExtent = extent;
            fillRect.sizeDelta = vertical
                ? new Vector2(0f, extent * fraction)
                : new Vector2(extent * fraction, 0f);
        }

        public virtual void ApplyTheme(QwResolvedTheme theme)
        {
            track.color = theme.SurfaceSunken;
            ApplyFillColor(warnThreshold >= 0f ? lastAppliedTone : tone);
        }

        private void ApplyFillColor(QwTone value)
        {
            lastAppliedTone = value;
            fill.color = Theme.ToneColor(value);
        }

        private void SetFillAnchors()
        {
            if (vertical)
            {
                fillRect.anchorMin = new Vector2(0f, 0f);
                fillRect.anchorMax = new Vector2(1f, 0f);
                fillRect.pivot = new Vector2(0.5f, 0f);
            }
            else
            {
                fillRect.anchorMin = new Vector2(0f, 0f);
                fillRect.anchorMax = new Vector2(0f, 1f);
                fillRect.pivot = new Vector2(0f, 0.5f);
            }

            fillRect.anchoredPosition = Vector2.zero;
            fillRect.sizeDelta = Vector2.zero;
        }
    }

    /// <summary>Progress bar with an inner label ("INTEGRITY 87").</summary>
    public sealed class QwStatBar : QwProgressBar
    {
        private readonly TextMeshProUGUI label;
        private string lastLabel;
        private byte cachedComposition;
        private string cachedPrefix = string.Empty;
        private string cachedMiddle = string.Empty;
        private string cachedSuffix = string.Empty;
        private int cachedValue = int.MinValue;
        private int cachedValue2 = int.MinValue;

        internal QwStatBar(QwContainer parent, string title)
            : base(parent, "StatBar")
        {
            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(Go.transform, false);
            label = QwTmp.Create(labelGo);
            label.fontSize = QwTokens.CaptionSize;
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

            lastLabel = title;
            label.text = title;
            QwAnchors.Stretch((RectTransform)labelGo.transform, 6f, 0f, 6f, 0f);
            ApplyTheme(Theme);
        }

        /// <summary>Dirty-checked label update.</summary>
        public void SetLabel(string value)
        {
            if (string.Equals(lastLabel, value, System.StringComparison.Ordinal))
            {
                return;
            }

            cachedComposition = 0;
            lastLabel = value;
            label.text = value;
        }

        /// <summary>Prefix + int label that only concatenates on change ("INTEGRITY ", 87).</summary>
        public void SetLabel(string prefix, int value)
        {
            if (cachedComposition == 1 && value == cachedValue && ReferenceEquals(prefix, cachedPrefix))
            {
                return;
            }

            cachedComposition = 1;
            cachedPrefix = prefix;
            cachedValue = value;
            lastLabel = prefix + value;
            label.text = lastLabel;
        }

        /// <summary>Two-integer label update that only composes when an input changes.</summary>
        public void SetLabel(string prefix, int first, string middle, int second, string suffix = "")
        {
            if (cachedComposition == 2 &&
                first == cachedValue &&
                second == cachedValue2 &&
                ReferenceEquals(prefix, cachedPrefix) &&
                ReferenceEquals(middle, cachedMiddle) &&
                ReferenceEquals(suffix, cachedSuffix))
            {
                return;
            }

            cachedComposition = 2;
            cachedPrefix = prefix;
            cachedMiddle = middle;
            cachedSuffix = suffix;
            cachedValue = first;
            cachedValue2 = second;
            lastLabel = prefix + first + middle + second + suffix;
            label.text = lastLabel;
        }

        public override void ApplyTheme(QwResolvedTheme theme)
        {
            base.ApplyTheme(theme);
            if (label != null)
            {
                label.color = theme.Text;
            }
        }
    }
}
