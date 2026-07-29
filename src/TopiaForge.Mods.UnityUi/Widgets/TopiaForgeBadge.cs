using TMPro;
using UnityEngine;
using UImage = UnityEngine.UI.Image;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Status chip (ENABLED / RESTART / LOADED...). Chip-radius fill tinted by tone
    /// with a matching ring; Set() dirty-checks both text and tone.
    /// </summary>
    public sealed class TopiaForgeBadge : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        private readonly UImage fill;
        private readonly UImage ring;
        private readonly TextMeshProUGUI label;
        private string lastText;
        private TopiaForgeTone tone;

        internal TopiaForgeBadge(TopiaForgeContainer parent, string text, TopiaForgeTone initialTone)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("Badge"))
        {
            tone = initialTone;
            fill = Go.AddComponent<UImage>();
            fill.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Chip);
            fill.type = UImage.Type.Sliced;
            fill.raycastTarget = false;

            var ringGo = new GameObject("Ring", typeof(RectTransform));
            ringGo.transform.SetParent(Go.transform, false);
            ring = ringGo.AddComponent<UImage>();
            ring.sprite = TopiaForgeSprites.Ring(TopiaForgeRadius.Chip, TopiaForgeTokens.BorderStandard);
            ring.type = UImage.Type.Sliced;
            ring.raycastTarget = false;
            TopiaForgeAnchors.Stretch((RectTransform)ringGo.transform);

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(Go.transform, false);
            label = TopiaForgeTmp.Create(labelGo);
            label.fontSize = TopiaForgeTokens.CaptionSize;
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

            lastText = text;
            label.text = text;
            TopiaForgeAnchors.Stretch((RectTransform)labelGo.transform, 8f, 2f, 8f, 2f);

            this.FixedHeight(22f);
            FitWidth();
            ApplyTheme(Theme);
        }

        /// <summary>Dirty-checked text + tone update.</summary>
        public void Set(string text, TopiaForgeTone value)
        {
            var changed = false;
            if (!string.Equals(lastText, text, System.StringComparison.Ordinal))
            {
                lastText = text;
                label.text = text;
                FitWidth();
                changed = true;
            }

            if (value != tone)
            {
                tone = value;
                changed = true;
            }

            if (changed)
            {
                ApplyTheme(Theme);
            }
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            var accent = theme.ToneColor(tone);
            var background = accent;
            background.a = Scheme == TopiaForgeScheme.Paper ? 0.14f : 0.22f;
            fill.color = background;
            ring.color = accent;
            label.color = tone == TopiaForgeTone.Neutral ? theme.TextMuted : accent;
        }

        private void FitWidth()
        {
            var width = label.GetPreferredValues(lastText).x + 18f;
            var layout = EnsureLayoutElement();
            layout.minWidth = width;
            layout.preferredWidth = width;
        }
    }
}
