using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Global theme state shared by every UiHost in the process (the assembly is
    /// deduped by the loader, so statics are process-wide). Hosts resolve their own
    /// TopiaForgeResolvedTheme from this plus their per-mod accent; widgets refresh through the
    /// version counter rather than rebuilding, so focus/scroll/selection survive theme
    /// changes.
    /// </summary>
    public static class TopiaForgeTheme
    {
        private static bool highContrast;
        private static float uiScale = 1f;
        private static bool reducedMotion;
        private static float motionScale = 1f;

        /// <summary>Bumped on every change; consumers compare against their applied version.</summary>
        public static int Version { get; private set; } = 1;

        /// <summary>Raised after any global theme or accessibility setting changes.</summary>
        public static event Action? Changed;

        /// <summary>Accessibility: re-tones colors for legibility (ports Zombies' HudColor mode).</summary>
        public static bool HighContrast
        {
            get => highContrast;
            set => Set(ref highContrast, value);
        }

        /// <summary>Accessibility: global UI scale in [0.75, 1.5], applied via canvas reference resolution.</summary>
        public static float UiScale
        {
            get => uiScale;
            set => Set(ref uiScale, Clamp(value, 0.75f, 1.5f));
        }

        /// <summary>Accessibility: disables transitions, pulses, and punches entirely.</summary>
        public static bool ReducedMotion
        {
            get => reducedMotion;
            set => Set(ref reducedMotion, value);
        }

        /// <summary>HUD motion intensity in [0, 2] (0 = no pulses; absorbs Zombies' HudMotionIntensity).</summary>
        public static float MotionScale
        {
            get => motionScale;
            set => Set(ref motionScale, Clamp(value, 0f, 2f));
        }

        /// <summary>Effective motion multiplier (0 when reduced motion is on).</summary>
        public static float EffectiveMotion => reducedMotion ? 0f : motionScale;

        private static void Set<T>(ref T field, T value)
        {
            if (Equals(field, value))
            {
                return;
            }

            field = value;
            Version++;
            TopiaForgeCallbacks.Invoke(Changed, "Theme Changed");
        }

        private static float Clamp(float value, float min, float max)
        {
            return TopiaForgeAccessibilityProfile.ClampFinite(value, min, max, 1f);
        }
    }
}
