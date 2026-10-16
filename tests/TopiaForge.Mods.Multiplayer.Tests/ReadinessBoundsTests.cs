extern alias provider;

using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using JoinReadiness = provider::TopiaForge.Multiplayer.Network.JoinReadiness;
using LiveJoinTimings = provider::TopiaForge.Multiplayer.Network.LiveJoinTimings;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// How long a joining player waits to be Ready, and how long its host waits for it, over real providers on the virtual
/// network: a replacement before the player is Ready gets its own snapshot deadline, repeated replacements cannot keep it
/// waiting, and the host waits out an honest player's whole allowance.
/// </summary>
internal static class ReadinessBoundsTests
{
    private static readonly PackageSpec Sandbox = PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a');

    internal static void AReplacementMidJoinGetsItsOwnSnapshotDeadline()
    {
        // Thirty seconds for each snapshot, ten without progress, two minutes of holds: production allows two, one and five.
        using var rig = new ControlRig(151, Sandbox) { Timings = new LiveJoinTimings(30_000_000, 10_000_000, 120_000_000) };
        RegisterWorld(rig.Host, 1, 400 * 1024);
        rig.StartHosting(new InternalHostSettings("Large world", 4));
        var guest = rig.AddPeer("guest", Sandbox);
        var hold = guest.Control.HoldReadiness("loading the host's world");
        var joinedAt = rig.Network.Now;
        That(guest.Control.Join(guest.Target(), new InternalJoinSettings()).Succeeded, "the guest joins while holding");
        That(rig.Run(() => guest.Provider.Engine.IsAwaitingLocalReadiness, 30_000_000, 20_000), "the snapshot arrives, and the guest waits for its runtime");
        That(rig.Network.Now - joinedAt > 5_000_000, "the snapshot took seconds on the link (" + (rig.Network.Now - joinedAt) + " us)");

        // The runtime reports progress until the snapshot's own deadline is long past, and the host then replaces the session.
        Spend(rig, joinedAt + 46_000_000, hold.ReportProgress);
        That(guest.Control.GetStatus().JoinPhase == InternalJoinPhase.Synchronizing && guest.Provider.Engine.IsAwaitingLocalReadiness,
            "46 seconds after joining the guest is still held, past the snapshot's deadline");
        That(rig.Host.Control.ReplaceSession().Succeeded, "the host replaces the session before the guest is Ready");

        // The new snapshot takes as long as the first; its deadline starts with the replacement, not with the join.
        That(rig.Run(() =>
            {
                hold.ReportProgress();
                return guest.Control.GetStatus().JoinPhase != InternalJoinPhase.Synchronizing ||
                       (guest.Provider.Engine.SessionReplacementsReceived == 1 && guest.Provider.Engine.IsAwaitingLocalReadiness);
            }, 30_000_000, 20_000) && guest.Control.GetStatus().JoinPhase == InternalJoinPhase.Synchronizing,
            "the guest receives the new snapshot and waits again (" + guest.Control.GetStatus().JoinPhase + ": " + guest.Control.GetStatus().JoinDetail + ")");
        hold.Dispose();
        That(rig.Run(() => guest.Control.GetStatus().JoinPhase == InternalJoinPhase.InSession), "and releasing the hold completes the join");
        That(guest.Provider.Engine.ReadinessHeldSinceMicros == null, "once Ready, the holds' total starts again with the next hold");
    }

    internal static void RepeatedReplacementsCannotKeepAPlayerWaiting()
    {
        // Ten seconds for each snapshot, two without progress, six of holds in all.
        using var rig = new ControlRig(152, Sandbox) { Timings = new LiveJoinTimings(10_000_000, 2_000_000, 6_000_000) };
        rig.StartHosting(new InternalHostSettings("Restless", 4));
        var guest = rig.AddPeer("guest", Sandbox);
        var stuck = guest.Control.HoldReadiness("loading a world that keeps changing");
        That(guest.Control.Join(guest.Target(), new InternalJoinSettings()).Succeeded, "the guest joins while holding");
        That(rig.Run(() => guest.Provider.Engine.IsAwaitingLocalReadiness), "and waits for its runtime");

        // The host replaces the session every second and a half, so a new snapshot keeps arriving: the holds' total still
        // counts from the first hold, and the join ends there.
        var heldAt = rig.Network.Now;
        var nextReplacement = heldAt + 1_500_000;
        That(rig.Run(() =>
            {
                if (rig.Network.Now >= nextReplacement)
                {
                    rig.Host.Control.ReplaceSession();
                    nextReplacement += 1_500_000;
                }

                return guest.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended;
            }, 60_000_000, 20_000), "a host that keeps replacing the session cannot keep the guest waiting");
        var elapsed = rig.Network.Now - heldAt;
        var status = guest.Control.GetStatus();
        That(guest.Provider.Engine.SessionReplacementsReceived >= 3 && status.JoinFailure == InternalLiveFailure.TimedOut &&
             status.JoinDetail == "The session did not finish loading in time." && elapsed >= 5_900_000 && elapsed < 8_000_000,
            "it times out at the holds' total from the first hold, after " + guest.Provider.Engine.SessionReplacementsReceived + " replacements (" + elapsed + " us)");
        stuck.Dispose();
    }

    internal static void HostsWaitOutAPlayersWholeAllowance()
    {
        // The rule, stated once: the host waits for a player's whole allowance, a snapshot's wait and the holds', and more.
        That(new EngineOptions().ReadyTimeoutMicros >= LiveJoinTimings.Default.AllowanceMicros + 30_000_000 &&
             LiveJoinTimings.Default.AllowanceMicros == InternalReadinessLimits.AllowanceSeconds * 1_000_000L,
            "the host's bound covers the player's whole allowance, and a margin");

        // Production timings: a 1.6 MB snapshot on the link's 32 KiB a second, and a hold that reports progress throughout.
        using var rig = new ControlRig(153, Sandbox);
        RegisterWorld(rig.Host, 2, 800 * 1024);
        rig.StartHosting(new InternalHostSettings("Big world", 4));
        var guest = rig.AddPeer("guest", Sandbox);
        guest.Runtime.PreferredDisplayName = "Sam";
        var hold = guest.Control.HoldReadiness("loading a big world");
        var joinedAt = rig.Network.Now;
        That(guest.Control.Join(guest.Target(), new InternalJoinSettings()).Succeeded, "the guest joins while holding");
        That(rig.Run(() =>
            {
                hold.ReportProgress();
                return guest.Provider.Engine.IsAwaitingLocalReadiness;
            }, 120_000_000, 50_000), "the snapshot arrives");
        var heldAt = rig.Network.Now;
        That(heldAt - joinedAt > 35_000_000, "it took over half a minute (" + (heldAt - joinedAt) / 1_000_000 + " s)");

        // The holds run on past what the host waited before, still within the player's own limit from the snapshot.
        Spend(rig, joinedAt + 335_000_000, hold.ReportProgress, 50_000);
        That(rig.Network.Now - heldAt < LiveJoinTimings.Default.HoldLimitMicros && guest.Control.GetStatus().JoinPhase == InternalJoinPhase.Synchronizing,
            "the player is still within its own limit, and still joining (" + guest.Control.GetStatus().JoinPhase + ": " + guest.Control.GetStatus().JoinDetail + ")");
        That(rig.Host.Session.Snapshot.Participants.Any(participant => participant.DisplayName == "Sam" && participant.IsConnected), "and the host still waits for it");
        hold.Dispose();
        That(rig.Run(() => guest.Control.GetStatus().JoinPhase == InternalJoinPhase.InSession && rig.Host.Control.IsParticipantReady(rig.Host.IdOf("Sam"))),
            "the slow join completes on both sides");
    }

    internal static void TheJoinClockKeepsEveryBound()
    {
        var timings = new LiveJoinTimings(10_000_000, 2_000_000, 6_000_000);

        // Nothing arrives: the join ends at the snapshot's deadline.
        That(EndsAt(timings, (now, clock) => clock.Check(now, true, false, null, 0, 0)) == (10_000_000, JoinReadiness.SendingTooLong),
            "a snapshot that never comes ends the join at its deadline");

        // A host that sends replacements without their snapshots, so the player is never held: each starts the snapshot's
        // deadline again, but not the whole allowance.
        That(EndsAt(timings, (now, clock) => clock.Check(now, true, false, null, 0, now / 5_000_000)) == (16_000_000, JoinReadiness.SendingTooLong),
            "a host that only ever replaces the session ends the join at the whole allowance");

        // Held with progress all along: the holds' total from the first hold ends it, whatever else happens.
        That(EndsAt(timings, (now, clock) => clock.Check(now, true, now >= 1_000_000, now >= 1_000_000 ? 1_000_000 : null, now, 0)) ==
             (7_000_000, JoinReadiness.LoadingTooLong), "holds that keep reporting progress still end at their total");

        // Held without progress: the idle bound ends it.
        That(EndsAt(timings, (now, clock) => clock.Check(now, true, now >= 1_000_000, now >= 1_000_000 ? 1_000_000 : null, 1_000_000, 0)) ==
             (3_000_000, JoinReadiness.LoadingTooLong), "holds without progress end at the idle bound");

        // Once Ready, a replacement starts a new wait with a new allowance.
        var ready = new JoinReadiness(timings);
        ready.LinkUp(0, 0);
        ready.Ready();
        That(ready.Check(50_000_000, false, false, null, 0, 0) == null && ready.Check(60_000_000, true, false, null, 0, 1) == null &&
             ready.Check(69_900_000, true, false, null, 0, 1) == null && ready.Check(70_000_000, true, false, null, 0, 1) == JoinReadiness.SendingTooLong,
            "a replacement after the player was Ready is waited for as a join is");
    }

    /// <summary>Steps a join's clock from link-up a tenth of a second at a time, and returns when and why it ends.</summary>
    private static (long, string?) EndsAt(LiveJoinTimings timings, Func<long, JoinReadiness, string?> check)
    {
        var clock = new JoinReadiness(timings);
        clock.LinkUp(0, 0);
        for (long now = 100_000; now <= 60_000_000; now += 100_000)
        {
            var ended = check(now, clock);
            if (ended != null) return (now, ended);
        }

        return (-1, null);
    }

    /// <summary>Registers a world of <paramref name="parts"/> states of <paramref name="bytes"/> each, which every snapshot carries.</summary>
    private static void RegisterWorld(ControlPeer host, int parts, int bytes)
    {
        for (var part = 0; part < parts; part++)
        {
            var registered = host.Session.RegisterState(new ReplicatedStateDefinition<BlobValue>("world.part-" + part, new BlobValue(new byte[bytes]), new BlobCodec()));
            if (!registered.Succeeded) throw new InvalidOperationException("Assertion failed: the world registers (" + registered.ErrorMessage + ")");
        }
    }

    /// <summary>Runs the rig until <paramref name="untilMicros"/>, doing <paramref name="each"/> every frame.</summary>
    private static void Spend(ControlRig rig, long untilMicros, Action each, long frameMicros = 20_000) =>
        rig.Run(
            () =>
            {
                each();
                return rig.Network.Now >= untilMicros;
            },
            untilMicros - rig.Network.Now + frameMicros,
            frameMicros);

    private sealed class BlobValue
    {
        internal BlobValue(byte[] bytes)
        {
            Bytes = bytes;
        }

        internal byte[] Bytes { get; }
    }

    private sealed class BlobCodec : IMultiplayerCodec<BlobValue>
    {
        public int MaximumEncodedBytes => 1024 * 1024;

        public OperationResult<byte[]> Encode(BlobValue value) => OperationResult<byte[]>.Success((byte[])value.Bytes.Clone());

        public OperationResult<BlobValue> Decode(byte[] bytes) => OperationResult<BlobValue>.Success(new BlobValue(bytes));
    }
}
