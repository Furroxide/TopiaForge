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
            RunCore();
            PlayersRemoteContractTests.CountdownWidgets();
            PresentationRuntimeTests.CountdownWidgets();
            Console.WriteLine("Countdown wire: shared codec, malformed/epoch refusal, delayed/paused scopes, targeted UI, shared HUD and late join passed.");
        }

        internal static void RunCore()
        {
            RoundTripAndEpoch();
            MalformedAndWholeComposition();
            DelayedFakeScopesAndPause();
            FakeRemoteValidation();
            InitialEpochAdoptionBoundaries();
            LoopbackReplacement();
            Console.WriteLine("Countdown core: wire, malformed compositions, shared fake clocks and replacement passed.");
        }

        private static void InitialEpochAdoptionBoundaries()
        {
            var initial = "initial:" + Guid.NewGuid().ToString("N");
            var retired = "retired:" + Guid.NewGuid().ToString("N");
            var provider = "provider:" + Guid.NewGuid().ToString("N");
            var currentTimer = new UiCountdown(30);
            var retiredTimer = new UiCountdown(30);
            UiClockBinding.Bind(currentTimer, initial);
            UiClockBinding.Bind(retiredTimer, retired);
            UiClockBinding.AdoptInitialEpoch(initial, provider);
            That(UiClockBinding.IsCurrent(currentTimer, provider) && currentTimer.EndsAt == 30,
                "initial adoption preserves the exact already-bound deadline");
            That(!UiClockBinding.IsCurrent(retiredTimer, provider), "initial adoption does not revive a previous world");
            Refused(() => UiClockBinding.Bind(retiredTimer, provider), "retired epoch cannot be adopted");
            Refused(() => UiClockBinding.Bind(currentTimer, "replacement"), "adopted countdown still expires on replacement");
        }

        private static void LoopbackReplacement()
        {
            var network = new TopiaForge.ModManager.TestDoubles.LoopbackNetPair();
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            ((IUiClockReceiver)ui).FollowClock(() => network.Host.UiClockTime, () => network.Host.UiClockEpoch);
            for (var i = 0; i < 120; i++) network.Host.Pump();
            var countdown = new UiCountdown(ui.ClockTime + 30, id: "timer");
            var created = ui.CreateSurface(new UiSurfaceRequest("replacement", "Timer", "", content: countdown));
            That(created.Succeeded, "loopback replacement starts with a bound timer");
            var epoch = network.Host.UiClockEpoch;
            var elapsed = ui.ClockTime;
            network.ReplaceSession();
            That(network.Host.UiClockEpoch != epoch && ui.ClockTime == elapsed,
                "loopback session replacement changes identity while preserving elapsed time");
            That(created.Value!.SetContent(countdown).ErrorCode == ModErrorCode.InvalidArgument,
                "loopback session replacement refuses the previous countdown");
            That(((FakeUiSurface)created.Value).TryGetCountdownSeconds("timer", out var remaining) && remaining == 0,
                "an already displayed loopback timer expires at replacement");
            That(created.Value.SetContent(new UiCountdown(ui.ClockTime + 5, id: "timer")).Succeeded,
                "loopback replacement accepts a fresh deadline");
            epoch = network.Host.UiClockEpoch;
            network.Host.BeginUiWorld();
            That(network.Host.UiClockEpoch != epoch && ui.ClockTime == 0,
                "a new world separately resets the loopback timeline origin");
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
