using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Where a level sits relative to a meter's threshold and hot zones.</summary>
    public enum TopiaForgeLevelZone
    {
        /// <summary>Below the configured threshold (for example voice activation).</summary>
        BelowThreshold,

        /// <summary>At or above the threshold and below the hot zone.</summary>
        Normal,

        /// <summary>Loud: at or above the hot level.</summary>
        Hot,

        /// <summary>At or above the clipping level.</summary>
        Clipping,
    }

    /// <summary>
    /// Decibel math for level meters. Linear sample values (RMS or peak, full scale 1) map onto
    /// dBFS and then onto a zero-to-one meter position between a floor and a ceiling. Every
    /// function is allocation-free and tolerates NaN, negative and infinite input.
    /// </summary>
    public static class TopiaForgeLevelMath
    {
        /// <summary>The level reported for silence, non-positive or NaN input.</summary>
        public const float SilenceDecibels = -120f;

        /// <summary>The highest level reported; louder input clamps here.</summary>
        public const float MaxDecibels = 24f;

        /// <summary>The default meter floor.</summary>
        public const float DefaultFloorDecibels = -60f;

        /// <summary>The default meter ceiling (full scale).</summary>
        public const float DefaultCeilingDecibels = 0f;

        /// <summary>The default start of the hot zone.</summary>
        public const float DefaultHotDecibels = -6f;

        /// <summary>The default clipping level.</summary>
        public const float DefaultClipDecibels = -1f;

        /// <summary>Converts a linear amplitude to dBFS (20 * log10), clamped to the reported range.</summary>
        public static float ToDecibels(float linear)
        {
            if (float.IsNaN(linear) || linear <= 0f)
            {
                return SilenceDecibels;
            }

            if (float.IsPositiveInfinity(linear))
            {
                return MaxDecibels;
            }

            var decibels = 20f * (float)Math.Log10(linear);
            return decibels < SilenceDecibels ? SilenceDecibels : decibels > MaxDecibels ? MaxDecibels : decibels;
        }

        /// <summary>Converts dBFS back to a linear amplitude; the silence level and below map to zero.</summary>
        public static float ToLinear(float decibels)
        {
            if (float.IsNaN(decibels) || decibels <= SilenceDecibels)
            {
                return 0f;
            }

            if (decibels > MaxDecibels)
            {
                decibels = MaxDecibels;
            }

            return (float)Math.Pow(10.0, decibels / 20.0);
        }

        /// <summary>
        /// Maps a level onto [0, 1] across [<paramref name="floorDecibels"/>, <paramref name="ceilingDecibels"/>].
        /// Values outside the range clamp; NaN or an empty range yields zero.
        /// </summary>
        public static float ToFraction(float decibels, float floorDecibels, float ceilingDecibels)
        {
            if (float.IsNaN(decibels) || !(ceilingDecibels > floorDecibels) || float.IsInfinity(ceilingDecibels - floorDecibels))
            {
                return 0f;
            }

            var t = (decibels - floorDecibels) / (ceilingDecibels - floorDecibels);
            return t <= 0f ? 0f : t >= 1f ? 1f : t;
        }

        /// <summary>Maps a linear amplitude straight onto a meter position.</summary>
        public static float LinearToFraction(float linear, float floorDecibels, float ceilingDecibels)
        {
            return ToFraction(ToDecibels(linear), floorDecibels, ceilingDecibels);
        }

        /// <summary>
        /// Classifies a level. Clipping wins over hot, hot over the threshold; a NaN threshold means
        /// no threshold, and a NaN level counts as silence.
        /// </summary>
        public static TopiaForgeLevelZone Zone(float decibels, float thresholdDecibels, float hotDecibels, float clipDecibels)
        {
            if (float.IsNaN(decibels))
            {
                decibels = SilenceDecibels;
            }

            if (decibels >= clipDecibels)
            {
                return TopiaForgeLevelZone.Clipping;
            }

            if (decibels >= hotDecibels)
            {
                return TopiaForgeLevelZone.Hot;
            }

            return !float.IsNaN(thresholdDecibels) && decibels < thresholdDecibels
                ? TopiaForgeLevelZone.BelowThreshold
                : TopiaForgeLevelZone.Normal;
        }

        /// <summary>
        /// Meter ballistics in position units: rises instantly to a louder target and falls toward a
        /// quieter one at <paramref name="fallPerSecond"/>. A non-positive fall rate disables smoothing.
        /// </summary>
        public static float Release(float displayed, float target, float deltaSeconds, float fallPerSecond)
        {
            displayed = Clamp01(displayed);
            target = Clamp01(target);
            if (target >= displayed)
            {
                return target;
            }

            if (!(fallPerSecond > 0f) || float.IsInfinity(fallPerSecond))
            {
                return target;
            }

            var step = fallPerSecond * Sanitize(deltaSeconds);
            var next = displayed - step;
            return next < target ? target : next;
        }

        internal static float Clamp01(float value)
        {
            return float.IsNaN(value) || value <= 0f ? 0f : value >= 1f ? 1f : value;
        }

        internal static float Sanitize(float deltaSeconds)
        {
            return float.IsNaN(deltaSeconds) || deltaSeconds <= 0f ? 0f : float.IsInfinity(deltaSeconds) ? 3600f : deltaSeconds;
        }
    }

    /// <summary>
    /// Peak-hold marker state, in meter-position units. A new peak latches immediately and is held
    /// for <see cref="HoldSeconds"/>; the marker then falls at <see cref="FallPerSecond"/> but never
    /// below the live level. Deterministic: time only advances through <see cref="Update"/>.
    /// </summary>
    public sealed class TopiaForgePeakHold
    {
        private float holdRemaining;

        /// <summary>Creates a peak hold.</summary>
        /// <exception cref="ArgumentOutOfRangeException">A duration or rate is negative or not finite.</exception>
        public TopiaForgePeakHold(float holdSeconds = 1f, float fallPerSecond = 0.5f)
        {
            if (!(holdSeconds >= 0f) || float.IsInfinity(holdSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(holdSeconds), "The hold time must be finite and non-negative.");
            }

            if (!(fallPerSecond >= 0f) || float.IsInfinity(fallPerSecond))
            {
                throw new ArgumentOutOfRangeException(nameof(fallPerSecond), "The fall rate must be finite and non-negative.");
            }

            HoldSeconds = holdSeconds;
            FallPerSecond = fallPerSecond;
        }

        /// <summary>Gets how long a new peak is held before it starts to fall.</summary>
        public float HoldSeconds { get; }

        /// <summary>Gets how fast the marker falls, in meter positions per second.</summary>
        public float FallPerSecond { get; }

        /// <summary>Gets the held position in [0, 1].</summary>
        public float Value { get; private set; }

        /// <summary>Feeds the current peak position and the time elapsed since the last update.</summary>
        public void Update(float fraction, float deltaSeconds)
        {
            fraction = TopiaForgeLevelMath.Clamp01(fraction);
            var elapsed = TopiaForgeLevelMath.Sanitize(deltaSeconds);
            if (fraction >= Value)
            {
                Value = fraction;
                holdRemaining = HoldSeconds;
                return;
            }

            if (holdRemaining > 0f)
            {
                holdRemaining -= elapsed;
                if (holdRemaining >= 0f)
                {
                    return;
                }

                elapsed = -holdRemaining;
                holdRemaining = 0f;
            }

            var fallen = Value - (FallPerSecond * elapsed);
            Value = fallen < fraction ? fraction : fallen;
        }

        /// <summary>Drops the marker to zero.</summary>
        public void Reset()
        {
            Value = 0f;
            holdRemaining = 0f;
        }
    }
}
