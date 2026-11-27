using System;
using System.Globalization;

namespace TopiaForge.Mods
{
    internal enum UiStatusLevel { Normal, Warning, Critical }

    internal readonly struct UiStatusPresentation
    {
        internal UiStatusPresentation(UiStatusLevel level, UiTone baseTone)
        {
            Level = level;
            Tone = level switch
            {
                UiStatusLevel.Warning => UiTone.Warning,
                UiStatusLevel.Critical => UiTone.Danger,
                _ => baseTone,
            };
        }

        internal UiStatusLevel Level { get; }
        internal UiTone Tone { get; }
        internal UiGlyph? Glyph => Level switch
        {
            UiStatusLevel.Warning => UiGlyph.Warning,
            UiStatusLevel.Critical => UiGlyph.Cross,
            _ => null,
        };

        internal static UiStatusPresentation For(UiCountdown countdown, double remainingSeconds) =>
            new UiStatusPresentation(countdown.Thresholds?.LevelFor(remainingSeconds) ?? UiStatusLevel.Normal, countdown.Tone);

        internal static UiStatusPresentation For(UiProgress progress) =>
            new UiStatusPresentation(progress.Thresholds?.LevelForFraction(progress.Fraction) ?? UiStatusLevel.Normal, progress.Tone);

        internal static UiStatusPresentation For(UiStatBar stat) =>
            new UiStatusPresentation(stat.Thresholds?.LevelFor(stat.Value) ?? UiStatusLevel.Normal, stat.Tone);
    }

    internal static class UiStatusFormatting
    {
        internal static string Progress(string label, float fraction) =>
            WithLabel(label, Percentage((int)Math.Round(fraction * 100f, MidpointRounding.AwayFromZero)));

        internal static string Stat(string label, int value, int maximum, UiStatFormat format) =>
            WithLabel(label, format switch
            {
                UiStatFormat.Value => Number(value),
                UiStatFormat.ValueOfMaximum => Number(value) + " / " + Number(maximum),
                UiStatFormat.Percent => Percentage((value * 100 + maximum / 2) / maximum),
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            });

        internal static string WithLabel(string label, string value) => label.Length == 0 ? value : label + " " + value;

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Percentage(int value) => Number(value) + "%";
    }
}
