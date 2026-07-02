using TMPro;
using UnityEngine;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// TMP text handle. Runtime setters dirty-check (including a cached-int overload)
    /// so per-frame HUD updates allocate nothing while values are unchanged.
    /// </summary>
    public sealed class QwLabel : QwWidget, IQwThemeAware
    {
        private readonly TextMeshProUGUI text;
        private readonly QwTextStyle style;
        private QwTone tone = QwTone.Neutral;
        private bool hasCustomColor;
        private string lastText;
        private string cachedPrefix = string.Empty;
        private int cachedValue = int.MinValue;

        internal QwLabel(QwContainer parent, string initialText, QwTextStyle textStyle)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Label"))
        {
            style = textStyle;
            text = Go.AddComponent<TextMeshProUGUI>();
            text.raycastTarget = false;
            text.fontSize = QwTokens.SizeOf(style);
            text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Overflow;
            text.alignment = TextAlignmentOptions.Left;

            var font = QwFonts.For(style);
            if (font != null)
            {
                text.font = font;
            }

            var needsFauxBold =
                (QwTokens.IsBold(style) && QwFonts.UseFauxBold) ||
                (QwTokens.IsDisplay(style) && QwFonts.UseFauxDisplay);
            if (needsFauxBold)
            {
                text.fontStyle = FontStyles.Bold;
            }

            lastText = initialText;
            text.text = initialText;
            ApplyTheme(Theme);
        }

        /// <summary>Semantic color role; re-applied automatically on theme change.</summary>
        public QwLabel Tone(QwTone value)
        {
            tone = value;
            hasCustomColor = false;
            text.color = Theme.ToneColor(tone);
            return this;
        }

        public QwLabel AlignCenter()
        {
            text.alignment = TextAlignmentOptions.Center;
            return this;
        }

        public QwLabel AlignRight()
        {
            text.alignment = TextAlignmentOptions.Right;
            return this;
        }

        public QwLabel AlignTopLeft()
        {
            text.alignment = TextAlignmentOptions.TopLeft;
            return this;
        }

        /// <summary>Single-line label (HUD counter rows that must never bleed into the next row).</summary>
        public QwLabel NoWrap()
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

            lastText = value;
            text.text = value;
        }

        /// <summary>
        /// Prefix + integer update that only concatenates when the value changes —
        /// the per-frame HUD counter pattern ("WAVE ", wave).
        /// </summary>
        public void SetText(string prefix, int value)
        {
            if (value == cachedValue && ReferenceEquals(prefix, cachedPrefix))
            {
                return;
            }

            cachedPrefix = prefix;
            cachedValue = value;
            var composed = prefix + value;
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

        public void ApplyTheme(QwResolvedTheme theme)
        {
            if (!hasCustomColor)
            {
                text.color = theme.ToneColor(tone);
            }
        }
    }
}
