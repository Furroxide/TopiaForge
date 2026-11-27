using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Round-trip ceilings, in milliseconds, for each signal-bar count. A ping at or below
    /// <see cref="FourBars"/> shows four bars; one above <see cref="OneBar"/> shows none.
    /// </summary>
    public readonly struct TopiaForgeSignalThresholds : IEquatable<TopiaForgeSignalThresholds>
    {
        /// <summary>Creates thresholds; each ceiling must be positive and strictly above the previous one.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The ceilings are not positive and strictly ascending.</exception>
        public TopiaForgeSignalThresholds(int fourBars, int threeBars, int twoBars, int oneBar)
        {
            if (fourBars <= 0 || threeBars <= fourBars || twoBars <= threeBars || oneBar <= twoBars)
            {
                throw new ArgumentOutOfRangeException(nameof(fourBars), "Signal ceilings must be positive and strictly ascending.");
            }

            FourBars = fourBars;
            ThreeBars = threeBars;
            TwoBars = twoBars;
            OneBar = oneBar;
        }

        /// <summary>Gets the default ceilings: 60, 120, 200 and 350 milliseconds.</summary>
        public static TopiaForgeSignalThresholds Default => new TopiaForgeSignalThresholds(60, 120, 200, 350);

        /// <summary>Gets the highest ping, in milliseconds, that still shows four bars.</summary>
        public int FourBars { get; }

        /// <summary>Gets the highest ping, in milliseconds, that still shows three bars.</summary>
        public int ThreeBars { get; }

        /// <summary>Gets the highest ping, in milliseconds, that still shows two bars.</summary>
        public int TwoBars { get; }

        /// <summary>Gets the highest ping, in milliseconds, that still shows one bar.</summary>
        public int OneBar { get; }

        internal bool IsValid => FourBars > 0;

        /// <inheritdoc/>
        public bool Equals(TopiaForgeSignalThresholds other)
        {
            return FourBars == other.FourBars && ThreeBars == other.ThreeBars && TwoBars == other.TwoBars && OneBar == other.OneBar;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is TopiaForgeSignalThresholds other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = FourBars;
                hash = (hash * 397) ^ ThreeBars;
                hash = (hash * 397) ^ TwoBars;
                return (hash * 397) ^ OneBar;
            }
        }
    }

    /// <summary>
    /// Maps connection measurements onto the zero-to-four bars a <c>TopiaForgeSignalBars</c> glyph
    /// draws. Generic on purpose: callers translate their own quality type into a ping, a
    /// normalized quality, or a bucketed level.
    /// </summary>
    public static class TopiaForgeSignalMath
    {
        /// <summary>The number of bars in a full-strength glyph.</summary>
        public const int MaxBars = 4;

        /// <summary>The bar count that means the signal is not known.</summary>
        public const int Unknown = -1;

        /// <summary>Bars for a round-trip time using <see cref="TopiaForgeSignalThresholds.Default"/>.</summary>
        public static int BarsForPing(int pingMilliseconds)
        {
            return BarsForPing(pingMilliseconds, TopiaForgeSignalThresholds.Default);
        }

        /// <summary>Bars for a round-trip time; a negative ping is <see cref="Unknown"/>.</summary>
        public static int BarsForPing(int pingMilliseconds, TopiaForgeSignalThresholds thresholds)
        {
            if (pingMilliseconds < 0)
            {
                return Unknown;
            }

            if (!thresholds.IsValid)
            {
                thresholds = TopiaForgeSignalThresholds.Default;
            }

            if (pingMilliseconds <= thresholds.FourBars)
            {
                return 4;
            }

            if (pingMilliseconds <= thresholds.ThreeBars)
            {
                return 3;
            }

            if (pingMilliseconds <= thresholds.TwoBars)
            {
                return 2;
            }

            return pingMilliseconds <= thresholds.OneBar ? 1 : 0;
        }

        /// <summary>
        /// Bars for a normalized quality where 0 is unusable and 1 is perfect, rounded to the nearest
        /// bar. Out-of-range values clamp; NaN or infinity is <see cref="Unknown"/>.
        /// </summary>
        public static int BarsForQuality(float quality)
        {
            if (float.IsNaN(quality) || float.IsInfinity(quality))
            {
                return Unknown;
            }

            var clamped = quality < 0f ? 0f : quality > 1f ? 1f : quality;
            return (int)Math.Floor((clamped * MaxBars) + 0.5f);
        }

        /// <summary>
        /// Bars for a bucketed level (for example a quality enum) where 0 is the worst bucket and
        /// <paramref name="maxLevel"/> the best. A negative level is <see cref="Unknown"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLevel"/> is not positive.</exception>
        public static int BarsForLevel(int level, int maxLevel)
        {
            if (maxLevel <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLevel), "A bucketed scale needs at least two levels.");
            }

            if (level < 0)
            {
                return Unknown;
            }

            if (level >= maxLevel)
            {
                return MaxBars;
            }

            return (int)Math.Floor(((double)level * MaxBars / maxLevel) + 0.5);
        }

        /// <summary>Normalizes a raw bar count: negative values become <see cref="Unknown"/>, large ones cap at four.</summary>
        public static int Clamp(int bars)
        {
            return bars < 0 ? Unknown : bars > MaxBars ? MaxBars : bars;
        }
    }
}
