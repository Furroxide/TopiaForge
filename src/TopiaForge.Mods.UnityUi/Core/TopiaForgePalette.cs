namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// The TopiaForge brand palette. Values are exact ports of TopiaForgePalette in
    /// packages/launcher_ui/lib/src/launcher_theme.dart — the launcher and the in-game
    /// UI share one brand. Do not tweak these here; brand changes start in the launcher
    /// design system.
    /// </summary>
    public static class TopiaForgePalette
    {
        public static readonly TopiaForgeRgba Paper = TopiaForgeRgba.Hex(0xF5F1E8);
        public static readonly TopiaForgeRgba PaperWarm = TopiaForgeRgba.Hex(0xFFF7E9);
        public static readonly TopiaForgeRgba Surface = TopiaForgeRgba.Hex(0xFFFCF6);
        public static readonly TopiaForgeRgba SurfaceAlt = TopiaForgeRgba.Hex(0xFFF3E4);
        public static readonly TopiaForgeRgba SurfaceTint = TopiaForgeRgba.Hex(0xFFE0BE);
        public static readonly TopiaForgeRgba SelectedTint = TopiaForgeRgba.Hex(0xFFE8D1);
        public static readonly TopiaForgeRgba Border = TopiaForgeRgba.Hex(0xE4B373);
        public static readonly TopiaForgeRgba Launch = TopiaForgeRgba.Hex(0xFF7A11);
        public static readonly TopiaForgeRgba LaunchDark = TopiaForgeRgba.Hex(0xCC620E);
        public static readonly TopiaForgeRgba Ink = TopiaForgeRgba.Hex(0x2D3748);
        public static readonly TopiaForgeRgba MutedText = TopiaForgeRgba.Hex(0x6C6670);
        public static readonly TopiaForgeRgba FaintText = TopiaForgeRgba.Hex(0x928A7C);
        public static readonly TopiaForgeRgba Accent = TopiaForgeRgba.Hex(0x20F6FE);
        public static readonly TopiaForgeRgba AccentDark = TopiaForgeRgba.Hex(0x168E96);
        public static readonly TopiaForgeRgba AccentDeep = TopiaForgeRgba.Hex(0x0F6A70);
        public static readonly TopiaForgeRgba Magenta = TopiaForgeRgba.Hex(0xFF6B9D);
        public static readonly TopiaForgeRgba MagentaDark = TopiaForgeRgba.Hex(0xB9446C);
        public static readonly TopiaForgeRgba Good = TopiaForgeRgba.Hex(0x148D63);
        public static readonly TopiaForgeRgba Warning = TopiaForgeRgba.Hex(0xD68017);
        public static readonly TopiaForgeRgba Danger = TopiaForgeRgba.Hex(0xC83E4D);
        public static readonly TopiaForgeRgba DarkPanel = TopiaForgeRgba.Hex(0x2D3748);
        public static readonly TopiaForgeRgba LogPanel = TopiaForgeRgba.Hex(0x1F2530);
        public static readonly TopiaForgeRgba White = TopiaForgeRgba.Hex(0xFFFFFF);

        // HUD-only derived constants. Explicit rather than algorithmic so the dark
        // in-game idiom stays brand-controlled (mirrors the launcher's LogViewer look).
        public static readonly TopiaForgeRgba HudBackdrop = TopiaForgeRgba.Hex(0x10141B);
        public static readonly TopiaForgeRgba HudSunken = TopiaForgeRgba.Hex(0x161B24);
        public static readonly TopiaForgeRgba HudTint = TopiaForgeRgba.Hex(0x3A465C);
        public static readonly TopiaForgeRgba HudMuted = TopiaForgeRgba.Hex(0xC7C1B4);
        public static readonly TopiaForgeRgba HudGood = TopiaForgeRgba.Hex(0x2FBF8F);
        public static readonly TopiaForgeRgba HudWarning = TopiaForgeRgba.Hex(0xF2A03D);
        public static readonly TopiaForgeRgba HudDanger = TopiaForgeRgba.Hex(0xFF5C6E);
    }
}
