using System;

namespace TopiaForge.Mods
{
    /// <summary>Controls how a stat bar labels its current value.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public enum UiStatFormat
    {
        /// <summary>Shows the current value.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        Value = 0,

        /// <summary>Shows the current value and maximum.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        ValueOfMaximum = 1,

        /// <summary>Shows the current value as a whole-number percentage of the maximum.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        Percent = 2
    }

    /// <summary>Shows a labelled stat inside a read-only bar with optional value thresholds.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class UiStatBar : UiNode
    {
        /// <summary>Creates a bar whose label remains readable across the filled and empty parts of the track.</summary>
        /// <param name="label">The visible plain-text label, from 1 to 24 characters.</param>
        /// <param name="value">The current value, from zero through the maximum.</param>
        /// <param name="maximum">The maximum value, from 1 to 1,000,000.</param>
        /// <param name="thresholds">Optional warning and critical thresholds in the same units as the value.</param>
        /// <param name="format">How to show the current value next to the label.</param>
        /// <param name="tone">The semantic tone before reaching either threshold.</param>
        /// <param name="id">The optional stable presentation id.</param>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiStatBar(string label, int value, int maximum, UiThresholds? thresholds = null,
            UiStatFormat format = UiStatFormat.Value, UiTone tone = UiTone.Success, string? id = null)
            : base(id)
        {
            if (label == null) throw new ArgumentNullException(nameof(label));
            if (string.IsNullOrWhiteSpace(label) || label.Length > 24)
                throw new ArgumentException("A stat bar label must contain 1 to 24 characters.", nameof(label));
            if (maximum < 1 || maximum > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximum));
            if (value < 0 || value > maximum) throw new ArgumentOutOfRangeException(nameof(value));
            if ((uint)format > (uint)UiStatFormat.Percent) throw new ArgumentOutOfRangeException(nameof(format));
            UiWidgetValues.Tone(tone);

            Label = label;
            Value = value;
            Maximum = maximum;
            Thresholds = thresholds;
            Format = format;
            Tone = tone;
            DisplayLabel = UiStatusFormatting.Stat(label, value, maximum, format);
        }

        /// <summary>Gets the plain-text label.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string Label { get; }
        /// <summary>Gets the current value.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public int Value { get; }
        /// <summary>Gets the maximum value.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public int Maximum { get; }
        /// <summary>Gets the warning and critical values, when supplied.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiThresholds? Thresholds { get; }
        /// <summary>Gets the display format of the current value.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiStatFormat Format { get; }
        /// <summary>Gets the base semantic tone.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiTone Tone { get; }

        internal float Fraction => (float)Value / Maximum;
        internal string DisplayLabel { get; }
    }
}
