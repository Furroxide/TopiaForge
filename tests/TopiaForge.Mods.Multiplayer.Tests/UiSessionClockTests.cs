using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class UiSessionClockTests
{
    internal static void Run()
    {
        DelayedJoinLatencyAndReplacement();
        OffsetAndNoBackwardsJump();
        Console.WriteLine("UI session clock: authenticated skew/RTT estimates, late join, monotonic samples and replacement epochs passed.");
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
