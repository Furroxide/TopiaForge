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
        }
        /// <summary>Gets the completed fraction, from zero to one.</summary>
        public float Fraction { get; }
        /// <summary>Gets the plain-text caption.</summary>
        public string Label { get; }
        /// <summary>Gets the semantic tone.</summary>
        public UiTone Tone { get; }
    }

    /// <summary>A countdown rendered automatically on the owning UI service's local session clock.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class UiCountdown : UiNode
    {
        /// <summary>Creates a countdown ending at <paramref name="endsAt"/> on <see cref="IUiService.ClockTime"/>.</summary>
        public UiCountdown(double endsAt, string label = "", UiTone tone = UiTone.Neutral, string? id = null) : base(id)
        {
            if (double.IsNaN(endsAt) || double.IsInfinity(endsAt) || endsAt < 0) throw new ArgumentOutOfRangeException(nameof(endsAt));
            UiWidgetValues.Text(label, nameof(label)); UiWidgetValues.Tone(tone);
            EndsAt = endsAt; Label = label; Tone = tone;
        }
        /// <summary>Gets the deadline in local session-clock seconds.</summary>
        public double EndsAt { get; }
        /// <summary>Gets the caption preceding the remaining whole seconds.</summary>
        public string Label { get; }
        /// <summary>Gets the semantic tone.</summary>
        public UiTone Tone { get; }
        /// <summary>Rounds remaining seconds up, clamps expired deadlines to zero and bounds very distant deadlines.</summary>
        public int RemainingSeconds(double clockTime)
        {
            if (double.IsNaN(clockTime) || double.IsInfinity(clockTime) || clockTime < 0) throw new ArgumentOutOfRangeException(nameof(clockTime));
            return (int)Math.Min(int.MaxValue, Math.Ceiling(Math.Max(0, EndsAt - clockTime)));
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
