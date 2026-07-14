namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Which of the two brand schemes a layer renders with.</summary>
    public enum TopiaForgeScheme
    {
        /// <summary>Light warm-paper scheme — full-screen manager surfaces, windows, modals, menus.</summary>
        Paper,

        /// <summary>Dark translucent scheme — gameplay HUD overlays drawn over the world.</summary>
        Hud,
    }

    /// <summary>
    /// The semantic color roles every widget draws from. One role set, resolved twice
    /// (Paper and HUD) by TopiaForgeSchemes so both contexts stay one brand.
    /// </summary>
    public readonly struct TopiaForgeSchemeColors
    {
        public TopiaForgeRgba Backdrop { get; init; }
        public TopiaForgeRgba Surface { get; init; }
        public TopiaForgeRgba SurfaceAlt { get; init; }
        public TopiaForgeRgba SurfaceSunken { get; init; }
        public TopiaForgeRgba Tint { get; init; }
        public TopiaForgeRgba SelectedTint { get; init; }
        public TopiaForgeRgba Outline { get; init; }
        public TopiaForgeRgba OutlineStrong { get; init; }
        public TopiaForgeRgba Primary { get; init; }
        public TopiaForgeRgba PrimaryPressed { get; init; }
        public TopiaForgeRgba OnPrimary { get; init; }
        public TopiaForgeRgba Accent { get; init; }
        public TopiaForgeRgba AccentPressed { get; init; }
        public TopiaForgeRgba Text { get; init; }
        public TopiaForgeRgba TextMuted { get; init; }
        public TopiaForgeRgba TextFaint { get; init; }
        public TopiaForgeRgba Success { get; init; }
        public TopiaForgeRgba Warning { get; init; }
        public TopiaForgeRgba Danger { get; init; }
        public TopiaForgeRgba OnStatus { get; init; }
        public TopiaForgeRgba Shadow { get; init; }
        public TopiaForgeRgba ShadowStrong { get; init; }
        public TopiaForgeRgba FocusRing { get; init; }
    }
}
