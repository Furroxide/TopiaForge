using System;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiKitStatusWireTests
    {
        private const string Epoch = "e";
        private const string Owner = "example.status";
        private static readonly UiWireEnvelope Envelope = new UiWireEnvelope(Owner, new byte[32], 1, null, new byte[32]);
        private static readonly byte[] Prefix = Write(null).Bytes[..^1];

        private static readonly byte[] Formatted = Convert.FromHexString(
            "2B0163000000000000F83F016501430101000000000000244000000000000014400006");
        private static readonly byte[] ThresholdProgress = Convert.FromHexString(
            "2C01700000003F0150000000000000E03F000000000000E83F0103");
        private static readonly byte[] Stat = Convert.FromHexString("2D017301538101AC02000106");

        internal static void Run()
        {
            TheOldestTagsRetainTheirExactBytes();
            NewFieldsUseTheAssignedTags();
            InvalidStatusFieldsAreRefused();
            RetainedCountdownUsesFormattedZeroAndPreservesItsClock();
            CompleteFramesKeepTheirLimit();
            Console.WriteLine("K2 status wire: exact tags 43–45, older bytes, bounded fields, framed limits and retained clocks passed.");
        }

        private static void TheOldestTagsRetainTheirExactBytes()
        {
            for (var tone = UiTone.Neutral; tone <= UiTone.Accent; tone++)
            {
                var newerTone = tone > UiTone.Danger;
                var progress = new UiProgress(0.5f, "P", tone, "p");
                var clock = new UiCountdown(1.5d, "C", tone, "c");
                var seconds = UiCountdown.WithFormat(1.5d, "C", UiCountdownFormat.Seconds, tone: tone, id: "c");
                var expectedProgress = Convert.FromHexString(newerTone ? "2101700000003F015000" : "0C0101700000003F015000");
                var expectedCountdown = Convert.FromHexString(newerTone
                    ? "220163000000000000F83F0143000165" : "0D010163000000000000F83F0165014300");
                expectedProgress[^1] = (byte)tone;
                expectedCountdown[newerTone ? ^3 : ^1] = (byte)tone;
                var progressBytes = newerTone ? Frame(expectedProgress) : expectedProgress;
                var countdownBytes = newerTone ? Frame(expectedCountdown) : expectedCountdown;
                Check(Write(progress, newerTone).Bytes.SequenceEqual(progressBytes) &&
                    Write(clock, newerTone).Bytes.SequenceEqual(countdownBytes) && Write(seconds, newerTone).Bytes.SequenceEqual(countdownBytes),
                    "Ordinary progress and seconds countdowns retain the exact legacy or K1 layout for every tone.");
                Check(UiWireCodec.RequiredContractVersion(progress) == (newerTone ? 2 : 1) &&
                    UiWireCodec.RequiredContractVersion(seconds) == (newerTone ? 2 : 1) &&
                    Read(progressBytes, newerTone) is UiProgress p && p.Tone == tone && p.Thresholds == null &&
                    Read(countdownBytes, newerTone) is UiCountdown c && c.Tone == tone && c.Format == UiCountdownFormat.Seconds,
                    "Ordinary status stays readable by its original contract version.");
                var expired = Convert.FromHexString(newerTone ? "0F0163034320300000" : "01034320300000");
                expired[^1] = (byte)tone;
                Check(Write(clock, newerTone, "next", retained: true).Bytes.SequenceEqual(newerTone ? Frame(expired) : expired),
                    "Ordinary countdowns retain their existing exact zero surrogate.");
            }
        }

        private static void NewFieldsUseTheAssignedTags()
        {
            var countdown = UiCountdown.WithFormat(1.5d, "C", UiCountdownFormat.MinutesSeconds,
                new UiThresholds(10d, 5d), UiTone.Accent, "c");
            var progress = UiProgress.WithThresholds(0.5f, "P", new UiThresholds(0.5d, 0.75d, UiThresholdDirection.AtOrAbove), UiTone.Danger, "p");
            var stat = new UiStatBar("S", 129, 300, format: UiStatFormat.ValueOfMaximum, tone: UiTone.Accent, id: "s");
            var typedCountdown = (UiCountdown)Exact(countdown, Formatted);
            var typedProgress = (UiProgress)Exact(progress, ThresholdProgress);
            var typedStat = (UiStatBar)Exact(stat, Stat);
            var thresholdSeconds = Exact(UiCountdown.WithFormat(1.5d, "C", UiCountdownFormat.Seconds,
                countdown.Thresholds, UiTone.Accent, "c"), Changed(Formatted, 15, 0));
            Check(typedCountdown is
            {
                Id: "c", EndsAt: 1.5, ClockEpoch: Epoch, Label: "C", Tone: UiTone.Accent,
                Format: UiCountdownFormat.MinutesSeconds, Thresholds: { Warning: 10, Critical: 5, Direction: UiThresholdDirection.AtOrBelow }
            } &&
                thresholdSeconds is UiCountdown { Format: UiCountdownFormat.Seconds, Thresholds.Critical: 5d } &&
                typedProgress is
                {
                    Id: "p", Fraction: 0.5f, Label: "P", Tone: UiTone.Danger,
                    Thresholds: { Warning: 0.5, Critical: 0.75, Direction: UiThresholdDirection.AtOrAbove }
                } &&
                typedStat is { Id: "s", Label: "S", Value: 129, Maximum: 300, Thresholds: null, Format: UiStatFormat.ValueOfMaximum, Tone: UiTone.Accent },
                "The typed status payload preserves exact values, thresholds and the original live clock.");
            Check(Read(Write(UiCountdown.WithFormat(2, "No thresholds", UiCountdownFormat.HoursMinutesSeconds)).Bytes)
                    is UiCountdown { Id: null, Format: UiCountdownFormat.HoursMinutesSeconds, Thresholds: null } &&
                Read(Write(new UiStatBar(new string('S', 24), 1_000_000, 1_000_000, new UiThresholds(200d, 100d), UiStatFormat.Percent)).Bytes)
                    is UiStatBar
                {
                    Id: null, Value: 1_000_000, Maximum: 1_000_000, Format: UiStatFormat.Percent,
                    Thresholds: { Warning: 200, Critical: 100 }
                },
                "Optional IDs and thresholds round-trip independently at the configured bounds.");
            var adjacent = Math.BitIncrement(.5d);
            var precise = UiProgress.WithThresholds(.5f, "P", new UiThresholds(.5d, adjacent, UiThresholdDirection.AtOrAbove), id: "p");
            var preciseBytes = Write(precise).Bytes;
            Check(Read(preciseBytes) is UiProgress decoded &&
                decoded.Thresholds is { Warning: .5d, Direction: UiThresholdDirection.AtOrAbove } thresholds &&
                thresholds.Critical == adjacent &&
                UiStatusPresentation.For(decoded) is { Level: UiStatusLevel.Warning, Glyph: UiGlyph.Warning } &&
                Write(decoded).Bytes.SequenceEqual(preciseBytes),
                "Adjacent progress thresholds preserve their authored values, warning and canonical bytes across the wire.");

            Check(!UiWireCodec.Invoke(typedCountdown, "c", "") && !UiWireCodec.Invoke(typedProgress, "p", "") &&
                !UiWireCodec.Invoke(typedStat, "s", ""), "Status leaves expose no remote callback.");
        }

        private static UiNode Exact(UiNode node, byte[] payload)
        {
            var bytes = Write(node).Bytes;
            Check(bytes.SequenceEqual(Frame(payload)) &&
                UiWireCodec.Measure(node, Epoch, bytes.Length, envelope: Envelope) == bytes.Length &&
                UiWireCodec.RequiredContractVersion(new UiColumn(new UiCard(new UiPadding(node, new UiInsets(UiSpace.Sm))))) == 3,
                "A nested status node chooses contract 3 and measures exactly the assigned field bytes for tag " + payload[0] + ".");
            var received = Read(bytes);
            Check(Write(received).Bytes.SequenceEqual(bytes), "Decoding preserves canonical status wire bytes.");
            Refused(() => Write(node, enhanced: false), "A status node requires an ownership envelope.");
            Refused(() => Read(payload, enhanced: false), "A status tag is rejected with no ownership envelope.");
            return received;
        }

        private static void InvalidStatusFieldsAreRefused()
        {
            Refused(() => Read(Frame(Changed(Formatted, 15, 3))), "Unknown countdown format is refused.");
            Refused(() => Read(Frame(Changed(Formatted, 16, 2))), "A countdown's threshold presence byte must be 0 or 1.");
            var ascendingCountdown = Changed(Formatted, ^2, 1);
            Formatted.AsSpan(17, 8).CopyTo(ascendingCountdown.AsSpan(25));
            Formatted.AsSpan(25, 8).CopyTo(ascendingCountdown.AsSpan(17));
            Refused(() => Read(Frame(ascendingCountdown)), "Countdowns require AtOrBelow, even with otherwise ordered thresholds.");
            Refused(() => Read(Frame(Changed(Formatted, ^2, 2))), "Unknown threshold direction is refused.");
            Refused(() => Read(Frame(Changed(Formatted, ^1, 7))), "Unknown status tone is refused.");
            Refused(() => Read(Frame(Changed(Formatted, 10, 0xF8))), "A negative countdown deadline is refused.");
            Refused(() => Read(Frame(Changed(Formatted, 10, 0x7F))), "A non-finite countdown deadline is refused.");
            Refused(() => Read(Frame(Changed(Changed(ThresholdProgress, 15, 0xF8), 16, 0x7F))), "A non-finite progress threshold is refused.");
            Refused(() => Read(Frame(Changed(ThresholdProgress, 16, 0x40))), "Fraction thresholds above one are refused.");
            Refused(() => Read(Frame(Changed(Changed(ThresholdProgress, 5, 0x80), 6, 0x7F))), "A non-finite progress fraction is refused.");
            Refused(() => Read(Frame(Changed(Stat, 9, 2))), "A stat bar's threshold presence byte must be 0 or 1.");
            Refused(() => Read(Frame(Changed(Stat, 8, 0))), "A noncanonical stat varuint is refused.");
            Refused(() => Read(Frame(Changed(Stat, 6, 3))), "A stat value above its maximum is refused.");
            Refused(() => Read(Frame(Changed(Stat, ^2, 3))), "Unknown stat format is refused.");
            Refused(() => Read(Frame(Stat[..^1])), "A truncated status fails before presentation.");
            Refused(() => Read(Frame(Stat).Append((byte)0).ToArray()), "A status frame cannot have trailing bytes.");
        }

        private static void RetainedCountdownUsesFormattedZeroAndPreservesItsClock()
        {
            var longLabel = new string('x', 127);
            var timer = UiCountdown.WithFormat(30, longLabel, UiCountdownFormat.HoursMinutesSeconds, new UiThresholds(10, 5), id: "clock");
            var minutes = UiCountdown.WithFormat(40, "Break", UiCountdownFormat.MinutesSeconds, tone: UiTone.Muted, id: "minutes");
            var seconds = UiCountdown.WithFormat(45, "", UiCountdownFormat.Seconds, new UiThresholds(1, 0), id: "seconds");
            var tabs = new UiTabs("tabs", new[] { new UiTab("front", "Front", new UiText("Front")),
                new UiTab("hidden", "Hidden", new UiTooltip(new UiColumn(timer, minutes, seconds), "Details")) }, "front");
            var measured = UiWireCodec.Measure(tabs, Epoch, 4096, envelope: Envelope);
            Check(timer.ClockIdentity == null && minutes.ClockIdentity == null && seconds.ClockIdentity == null,
                "Measuring every hidden status field leaves authored clocks unbound.");
            var live = Write(tabs);
            var identity = timer.ClockIdentity;
            Check(live.Bytes.Length == measured && live.Timed && UiClockBinding.IsCurrent(tabs, Epoch),
                "Writing the typed tree binds every nested countdown, including the hidden tab.");
            var current = Write(tabs, retained: true);
            Check(current.Timed && live.Bytes.SequenceEqual(current.Bytes), "A retained current clock keeps its original deadline.");
            const string replacement = "new-epoch";
            Refused(() => Read(live.Bytes, epoch: replacement), "The decoder refuses a stale transmitted deadline.");
            var expired = Write(tabs, epoch: replacement, retained: true);
            var page = (UiColumn)((UiTooltip)((UiTabs)Read(expired.Bytes, epoch: replacement)).Tabs[1].Child).Child;
            Check(!expired.Timed && expired.Bytes.Length < live.Bytes.Length &&
                page.Children[0] is UiText first && first.Id == "clock" && first.Text == longLabel + " 0:00:00" && first.Tone == UiTone.Danger &&
                page.Children[1] is UiText { Id: "minutes", Text: "Break 0:00", Tone: UiTone.Muted } &&
                page.Children[2] is UiText { Id: "seconds", Text: "0", Tone: UiTone.Danger } &&
                ReferenceEquals(timer.ClockIdentity, identity) && timer.ClockEpoch == Epoch,
                "Retained status projects the exact clock format and tone at zero without carrying a stale deadline.");
            var fresh = UiCountdown.WithFormat(70d, "Fresh", UiCountdownFormat.MinutesSeconds);
            var writer = new WorldWireWriter(4096);
            Refused(() => UiWireCodec.Write(writer, new UiColumn(fresh, tabs), replacement, Envelope),
                "Reusing an ended clock in a new tree is rejected.");
            Check(writer.Length == 0 && fresh.ClockIdentity == null,
                "A stale nested deadline fails before writing or binding a preceding fresh countdown.");
        }

        private static void CompleteFramesKeepTheirLimit()
        {
            const int maximum = 32 * 1024;
            var now = UiCountdown.WithFormat(300, "Status", UiCountdownFormat.HoursMinutesSeconds, id: "clock");
            var request = new UiSurfaceRequest("frame", "Status frame", "Status");
            var filler = Enumerable.Range(0, 7).Select(_ => (UiNode)new UiText(new string('x', 4096))).ToArray();
            UiColumn Tree(int tail) => new UiColumn(filler.Concat(new UiNode[]
            {
                now, UiProgress.WithThresholds(0.9f, "Power", new UiThresholds(0.85d, 0.95d, UiThresholdDirection.AtOrAbove)),
                new UiStatBar("Integrity", 129, 300), new UiText(new string('x', tail)),
            }).ToArray());

            int Measure(UiNode node) => UiWireCodec.MeasureTargetedSurface(Owner, "recipient", 1, 1,
                request, request.Body, true, node, Epoch, envelope: Envelope);
            int WriteFrame(UiNode node)
            {
                var writer = new WorldWireWriter(maximum);
                writer.WriteByte(1);
                writer.WriteByte(10);
                writer.WriteString(Owner);
                writer.WriteString("recipient");
                writer.WriteVarUInt(1);
                writer.WriteVarUInt(1);
                UiWireCodec.WriteTargetedSurfaceBody(writer, request, request.Body, true, node, Epoch, retained: false, envelope: Envelope);
                return writer.Length;
            }

            var tail = 256 + maximum - Measure(Tree(256));
            var exact = Tree(tail);
            Check(Measure(exact) == maximum && WriteFrame(exact) == maximum && now.ClockEpoch == null,
                "All tags and ownership bytes fit exactly at the targeted frame limit without binding an uncommitted clock.");
            BudgetRefused(() => Measure(Tree(tail + 1)));
            BudgetRefused(() => WriteFrame(Tree(tail + 1)));
            Check(now.ClockEpoch == null, "A full-frame quota failure leaves the authored deadline unbound.");
        }

        private static byte[] Changed(byte[] source, Index position, byte value)
        {
            var bytes = (byte[])source.Clone();
            bytes[position] = value;
            return bytes;
        }

        private static byte[] Frame(byte[] node) => Prefix.Concat(node).ToArray();

        private static (byte[] Bytes, bool Timed) Write(UiNode? node, bool enhanced = true, string epoch = Epoch, bool retained = false)
        {
            var writer = new WorldWireWriter(32 * 1024);
            var envelope = enhanced ? Envelope : null;
            var timed = retained ? UiWireCodec.WriteRetained(writer, node, epoch, envelope) : UiWireCodec.Write(writer, node, epoch, envelope);
            return (writer.Segment.AsSpan().ToArray(), timed);
        }

        private static UiNode Read(byte[] bytes, bool enhanced = true, string epoch = Epoch)
        {
            var reader = new WorldWireReader();
            reader.Reset(bytes);
            static void Reply(string id, string value) => throw new InvalidOperationException("Status display cannot reply.");
            UiNode? node;
            if (enhanced)
            {
                node = UiWireCodec.ReadWithEnvelope(reader, Reply, epoch, out var admitted);
                Check(admitted is { OwnerPackageId: Owner, Generation: 1 }, "Status uses the complete ownership envelope.");
            }
            else node = UiWireCodec.Read(reader, Reply, epoch);
            UiWireCodec.End(reader);
            return node ?? throw new InvalidOperationException("Expected a status wire node.");
        }

        private static void Refused(Action action, string reason)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException(reason);
        }

        private static void BudgetRefused(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("One extra payload byte must fail the targeted frame quota.");
        }

        private static void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
        }
    }
}
