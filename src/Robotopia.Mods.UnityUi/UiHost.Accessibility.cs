using System;

namespace Robotopia.Mods.UnityUi
{
    public sealed partial class UiHost
    {
        private QwAccessibilityProfile accessibilityProfile = QwAccessibilityProfile.Default;
        private int themeRevision = 1;

        /// <summary>
        /// Raised after this host's accessibility profile changes. Global theme
        /// changes continue to use <see cref="QwTheme.Changed"/>.
        /// </summary>
        public event Action? AccessibilityProfileChanged;

        public QwAccessibilityProfile AccessibilityProfile => accessibilityProfile;

        public QwEffectiveAccessibility EffectiveAccessibility => accessibilityProfile.Resolve(
            QwTheme.HighContrast,
            QwTheme.UiScale,
            QwTheme.ReducedMotion,
            QwTheme.MotionScale);

        public bool EffectiveHighContrast => EffectiveAccessibility.HighContrast;

        public float EffectiveUiScale => EffectiveAccessibility.UiScale;

        public bool EffectiveReducedMotion => EffectiveAccessibility.ReducedMotion;

        public float EffectiveMotion => EffectiveAccessibility.MotionIntensity;

        /// <summary>
        /// Applies host-local accessibility preferences without mutating any other
        /// mod's UI. Passing null restores the neutral host profile.
        /// </summary>
        public void SetAccessibilityProfile(QwAccessibilityProfile? profile)
        {
            ThrowIfDisposed();
            var normalized = profile ?? QwAccessibilityProfile.Default;
            if (accessibilityProfile.Equals(normalized))
            {
                return;
            }

            accessibilityProfile = normalized;
            RefreshResolvedTheme(reapplyScalers: true);
            QwCallbacks.Invoke(AccessibilityProfileChanged, "Accessibility profile changed");
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
                        QwLayers.ApplyScaler(scaler, EffectiveUiScale);
                    }
                }
            }

            WalkThemeAware();
        }
    }
}
