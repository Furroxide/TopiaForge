using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class UiSessionClockTests
{
    internal static void Run()
    {
        InitialTimelineAdoption();
        ProviderAdoptionAndSameWorldHosting();
        DelayedJoinLatencyAndReplacement();
        OffsetAndNoBackwardsJump();
        Console.WriteLine("UI session clock: authenticated skew/RTT estimates, late join, monotonic samples and replacement epochs passed.");
    }

    private static void InitialTimelineAdoption()
    {
        var engine = SessionEngine.CreateHost(new EngineOptions(), MultiplayerProcessKind.Interactive,
            new MultiplayerSessionId("initial-clock"), new SessionSeed(1), new ParticipantId("host"), "Host", 100_000_000);
        try
        {
            var epoch = engine.UiClockEpoch;
            That(engine.TryAdoptUiClock(12.5, 105_000_000), "the initial host adopts runtime elapsed time once");
            engine.Pump(105_000_000);
            That(engine.UiClockTime == 12.5 && engine.UiClockEpoch == epoch,
                "delayed first pump does not count pre-attachment provider age twice");
            engine.Pump(106_000_000);
            That(engine.UiClockTime == 13.5, "adopted time advances from the actual attachment timestamp");
            That(!engine.TryAdoptUiClock(0, 106_000_000) && engine.UiClockTime == 13.5,
                "a second adoption cannot re-anchor an existing timeline");
            engine.BeginUiWorld();
            That(engine.UiClockTime == 0 && engine.UiClockEpoch != epoch &&
                !engine.TryAdoptUiClock(13.5, 106_000_000), "new worlds cannot adopt the initial timeline again");
        }
        finally { engine.Shutdown(MultiplayerSessionEndReason.HostEnded); }
    }

    private static void ProviderAdoptionAndSameWorldHosting()
    {
        using var rig = new ControlRig(381);
        rig.Network.RunFor(5_000_000);
        var clock = (IInternalUiClock)rig.Host.Control;
        var epoch = clock.UiClockEpoch;
        var adoptedAt = rig.Network.Now;
        That(clock.TryAdoptUiClock(12.5), "the real solo provider adopts the runtime timeline");
        rig.Host.Frame();
        That(clock.UiClockTime == 12.5, "the provider uses its current monotonic reading at delayed attachment");
        rig.StartHosting(new InternalHostSettings("Clock", 4));
        That(clock.UiClockEpoch == epoch &&
            Math.Abs(clock.UiClockTime - (12.5 + (rig.Network.Now - adoptedAt) / 1_000_000d)) < .000001,
            "the real provider opens the same world without replacing or re-anchoring its timeline");
        var guest = rig.AddPeer("clock-guest");
        rig.JoinAndWait(guest);
        var guestClock = (IInternalUiClock)guest.Control;
        That(guestClock.UiClockEpoch == epoch && Math.Abs(guestClock.UiClockTime - clock.UiClockTime) < .12,
            "the joined provider receives the adopted host timeline through the existing authenticated wire");
        That(!clock.TryAdoptUiClock(0) && !guestClock.TryAdoptUiClock(0),
            "online host and client refuse initial-clock adoption");
    }

    private static void DelayedJoinLatencyAndReplacement()
    {
        using var rig = new EngineHarness(latencyTicks: 3, configure: options => options.ClockSyncIntervalMicros = 100_000);
        rig.Advance(600);
        var oldEpoch = rig.Host.UiClockEpoch; rig.Host.BeginUiWorld();
        That(rig.Host.UiClockEpoch != oldEpoch && rig.Host.UiClockTime == 0, "a new solo world resets its timeline and epoch in the same engine");
        rig.Advance(600);
        var client = rig.AddClient("late"); rig.Advance(180);
        That(client.Engine.Snapshot.State == MultiplayerSessionState.Ready, "late client reaches Ready");
        That(rig.Host.UiClockTime > 12 && Math.Abs(rig.Host.UiClockTime - client.Engine.UiClockTime) < .12,
            "late client estimates host elapsed time, not its own start time, through a delayed link");
        That(rig.Host.UiClockEpoch == client.Engine.UiClockEpoch, "authenticated session identity and epoch agree");
        var before = client.Engine.UiClockTime; var epoch = client.Engine.UiClockEpoch;
        rig.Advance(120);
        That(client.Engine.UiClockTime >= before + 1.9, "UI time advances independently of gameplay scale or fixed-physics callbacks");
        rig.Host.ReplaceSession(new MultiplayerSessionId("next-world"), new SessionSeed(73)); rig.Advance(120);
        That(client.Engine.UiClockEpoch == rig.Host.UiClockEpoch && client.Engine.UiClockEpoch != epoch, "replacement retires the old epoch");
        That(Math.Abs(rig.Host.UiClockTime - client.Engine.UiClockTime) < .12, "replacement keeps the current host timeline");
    }

    private static void OffsetAndNoBackwardsJump()
    {
        var peer = new RawHostClient(); peer.Join();
        peer.Host.Send((frame, body) => SessionMessages.WriteClockSync(frame, body, 8_000_000_000, 600, 200_000, 10_000_000));
        peer.Pump();
        That(Math.Abs(peer.Engine.UiClockTime - 10.1) < .001, "host elapsed sample and half RTT remove a very different local clock origin");
        var before = peer.Engine.UiClockTime;
        peer.Host.Send((frame, body) => SessionMessages.WriteClockSync(frame, body, 8_000_010_000, 601, 20_000, 9_000_000));
        peer.Pump();
        That(peer.Engine.UiClockTime >= before, "a lower delayed sample never makes a displayed timer gain time");
        peer.Engine.Shutdown(MultiplayerSessionEndReason.HostEnded);
    }

    private static void That(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
