using System;

namespace TopiaForge.Mods
{
    /// <summary>Identifies which side of a warning or critical threshold activates it.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public enum UiThresholdDirection
    {
        /// <summary>Activates at or below the threshold.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        AtOrBelow = 0,

        /// <summary>Activates at or above the threshold.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        AtOrAbove = 1
    }

    /// <summary>Provides immutable warning and critical boundaries in the units of the status being shown.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class UiThresholds
    {
        /// <summary>Creates ordered, finite warning and critical thresholds.</summary>
        /// <param name="warning">The boundary where the warning state begins.</param>
        /// <param name="critical">The boundary where the critical state begins.</param>
        /// <param name="direction">AtOrBelow requires warning above critical. AtOrAbove requires warning below critical.</param>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiThresholds(double warning, double critical, UiThresholdDirection direction = UiThresholdDirection.AtOrBelow)
        {
            if (!double.IsFinite(warning)) throw new ArgumentOutOfRangeException(nameof(warning));
            if (!double.IsFinite(critical)) throw new ArgumentOutOfRangeException(nameof(critical));
            if ((uint)direction > (uint)UiThresholdDirection.AtOrAbove)
                throw new ArgumentOutOfRangeException(nameof(direction));
            if (direction == UiThresholdDirection.AtOrBelow ? warning <= critical : warning >= critical)
                throw new ArgumentException("The warning boundary must be reached before the critical boundary.", nameof(warning));

            Warning = warning;
            Critical = critical;
            Direction = direction;
        }

        /// <summary>Gets the warning boundary.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public double Warning { get; }
        /// <summary>Gets the critical boundary.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public double Critical { get; }
        /// <summary>Gets the side of the boundary that makes it active.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiThresholdDirection Direction { get; }

        /// <summary>Gets Danger at the critical boundary or beyond, Warning at the warning boundary or beyond, and the base tone otherwise.</summary>
        /// <param name="value">The finite value to compare in the units in which the thresholds were declared.</param>
        /// <param name="baseTone">The semantic tone before either threshold has been reached.</param>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiTone ToneFor(double value, UiTone baseTone)
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if ((uint)baseTone > (uint)UiTone.Accent) throw new ArgumentOutOfRangeException(nameof(baseTone));
            return new UiStatusPresentation(LevelFor(value), baseTone).Tone;
        }

        internal UiStatusLevel LevelFor(double value) => LevelFor(value, Warning, Critical);

        internal UiStatusLevel LevelForFraction(float fraction)
        {
            var warning = (float)Warning;
            var critical = (float)Critical;

            // Keep float-inclusive boundaries for ordinary progress. Compare the authored boundaries
            // at full precision when rounding would merge them.
            return warning == critical ? LevelFor(fraction) : LevelFor(fraction, warning, critical);
        }

        private UiStatusLevel LevelFor(double value, double warning, double critical)
        {
            if (Direction == UiThresholdDirection.AtOrBelow)
            {
                if (value <= critical) return UiStatusLevel.Critical;
                return value <= warning ? UiStatusLevel.Warning : UiStatusLevel.Normal;
            }

            if (value >= critical) return UiStatusLevel.Critical;
            return value >= warning ? UiStatusLevel.Warning : UiStatusLevel.Normal;
        }
    }
}
