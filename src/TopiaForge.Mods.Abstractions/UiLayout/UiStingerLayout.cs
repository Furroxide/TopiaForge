namespace TopiaForge.Mods
{
    internal static class UiStingerLayout
    {
        internal const float MaximumWidth = 900f;
        internal const float HeightFraction = 0.30f;
        internal const float PaddingX = 24f;
        internal const float PaddingY = 8f;
        internal const float LineGap = 4f;
        internal const float GlyphSize = 40f;
        internal const float GlyphGap = 12f;
        internal const int MaximumLines = 2;
        internal const float EntranceOvershoot = 1.02f;
        internal const float GreatestShadow = 8f;
        internal const float CrosshairGap = 1f;

        internal static bool NeedsDeclaredFace(UiThemeDescriptor descriptor, UiTextStyle role)
        {
            var name = descriptor.GetText(role).Font;
            for (var index = 0; index < descriptor.Fonts.Count; index++)
                if (descriptor.Fonts[index].Id == name) return true;
            return false;
        }

        internal static float TitleWidth(float panelWidth, bool hasGlyph) =>
            panelWidth - 2f * PaddingX - (hasGlyph ? GlyphSize + GlyphGap : 0f);

        internal static float HeightBeforeCrosshair(float centreToCrosshair) =>
            2f * (centreToCrosshair - CrosshairGap) / EntranceOvershoot - 2f * GreatestShadow;
    }
}
