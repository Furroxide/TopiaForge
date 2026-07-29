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
        /// <summary>Gets the backdrop color.</summary>
        public TopiaForgeRgba Backdrop { get; init; }
        /// <summary>Gets the surface color.</summary>
        public TopiaForgeRgba Surface { get; init; }
        /// <summary>Gets the surface alt color.</summary>
        public TopiaForgeRgba SurfaceAlt { get; init; }
        /// <summary>Gets the surface sunken color.</summary>
        public TopiaForgeRgba SurfaceSunken { get; init; }
        /// <summary>Gets the tint color.</summary>
        public TopiaForgeRgba Tint { get; init; }
        /// <summary>Gets the selected tint color.</summary>
        public TopiaForgeRgba SelectedTint { get; init; }
        /// <summary>Gets the outline color.</summary>
        public TopiaForgeRgba Outline { get; init; }
        /// <summary>Gets the outline strong color.</summary>
        public TopiaForgeRgba OutlineStrong { get; init; }
        /// <summary>Gets the primary color.</summary>
        public TopiaForgeRgba Primary { get; init; }
        /// <summary>Gets the primary pressed color.</summary>
        public TopiaForgeRgba PrimaryPressed { get; init; }
        /// <summary>Gets the on primary color.</summary>
        public TopiaForgeRgba OnPrimary { get; init; }
        /// <summary>Gets the accent color.</summary>
        public TopiaForgeRgba Accent { get; init; }
        /// <summary>Gets the accent pressed color.</summary>
        public TopiaForgeRgba AccentPressed { get; init; }
        /// <summary>Gets the text color.</summary>
        public TopiaForgeRgba Text { get; init; }
        /// <summary>Gets the text muted color.</summary>
        public TopiaForgeRgba TextMuted { get; init; }
        /// <summary>Gets the text faint color.</summary>
        public TopiaForgeRgba TextFaint { get; init; }
        /// <summary>Gets the success color.</summary>
        public TopiaForgeRgba Success { get; init; }
        /// <summary>Gets the warning color.</summary>
        public TopiaForgeRgba Warning { get; init; }
        /// <summary>Gets the danger color.</summary>
        public TopiaForgeRgba Danger { get; init; }
        /// <summary>Gets the on status color.</summary>
        public TopiaForgeRgba OnStatus { get; init; }
        /// <summary>Gets the shadow color.</summary>
        public TopiaForgeRgba Shadow { get; init; }
        /// <summary>Gets the shadow strong color.</summary>
        public TopiaForgeRgba ShadowStrong { get; init; }
        /// <summary>Gets the focus ring color.</summary>
        public TopiaForgeRgba FocusRing { get; init; }
    }
}
