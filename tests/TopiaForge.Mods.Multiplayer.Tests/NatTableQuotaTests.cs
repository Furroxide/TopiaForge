extern alias provider;

using System.Collections;
using System.Net;
using System.Reflection;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Stun;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// One TURN allocation's tables, white-box. Every permission and peer-path is claimed by a scope, one per remote side,
/// and stays exactly as long as some scope claims it. A scope holds at most sixteen permissions and twenty-four paths,
/// replacing its own oldest or least recently used beyond that; thirty-two scopes can each hold their whole share at once;
/// what one scope gives up never removes what another claims; ports no scope claims share a pool of sixty-four, eight per
/// address, and never bind a channel; and a channel number comes back to its own peer at once, to any other only after the
/// binding would have lapsed plus the five-minute guard. The literal numbers are the shares themselves, so a change to one
/// fails here rather than passing against its own constant.
/// </summary>
internal static class NatTableQuotaTests
{
    private const long Second = 1_000_000;

    internal static void OnePeersFloodReplacesOnlyItsOwnPermissions()
    {
        var harness = new TurnHarness(3130);
        harness.Start();
        harness.RunFor(Second);
        for (var index = 0; index < 20; index++) harness.Client.Permit(IPAddress.Parse("45.0.0." + index), "A", harness.Now);
        harness.RunFor(2 * Second);
        That(Claims(harness.Client, "A").Permissions == 16 && Permissions(harness.Client) == 16, "one peer holds exactly its share of sixteen (" + Claims(harness.Client, "A").Permissions + ")");
        That(!harness.Client.IsPermitted(IPAddress.Parse("45.0.0.3")) && harness.Client.IsPermitted(IPAddress.Parse("45.0.0.4")) && harness.Client.IsPermitted(IPAddress.Parse("45.0.0.19")),
            "it replaced its own four oldest, not its newest");

        harness.Client.Permit(IPAddress.Parse("45.9.9.9"), "B", harness.Now);
        harness.RunFor(2 * Second);
        That(harness.Client.IsPermitted(IPAddress.Parse("45.9.9.9")) && Claims(harness.Client, "A").Permissions == 16,
            "another peer's permission installs independently, and the first peer's are untouched (" + Claims(harness.Client, "A").Permissions + ")");
    }

    internal static void AClaimSharedByTwoPeersStaysWhileEitherHoldsIt()
    {
        var harness = new TurnHarness(3131);
        harness.Start();
        harness.RunFor(Second);

        // Two peers relayed from one address, as relays that share an address are: each claims its own path there.
        var shared = IPAddress.Parse("45.9.0.1");
        var first = new IPEndPoint(shared, 50001);
        var second = new IPEndPoint(shared, 50002);
        harness.Client.PathTo(first, "A", harness.Now);
        var secondPath = harness.Client.PathTo(second, "B", harness.Now);
        harness.RunFor(Second);
        harness.SendData(secondPath, new byte[] { 0xE8, 1 });
        harness.RunFor(Second);
        That(harness.Client.UsesChannel(second, harness.Now), "B's path through the shared address is bound");

        // A claims B's very path as well, as a hostile peer naming another's relayed address and port would.
        harness.Client.PathTo(second, "A", harness.Now);
        That(Claims(harness.Client, "A").Paths == 2 && Claims(harness.Client, "B").Paths == 1, "both claim the one path");

        // A claims past its share, so the claim it gives up is its oldest, the shared address; B still claims it.
        for (var index = 0; index < 16; index++) harness.Client.Permit(IPAddress.Parse("45.9.1." + (index + 1)), "A", harness.Now);
        harness.RunFor(Second);
        That(Claims(harness.Client, "A").Permissions == 16 && !Holds(harness.Client, "A", shared), "A gave up its oldest claim, the shared address");
        That(harness.Client.IsPermitted(shared) && harness.Client.UsesChannel(second, harness.Now), "which stays, with B's path and its channel, while B claims it");
        harness.PeerSends(second, new byte[] { 0xE8, 2 });
        That(harness.Delivered.Count(item => item.Path.Equals(secondPath) && item.Channel) == 1, "and B's data still arrives over its channel");

        // A lets go of everything: its own path goes, and the path B claims too stays, channel and all.
        harness.Client.Release("A", harness.Now);
        That(!HasPeer(harness.Client, first) && HasPeer(harness.Client, second) && harness.Client.UsesChannel(second, harness.Now) && harness.Client.Scopes == 1,
            "A's release takes only what A alone claimed");
        harness.PeerSends(second, new byte[] { 0xE8, 3 });
        That(harness.Delivered.Count(item => item.Path.Equals(secondPath) && item.Channel) == 2, "and B's data still arrives over its channel");

        // Once B lets go too, nothing claims the address, and it goes with every path on it.
        harness.Client.Release("B", harness.Now);
        That(!harness.Client.IsPermitted(shared) && Peers(harness.Client, shared) == 0 && harness.Client.ChannelCount == 0 && Permissions(harness.Client) == 0,
            "the address goes with the last claim on it");
    }

    internal static void APeerHoldsAtMostItsShareOfPaths()
    {
        var harness = new TurnHarness(3150);
        harness.Start();
        harness.RunFor(Second);
        var other = new IPEndPoint(IPAddress.Parse("45.6.6.6"), 5000);
        harness.Client.PathTo(other, "Y", harness.Now);
        var address = IPAddress.Parse("45.5.5.5");
        for (var port = 0; port < 24; port++) harness.Client.PathTo(new IPEndPoint(address, 40000 + port), "X", harness.Now);
        harness.RunFor(Second);

        // The path the peer uses stays warm; six more claims push out its six least recently used, and nothing else.
        var warm = new IPEndPoint(address, 40000);
        harness.SendData(harness.Client.PathTo(warm), new byte[] { 0xE8, 1 });
        harness.RunFor(Second);
        for (var port = 24; port < 30; port++) harness.Client.PathTo(new IPEndPoint(address, 40000 + port), "X", harness.Now);
        That(Claims(harness.Client, "X").Paths == 24 && Peers(harness.Client, address) == 24, "one peer holds exactly its share of twenty-four paths (" + Claims(harness.Client, "X").Paths + ")");
        That(HasPeer(harness.Client, warm) && Enumerable.Range(1, 6).All(port => !HasPeer(harness.Client, new IPEndPoint(address, 40000 + port))) && HasPeer(harness.Client, new IPEndPoint(address, 40007)),
            "the six it gave up were its least recently used, never the one it uses");
        That(HasPeer(harness.Client, other) && Claims(harness.Client, "Y").Paths == 1, "and another peer's path is untouched");
    }

    internal static void EveryPeerAlwaysFindsItsWholeShare()
    {
        var harness = new TurnHarness(3141);
        harness.Start();
        harness.RunFor(Second);

        // Thirty-two peers each claim their whole share at once: sixteen permissions and twenty-four paths.
        for (var scope = 0; scope < 32; scope++)
        {
            for (var index = 0; index < 16; index++) harness.Client.Permit(Address(scope, index), "S" + scope, harness.Now);
            for (var port = 0; port < 24; port++) harness.Client.PathTo(new IPEndPoint(Address(scope, port % 16), 40000 + port), "S" + scope, harness.Now);
        }

        harness.RunFor(5 * Second);
        That(Permissions(harness.Client) == 512 && Peers(harness.Client, null) == 768, "the tables hold every share (" + Permissions(harness.Client) + ", " + Peers(harness.Client, null) + ")");
        var installed = 0;
        for (var scope = 0; scope < 32; scope++)
        {
            for (var index = 0; index < 16; index++) installed += harness.Client.IsPermitted(Address(scope, index)) ? 1 : 0;
        }

        That(installed == 512, "and every one is installed: no peer's share came up short (" + installed + ")");

        // A thirty-third peer gets nothing and takes nothing; once one ends, the next has its whole share at once.
        harness.Client.Permit(IPAddress.Parse("46.0.0.1"), "T", harness.Now);
        harness.RunFor(Second);
        That(!harness.Client.IsPermitted(IPAddress.Parse("46.0.0.1")) && Permissions(harness.Client) == 512 && harness.Client.Scopes == 32, "a thirty-third peer claims nothing");
        harness.Client.Release("S0", harness.Now);
        for (var index = 0; index < 16; index++) harness.Client.Permit(IPAddress.Parse("46.0.0." + (index + 1)), "T", harness.Now);
        for (var port = 0; port < 24; port++) harness.Client.PathTo(new IPEndPoint(IPAddress.Parse("46.0.0." + ((port % 16) + 1)), 40000 + port), "T", harness.Now);
        harness.RunFor(Second);
        That(Claims(harness.Client, "T").Permissions == 16 && Claims(harness.Client, "T").Paths == 24 && Enumerable.Range(1, 16).All(index => harness.Client.IsPermitted(IPAddress.Parse("46.0.0." + index))),
            "once one peer ends, the next holds its whole share (" + Claims(harness.Client, "T").Permissions + ", " + Claims(harness.Client, "T").Paths + ")");
    }

    internal static void UnclaimedPortsArePooledAndNeverBound()
    {
        var harness = new TurnHarness(3155);
        harness.Start();
        harness.RunFor(Second);
        for (var address = 0; address < 12; address++) harness.Client.Permit(IPAddress.Parse("45.7." + address + ".1"), "S", harness.Now);
        var honest = new IPEndPoint(IPAddress.Parse("45.7.0.1"), 5000);
        var honestPath = harness.Client.PathTo(honest, "H", harness.Now);
        harness.RunFor(Second);
        harness.SendData(honestPath, new byte[] { 0xE8, 1 });
        harness.RunFor(Second);
        That(harness.Client.UsesChannel(honest, harness.Now), "a claimed path is bound");
        var binds = harness.Server.ChannelBinds;

        // The honest path's own address sprays twenty ports: eight are pooled. Eleven more addresses spray eight each.
        for (var port = 0; port < 20; port++) harness.PeerSends(new IPEndPoint(IPAddress.Parse("45.7.0.1"), 41000 + port), new byte[] { 0xE1, (byte)port });
        That(Unclaimed(harness.Client, IPAddress.Parse("45.7.0.1")) == 8, "one address holds eight pooled ports (" + Unclaimed(harness.Client, IPAddress.Parse("45.7.0.1")) + ")");
        for (var address = 1; address < 12; address++)
        {
            for (var port = 0; port < 8; port++) harness.PeerSends(new IPEndPoint(IPAddress.Parse("45.7." + address + ".1"), 41000 + port), new byte[] { 0xE1, (byte)port });
        }

        That(Unclaimed(harness.Client, null) == 64 && harness.Client.UnclaimedPaths == 64, "the pool holds sixty-four in all (" + Unclaimed(harness.Client, null) + ")");

        // Sending to a pooled port, as when answering a check from it, binds nothing either: it goes as a Send indication.
        var pooled = new IPEndPoint(IPAddress.Parse("45.7.11.1"), 41006);
        That(harness.SendData(harness.Client.PathTo(pooled), new byte[] { 0xE8, 7 }) > 0 && harness.Sent[^1][0] < 0x40, "a pooled port is sent to without a channel");
        harness.RunFor(Second);
        That(harness.Server.ChannelBinds == binds && !harness.Client.UsesChannel(pooled, harness.Now), "and no pooled port bound a channel (" + (harness.Server.ChannelBinds - binds) + ")");
        harness.PeerSends(honest, new byte[] { 0xE8, 2 });
        That(harness.Client.UsesChannel(honest, harness.Now) && harness.Delivered.Count(item => item.Path.Equals(honestPath) && item.Channel) == 1,
            "the claimed path was never pushed out, and still delivers over its channel");

        // A pooled port a peer then claims leaves the pool and binds.
        var adopted = new IPEndPoint(IPAddress.Parse("45.7.11.1"), 41007);
        harness.Client.PathTo(adopted, "S", harness.Now);
        harness.PeerSends(adopted, new byte[] { 0xE1, 3 });
        harness.RunFor(Second);
        That(harness.Client.UsesChannel(adopted, harness.Now) && harness.Client.UnclaimedPaths == 63, "a pooled port a peer claims leaves the pool and binds");

        // A spray pushes out its own kind first: the pooled port this side sent to outlasts newer ones only ever heard from.
        for (var address = 0; address < 4; address++)
        {
            for (var port = 0; port < 2; port++) harness.PeerSends(new IPEndPoint(IPAddress.Parse("45.7." + address + ".1"), 42000 + port), new byte[] { 0xE1, (byte)port });
        }

        That(harness.Client.UnclaimedPaths == 64 && HasPeer(harness.Client, pooled), "the port this side sent to stays pooled while sprayed ports make way");
    }

    internal static void AChannelNumberComesBackOnlyToItsOwnPeer()
    {
        var harness = new TurnHarness(3160);
        harness.Start();
        harness.RunFor(Second);
        var peer = new IPEndPoint(IPAddress.Parse("45.8.8.8"), 40000);
        var path = harness.Client.PathTo(peer, "A", harness.Now);
        harness.RunFor(Second);
        harness.SendData(path, new byte[] { 0xE8, 1 });
        harness.RunFor(Second);
        var number = ChannelOf(harness.Client, peer);
        That(number != 0 && harness.Client.UsesChannel(peer, harness.Now), "the peer's path is bound (" + number + ")");
        harness.Client.Release("A", harness.Now);
        That(!HasPeer(harness.Client, peer) && harness.Client.ChannelCount == 0, "and goes with its claim");

        // Claimed again while the server may still hold its binding, the same peer takes its own number back, so the
        // server's binding and this side's agree and nothing it relays on that number is lost.
        var again = harness.Client.PathTo(peer, "A again", harness.Now);
        harness.RunFor(Second);
        harness.SendData(again, new byte[] { 0xE8, 2 });
        harness.RunFor(Second);
        var bindings = harness.Server.Allocations.Single().Channels;
        That(ChannelOf(harness.Client, peer) == number && harness.Client.UsesChannel(peer, harness.Now), "the same peer takes its own number back (" + ChannelOf(harness.Client, peer) + ")");
        That(bindings.Count == 1 && bindings.ContainsKey(number), "and the server holds that one binding, refused nothing");

        // Given up again, the number is kept from every other peer until the binding would lapse, ten minutes after its
        // last bind request, and the five-minute guard after that.
        harness.Client.Release("A again", harness.Now);
        SetNextChannel(harness.Client, 0x5000);
        var other = new IPEndPoint(IPAddress.Parse("45.8.8.9"), 40000);
        var otherPath = harness.Client.PathTo(other, "B", harness.Now);
        harness.RunFor(Second);
        harness.SendData(otherPath, new byte[] { 0xE8, 3 });
        That(ChannelOf(harness.Client, other) == 0, "another peer gets no number while the guard holds");
        harness.RunFor(14 * 60 * Second);
        harness.SendData(otherPath, new byte[] { 0xE8, 4 });
        That(ChannelOf(harness.Client, other) == 0, "not even fourteen minutes later");
        harness.RunFor(2 * 60 * Second);
        harness.SendData(otherPath, new byte[] { 0xE8, 5 });
        harness.RunFor(Second);
        That(ChannelOf(harness.Client, other) == number && harness.Client.UsesChannel(other, harness.Now),
            "once ten minutes and the guard have passed, it is reused (" + ChannelOf(harness.Client, other) + ")");
    }

    internal static void AnUnansweredRefreshKeepsItsNumberGuardedLonger()
    {
        var harness = new TurnHarness(3165);
        harness.Start();
        harness.RunFor(Second);
        var peer = new IPEndPoint(IPAddress.Parse("45.8.9.8"), 40000);
        var path = harness.Client.PathTo(peer, "A", harness.Now);
        harness.RunFor(Second);
        harness.SendData(path, new byte[] { 0xE8, 1 });
        harness.RunFor(Second);
        var number = ChannelOf(harness.Client, peer);
        var boundAt = harness.Now;

        // The refresh at eight minutes reaches the server, which refreshes the binding, but every answer is lost; the peer
        // then goes before the client's own record of the last answer has lapsed.
        harness.Intercept = bytes => StunMessage.TryParse(bytes, 0, bytes.Length, out var answer) && answer.Method == StunMethod.ChannelBind;
        harness.RunFor(500 * Second);
        harness.Client.Release("A", harness.Now);
        harness.Intercept = null;
        SetNextChannel(harness.Client, 0x5000);
        var other = new IPEndPoint(IPAddress.Parse("45.8.9.9"), 40000);
        var otherPath = harness.Client.PathTo(other, "B", harness.Now);
        harness.RunFor(Second);

        // Past the last answered bind's lapse and its guard, the number is still kept, since the server took the refresh.
        harness.RunFor(boundAt + (1000 * Second) - harness.Now);
        harness.SendData(otherPath, new byte[] { 0xE8, 2 });
        harness.RunFor(Second);
        That(ChannelOf(harness.Client, other) == 0, "the number stays kept while the binding the unanswered refresh renewed may last");
        harness.RunFor(boundAt + (1400 * Second) - harness.Now);
        harness.SendData(otherPath, new byte[] { 0xE8, 3 });
        harness.RunFor(Second);
        That(ChannelOf(harness.Client, other) == number && harness.Client.UsesChannel(other, harness.Now),
            "and comes back once that binding has lapsed and the guard passed (" + ChannelOf(harness.Client, other) + ")");
    }

    // --- reflection helpers, so the tests read the allocation's own tables ---

    private static IPAddress Address(int scope, int index) => IPAddress.Parse("45." + scope + "." + index + ".1");

    internal static int Permissions(TurnClient client) => Field<IDictionary>(client, "permissions").Count;

    internal static (int Permissions, int Paths) Claims(TurnClient client, string scope)
    {
        var scopes = Field<IDictionary>(client, "scopes");
        if (!scopes.Contains(scope)) return (0, 0);
        var held = scopes[scope]!;
        return (((IList)Property(held, "Permissions")!).Count, ((IList)Property(held, "Paths")!).Count);
    }

    private static bool Holds(TurnClient client, string scope, IPAddress address)
    {
        var scopes = Field<IDictionary>(client, "scopes");
        if (!scopes.Contains(scope)) return false;
        foreach (var permission in (IList)Property(scopes[scope]!, "Permissions")!)
        {
            if (NatAddresses.SameAddress((IPAddress)Property(permission!, "Address")!, address)) return true;
        }

        return false;
    }

    private static IEnumerable<object> PeerEntries(TurnClient client) => Field<IDictionary>(client, "peersByPath").Values.Cast<object>();

    internal static int Peers(TurnClient client, IPAddress? address = null) =>
        PeerEntries(client).Count(peer => address == null || NatAddresses.SameAddress(((IPEndPoint)Property(peer, "EndPoint")!).Address, address));

    internal static int Unclaimed(TurnClient client, IPAddress? address) =>
        PeerEntries(client).Count(peer => !(bool)Property(peer, "Claimed")! && (address == null || NatAddresses.SameAddress(((IPEndPoint)Property(peer, "EndPoint")!).Address, address)));

    private static bool HasPeer(TurnClient client, IPEndPoint endpoint) => PeerEntries(client).Any(peer => NatAddresses.SameEndPoint((IPEndPoint)Property(peer, "EndPoint")!, endpoint));

    private static ushort ChannelOf(TurnClient client, IPEndPoint endpoint)
    {
        var peer = PeerEntries(client).FirstOrDefault(entry => NatAddresses.SameEndPoint((IPEndPoint)Property(entry, "EndPoint")!, endpoint));
        return peer == null ? (ushort)0 : (ushort)Property(peer, "Channel")!;
    }

    private static void SetNextChannel(TurnClient client, int value) =>
        typeof(TurnClient).GetField("nextChannel", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(client, value);

    private static T Field<T>(TurnClient client, string name) => (T)typeof(TurnClient).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;

    private static object? Property(object instance, string name) =>
        instance.GetType().GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance);
}
