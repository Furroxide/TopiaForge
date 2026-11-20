using TMPro;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class TopiaForgeBadge
    {
        private bool declarativeRow;

        internal TMP_Text RowLabel => label;

        internal void UseDeclarativeRow()
        {
            declarativeRow = true;
            Go.name = "RowBadge";
            label.richText = false;
            label.parseCtrlCharacters = false;
            ApplyDeclarativeRowTheme(Theme);
        }

        internal void SetRowBoundary(Color ink) => ring.color = ink;

        private void ApplyDeclarativeRowTheme(TopiaForgeResolvedTheme theme)
        {
            var descriptor = theme.Descriptor ?? UiThemePackParser.Default;
            var scheme = descriptor.SelectScheme(theme.Scheme == TopiaForgeScheme.Hud, theme.HighContrast);
            var pair = scheme.GetPair(UiVirtualListRowLayout.BadgeRole);
            var uppercase = TopiaForgeTypography.Apply(label, theme, TopiaForgeTextStyle.Label, Host.EffectiveUiScale);
            var text = uppercase ? lastText.ToUpperInvariant() : lastText;
            if (!string.Equals(label.text, text, System.StringComparison.Ordinal)) label.text = text;
            fill.SetShape(descriptor.Shape.Small);
            ring.SetShape(descriptor.Shape.Small, UiControlFocusGeometry.Stroke(theme.HighContrast));
            fill.color = ColorOf(pair.Fill);
            label.color = ColorOf(pair.On);
        }

        private static Color32 ColorOf(UiThemeColor value) => new Color32(value.R, value.G, value.B, 255);
    }
}
