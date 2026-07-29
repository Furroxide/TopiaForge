using System;

namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class UiHost
    {
        private TopiaForgeAccessibilityProfile accessibilityProfile = TopiaForgeAccessibilityProfile.Default;
        private int themeRevision = 1;

        /// <summary>
        /// Raised after this host's accessibility profile changes. Global theme
        /// changes continue to use <see cref="TopiaForgeTheme.Changed"/>.
        /// </summary>
        public event Action? AccessibilityProfileChanged;

        /// <summary>Gets accessibility profile.</summary>
        public TopiaForgeAccessibilityProfile AccessibilityProfile => accessibilityProfile;

        /// <summary>Gets effective accessibility.</summary>
        public TopiaForgeEffectiveAccessibility EffectiveAccessibility => accessibilityProfile.Resolve(
            TopiaForgeTheme.HighContrast,
            TopiaForgeTheme.UiScale,
            TopiaForgeTheme.ReducedMotion,
            TopiaForgeTheme.MotionScale);

        /// <summary>Gets effective high contrast.</summary>
        public bool EffectiveHighContrast => EffectiveAccessibility.HighContrast;

        /// <summary>Gets effective UI scale.</summary>
        public float EffectiveUiScale => EffectiveAccessibility.UiScale;

        /// <summary>Gets effective reduced motion.</summary>
        public bool EffectiveReducedMotion => EffectiveAccessibility.ReducedMotion;

        /// <summary>Gets effective motion.</summary>
        public float EffectiveMotion => EffectiveAccessibility.MotionIntensity;

        /// <summary>
        /// Applies host-local accessibility preferences without mutating any other
        /// mod's UI. Passing null restores the neutral host profile.
        /// </summary>
        public void SetAccessibilityProfile(TopiaForgeAccessibilityProfile? profile)
        {
            ThrowIfDisposed();
            var normalized = profile ?? TopiaForgeAccessibilityProfile.Default;
            if (accessibilityProfile.Equals(normalized))
            {
                return;
            }

            accessibilityProfile = normalized;
            RefreshResolvedTheme(reapplyScalers: true);
            TopiaForgeCallbacks.Invoke(AccessibilityProfileChanged, "Accessibility profile changed");
        }

        private void RefreshResolvedTheme(bool reapplyScalers)
        {
            unchecked
            {
                themeRevision++;
            }

            paperTheme = null;
            hudTheme = null;
            if (reapplyScalers)
            {
                foreach (var scaler in scalers)
                {
                    if (scaler != null)
                    {
                        TopiaForgeLayers.ApplyScaler(scaler, EffectiveUiScale);
                    }
                }
            }

            WalkThemeAware();
        }
    }
}
