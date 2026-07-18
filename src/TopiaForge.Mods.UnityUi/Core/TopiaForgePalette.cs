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
        /// <summary>Gets the paper color.</summary>
        public static readonly TopiaForgeRgba Paper = TopiaForgeRgba.Hex(0xF5F1E8);
        /// <summary>Gets the paper warm color.</summary>
        public static readonly TopiaForgeRgba PaperWarm = TopiaForgeRgba.Hex(0xFFF7E9);
        /// <summary>Gets the surface color.</summary>
        public static readonly TopiaForgeRgba Surface = TopiaForgeRgba.Hex(0xFFFCF6);
        /// <summary>Gets the surface alt color.</summary>
        public static readonly TopiaForgeRgba SurfaceAlt = TopiaForgeRgba.Hex(0xFFF3E4);
        /// <summary>Gets the surface tint color.</summary>
        public static readonly TopiaForgeRgba SurfaceTint = TopiaForgeRgba.Hex(0xFFE0BE);
        /// <summary>Gets the selected tint color.</summary>
        public static readonly TopiaForgeRgba SelectedTint = TopiaForgeRgba.Hex(0xFFE8D1);
        /// <summary>Gets the border color.</summary>
        public static readonly TopiaForgeRgba Border = TopiaForgeRgba.Hex(0xE4B373);
        /// <summary>Gets the launch color.</summary>
        public static readonly TopiaForgeRgba Launch = TopiaForgeRgba.Hex(0xFF7A11);
        /// <summary>Gets the launch dark color.</summary>
        public static readonly TopiaForgeRgba LaunchDark = TopiaForgeRgba.Hex(0xCC620E);
        /// <summary>Gets the ink color.</summary>
        public static readonly TopiaForgeRgba Ink = TopiaForgeRgba.Hex(0x2D3748);
        /// <summary>Gets the muted text color.</summary>
        public static readonly TopiaForgeRgba MutedText = TopiaForgeRgba.Hex(0x6C6670);
        /// <summary>Gets the faint text color.</summary>
        public static readonly TopiaForgeRgba FaintText = TopiaForgeRgba.Hex(0x928A7C);
        /// <summary>Gets the accent color.</summary>
        public static readonly TopiaForgeRgba Accent = TopiaForgeRgba.Hex(0x20F6FE);
        /// <summary>Gets the accent dark color.</summary>
        public static readonly TopiaForgeRgba AccentDark = TopiaForgeRgba.Hex(0x168E96);
        /// <summary>Gets the accent deep color.</summary>
        public static readonly TopiaForgeRgba AccentDeep = TopiaForgeRgba.Hex(0x0F6A70);
        /// <summary>Gets the magenta color.</summary>
        public static readonly TopiaForgeRgba Magenta = TopiaForgeRgba.Hex(0xFF6B9D);
        /// <summary>Gets the magenta dark color.</summary>
        public static readonly TopiaForgeRgba MagentaDark = TopiaForgeRgba.Hex(0xB9446C);
        /// <summary>Gets the good color.</summary>
        public static readonly TopiaForgeRgba Good = TopiaForgeRgba.Hex(0x148D63);
        /// <summary>Gets the warning color.</summary>
        public static readonly TopiaForgeRgba Warning = TopiaForgeRgba.Hex(0xD68017);
        /// <summary>Gets the danger color.</summary>
        public static readonly TopiaForgeRgba Danger = TopiaForgeRgba.Hex(0xC83E4D);
        /// <summary>Gets the dark panel color.</summary>
        public static readonly TopiaForgeRgba DarkPanel = TopiaForgeRgba.Hex(0x2D3748);
        /// <summary>Gets the log panel color.</summary>
        public static readonly TopiaForgeRgba LogPanel = TopiaForgeRgba.Hex(0x1F2530);
        /// <summary>Gets the white color.</summary>
        public static readonly TopiaForgeRgba White = TopiaForgeRgba.Hex(0xFFFFFF);

        // HUD-only derived constants. Explicit rather than algorithmic so the dark
        // in-game idiom stays brand-controlled (mirrors the launcher's LogViewer look).
        /// <summary>Gets the HUD backdrop color.</summary>
        public static readonly TopiaForgeRgba HudBackdrop = TopiaForgeRgba.Hex(0x10141B);
        /// <summary>Gets the HUD sunken color.</summary>
        public static readonly TopiaForgeRgba HudSunken = TopiaForgeRgba.Hex(0x161B24);
        /// <summary>Gets the HUD tint color.</summary>
        public static readonly TopiaForgeRgba HudTint = TopiaForgeRgba.Hex(0x3A465C);
        /// <summary>Gets the HUD muted color.</summary>
        public static readonly TopiaForgeRgba HudMuted = TopiaForgeRgba.Hex(0xC7C1B4);
        /// <summary>Gets the HUD good color.</summary>
        public static readonly TopiaForgeRgba HudGood = TopiaForgeRgba.Hex(0x2FBF8F);
        /// <summary>Gets the HUD warning color.</summary>
        public static readonly TopiaForgeRgba HudWarning = TopiaForgeRgba.Hex(0xF2A03D);
        /// <summary>Gets the HUD danger color.</summary>
        public static readonly TopiaForgeRgba HudDanger = TopiaForgeRgba.Hex(0xFF5C6E);
    }
}
