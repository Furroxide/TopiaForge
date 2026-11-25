using System;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiLayoutRenderer
        {
            private sealed partial class NativeArtLeaf
            {
                private sealed class NativePipRefill
                {
                    private readonly TopiaForgeContainer host;
                    private readonly UiPips source;
                    private readonly TMP_Text label;
                    private readonly NativePip[] marks;
                    private readonly UiCallbackGate callbacks;
                    private readonly char[] digits;
                    private readonly int prefixLength;
                    private UiRefillPresentation painted;
                    private float width;
                    private float labelHeight;
                    private float stroke;
                    private float cornerRadius;
                    private UiThemeColor ink;

                    internal NativePipRefill(TopiaForgeContainer host, UiPips source, TMP_Text label, NativePip[] marks,
                        UiCallbackGate callbacks)
                    {
                        this.host = host;
                        this.source = source;
                        this.label = label;
                        this.marks = marks;
                        this.callbacks = callbacks;
                        ReadoutSample = source.DisplayLabel + " (99% refilling)";
                        digits = new char[ReadoutSample.Length];
                        source.Label.AsSpan().CopyTo(digits);
                        prefixLength = source.Label.Length + 2;
                        digits[prefixLength - 2] = ':';
                        digits[prefixLength - 1] = ' ';
                        host.Go.AddComponent<UiSurfaceLayoutFrame>().Initialize(Tick);
                    }

                    internal string ReadoutSample { get; }

                    internal UiPipTrack Arrange(float width, float labelHeight, float stroke, float cornerRadius, UiThemeColor ink)
                    {
                        this.width = width;
                        this.labelHeight = labelHeight;
                        this.stroke = stroke;
                        this.cornerRadius = cornerRadius;
                        this.ink = ink;
                        painted = Read();
                        WriteReadout(painted);
                        return new UiPipTrack(painted, width);
                    }

                    private UiRefillPresentation Read() => UiRefillPresentation.For(source, callbacks.Clock(), callbacks.ClockEpoch());

                    private void Tick()
                    {
                        if (width == 0f) return;
                        var current = Read();
                        var changedSecond = current.RemainingSeconds != painted.RemainingSeconds;
                        var changedCount = current.Filled != painted.Filled;
                        if (changedSecond || changedCount) WriteReadout(current);
                        if (changedCount || current.Fraction != painted.Fraction && (!host.Host.EffectiveReducedMotion || changedSecond))
                        {
                            var track = new UiPipTrack(current, width);
                            PaintMark(track, painted.Filled);
                            if (changedCount) PaintMark(track, current.Filled);
                        }
                        painted = current;
                    }

                    private void PaintMark(UiPipTrack track, int index)
                    {
                        if (index >= marks.Length) return;
                        var mark = track.Mark(index);
                        var radius = Math.Min(cornerRadius, Math.Min(mark.Width, mark.Height) / 2f);
                        PaintPipFill(marks[index], mark.Fill(stroke, radius), labelHeight, ink);
                    }

                    private void WriteReadout(UiRefillPresentation reading)
                    {
                        var length = prefixLength + UiCountdownFormatting.WriteDigits(reading.Filled, UiCountdownFormat.Seconds,
                            digits.AsSpan(prefixLength));
                        " / ".AsSpan().CopyTo(digits.AsSpan(length));
                        length += 3;
                        length += UiCountdownFormatting.WriteDigits(reading.Total, UiCountdownFormat.Seconds, digits.AsSpan(length));
                        if (reading.Percentage is int percent)
                        {
                            " (".AsSpan().CopyTo(digits.AsSpan(length));
                            length += 2;
                            length += UiCountdownFormatting.WriteDigits(percent, UiCountdownFormat.Seconds, digits.AsSpan(length));
                            "% refilling)".AsSpan().CopyTo(digits.AsSpan(length));
                            length += "% refilling)".Length;
                        }
                        label.SetCharArray(digits, 0, length);
                    }
                }
            }
        }
    }
}
