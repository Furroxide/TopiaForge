using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Accessibility preferences scoped to one <c>UiHost</c>. Boolean preferences
    /// can only strengthen the process-wide settings; scale and motion compose with
    /// the global values and are clamped to the TopiaForgeUi supported ranges.
    /// </summary>
    public sealed class TopiaForgeAccessibilityProfile : IEquatable<TopiaForgeAccessibilityProfile>
    {
        public static TopiaForgeAccessibilityProfile Default { get; } = new TopiaForgeAccessibilityProfile();

        public TopiaForgeAccessibilityProfile(
            bool highContrast = false,
            float uiScale = 1f,
            bool reducedMotion = false,
            float motionIntensity = 1f)
        {
            HighContrast = highContrast;
            UiScale = ClampFinite(uiScale, 0.75f, 1.5f, 1f);
            ReducedMotion = reducedMotion;
            MotionIntensity = ClampFinite(motionIntensity, 0f, 2f, 1f);
        }

        public bool HighContrast { get; }

        public float UiScale { get; }

        public bool ReducedMotion { get; }

        public float MotionIntensity { get; }

        /// <summary>Composes this profile with the current process-wide settings.</summary>
        public TopiaForgeEffectiveAccessibility Resolve(
            bool globalHighContrast,
            float globalUiScale,
            bool globalReducedMotion,
            float globalMotionIntensity)
        {
            var reduced = globalReducedMotion || ReducedMotion;
            return new TopiaForgeEffectiveAccessibility(
                globalHighContrast || HighContrast,
                ClampFinite(globalUiScale * UiScale, 0.75f, 1.5f, 1f),
                reduced,
                reduced
                    ? 0f
                    : ClampFinite(globalMotionIntensity * MotionIntensity, 0f, 2f, 1f));
        }

        public bool Equals(TopiaForgeAccessibilityProfile? other)
        {
            return other != null
                && HighContrast == other.HighContrast
                && UiScale.Equals(other.UiScale)
                && ReducedMotion == other.ReducedMotion
                && MotionIntensity.Equals(other.MotionIntensity);
        }

        public override bool Equals(object? obj) => Equals(obj as TopiaForgeAccessibilityProfile);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = HighContrast.GetHashCode();
                hash = (hash * 397) ^ UiScale.GetHashCode();
                hash = (hash * 397) ^ ReducedMotion.GetHashCode();
                return (hash * 397) ^ MotionIntensity.GetHashCode();
            }
        }

        internal static float ClampFinite(float value, float min, float max, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return fallback;
            }

            return value < min ? min : value > max ? max : value;
        }
    }

    /// <summary>Resolved host accessibility values after global composition.</summary>
    public readonly struct TopiaForgeEffectiveAccessibility
    {
        internal TopiaForgeEffectiveAccessibility(
            bool highContrast,
            float uiScale,
            bool reducedMotion,
            float motionIntensity)
        {
            HighContrast = highContrast;
            UiScale = uiScale;
            ReducedMotion = reducedMotion;
            MotionIntensity = motionIntensity;
        }

        public bool HighContrast { get; }

        public float UiScale { get; }

        public bool ReducedMotion { get; }

        public float MotionIntensity { get; }
    }
}
