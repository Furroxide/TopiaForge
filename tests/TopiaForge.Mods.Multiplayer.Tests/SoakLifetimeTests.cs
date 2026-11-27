extern alias provider;

using TopiaForge.Mods;
using Provider = provider::TopiaForge.Multiplayer.MultiplayerProvider;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>Checks the seeded source through public snapshots, including replacement and reconnect retirement.</summary>
internal static class SoakLifetimeTests
{
    internal static void Verify()
    {
        using (var first = new Provider(LiveRig.ProviderId))
        using (var second = new Provider(LiveRig.ProviderId))
        {
            That(first.Snapshot.Participants[0].LifetimeId != Guid.Empty &&
                 first.Snapshot.Participants[0].LifetimeId != second.Snapshot.Participants[0].LifetimeId,
                "production defaults still issue fresh opaque lifetimes");
        }

        using var rig = new SoakRig(140);
        using (var repeat = new SoakRig(140))
        using (var otherSeed = new SoakRig(141))
        {
            var tokens = rig.Peers.Select(peer => peer.Provider.Snapshot.Participants[0].LifetimeId).ToArray();
            That(tokens.SequenceEqual(repeat.Peers.Select(peer => peer.Provider.Snapshot.Participants[0].LifetimeId)),
                "a seed reproduces each peer's initial lifetime");
            That(tokens.All(token => token != Guid.Empty) && tokens.Distinct().Count() == tokens.Length &&
                 !tokens.Intersect(otherSeed.Peers.Select(peer => peer.Provider.Snapshot.Participants[0].LifetimeId)).Any(),
                "seeded lifetimes are nonempty and separate peer and seed streams");
        }

        rig.Start();
        rig.Quiet = true;
        var host = rig.Host.Provider.Snapshot.Participants[0];
        var guest = rig.Clients[0];
        rig.Join(guest);
        Until(rig, () => guest.InSession);
        var firstGuest = rig.Host.Provider.Snapshot.Participants.Single(row => !row.IsHost);
        That(rig.Host.Provider.Snapshot.Participants[0].LifetimeId == host.LifetimeId,
            "admitting another player preserves the host lifetime");
        Shared(rig, guest);
        for (var i = 0; i < 20; i++) rig.Frame();
        That(rig.Host.Provider.Snapshot.Participants.Single(row => !row.IsHost).LifetimeId == firstGuest.LifetimeId,
            "ordinary pumping preserves an injected lifetime");

        guest.Join!.Leave();
        rig.Join(guest);
        Until(rig, () => guest.InSession);
        var rejoined = rig.Host.Provider.Snapshot.Participants.Single(row => !row.IsHost);
        That(rejoined.Id.Equals(firstGuest.Id) && rejoined.LifetimeId != firstGuest.LifetimeId,
            "reconnecting the same participant retires its injected lifetime");
        Shared(rig, guest);

        rig.Host.Engine.ReplaceSession(new MultiplayerSessionId("soak-lifetime-replacement"), new SessionSeed(142));
        var replaced = rig.Host.Provider.Snapshot.Participants;
        That(replaced.Single(row => row.IsHost).LifetimeId != host.LifetimeId &&
             replaced.Single(row => !row.IsHost).LifetimeId != rejoined.LifetimeId,
            "replacement retires every injected lifetime");
        Until(rig, () => guest.Provider.Snapshot.Id.Equals(rig.Host.Provider.Snapshot.Id) && guest.InSession);
        Shared(rig, guest);
        Console.WriteLine("Soak lifetime source: default freshness, seed/peer separation, stability, shared wire, reconnect and replacement passed.");
    }

    private static void Shared(SoakRig rig, SoakPeer guest)
    {
        var expected = rig.Host.Provider.Snapshot.Participants;
        That(expected.All(row => guest.Provider.Snapshot.Participants.Any(remote =>
                remote.Id.Equals(row.Id) && remote.LifetimeId == row.LifetimeId)),
            "the wire shares the exact host-issued lifetimes with the guest");
    }

    private static void Until(SoakRig rig, Func<bool> settled)
    {
        var deadline = rig.Network.Now + 10_000_000;
        while (!settled() && rig.Network.Now < deadline) rig.Frame();
        That(settled(), "the focused lifetime scenario settles");
    }
}
