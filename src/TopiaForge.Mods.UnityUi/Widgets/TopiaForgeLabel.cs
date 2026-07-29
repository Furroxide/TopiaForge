using TMPro;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// TMP text handle. Runtime setters dirty-check (including a cached-int overload)
    /// so per-frame HUD updates allocate nothing while values are unchanged.
    /// </summary>
    public sealed class TopiaForgeLabel : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private readonly TextMeshProUGUI text;
        private readonly TopiaForgeTextStyle style;
        private TopiaForgeTone tone = TopiaForgeTone.Neutral;
        private bool hasCustomColor;
        private string lastText;
        private byte cachedComposition;
        private string cachedPrefix = string.Empty;
        private string cachedMiddle = string.Empty;
        private string cachedSuffix = string.Empty;
        private string cachedFormat = string.Empty;
        private int cachedValue = int.MinValue;
        private int cachedValue2 = int.MinValue;
        private int cachedValue3 = int.MinValue;
        private int cachedValue4 = int.MinValue;

        internal TopiaForgeLabel(TopiaForgeContainer parent, string initialText, TopiaForgeTextStyle textStyle)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Label"))
        {
            style = textStyle;
            text = TopiaForgeTmp.Create(Go);
            text.fontSize = TopiaForgeTokens.SizeOf(style);
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.alignment = TextAlignmentOptions.Left;

            var font = TopiaForgeFonts.For(style);
            if (font != null)
            {
                text.font = font;
            }

            var needsFauxBold =
                (TopiaForgeTokens.IsBold(style) && TopiaForgeFonts.UseFauxBold) ||
                (TopiaForgeTokens.IsDisplay(style) && TopiaForgeFonts.UseFauxDisplay);
            if (needsFauxBold)
            {
                text.fontStyle = FontStyles.Bold;
            }

            lastText = initialText;
            text.text = initialText;
            ApplyTheme(Theme);
        }

        /// <summary>Semantic color role; re-applied automatically on theme change.</summary>
        public TopiaForgeLabel Tone(TopiaForgeTone value)
        {
            SetTone(value);
            return this;
        }

        /// <summary>Dirty-checked runtime semantic color role.</summary>
        public void SetTone(TopiaForgeTone value)
        {
            if (!hasCustomColor && tone == value)
            {
                return;
            }

            tone = value;
            hasCustomColor = false;
            text.color = Theme.ToneColor(tone);
        }

        /// <summary>Aligns this element to the center.</summary>
        public TopiaForgeLabel AlignCenter()
        {
            text.alignment = TextAlignmentOptions.Center;
            return this;
        }

        /// <summary>Aligns this element to the right.</summary>
        public TopiaForgeLabel AlignRight()
        {
            text.alignment = TextAlignmentOptions.Right;
            return this;
        }

        /// <summary>Aligns this element to the top left.</summary>
        public TopiaForgeLabel AlignTopLeft()
        {
            text.alignment = TextAlignmentOptions.TopLeft;
            return this;
        }

        /// <summary>Single-line label (HUD counter rows that must never bleed into the next row).</summary>
        public TopiaForgeLabel NoWrap()
        {
            text.textWrappingMode = TextWrappingModes.NoWrap;
            return this;
        }

        /// <summary>Dirty-checked text update.</summary>
        public void SetText(string value)
        {
            if (string.Equals(lastText, value, System.StringComparison.Ordinal))
            {
                return;
            }

            cachedComposition = 0;
            lastText = value;
            text.text = value;
        }

        /// <summary>
        /// Prefix + integer update that only concatenates when the value changes —
        /// the per-frame HUD counter pattern ("WAVE ", wave).
        /// </summary>
        public void SetText(string prefix, int value)
        {
            if (cachedComposition == 1 && value == cachedValue && ReferenceEquals(prefix, cachedPrefix))
            {
                return;
            }

            cachedComposition = 1;
            cachedPrefix = prefix;
            cachedValue = value;
            var composed = prefix + value;
            lastText = composed;
            text.text = composed;
        }

        /// <summary>
        /// Formatted integer update that only formats when the value or format changes.
        /// </summary>
        public void SetNumber(int value, string format)
        {
            SetNumber(string.Empty, value, format);
        }

        /// <summary>
        /// Prefix + formatted integer update that only allocates when an input changes.
        /// </summary>
        public void SetNumber(string prefix, int value, string format)
        {
            if (cachedComposition == 2 &&
                value == cachedValue &&
                ReferenceEquals(prefix, cachedPrefix) &&
                ReferenceEquals(format, cachedFormat))
            {
                return;
            }

            cachedComposition = 2;
            cachedPrefix = prefix;
            cachedFormat = format;
            cachedValue = value;
            var composed = prefix + value.ToString(format, System.Globalization.CultureInfo.CurrentCulture);
            lastText = composed;
            text.text = composed;
        }

        /// <summary>Two-integer text update that only composes when an input changes.</summary>
        public void SetText(string prefix, int first, string middle, int second)
        {
            if (cachedComposition == 3 &&
                first == cachedValue &&
                second == cachedValue2 &&
                ReferenceEquals(prefix, cachedPrefix) &&
                ReferenceEquals(middle, cachedMiddle))
            {
                return;
            }

            cachedComposition = 3;
            cachedPrefix = prefix;
            cachedMiddle = middle;
            cachedValue = first;
            cachedValue2 = second;
            var composed = prefix + first + middle + second;
            lastText = composed;
            text.text = composed;
        }

        /// <summary>Four-integer text update that only composes when an input changes.</summary>
        public void SetText(
            string prefix,
            int first,
            string secondPrefix,
            int second,
            string thirdPrefix,
            int third,
            string fourthPrefix,
            int fourth)
        {
            if (cachedComposition == 4 &&
                first == cachedValue &&
                second == cachedValue2 &&
                third == cachedValue3 &&
                fourth == cachedValue4 &&
                ReferenceEquals(prefix, cachedPrefix) &&
                ReferenceEquals(secondPrefix, cachedMiddle) &&
                ReferenceEquals(thirdPrefix, cachedSuffix) &&
                ReferenceEquals(fourthPrefix, cachedFormat))
            {
                return;
            }

            cachedComposition = 4;
            cachedPrefix = prefix;
            cachedMiddle = secondPrefix;
            cachedSuffix = thirdPrefix;
            cachedFormat = fourthPrefix;
            cachedValue = first;
            cachedValue2 = second;
            cachedValue3 = third;
            cachedValue4 = fourth;
            var composed = prefix + first + secondPrefix + second + thirdPrefix + third + fourthPrefix + fourth;
            lastText = composed;
            text.text = composed;
        }

        /// <summary>Custom color (dirty-checked). High-contrast emphasis is applied by the theme.</summary>
        public void SetColor(Color color)
        {
            hasCustomColor = true;
            var emphasized = Theme.Emphasize(color);
            if (text.color != emphasized)
            {
                text.color = emphasized;
            }
        }

        /// <summary>Grows the rect to the wrapped text height (absolute-placement labels).</summary>
        public void FitHeight()
        {
            text.ForceMeshUpdate();
            var preferred = text.GetPreferredValues(lastText, Rect.rect.width, 0f);
            var layout = EnsureLayoutElement();
            layout.minHeight = preferred.y;
            layout.preferredHeight = preferred.y;
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            if (!hasCustomColor)
            {
                text.color = theme.ToneColor(tone);
            }
        }
    }
}
