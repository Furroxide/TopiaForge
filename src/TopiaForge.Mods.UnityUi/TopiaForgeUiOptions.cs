using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Host configuration. Mods normally use TopiaForgeUi.For(context), which fills this from
    /// the mod's id, data path, and logger; the manager passes its own values.
    /// </summary>
    public sealed class TopiaForgeUiOptions
    {
        /// <summary>Stable owner id (mod id). Keys window persistence and layer names.</summary>
        public string OwnerId { get; set; } = "unknown";

        /// <summary>
        /// Optional accent override. Replaces the Accent/FocusRing roles only — the
        /// primary brand orange stays constant across all mods. On the Paper scheme the
        /// accent is auto-darkened until it reads against the light surface.
        /// </summary>
        public TopiaForgeRgba? Accent { get; set; }

        /// <summary>Directory for UI state persistence (window rects). Falls back to in-memory.</summary>
        public string? DataDirectory { get; set; }

        /// <summary>
        /// Optional host-scoped accessibility preferences. These compose with, and
        /// cannot weaken, process-wide high-contrast or reduced-motion settings.
        /// </summary>
        public TopiaForgeAccessibilityProfile AccessibilityProfile { get; set; } = TopiaForgeAccessibilityProfile.Default;

        /// <summary>Gets or sets log info.</summary>
        public Action<string>? LogInfo { get; set; }
        /// <summary>Gets or sets log warn.</summary>
        public Action<string>? LogWarn { get; set; }
        /// <summary>Gets or sets log error.</summary>
        public Action<string>? LogError { get; set; }
    }
}
