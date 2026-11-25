using System;

namespace TopiaForge.Mods
{
    internal static partial class UiWireCodec
    {
        private const byte FormattedCountdownTag = 43;
        private const byte ThresholdProgressTag = 44;
        private const byte StatBarTag = 45;

        private static byte StatusTag(UiNode? node) => node switch
        {
            UiCountdown { UsesStatusFormat: true } => FormattedCountdownTag,
            UiProgress { Thresholds: not null } => ThresholdProgressTag,
            UiStatBar => StatBarTag,
            _ => 0,
        };

        private static void WriteStatusNode(IUiWireWriter writer, UiNode node, byte tag, string clockEpoch, ref bool timed)
        {
            writer.WriteByte(tag);
            Text(writer, node.Id ?? string.Empty);
            switch (node)
            {
                case UiCountdown countdown:
                    WriteDouble(writer, countdown.EndsAt);
                    Text(writer, clockEpoch);
                    Text(writer, countdown.Label);
                    writer.WriteByte((byte)countdown.Format);
                    WriteOptionalThresholds(writer, countdown.Thresholds);
                    writer.WriteByte((byte)countdown.Tone);
                    timed = true;
                    return;
                case UiProgress progress:
                    writer.WriteSingle(progress.Fraction);
                    Text(writer, progress.Label);
                    WriteThresholds(writer, progress.Thresholds!);
                    writer.WriteByte((byte)progress.Tone);
                    return;
                case UiStatBar stat:
                    Text(writer, stat.Label);
                    writer.WriteVarUInt((ulong)stat.Value);
                    writer.WriteVarUInt((ulong)stat.Maximum);
                    WriteOptionalThresholds(writer, stat.Thresholds);
                    writer.WriteByte((byte)stat.Format);
                    writer.WriteByte((byte)stat.Tone);
                    return;
                default:
                    throw new ArgumentException("This UI status node cannot be sent to a player.");
            }
        }

        private static void WriteExpiredCountdown(IUiWireWriter writer, UiCountdown countdown)
        {
            if (countdown.UsesStatusFormat)
            {
                var tone = UiStatusPresentation.For(countdown, 0).Tone;
                WriteText(writer, UiCountdownFormatting.Text(countdown.Label, 0, countdown.Format), UiTextStyle.Body, tone, countdown.Id);
                return;
            }
            // A legacy countdown keeps its original plain-text expiry encoding.
            WriteText(writer, UiStatusFormatting.WithLabel(countdown.Label, "0"), UiTextStyle.Body, countdown.Tone,
                EnhancedTone(countdown.Tone) ? countdown.Id : null);
        }

        private static void WriteOptionalThresholds(IUiWireWriter writer, UiThresholds? thresholds)
        {
            Flag(writer, thresholds != null);
            if (thresholds != null) WriteThresholds(writer, thresholds);
        }

        private static void WriteThresholds(IUiWireWriter writer, UiThresholds thresholds)
        {
            WriteDouble(writer, thresholds.Warning);
            WriteDouble(writer, thresholds.Critical);
            writer.WriteByte((byte)thresholds.Direction);
        }

        private static void WriteDouble(IUiWireWriter writer, double value) =>
            writer.WriteUInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));

        private sealed partial class Decoder
        {
            private UiNode StatusNode(byte type)
            {
                var id = LayoutId();
                return type switch
                {
                    FormattedCountdownTag => FormattedCountdown(id),
                    ThresholdProgressTag => UiProgress.WithThresholds(Number(r), Text(r, 256), Thresholds(), Tone(), id),
                    StatBarTag => new UiStatBar(Text(r, 24), Count(1_000_000), Count(1_000_000), OptionalThresholds(),
                        (UiStatFormat)Byte(r, (byte)UiStatFormat.Percent), Tone(), id),
                    _ => throw Bad(),
                };
            }

            private UiCountdown FormattedCountdown(string? id)
            {
                var deadline = Double();
                if (Text(r, 256) != clockEpoch)
                    throw new ArgumentException("UiCountdown clock epoch is stale; request the current composition.");
                var label = Text(r, 256);
                var format = (UiCountdownFormat)Byte(r, (byte)UiCountdownFormat.HoursMinutesSeconds);
                return UiCountdown.WithFormat(deadline, label, format, OptionalThresholds(), Tone(), id);
            }

            private UiThresholds? OptionalThresholds() => Present() ? Thresholds() : null;

            private UiThresholds Thresholds() =>
                new UiThresholds(Double(), Double(), (UiThresholdDirection)Byte(r, (byte)UiThresholdDirection.AtOrAbove));

            private double Double()
            {
                if (!r.TryReadUInt64(out var bits)) throw Bad();
                var value = BitConverter.Int64BitsToDouble(unchecked((long)bits));
                if (!double.IsFinite(value)) throw Bad();
                return value;
            }
        }
    }
}
