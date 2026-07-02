namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// The QuantumWorks brand palette. Values are exact ports of QuantumWorksPalette in
    /// packages/launcher_ui/lib/src/launcher_theme.dart — the launcher and the in-game
    /// UI share one brand. Do not tweak these here; brand changes start in the launcher
    /// design system.
    /// </summary>
    public static class QwPalette
    {
        public static readonly QwRgba Paper = QwRgba.Hex(0xF5F1E8);
        public static readonly QwRgba PaperWarm = QwRgba.Hex(0xFFF7E9);
        public static readonly QwRgba Surface = QwRgba.Hex(0xFFFCF6);
        public static readonly QwRgba SurfaceAlt = QwRgba.Hex(0xFFF3E4);
        public static readonly QwRgba SurfaceTint = QwRgba.Hex(0xFFE0BE);
        public static readonly QwRgba SelectedTint = QwRgba.Hex(0xFFE8D1);
        public static readonly QwRgba Border = QwRgba.Hex(0xE4B373);
        public static readonly QwRgba Launch = QwRgba.Hex(0xFF7A11);
        public static readonly QwRgba LaunchDark = QwRgba.Hex(0xCC620E);
        public static readonly QwRgba Ink = QwRgba.Hex(0x2D3748);
        public static readonly QwRgba MutedText = QwRgba.Hex(0x6C6670);
        public static readonly QwRgba FaintText = QwRgba.Hex(0x928A7C);
        public static readonly QwRgba Accent = QwRgba.Hex(0x20F6FE);
        public static readonly QwRgba AccentDark = QwRgba.Hex(0x168E96);
        public static readonly QwRgba AccentDeep = QwRgba.Hex(0x0F6A70);
        public static readonly QwRgba Magenta = QwRgba.Hex(0xFF6B9D);
        public static readonly QwRgba MagentaDark = QwRgba.Hex(0xB9446C);
        public static readonly QwRgba Good = QwRgba.Hex(0x148D63);
        public static readonly QwRgba Warning = QwRgba.Hex(0xD68017);
        public static readonly QwRgba Danger = QwRgba.Hex(0xC83E4D);
        public static readonly QwRgba DarkPanel = QwRgba.Hex(0x2D3748);
        public static readonly QwRgba LogPanel = QwRgba.Hex(0x1F2530);
        public static readonly QwRgba White = QwRgba.Hex(0xFFFFFF);

        // HUD-only derived constants. Explicit rather than algorithmic so the dark
        // in-game idiom stays brand-controlled (mirrors the launcher's LogViewer look).
        public static readonly QwRgba HudBackdrop = QwRgba.Hex(0x10141B);
        public static readonly QwRgba HudSunken = QwRgba.Hex(0x161B24);
        public static readonly QwRgba HudTint = QwRgba.Hex(0x3A465C);
        public static readonly QwRgba HudMuted = QwRgba.Hex(0xC7C1B4);
        public static readonly QwRgba HudGood = QwRgba.Hex(0x2FBF8F);
        public static readonly QwRgba HudWarning = QwRgba.Hex(0xF2A03D);
        public static readonly QwRgba HudDanger = QwRgba.Hex(0xFF5C6E);
    }
}
