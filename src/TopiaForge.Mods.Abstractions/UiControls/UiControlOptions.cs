using System;
using System.Globalization;

namespace TopiaForge.Mods
{
    /// <summary>Plain-text placeholder and validation feedback for a text input.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class UiInputDecoration
    {
        /// <summary>Creates a decoration with an optional error of 1 to 256 text units.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiInputDecoration(string placeholder = "", string? error = null)
        {
            Placeholder = placeholder ?? throw new ArgumentNullException(nameof(placeholder));
            if (error != null) UiArtValues.Label(error, false, nameof(error));
            Error = error;
        }

        /// <summary>Gets the plain text shown when the input is empty.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string Placeholder { get; }

        /// <summary>Gets the plain-text validation error, if supplied.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string? Error { get; }
    }

    public sealed partial class UiTextInput
    {
        /// <summary>Creates an editable text field with an optional visible error.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public static UiTextInput WithDecoration(string id, string label, string value, Action<string> changed,
            UiInputDecoration decoration, int maximumLength = 256, bool enabled = true) =>
            new UiTextInput(id, label, value, changed, decoration, maximumLength, enabled);

        private UiTextInput(string id, string label, string value, Action<string> changed,
            UiInputDecoration decoration, int maximumLength, bool enabled)
            : this(id, label, value, changed,
                (decoration ?? throw new ArgumentNullException(nameof(decoration))).Placeholder, maximumLength, enabled)
        {
            Error = decoration.Error;
            UsesExplicitDecoration = true;
        }

        /// <summary>Gets the authored validation error. The field remains editable.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string? Error { get; }

        internal bool UsesExplicitDecoration { get; }
    }

    /// <summary>Specifies the meaning of a slider's numeric readout.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public enum UiValueFormatKind
    {
        /// <summary>Displays the value as a fixed-decimal number.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        Number = 0,
        /// <summary>Displays a whole percent of the slider's range.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        Percent = 1,
        /// <summary>Displays the value with a multiplication sign.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        Multiplier = 2,
    }

    /// <summary>Formats a slider value in the invariant culture.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class UiValueFormat
    {
        private static readonly UiValueFormat[] Numbers = NumberFormats(UiValueFormatKind.Number);
        private static readonly UiValueFormat[] Multipliers = NumberFormats(UiValueFormatKind.Multiplier);
        private static readonly UiValueFormat Percentage = new UiValueFormat(UiValueFormatKind.Percent, 0);
        private readonly string pattern;

        private UiValueFormat(UiValueFormatKind kind, int decimals)
        {
            Kind = kind;
            Decimals = decimals;
            pattern = "F" + decimals.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Uses the specified number of decimal places, from zero to three.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public static UiValueFormat Number(int decimals = 0) => GetNumberFormat(Numbers, decimals);

        /// <summary>Uses a whole percent of the minimum-to-maximum range.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public static UiValueFormat Percent() => Percentage;

        /// <summary>Uses the specified zero to three decimal places followed by a multiplication sign.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public static UiValueFormat Multiplier(int decimals = 2) => GetNumberFormat(Multipliers, decimals);

        /// <summary>Gets the meaning of the displayed value.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiValueFormatKind Kind { get; }

        /// <summary>Gets the number of decimal places.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public int Decimals { get; }

        internal static UiValueFormat FromWire(UiValueFormatKind kind, int decimals) => kind switch
        {
            UiValueFormatKind.Number => Number(decimals),
            UiValueFormatKind.Multiplier => Multiplier(decimals),
            UiValueFormatKind.Percent when decimals == 0 => Percentage,
            _ => throw new ArgumentException("Invalid value format.", nameof(kind)),
        };

        internal string Readout(float value, float minimum, float maximum)
        {
            if (Kind == UiValueFormatKind.Percent)
            {
                var fraction = ((double)value - minimum) / ((double)maximum - minimum);
                return (fraction * 100d).ToString("F0", CultureInfo.InvariantCulture) + "%";
            }

            var formatted = value.ToString(pattern, CultureInfo.InvariantCulture);
            return Kind == UiValueFormatKind.Multiplier ? formatted + "×" : formatted;
        }

        private static UiValueFormat[] NumberFormats(UiValueFormatKind kind) =>
            new[] { new UiValueFormat(kind, 0), new UiValueFormat(kind, 1), new UiValueFormat(kind, 2), new UiValueFormat(kind, 3) };

        private static UiValueFormat GetNumberFormat(UiValueFormat[] formats, int decimals)
        {
            if ((uint)decimals >= formats.Length) throw new ArgumentOutOfRangeException(nameof(decimals));
            return formats[decimals];
        }
    }

    public sealed partial class UiSlider
    {
        /// <summary>Creates a formatted slider with an optional sequence of evenly spaced steps.</summary>
        /// <remarks>Divisions is zero for continuous input, or 1 to 1,000. The initial value must be on a step.</remarks>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiSlider(string id, string label, float minimum, float maximum, float value,
            Action<float> changed, int divisions, UiValueFormat format, bool enabled = true)
            : this(id, label, minimum, maximum, value, changed, enabled)
        {
            if ((uint)divisions > 1000) throw new ArgumentOutOfRangeException(nameof(divisions));
            if (UiSliderSteps.Snap(value, minimum, maximum, divisions) != value)
                throw new ArgumentException("The initial value must lie on a slider step.", nameof(value));
            Format = format ?? throw new ArgumentNullException(nameof(format));
            Divisions = divisions;
            UsesExplicitFormat = true;
        }

        /// <summary>Gets the interval count, or zero for continuous input.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public int Divisions { get; }

        /// <summary>Gets the requested numeric format. Existing sliders report Number with two decimal places.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiValueFormat Format { get; } = UiValueFormat.Number(2);

        internal bool UsesExplicitFormat { get; }
    }

    internal static class UiSliderSteps
    {
        internal static float Snap(float value, float minimum, float maximum, int divisions)
        {
            if (divisions == 0 || value == minimum || value == maximum) return value;
            return At(Index(value, minimum, maximum, divisions), minimum, maximum, divisions);
        }

        internal static int Index(float value, float minimum, float maximum, int divisions) =>
            (int)Math.Round(((double)value - minimum) / ((double)maximum - minimum) * divisions, MidpointRounding.AwayFromZero);

        internal static float At(int index, float minimum, float maximum, int divisions) =>
            index <= 0 ? minimum : index >= divisions ? maximum :
                (float)(minimum + ((double)maximum - minimum) * index / divisions);
    }
}
