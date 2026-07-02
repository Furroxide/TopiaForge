using System;

namespace Robotopia.Mods.UnityUi
{
    public enum QwEase
    {
        Linear,
        InQuad,
        OutQuad,
        InOutQuad,
        OutCubic,
        OutBack,
    }

    /// <summary>Pure easing functions used by the tween runner. Input clamped to [0, 1].</summary>
    public static class QwEasing
    {
        private const float BackOvershoot = 1.70158f;

        public static float Evaluate(QwEase ease, float t)
        {
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return ease switch
            {
                QwEase.Linear => t,
                QwEase.InQuad => t * t,
                QwEase.OutQuad => 1f - ((1f - t) * (1f - t)),
                QwEase.InOutQuad => t < 0.5f ? 2f * t * t : 1f - ((-2f * t + 2f) * (-2f * t + 2f) / 2f),
                QwEase.OutCubic => 1f - ((1f - t) * (1f - t) * (1f - t)),
                QwEase.OutBack => OutBack(t),
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
