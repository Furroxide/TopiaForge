using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Defines the supported ease values.</summary>
    public enum TopiaForgeEase
    {
        /// <summary>Selects the linear option.</summary>
        Linear,
        /// <summary>Selects the in quad option.</summary>
        InQuad,
        /// <summary>Selects the out quad option.</summary>
        OutQuad,
        /// <summary>Selects the in out quad option.</summary>
        InOutQuad,
        /// <summary>Selects the out cubic option.</summary>
        OutCubic,
        /// <summary>Selects the out back option.</summary>
        OutBack,
    }

    /// <summary>Pure easing functions used by the tween runner. Input clamped to [0, 1].</summary>
    public static class TopiaForgeEasing
    {
        private const float BackOvershoot = 1.70158f;

        /// <summary>Evaluates the selected easing curve at a normalized time.</summary>
        public static float Evaluate(TopiaForgeEase ease, float t)
        {
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return ease switch
            {
                TopiaForgeEase.Linear => t,
                TopiaForgeEase.InQuad => t * t,
                TopiaForgeEase.OutQuad => 1f - ((1f - t) * (1f - t)),
                TopiaForgeEase.InOutQuad => t < 0.5f ? 2f * t * t : 1f - ((-2f * t + 2f) * (-2f * t + 2f) / 2f),
                TopiaForgeEase.OutCubic => 1f - ((1f - t) * (1f - t) * (1f - t)),
                TopiaForgeEase.OutBack => OutBack(t),
                _ => t,
            };
        }

        private static float OutBack(float t)
        {
            var c1 = BackOvershoot;
            var c3 = c1 + 1f;
            var u = t - 1f;
            return 1f + (c3 * u * u * u) + (c1 * u * u);
        }
    }
}
