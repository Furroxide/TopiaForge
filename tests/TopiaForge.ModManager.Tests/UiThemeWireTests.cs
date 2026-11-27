using System;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiThemeWireTests
    {
        internal static void Run()
        {
            LegacyTextKeepsItsBytes();
            EnhancedTreeRoundTripsWithTheEnvelope();
            FramedMeasureMatchesTheRealWriter();
            FailedMeasureLeavesTheClockUnbound();
            Console.WriteLine("UI theme wire: legacy bytes, enhanced fields, exact frame measure and clock admission passed.");
        }

        private static void LegacyTextKeepsItsBytes()
        {
            var writer = new WorldWireWriter();
            var text = new UiText("x", default);
            UiWireCodec.Write(writer, text);
            Check(writer.Segment.ToArray().SequenceEqual(new byte[] { 1, 1, (byte)'x', 0, 0 }),
                "the legacy text tag and its default role/tone must stay byte-identical");
            Check(UiWireCodec.Read(Reader(writer), Ignore) is UiText { Text: "x", Id: null }, "read legacy text");
            var invalid = new WorldWireReader();
            invalid.Reset(new byte[] { 1, 1, (byte)'x', 0, (byte)UiTone.Muted });
            Throws<ArgumentException>(() => UiWireCodec.Read(invalid, Ignore));
        }

        private static void EnhancedTreeRoundTripsWithTheEnvelope()
        {
            const string Epoch = "current";
            var clock = new UiCountdown(42, "Signal", UiTone.Primary, "timer");
            var tree = new UiColumn(new UiText("Signal", UiTextStyle.Numeral, UiTone.Muted, "heading"),
                new UiProgress(0.5f, "Charge", UiTone.Accent, "charge"), clock);
            var envelope = Envelope();
            var writer = new WorldWireWriter();
            Check(UiWireCodec.NeedsEnvelope(tree), "an enhanced leaf requires a root envelope");
            Throws<ArgumentException>(() => UiWireCodec.Write(writer, tree, Epoch));
            Check(clock.ClockEpoch == null, "a rejected initial frame must not bind its countdown");
            Check(UiWireCodec.Write(writer, tree, Epoch, envelope), "the framed composition carries its countdown");
            Check(writer.Segment.Array![0] == 14, "the envelope tag sits at the root");
            Throws<ArgumentException>(() => UiWireCodec.Read(Reader(writer), Ignore, Epoch));

            var reader = Reader(writer);
            var result = (UiColumn)UiWireCodec.ReadWithEnvelope(reader, Ignore, Epoch, out var received)!;
            UiWireCodec.End(reader);
            Check(received != null && received.OwnerPackageId == envelope.OwnerPackageId &&
                received.Generation == 129 && received.ThemeId == "survival" &&
                received.PackageDigest.Span.SequenceEqual(envelope.PackageDigest.Span) &&
                received.DescriptorDigest.Span.SequenceEqual(envelope.DescriptorDigest.Span), "all envelope fields round-trip");
            Check(result.Children[0] is UiText { Id: "heading", Text: "Signal", Style: UiTextStyle.Numeral, Tone: UiTone.Muted }, "enhanced text");
            Check(result.Children[1] is UiProgress { Id: "charge", Label: "Charge", Fraction: 0.5f, Tone: UiTone.Accent }, "enhanced progress");
            Check(result.Children[2] is UiCountdown { Id: "timer", Label: "Signal", EndsAt: 42, Tone: UiTone.Primary, ClockEpoch: Epoch }, "enhanced countdown");

            writer.Reset();
            Check(!UiWireCodec.WriteRetained(writer, tree, "next", envelope), "expired retained countdown carries no stale epoch");
            result = (UiColumn)UiWireCodec.ReadWithEnvelope(Reader(writer), Ignore, "next", out _)!;
            Check(result.Children[2] is UiText { Id: "timer", Text: "Signal 0", Tone: UiTone.Primary }, "expired timer keeps its readable tone");
        }

        private static void FramedMeasureMatchesTheRealWriter()
        {
            const string Owner = "example.station";
            const string Recipient = "guest";
            const ulong Token = 128;
            const ulong Revision = 16384;
            var tree = new UiText("Ready", UiTextStyle.Label, UiTone.Accent, "state");
            var request = new UiSurfaceRequest("station", "Station", "Current state", content: tree);
            var envelope = Envelope();
            var writer = new WorldWireWriter(32 * 1024);
            writer.Begin(10);
            writer.WriteString(Owner);
            writer.WriteString(Recipient);
            writer.WriteVarUInt(Token);
            writer.WriteVarUInt(Revision);
            UiWireCodec.WriteTargetedSurfaceBody(writer, request, request.Body, false, tree, "", false, envelope);
            var measure = UiWireCodec.MeasureTargetedSurface(Owner, Recipient, Token, Revision, request,
                request.Body, false, tree, "", envelope: envelope);
            Check(measure == writer.Length && measure > UiWireCodec.Measure(tree, "", 32768, envelope: envelope),
                "frame measure includes envelope, all text, token and revision");
            var reader = Reader(writer);
            Check(reader.TryReadHeader(out var kind) && kind == 10, "targeted message header");
            Check(UiWireCodec.Text(reader) == Owner && UiWireCodec.Text(reader) == Recipient &&
                UiWireCodec.Id(reader) == Token && UiWireCodec.Id(reader) == Revision, "targeted route fields");
            Check(UiWireCodec.Text(reader) == request.Id && UiWireCodec.Text(reader) == request.Title &&
                UiWireCodec.Text(reader) == request.Body, "targeted surface id, title and body");
        }

        private static void FailedMeasureLeavesTheClockUnbound()
        {
            var countdown = new UiCountdown(20, tone: UiTone.Accent);
            var text = new string('\u4e00', 4096);
            var tree = new UiColumn(new UiText(text), new UiText(text), new UiText(text), countdown);
            var request = new UiSurfaceRequest("large", "Large", "", content: tree);
            Throws<InvalidOperationException>(() => UiWireCodec.MeasureTargetedSurface(
                "example.station", "guest", 1, 1, request, "", true, tree, "rejected", envelope: Envelope()));
            Check(countdown.ClockEpoch == null, "a rejected byte budget must not stamp an epoch");
        }

        private static UiWireEnvelope Envelope()
        {
            var package = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
            var descriptor = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();
            return new UiWireEnvelope("example.station", package, 129, "survival", descriptor);
        }

        private static WorldWireReader Reader(WorldWireWriter writer)
        {
            var reader = new WorldWireReader();
            reader.Reset(writer.Segment.ToArray());
            return reader;
        }

        private static void Ignore(string _, string __) { }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Throws<T>(Action run) where T : Exception
        {
            try { run(); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }
    }
}
