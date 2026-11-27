using TMPro;
using TopiaForge.Mods;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Applies one resolved type role to the actual TMP handle without changing its readable data.</summary>
    internal static class TopiaForgeTypography
    {
        internal static bool Apply(TMP_Text text, TopiaForgeResolvedTheme theme, TopiaForgeTextStyle style, float uiScale)
        {
            if (theme.Descriptor is not { } descriptor) return false;
            var safeStyle = SafeStyle(style);
            var role = descriptor.GetText(safeStyle);
            var font = theme.RegisteredFont?.Invoke(safeStyle) ?? TopiaForgeFonts.ForThemeRole(role.Font, style) ?? text.font;
            return Apply(text, role, font, uiScale);
        }

        internal static bool Apply(TMP_Text text, UiThemeTextRole role, TMP_FontAsset? font, float uiScale)
        {
            var face = font == null ? default : font.faceInfo;
            var metrics = TopiaForgeTypographyMetrics.Resolve(role, uiScale, face.pointSize, face.ascentLine, face.descentLine, face.scale);
            if (font != null && text.font != font) text.font = font;
            text.fontSize = metrics.PointSize;
            text.characterSpacing = metrics.CharacterSpacing;
            return metrics.Uppercase;
        }

        internal static void ApplyControlText(TMP_Text text, TopiaForgeResolvedTheme theme, TopiaForgeTextStyle style, float uiScale)
        {
            var uppercase = Apply(text, theme, style, uiScale);
            var rest = text.fontStyle & ~FontStyles.UpperCase;
            text.fontStyle = uppercase ? rest | FontStyles.UpperCase : rest;
        }

        private static UiTextStyle SafeStyle(TopiaForgeTextStyle style) => style switch
        {
            TopiaForgeTextStyle.Banner => UiTextStyle.Banner,
            TopiaForgeTextStyle.Display => UiTextStyle.Display,
            TopiaForgeTextStyle.Title => UiTextStyle.Title,
            TopiaForgeTextStyle.Heading => UiTextStyle.Heading,
            TopiaForgeTextStyle.Label => UiTextStyle.Label,
            TopiaForgeTextStyle.Caption => UiTextStyle.Caption,
            TopiaForgeTextStyle.Numeral => UiTextStyle.Numeral,
            TopiaForgeTextStyle.Mono => UiTextStyle.Mono,
            _ => UiTextStyle.Body,
        };
    }
}
