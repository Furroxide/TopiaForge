namespace Robotopia.Mods.UnityUi
{
    /// <summary>Which of the two brand schemes a layer renders with.</summary>
    public enum QwScheme
    {
        /// <summary>Light warm-paper scheme — full-screen manager surfaces, windows, modals, menus.</summary>
        Paper,

        /// <summary>Dark translucent scheme — gameplay HUD overlays drawn over the world.</summary>
        Hud,
    }

    /// <summary>
    /// The semantic color roles every widget draws from. One role set, resolved twice
    /// (Paper and HUD) by QwSchemes so both contexts stay one brand.
    /// </summary>
    public readonly struct QwSchemeColors
    {
        public QwRgba Backdrop { get; init; }
        public QwRgba Surface { get; init; }
        public QwRgba SurfaceAlt { get; init; }
        public QwRgba SurfaceSunken { get; init; }
        public QwRgba Tint { get; init; }
        public QwRgba SelectedTint { get; init; }
        public QwRgba Outline { get; init; }
        public QwRgba OutlineStrong { get; init; }
        public QwRgba Primary { get; init; }
        public QwRgba PrimaryPressed { get; init; }
        public QwRgba OnPrimary { get; init; }
        public QwRgba Accent { get; init; }
        public QwRgba AccentPressed { get; init; }
        public QwRgba Text { get; init; }
        public QwRgba TextMuted { get; init; }
        public QwRgba TextFaint { get; init; }
        public QwRgba Success { get; init; }
        public QwRgba Warning { get; init; }
        public QwRgba Danger { get; init; }
        public QwRgba OnStatus { get; init; }
        public QwRgba Shadow { get; init; }
        public QwRgba ShadowStrong { get; init; }
        public QwRgba FocusRing { get; init; }
    }
}
