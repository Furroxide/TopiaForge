using System;

namespace TopiaForge.Mods
{
    /// <summary>A finite progress fraction with optional plain-text caption.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class UiProgress : UiNode
    {
        /// <summary>Creates a presentation-only progress bar. Update the composition when its value changes.</summary>
        public UiProgress(float fraction, string label = "", UiTone tone = UiTone.Neutral, string? id = null) : base(id)
        {
            if (float.IsNaN(fraction) || float.IsInfinity(fraction) || fraction < 0 || fraction > 1)
                throw new ArgumentOutOfRangeException(nameof(fraction), "Progress must be between zero and one.");
            UiWidgetValues.Text(label, nameof(label)); UiWidgetValues.Tone(tone);
            Fraction = fraction; Label = label; Tone = tone;
            DisplayLabel = label;
        }

        /// <summary>Creates a progress bar that displays its percentage and resolves status from fraction thresholds.</summary>
        /// <param name="fraction">The completed fraction, from zero to one.</param>
        /// <param name="label">The optional plain-text caption preceding the percentage.</param>
        /// <param name="thresholds">The warning and critical fractions, both from zero to one.</param>
        /// <param name="tone">The semantic tone before reaching either threshold.</param>
        /// <param name="id">The optional stable presentation id.</param>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public static UiProgress WithThresholds(float fraction, string label, UiThresholds thresholds,
            UiTone tone = UiTone.Accent, string? id = null) =>
            new UiProgress(fraction, label, thresholds, tone, id);

        private UiProgress(float fraction, string label, UiThresholds thresholds, UiTone tone, string? id)
            : this(fraction, label, tone, id)
        {
            if (thresholds == null) throw new ArgumentNullException(nameof(thresholds));
            if (thresholds.Warning < 0d || thresholds.Warning > 1d || thresholds.Critical < 0d || thresholds.Critical > 1d)
                throw new ArgumentOutOfRangeException(nameof(thresholds), "Progress thresholds must be between zero and one.");
            Thresholds = thresholds;
            DisplayLabel = UiStatusFormatting.Progress(label, fraction);
        }

        /// <summary>Gets the completed fraction, from zero to one.</summary>
        public float Fraction { get; }
        /// <summary>Gets the plain-text caption.</summary>
        public string Label { get; }
        /// <summary>Gets the semantic tone.</summary>
        public UiTone Tone { get; }
        /// <summary>Gets the warning and critical fractions, when supplied.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiThresholds? Thresholds { get; }

        internal string DisplayLabel { get; }
    }

    /// <summary>A countdown rendered automatically on the shared host world/session timeline.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class UiCountdown : UiNode
    {
        internal UiClockBinding.EpochIdentity? ClockIdentity { get; set; }
        internal string? ClockEpoch => ClockIdentity?.Value;
        /// <summary>Creates a countdown ending at <paramref name="endsAt"/> on <see cref="IUiService.ClockTime"/>.</summary>
        public UiCountdown(double endsAt, string label = "", UiTone tone = UiTone.Neutral, string? id = null) : base(id)
        {
            if (double.IsNaN(endsAt) || double.IsInfinity(endsAt) || endsAt < 0) throw new ArgumentOutOfRangeException(nameof(endsAt));
            UiWidgetValues.Text(label, nameof(label)); UiWidgetValues.Tone(tone);
            EndsAt = endsAt; Label = label; Tone = tone;
        }

        /// <summary>Creates a countdown in the requested format, with optional remaining-second thresholds.</summary>
        /// <param name="endsAt">The deadline on <see cref="IUiService.ClockTime"/>.</param>
        /// <param name="label">The optional plain-text caption preceding the digits.</param>
        /// <param name="format">The format of the rounded-up remaining time.</param>
        /// <param name="thresholds">Optional warning and critical thresholds compared against the unrounded remaining time.</param>
        /// <param name="tone">The semantic tone before reaching either threshold.</param>
        /// <param name="id">The optional stable presentation id.</param>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public static UiCountdown WithFormat(double endsAt, string label, UiCountdownFormat format,
            UiThresholds? thresholds = null, UiTone tone = UiTone.Neutral, string? id = null) =>
            new UiCountdown(endsAt, label, format, thresholds, tone, id);

        private UiCountdown(double endsAt, string label, UiCountdownFormat format, UiThresholds? thresholds,
            UiTone tone, string? id)
            : this(endsAt, label, tone, id)
        {
            if ((uint)format > (uint)UiCountdownFormat.HoursMinutesSeconds)
                throw new ArgumentOutOfRangeException(nameof(format));
            if (thresholds != null && thresholds.Direction != UiThresholdDirection.AtOrBelow)
                throw new ArgumentException("Countdown thresholds must apply at or below the stated seconds.", nameof(thresholds));
            Format = format;
            Thresholds = thresholds;
        }

        /// <summary>Gets the original deadline in shared host timeline seconds, unchanged by replication or late joining.</summary>
        public double EndsAt { get; }
        /// <summary>Gets the caption preceding the remaining whole seconds.</summary>
        public string Label { get; }
        /// <summary>Gets the semantic tone.</summary>
        public UiTone Tone { get; }
        /// <summary>Gets the display format, which defaults to whole seconds.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiCountdownFormat Format { get; }
        /// <summary>Gets the warning and critical remaining seconds, when supplied.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiThresholds? Thresholds { get; }
        internal bool UsesStatusFormat => Format != UiCountdownFormat.Seconds || Thresholds != null;

        /// <summary>Rounds remaining seconds up, clamps expired deadlines to zero and bounds very distant deadlines.</summary>
        public int RemainingSeconds(double clockTime) => UiCountdownFormatting.WholeSeconds(RemainingTime(clockTime));

        internal double RemainingTime(double clockTime)
        {
            if (!double.IsFinite(clockTime) || clockTime < 0d) throw new ArgumentOutOfRangeException(nameof(clockTime));
            return Math.Max(0d, EndsAt - clockTime);
        }
    }

    internal static class UiWidgetValues
    {
        internal static void Text(string text, string name)
        { if (text == null || text.Length > 256) throw new ArgumentException("UI text must contain at most 256 characters.", name); }
        internal static void Tone(UiTone tone)
        { if (!Enum.IsDefined(typeof(UiTone), tone)) throw new ArgumentOutOfRangeException(nameof(tone)); }
    }
}
