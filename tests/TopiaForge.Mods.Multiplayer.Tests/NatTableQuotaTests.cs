extern alias provider;

using System.Collections;
using System.Net;
using System.Reflection;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Ice;
using provider::TopiaForge.Multiplayer.NatTraversal.Signaling;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// One remote peer cannot fill a TURN allocation's permission or peer tables and shut everyone else out. A peer that
/// floods candidate addresses holds only its own share of permissions; a peer that sprays one address from many source
/// ports holds only its own share of unsolicited peer-paths; both leave room for the next honest joiner, and a session's
/// own working path is never evicted. Forgotten entries are reclaimed once their server-side lifetime has passed, never
/// before, and a channel number is not rebound to a different peer until its binding would have lapsed plus the guard.
/// </summary>
internal static class NatTableQuotaTests
{
    private const long Second = 1_000_000;

    // --- the two simulator reproductions ---

    internal static void AFloodOfCandidatesFromOnePeerLeavesRoomForHonestJoiners()
    {
        var scenario = new NatScenario(3100);
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, true, 1);
        var first = scenario.AddPeer("joiner a", scenario.Behind("joiner a router", NatBehaviour.FullCone, "198.51.100.20"), "192.168.2.20", 27250, true, 2);
        var second = scenario.AddPeer("joiner b", scenario.Behind("joiner b router", NatBehaviour.FullCone, "198.51.100.30"), "192.168.3.30", 27250, true, 3);
        scenario.StartHost(host);
        scenario.Network.RunFor(500_000);
        var (firstLink, firstHostSide) = scenario.Connect(host, first, scenario.StartJoin(first, peerId: "a1a1a1a1a1a1a1a1", prefix: "198.51.20"));
        That(scenario.Exchange(firstLink, firstHostSide, 1), "the first honest joiner plays");

        // A hostile peer floods 256 distinct candidate addresses over sixteen offers.
        var hostile = scenario.Signaling.ConnectPeer("ffffffffffffffff", false, "45.0.0");
        scenario.Network.RunFor(Second);
        var random = new SeededTransportRandom(77);
        for (var offer = 0; offer < 16; offer++)
        {
            var candidates = new List<SignalCandidate>();
            for (var index = 0; index < 16; index++) candidates.Add(new SignalCandidate(IceCandidateKind.ServerReflexive, "45.0." + offer + "." + (index + 1), 40000 + index, 1000u));
            hostile.TrySend(SignalMessage.Offer(IceCredentials.Generate(random), candidates));
            scenario.Network.RunFor(300_000);
        }

        scenario.Network.RunFor(2 * Second);
        That(Permissions(host.Agent.Relay, "ffffffffffffffff") <= TurnClient.MaximumPermissionsPerScope,
            "the hostile peer holds no more than its share of permissions (" + Permissions(host.Agent.Relay, "ffffffffffffffff") + ")");
        That(Permissions(host.Agent.Relay, null) < TurnClient.MaximumPermissions, "and the table is not full (" + Permissions(host.Agent.Relay, null) + ")");
        That(scenario.Exchange(firstLink, firstHostSide, 2), "the first joiner still plays, its permission never evicted by the flood");

        var (secondLink, secondHostSide) = scenario.Connect(host, second, scenario.StartJoin(second, peerId: "b2b2b2b2b2b2b2b2", prefix: "198.51.30"));
        That(scenario.Exchange(secondLink, secondHostSide, 3), "and a second honest joiner still gets in");
    }

    internal static void ASprayFromManyPortsLeavesRoomForHonestJoiners()
    {
        var scenario = new NatScenario(3110);
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, true, 1);
        var joiner = scenario.AddPeer("joiner", scenario.Behind("joiner router", NatBehaviour.FullCone, "198.51.100.20"), "192.168.2.20", 27250, true, 2);
        var attackerAddress = IPAddress.Parse("45.1.1.1");
        var sender = new HostileSender();
        var attackerNode = scenario.Network.AddAttachment("attacker", sender);
        scenario.Router.Place(attackerNode, scenario.Router.Internet, new IPEndPoint(attackerAddress, 40000));
        scenario.StartHost(host);
        scenario.Network.RunFor(2 * Second);

        // A hostile peer gets its one address permitted, then sprays the host's relay from three hundred source ports.
        var hostile = scenario.Signaling.ConnectPeer("ffffffffffffffff", false, "45.1.1");
        scenario.Network.RunFor(Second);
        hostile.TrySend(SignalMessage.Offer(IceCredentials.Generate(new SeededTransportRandom(78)), new List<SignalCandidate> { new(IceCandidateKind.ServerReflexive, "45.1.1.1", 40000, 1000u) }));
        scenario.Network.RunFor(2 * Second);
        var relayed = host.Agent.Relay!.RelayedAddress!;
        for (var port = 0; port < 300; port++) sender.Outbox.Enqueue((UdpPaths.For(new IPEndPoint(attackerAddress, 41000 + port)), UdpPaths.For(relayed), new byte[] { 0xE1, 1, 2, 3, 4, 5, 6, 7 }));
        scenario.Network.RunFor(3 * Second);

        That(Peers(host.Agent.Relay, attackerAddress, false) <= TurnClient.MaximumUnsolicitedPortsPerAddress,
            "the sprayer holds no more than its share of unsolicited ports (" + Peers(host.Agent.Relay, attackerAddress, false) + ")");
        That(Peers(host.Agent.Relay, null, null) < TurnClient.MaximumPeers, "and the peer table is not full (" + Peers(host.Agent.Relay, null, null) + ")");

        var (link, hostSide) = scenario.Connect(host, joiner, scenario.StartJoin(joiner, peerId: "b2b2b2b2b2b2b2b2", prefix: "198.51.20"));
        That(scenario.Exchange(link, hostSide, 1), "an honest joiner still gets in past the spray");
    }

    internal static void ChurnAcrossCreditCyclesKeepsAdmittingJoiners()
    {
        var scenario = new NatScenario(3120, ttlSeconds: 150) { Renewable = true };
        var host = scenario.AddPeer("host", scenario.Behind("host router", NatBehaviour.FullCone, "198.51.100.10"), "192.168.1.10", 27250, true, 1);
        scenario.StartHost(host);
        scenario.Network.RunFor(500_000);

        // Forty joiners come and go over several 150-second credential cycles; the table must never fill.
        var worst = 0;
        for (var index = 0; index < 40; index++)
        {
            var home = scenario.Behind("joiner router " + index, NatBehaviour.FullCone, "198.51." + (101 + (index % 120)) + ".20");
            var joiner = scenario.AddPeer("joiner " + index, home, "192.168.9." + (10 + (index % 200)), 27250, true, 4 + index);
            var peerId = "cc" + index.ToString("x14", System.Globalization.CultureInfo.InvariantCulture);
            var (link, hostSide) = scenario.Connect(host, joiner, scenario.StartJoin(joiner, peerId: peerId, prefix: "198.51." + (101 + (index % 120))));
            That(scenario.Exchange(link, hostSide, (byte)index), "joiner " + index + " plays through the churn");
            worst = System.Math.Max(worst, Permissions(host.Agent.Relay, null));
            joiner.Agent.Stop(scenario.Network.Now);
            link.Close();
            scenario.Network.RunFor(20 * Second);
        }

        That(worst < TurnClient.MaximumPermissions, "the permission table never filled across the churn (worst " + worst + ")");
    }

    // --- per-peer quota and reclaim, white-box on one allocation ---

    internal static void OnePeersFloodReplacesOnlyItsOwnPermissions()
    {
        var harness = new TurnHarness(3130);
        harness.Start();
        harness.RunFor(Second);
        for (var index = 0; index < 20; index++) harness.Client.Permit(IPAddress.Parse("45.0.0." + index), "A", harness.Now);
        harness.RunFor(2 * Second);
        That(Permissions(harness.Client, "A") == TurnClient.MaximumPermissionsPerScope, "one peer holds exactly its cap (" + Permissions(harness.Client, "A") + ")");
        That(!harness.Client.IsPermitted(IPAddress.Parse("45.0.0.0")) && harness.Client.IsPermitted(IPAddress.Parse("45.0.0.19")),
            "it replaced its own oldest, not its newest");

        harness.Client.Permit(IPAddress.Parse("45.9.9.9"), "B", harness.Now);
        harness.RunFor(2 * Second);
        That(harness.Client.IsPermitted(IPAddress.Parse("45.9.9.9")) && Permissions(harness.Client, "A") == TurnClient.MaximumPermissionsPerScope,
            "another peer's permission installs independently, and the first peer's are untouched (" + Permissions(harness.Client, "A") + ")");
    }

    internal static void ForgottenPermissionsAreReclaimedOnlyAfterTheyExpire()
    {
        var harness = new TurnHarness(3140);
        harness.Start();
        harness.RunFor(Second);
        for (var scope = 0; scope < TurnClient.MaximumPermissions / TurnClient.MaximumPermissionsPerScope; scope++)
        {
            for (var index = 0; index < TurnClient.MaximumPermissionsPerScope; index++) harness.Client.Permit(IPAddress.Parse("45." + scope + "." + index + ".1"), "S" + scope, harness.Now);
        }

        harness.RunFor(5 * Second);
        That(Permissions(harness.Client, null) == TurnClient.MaximumPermissions, "the table is full (" + Permissions(harness.Client, null) + ")");
        for (var scope = 0; scope < TurnClient.MaximumPermissions / TurnClient.MaximumPermissionsPerScope; scope++)
        {
            for (var index = 0; index < TurnClient.MaximumPermissionsPerScope; index++) harness.Client.Forget(IPAddress.Parse("45." + scope + "." + index + ".1"));
        }

        // Forgotten but not yet expired: the slot is not reclaimed, so a fresh address cannot take it.
        harness.Client.Permit(IPAddress.Parse("46.0.0.1"), "T", harness.Now);
        harness.RunFor(Second);
        That(!harness.Client.IsPermitted(IPAddress.Parse("46.0.0.1")), "a forgotten permission is not reclaimed before its lifetime passes");

        harness.RunFor(320 * Second);
        harness.Client.Permit(IPAddress.Parse("46.0.0.2"), "T", harness.Now);
        harness.RunFor(2 * Second);
        That(harness.Client.IsPermitted(IPAddress.Parse("46.0.0.2")), "once the lifetime has passed the slot is reclaimed and a fresh address installs");
    }

    internal static void UnsolicitedPortsAreCappedButSolicitedPortsAreNot()
    {
        var harness = new TurnHarness(3150);
        harness.Start();
        harness.RunFor(Second);
        var sprayed = IPAddress.Parse("45.5.5.5");
        harness.Client.Permit(sprayed, "X", harness.Now);
        harness.RunFor(Second);
        for (var port = 0; port < 20; port++) harness.PeerSends(new IPEndPoint(sprayed, 40000 + port), new byte[] { 0xE1, (byte)port });
        harness.RunFor(Second);
        That(Peers(harness.Client, sprayed, false) <= TurnClient.MaximumUnsolicitedPortsPerAddress,
            "unsolicited ports from one address are capped (" + Peers(harness.Client, sprayed, false) + ")");

        var solicited = IPAddress.Parse("45.6.6.6");
        harness.Client.Permit(solicited, "Y", harness.Now);
        harness.RunFor(Second);
        for (var port = 0; port < 12; port++) harness.SendData(TurnRelayPath.For(1, new IPEndPoint(solicited, 50000 + port)), new byte[] { 0xE8, (byte)port });
        harness.RunFor(Second);
        That(Peers(harness.Client, solicited, true) >= 12, "ports this side sends to are not capped (" + Peers(harness.Client, solicited, true) + ")");
    }

    internal static void AChannelNumberIsNotReboundBeforeItsGuard()
    {
        var harness = new TurnHarness(3160);
        harness.Start();
        harness.RunFor(Second);
        var sprayed = IPAddress.Parse("45.7.7.7");
        harness.Client.Permit(sprayed, "X", harness.Now);
        harness.RunFor(Second);

        // Fill the address's unsolicited ports so their channels are bound, then spend every fresh channel number so a new
        // binding can only come from reuse. Passing the cap once more evicts the oldest port and retires its channel.
        for (var port = 0; port < TurnClient.MaximumUnsolicitedPortsPerAddress; port++) harness.PeerSends(new IPEndPoint(sprayed, 40000 + port), new byte[] { 0xE1, (byte)port });
        harness.RunFor(Second);
        SetNextChannel(harness.Client, 0x5000);
        var retiredChannel = ChannelOf(harness.Client, new IPEndPoint(sprayed, 40000));
        That(retiredChannel != 0, "the oldest port holds a channel before it is evicted (" + retiredChannel + ")");

        harness.PeerSends(new IPEndPoint(sprayed, 50000), new byte[] { 0xE1, 99 });
        harness.RunFor(Second);
        var freeAt = ChannelFreeAt(harness.Client, retiredChannel);
        That(freeAt > harness.Now, "the retired number is held under its guard (frees at " + freeAt + ", now " + harness.Now + ")");

        // The fresh numbers are spent and every other channel is still in use, so only the retired number can come back.
        That(!TryAcquire(harness.Client, freeAt - Second, out _), "no channel is handed out while the guard still holds");
        That(TryAcquire(harness.Client, freeAt, out var reused) && reused == retiredChannel, "once the guard has passed the retired number is reused (" + reused + ")");
    }

    // --- reflection helpers, so the tests read the allocation's own tables ---

    private static int Permissions(TurnClient? client, string? scope)
    {
        if (client == null) return -1;
        var dict = (IDictionary)typeof(TurnClient).GetField("permissions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        var count = 0;
        foreach (var value in dict.Values)
        {
            if (scope == null || string.Equals((string?)Property(value, "Scope"), scope, System.StringComparison.Ordinal)) count++;
        }

        return count;
    }

    private static int Peers(TurnClient? client, IPAddress? address, bool? solicited)
    {
        if (client == null) return -1;
        var dict = (IDictionary)typeof(TurnClient).GetField("peersByPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        var count = 0;
        foreach (var value in dict.Values)
        {
            var endpoint = (IPEndPoint)Property(value, "EndPoint")!;
            if (address != null && !NatAddresses.SameAddress(endpoint.Address, address)) continue;
            if (solicited != null && (bool)Property(value, "Solicited")! != solicited.Value) continue;
            count++;
        }

        return count;
    }

    private static ushort ChannelOf(TurnClient client, IPEndPoint peer)
    {
        var dict = (IDictionary)typeof(TurnClient).GetField("peersByPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        foreach (var value in dict.Values)
        {
            if (NatAddresses.SameEndPoint((IPEndPoint)Property(value, "EndPoint")!, peer)) return (ushort)Property(value, "Channel")!;
        }

        return 0;
    }

    private static void SetNextChannel(TurnClient client, int value) =>
        typeof(TurnClient).GetField("nextChannel", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, value);

    private static long ChannelFreeAt(TurnClient client, ushort channel)
    {
        var array = (long[])typeof(TurnClient).GetField("channelFreeAt", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        return array[channel - 0x4000];
    }

    private static bool TryAcquire(TurnClient client, long now, out ushort number)
    {
        var args = new object[] { now, (ushort)0 };
        var ok = (bool)typeof(TurnClient).GetMethod("TryAcquireChannel", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(client, args)!;
        number = (ushort)args[1];
        return ok;
    }

    private static object? Property(object instance, string name) =>
        instance.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);

    private sealed class HostileSender : IVirtualAttachment
    {
        internal Queue<(TransportPath Source, TransportPath Destination, byte[] Bytes)> Outbox { get; } = new();

        public void Receive(TransportPath source, TransportPath destination, byte[] datagram, long now)
        {
        }

        public void Poll(long now)
        {
        }

        public long NextTimeoutMicros() => long.MaxValue;

        public bool TryDequeue(out TransportPath? source, out TransportPath destination, out byte[] datagram)
        {
            if (Outbox.Count == 0)
            {
                source = null;
                destination = null!;
                datagram = null!;
                return false;
            }

            var (from, to, bytes) = Outbox.Dequeue();
            source = from;
            destination = to;
            datagram = bytes;
            return true;
        }
    }
}
