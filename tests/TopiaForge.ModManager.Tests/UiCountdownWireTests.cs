using System;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class UiCountdownWireTests
    {
        internal static void Run()
        {
            RoundTripAndEpoch();
            MalformedAndWholeComposition();
            DelayedFakeScopesAndPause();
            FakeRemoteValidation();
            PlayersRemoteContractTests.CountdownWidgets();
            PresentationRuntimeTests.CountdownWidgets();
            Console.WriteLine("Countdown wire: shared codec, malformed/epoch refusal, delayed/paused scopes, targeted UI, shared HUD and late join passed.");
        }

        private static void RoundTripAndEpoch()
        {
            var timer = new UiCountdown(1234.567890123, "Remaining", UiTone.Warning, "timer");
            var source = new UiColumn(new UiProgress(0.375f, "Tasks", UiTone.Success, "progress"), timer);
            var writer = new WorldWireWriter(32768);
            UiNodeWireCodec.Write(writer, source, "session:7");
            var bytes = Copy(writer);
            That(UiWireCodec.Measure(source, "session:7", 32768) == bytes.Length, "testing validation executes the same encoder and byte budget");
            var reader = Reader(bytes);
            var decoded = (UiColumn)UiNodeWireCodec.Read(reader, (_, _) => throw new InvalidOperationException("presentation has no callback"), "session:7")!;
            UiNodeWireCodec.End(reader);
            var progress = (UiProgress)decoded.Children[0]; var countdown = (UiCountdown)decoded.Children[1];
            That(progress.Fraction == 0.375f && progress.Label == "Tasks" && progress.Tone == UiTone.Success && progress.Id == "progress", "progress preserves every field");
            That(countdown.EndsAt == timer.EndsAt && countdown.Label == timer.Label && countdown.Tone == timer.Tone && countdown.Id == "timer", "deadline remains the exact host double");
            writer.Reset(); UiNodeWireCodec.Write(writer, decoded, "session:7");
            That(bytes.SequenceEqual(Copy(writer)), "resends and baselines never re-anchor an original deadline");
            Refused(() => UiNodeWireCodec.Read(Reader(bytes), (_, _) => { }, "session:8"), "stale wire epoch");
            writer.Reset(); Refused(() => UiNodeWireCodec.Write(writer, source, "session:8"), "reusing an authored node after replacement");
            That(writer.Length == 0, "epoch refusal precedes any encoded composition");
        }

        private static void MalformedAndWholeComposition()
        {
            foreach (var value in new[] { double.NaN, double.PositiveInfinity, -1d })
            {
                var writer = new WorldWireWriter(); writer.WriteByte(13); writer.WriteByte(1); writer.WriteString("timer");
                writer.WriteUInt64((ulong)BitConverter.DoubleToInt64Bits(value)); writer.WriteString("epoch"); writer.WriteString("Remaining"); writer.WriteByte(0);
                Refused(() => UiNodeWireCodec.Read(Reader(Copy(writer)), (_, _) => { }, "epoch"), "malformed deadline");
            }
            foreach (var bad in new[] { new byte[] { 12, 2 }, new byte[] { 13, 99 }, new byte[] { 255 } })
                Refused(() => UiNodeWireCodec.Read(Reader(bad), (_, _) => { }, "epoch"), "unknown widget/version");
            var output = new WorldWireWriter();
            Refused(() => UiNodeWireCodec.Write(output, new UiColumn(new UiProgress(1), new UnsupportedNode()), "epoch"), "nested unsupported node");
            That(output.Length == 0, "the complete shape is checked before serializing its first child");
            var large = new UiColumn(Enumerable.Range(0, 10).Select(_ => (UiNode)new UiText(new string('x', 4096))).ToArray());
            var limited = false;
            try { UiWireCodec.Measure(large, "epoch", 32768); } catch (InvalidOperationException) { limited = true; }
            That(limited, "valid but oversized composition exhausts the real byte quota");
        }

        private static void DelayedFakeScopesAndPause()
        {
            using var lifetime = new FakeModLifetime();
            var time = new DeterministicGameTime();
            var first = new FakeUiService(lifetime, time);
            time.AdvanceFrame(12);
            var second = new FakeUiService(lifetime, time);
            That(first.ClockTime == 12 && second.ClockTime == first.ClockTime, "a delayed scope never introduces a new origin");
            var timer = new UiCountdown(first.ClockTime + 10, id: "timer");
            var result = second.CreateSurface(new UiSurfaceRequest("timer", "Timer", "", content: timer));
            That(result.Succeeded, "a deadline crosses scopes on the same timeline");
            var surface = (FakeUiSurface)result.Value!;
            time.AdvanceFrame(0, 3);
            That(first.ClockTime == 15 && surface.TryGetCountdownSeconds("timer", out var seconds) && seconds == 7, "paused gameplay does not pause the clock");
            var fresh = new FakeUiService(lifetime, new DeterministicGameTime());
            var stale = fresh.CreateSurface(new UiSurfaceRequest("stale", "Timer", "", content: timer));
            That(stale.ErrorCode == ModErrorCode.InvalidArgument && fresh.Surfaces.Count == 0, "new sessions refuse old nodes before creating a surface");
        }

        private static void FakeRemoteValidation()
        {
            using var directory = new FakePlayerDirectory();
            var player = directory.Add(new ParticipantId("remote"), "Guest");
            var bad = player.Ui.CreateSurface(new UiSurfaceRequest("bad", "Bad", "", content: new UnsupportedNode()));
            That(bad.ErrorCode == ModErrorCode.InvalidArgument && bad.ErrorMessage.Contains(nameof(UnsupportedNode)), "the fake names unsupported shapes");
            var surface = player.Ui.CreateSurface(new UiSurfaceRequest("widgets", "Widgets", "", content: new UiColumn(new UiProgress(.5f), new UiCountdown(30))));
            That(surface.Succeeded, "the fake accepts replicated widgets");
            That(surface.Value!.SetContent(new UnsupportedNode()).ErrorCode == ModErrorCode.InvalidArgument, "the fake applies wire validation to updates");
            for (var i = 1; i < 64; i++) That(player.Ui.CreateSurface(new UiSurfaceRequest("surface-" + i, "Title", "")).Succeeded, "valid capacity fixture");
            That(player.Ui.CreateSurface(new UiSurfaceRequest("overflow", "Title", "")).ErrorCode == ModErrorCode.RateLimited, "a real surface quota is distinct");
        }

        private sealed class UnsupportedNode : UiNode { internal UnsupportedNode() : base(null) { } }
        private static WorldWireReader Reader(byte[] bytes) { var reader = new WorldWireReader(); reader.Reset(bytes); return reader; }
        private static byte[] Copy(WorldWireWriter writer) { var value = writer.Segment; var bytes = new byte[value.Count]; Array.Copy(value.Array!, value.Offset, bytes, 0, bytes.Length); return bytes; }
        private static void Refused(Action action, string reason)
        {
            try { action(); } catch (ArgumentException) { return; }
            throw new InvalidOperationException("Expected refusal: " + reason);
        }
        private static void That(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
