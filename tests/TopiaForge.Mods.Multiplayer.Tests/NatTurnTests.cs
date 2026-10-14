extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal.Stun;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A TURN client and the fake server with no network between them: every datagram arrives at once, and the clock jumps
/// to the next timer, so a 25-minute allocation runs in milliseconds.
/// </summary>
internal sealed class TurnHarness
{
    private readonly byte[] output = new byte[StunMessage.MaximumBytes];

    internal TurnHarness(int seed, Action<FakeTurnServer>? configure = null, string password = "turn-secret")
    {
        Server = new FakeTurnServer(IPAddress.Parse("203.0.113.1"), seed);
        Server.Users["player"] = ("turn-secret", long.MaxValue);
        configure?.Invoke(Server);
        ServerPath = UdpPaths.For(Server.EndPoint);
        Stun = new StunClient(new SeededTransportRandom(seed), (path, bytes, length) => ToServer(path, bytes, length));
        Client = new TurnClient(1, ServerPath, "player", password, Stun);
    }

    internal FakeTurnServer Server { get; }

    internal TransportPath ServerPath { get; }

    internal StunClient Stun { get; }

    internal TurnClient Client { get; }

    internal long Now { get; private set; } = 1_000_000;

    internal IPEndPoint ClientEndPoint { get; } = new(IPAddress.Parse("192.0.2.10"), 27250);

    internal IPEndPoint Relay => new(Server.Address, 50_000);

    internal List<(TransportPath Path, byte[] Data, bool Channel)> Delivered { get; } = new();

    internal List<(IPEndPoint To, byte[] Data)> AtPeers { get; } = new();

    internal List<byte[]> Sent { get; } = new();

    /// <summary>Gets or sets a hook that sees each server datagram before the client and may swallow it.</summary>
    internal Func<byte[], bool>? Intercept { get; set; }

    internal void Start() => Client.Start(Now);

    /// <summary>Wraps and sends a datagram to a relayed path; returns the wire length, 0 when it was held.</summary>
    internal int SendData(TransportPath path, byte[] data)
    {
        var length = Client.Wrap(path, data, 0, data.Length, output, Now);
        if (length > 0) ToServer(ServerPath, output, length);
        Settle();
        return length;
    }

    internal void PeerSends(IPEndPoint peer, byte[] data)
    {
        Server.Receive(peer, Relay, data, Now);
        Settle();
    }

    internal void Deliver(byte[] bytes) => ClientReceives(bytes);

    internal void RunFor(long micros)
    {
        var end = Now + micros;
        while (true)
        {
            Settle();
            Client.Poll(Now);
            Stun.Poll(Now);
            Server.Poll(Now);
            if (Client.TryWriteKeepalive(Now, output, out var length)) ToServer(ServerPath, output, length);
            Settle();
            var next = Math.Min(Math.Min(Stun.NextTimeoutMicros(), Client.NextTimeoutMicros()), Server.NextTimeoutMicros());
            if (next > end)
            {
                Now = end;
                break;
            }

            Now = Math.Max(Now + 1, next);
        }

        Client.Poll(Now);
        Stun.Poll(Now);
        Settle();
    }

    private void ToServer(TransportPath path, byte[] bytes, int length)
    {
        var copy = bytes.AsSpan(0, length).ToArray();
        Sent.Add(copy);
        UdpPaths.TryGetEndPoint(path, out var to);
        Server.Receive(ClientEndPoint, new IPEndPoint(provider::TopiaForge.Multiplayer.NatTraversal.NatAddresses.Unmap(to.Address), to.Port), copy, Now);
    }

    private void Settle()
    {
        while (Server.Outbox.Count > 0)
        {
            var (_, to, bytes) = Server.Outbox.Dequeue();
            if (to.Equals(ClientEndPoint)) ClientReceives(bytes);
            else AtPeers.Add((to, bytes));
        }
    }

    private void ClientReceives(byte[] bytes)
    {
        if (Intercept != null && Intercept(bytes)) return;
        if (bytes[0] >= 0x40 && bytes[0] <= 0x4F)
        {
            if (Client.TryUnwrap(bytes, 0, bytes.Length, out var path, out var offset, out var length)) Delivered.Add((path, bytes.AsSpan(offset, length).ToArray(), true));
            return;
        }

        if (!StunMessage.TryParse(bytes, 0, bytes.Length, out var message)) return;
        if (message.Class == StunClass.Success || message.Class == StunClass.Error) Stun.TryComplete(message, ServerPath, Now);
        else if (Client.TryReadData(message, Now, out var path, out var offset, out var length)) Delivered.Add((path, message.Bytes.AsSpan(offset, length).ToArray(), false));
    }
}

/// <summary>The TURN client against the fake server: the credential flows, failures, and lifetimes that outlast the server's.</summary>
internal static class NatTurnTests
{
    private static readonly IPEndPoint Peer = new(IPAddress.Parse("198.51.100.7"), 4000);

    internal static void AllocationsAnswerTheCredentialChallenge()
    {
        var harness = new TurnHarness(1);
        harness.Start();
        harness.RunFor(1_000_000);
        That(harness.Client.State == TurnAllocationState.Ready, "the allocation is ready (" + harness.Client.State + ", " + harness.Client.Failure + ")");
        That(harness.Server.Challenges == 1 && harness.Server.AllocateRequests == 1, "the first Allocate draws a 401 and the second, authenticated, succeeds");
        That(harness.Client.RelayedAddress!.Equals(harness.Relay) && harness.Client.MappedAddress!.Equals(harness.ClientEndPoint),
            "the client learns its relayed and mapped addresses");
        That(harness.Client.ExpiresAt > harness.Now + 590_000_000, "and the server's ten-minute lifetime");
        That(StunMessage.TryParse(harness.Sent[0], 0, harness.Sent[0].Length, out var first) && !first.HasIntegrity && first.HasFingerprint &&
             first.TryGetRequestedTransport(out var protocol) && protocol == 17, "the first Allocate asks for UDP without credentials");
        That(StunMessage.TryParse(harness.Sent[1], 0, harness.Sent[1].Length, out var second) && second.HasIntegrity &&
             second.TryGetText(StunAttribute.Realm, 763, out var realm) && realm == "relay.example.test", "the second carries the realm and integrity");
    }

    internal static void StaleNoncesAreRetriedWithTheFreshOne()
    {
        var harness = new TurnHarness(2, server => server.NonceLifetimeMicros = 60_000_000);
        harness.Start();
        harness.RunFor(1_000_000);
        harness.RunFor(100_000_000);
        harness.Client.Permit(Peer.Address, harness.Now);
        harness.RunFor(1_000_000);
        That(harness.Server.StaleNonces == 1 && harness.Client.Statistics.StaleNonceRetries == 1, "the stale nonce draws a 438 and one retry");
        That(harness.Client.IsPermitted(Peer.Address), "and the retry installs the permission");
        That(harness.Client.State == TurnAllocationState.Ready, "the allocation is unaffected");
    }

    internal static void TurnFailuresNameTheirCause()
    {
        var wrong = new TurnHarness(3, password: "not-the-secret");
        wrong.Start();
        wrong.RunFor(30_000_000);
        That(wrong.Client.State == TurnAllocationState.Failed && wrong.Client.Failure == TurnFailure.Unauthorized, "wrong credentials fail as unauthorized");
        That(wrong.Sent.Count == 2, "after exactly one authenticated attempt (" + wrong.Sent.Count + ")");

        foreach (var (code, failure) in new[] { (486, TurnFailure.QuotaReached), (508, TurnFailure.InsufficientCapacity), (403, TurnFailure.Forbidden), (500, TurnFailure.ServerError) })
        {
            var refused = new TurnHarness(4, server => server.AllocateError = code);
            refused.Start();
            refused.RunFor(1_000_000);
            That(refused.Client.State == TurnAllocationState.Failed && refused.Client.Failure == failure, "error " + code + " fails as " + failure);
        }

        var silent = new TurnHarness(5, server => server.Silent = true);
        silent.Start();
        var started = silent.Now;
        silent.RunFor(20_000_000);
        That(silent.Client.State == TurnAllocationState.Failed && silent.Client.Failure == TurnFailure.Timeout && silent.Sent.Count == 4,
            "a silent server times out after four transmissions");

        var stunOnly = new TurnHarness(6, server => server.StunOnly = true);
        stunOnly.Start();
        stunOnly.RunFor(20_000_000);
        That(stunOnly.Client.Failure == TurnFailure.Timeout, "a server that only speaks STUN times out too");

        // A second client on the same 5-tuple meets the first one's allocation.
        var twice = new TurnHarness(7);
        twice.Start();
        twice.RunFor(1_000_000);
        var duplicate = new TurnClient(2, twice.ServerPath, "player", "turn-secret", twice.Stun);
        duplicate.Start(twice.Now);
        twice.RunFor(1_000_000);
        That(duplicate.Failure == TurnFailure.AllocationMismatch && twice.Client.State == TurnAllocationState.Ready, "a second allocation on one 5-tuple is refused with 437");
        That(started > 0 && StunTiming.Gathering.TotalMicros == 5_500_000, "allocation uses the gathering schedule");
    }

    internal static void ForgedResponsesNeverCompleteARequest()
    {
        var harness = new TurnHarness(8);
        var forgedOnce = false;
        harness.Intercept = bytes =>
        {
            if (forgedOnce || !StunMessage.TryParse(bytes, 0, bytes.Length, out var real) || real.Class != StunClass.Success || real.Method != StunMethod.Allocate) return false;
            forgedOnce = true;
            var writer = new StunMessageWriter();
            var buffer = new byte[StunMessage.MaximumBytes];
            writer.Begin(buffer, StunMethod.Allocate, StunClass.Success, real.TransactionId);
            writer.AddAddress(StunAttribute.XorRelayedAddress, new IPEndPoint(IPAddress.Parse("198.51.100.99"), 9999));
            writer.AddUInt32(StunAttribute.Lifetime, 600);
            var length = writer.Finish(StunCredentials.LongTermKey("player", "relay.example.test", "guessed"), true);
            harness.Deliver(buffer.AsSpan(0, length).ToArray());
            return true;
        };
        harness.Start();
        harness.RunFor(10_000_000);
        That(forgedOnce, "a forged success arrived first");
        That(harness.Client.State == TurnAllocationState.Ready && harness.Client.RelayedAddress!.Equals(harness.Relay),
            "the client ignored it and took the server's own answer to a retransmission");
    }

    internal static void PermissionsAndChannelsAreRenewedBeforeTheyLapse()
    {
        var harness = new TurnHarness(9);
        harness.Start();
        harness.RunFor(1_000_000);
        harness.Client.Permit(Peer.Address, harness.Now);
        harness.RunFor(1_000_000);
        var path = harness.Client.PathTo(Peer);
        That(path.Kind == TransportPathKind.Relayed && TurnRelayPath.TryParse(path, out var allocation, out var named) && allocation == 1 && named.Equals(Peer),
            "a peer behind the relay is a relayed path naming the allocation and the peer");
        That(harness.SendData(path, new byte[] { 0xE8, 1 }) > 0 && harness.AtPeers.Count == 1, "the first datagram travels as a Send indication");
        harness.RunFor(1_000_000);
        That(harness.SendData(path, new byte[] { 0xE8, 2 }) == 6 && harness.Sent[^1][0] == 0x40, "once bound, datagrams carry a four-byte ChannelData header");

        // A datagram each way every five seconds for 25 minutes, so any moment a permission or channel lapsed shows.
        const int Rounds = 300;
        var sent = 0;
        for (var round = 0; round < Rounds; round++)
        {
            harness.RunFor(5_000_000);
            harness.PeerSends(Peer, new byte[] { 0xE8, (byte)round });
            sent += harness.SendData(path, new byte[] { 0xE8, 3, (byte)round }) > 0 ? 1 : 0;
        }

        That(harness.Server.Unpermitted == 0, "the permission never lapsed on the server (" + harness.Server.Unpermitted + " datagrams refused)");
        That(harness.Delivered.Count == Rounds && harness.Delivered.All(item => item.Path.Equals(path) && item.Channel),
            "for 25 minutes every peer datagram reaches the client over its channel (" + harness.Delivered.Count(item => item.Channel) + " of " + Rounds + ")");
        That(sent == Rounds && harness.AtPeers.Count == Rounds + 2, "and every datagram the client sends reaches the peer");
        That(harness.Client.Statistics.PermissionRequests >= 6 && harness.Client.Statistics.ChannelBinds >= 3 && harness.Server.Refreshes >= 2,
            "permissions, the channel and the allocation were all refreshed (" + harness.Client.Statistics.PermissionRequests + ", " +
            harness.Client.Statistics.ChannelBinds + ", " + harness.Server.Refreshes + ")");
        var channels = harness.Server.Allocations.Single().Channels.Keys;
        That(channels.All(channel => channel >= 0x4000 && channel <= 0x4FFF), "every channel number is in 0x4000 to 0x4FFF");
    }

    internal static void DataWaitsForAPermissionAndAChannel()
    {
        var harness = new TurnHarness(10);
        harness.Start();
        harness.RunFor(1_000_000);
        var path = harness.Client.PathTo(Peer);
        That(harness.SendData(path, new byte[] { 1 }) == 0 && harness.Client.Statistics.Held == 1, "nothing goes to a peer without a permission");
        harness.RunFor(1_000_000);
        That(harness.Client.IsPermitted(Peer.Address), "trying asks for the permission");
        var stranger = new IPEndPoint(IPAddress.Parse("198.51.100.200"), 5000);
        harness.PeerSends(stranger, new byte[] { 2 });
        That(harness.Delivered.Count == 0 && harness.Server.Unpermitted == 1, "a stranger without a permission never reaches the client");

        var bytes = new byte[] { 0x40, 0x00, 0x00, 0x02, 9, 9, 0, 0 };
        That(!harness.Client.TryUnwrap(bytes, 0, bytes.Length, out _, out _, out _), "ChannelData on an unbound channel is dropped");
        harness.SendData(path, new byte[] { 3 });
        harness.RunFor(1_000_000);
        harness.SendData(path, new byte[] { 4 });
        That(harness.Client.ChannelCount == 1, "a channel was bound");
        That(ChannelData.TryRead(bytes, 0, 8, out var channel, out _, out var length) && channel == 0x4000 && length == 2, "three bytes of padding are allowed");
        That(ChannelData.TryRead(bytes, 0, 7, out _, out _, out _), "so is one");
        That(!ChannelData.TryRead(new byte[] { 0x40, 0, 0, 9, 1 }, 0, 5, out _, out _, out _), "a length past the datagram is refused");
        That(!ChannelData.TryRead(new byte[] { 0x40, 0, 0, 0, 0, 0, 0, 0 }, 0, 8, out _, out _, out _), "four bytes of padding are not");
        That(!ChannelData.TryRead(new byte[] { 0x50, 0, 0, 0 }, 0, 4, out _, out _, out _) && !ChannelData.TryRead(new byte[] { 0x3F, 0xFF, 0, 0 }, 0, 4, out _, out _, out _),
            "channel numbers outside 0x4000 to 0x4FFF are refused");
    }

    internal static void ReleasingEndsTheAllocation()
    {
        var harness = new TurnHarness(11);
        harness.Start();
        harness.RunFor(1_000_000);
        That(harness.Server.Allocations.Count == 1, "an allocation exists");
        harness.Client.Release(harness.Now);
        harness.RunFor(1_000_000);
        That(harness.Client.State == TurnAllocationState.Released && harness.Server.Allocations.Count == 0, "a zero-lifetime refresh deletes it on the server");
        That(harness.SendData(harness.Client.PathTo(Peer), new byte[] { 1 }) == 0, "and nothing more is wrapped");
    }

    internal static void RelayCredentialsArriveInTheIceServersShape()
    {
        var body = """{"expiresAt":1760000600,"iceServers":[{"urls":["stun:stun.cloudflare.com:3478"]},{"credential":"secret-credential","urls":["turn:turn.cloudflare.com:3478?transport=udp","turn:turn.cloudflare.com:53?transport=udp","turn:turn.cloudflare.com:3478?transport=tcp","turns:turn.cloudflare.com:5349?transport=tcp"],"username":"user-name"}],"ttlSeconds":600}""";
        That(IceServerSet.TryParse(System.Text.Encoding.UTF8.GetBytes(body), out var set), "the control plane's answer parses");
        That(set.StunServers.Count == 1 && set.StunServers[0].Host == "stun.cloudflare.com" && set.StunServers[0].Port == 3478, "with its STUN server");
        That(set.TurnServers.Count == 2 && set.TurnServers[0].Port == 3478 && set.TurnServers[1].Port == 53, "and only its UDP TURN servers: TCP and TLS are later work");
        That(set.HasRelay && set.Username == "user-name" && set.Credential == "secret-credential" && set.TtlSeconds == 600, "and the credential with its lifetime");
        That(!set.ToString().Contains("secret", StringComparison.Ordinal), "which never prints");
        var refused = new[]
        {
            """{"iceServers":[{"credential":"c","urls":["turn:203.0.113.1:3478"],"username":"u"}],"ttlSeconds":600}""",
            """{"iceServers":[{"credential":"c","urls":["turn:a.example.test"],"username":"u"},{"credential":"d","urls":["turn:b.example.test"],"username":"v"}],"ttlSeconds":600}""",
            """{"iceServers":[{"urls":["turn:a.example.test"],"username":"u"}],"ttlSeconds":600}""",
            """{"iceServers":[{"credential":"two words","urls":["turn:a.example.test"],"username":"u"}],"ttlSeconds":600}""",
            """{"iceServers":[{"urls":["stun:a.example.test"]}],"ttlSeconds":0}""",
            """{"iceServers":[],"ttlSeconds":600}""",
            """{"ttlSeconds":600}""",
            """{ "iceServers":[{"urls":["stun:a.example.test"]}],"ttlSeconds":600}"""
        };
        foreach (var text in refused) That(!IceServerSet.TryParse(System.Text.Encoding.UTF8.GetBytes(text), out _), "a malformed or address-bearing answer is refused");
        That(!IceServerSet.TryParse(new byte[IceServerSet.MaximumBytes + 1], out _), "so is an oversized one");
        That(IceServerSet.TryParse(System.Text.Encoding.UTF8.GetBytes("""{"extra":1,"iceServers":[{"urls":["stun:a.example.test"]}],"ttlSeconds":600}"""), out var stunOnly) && !stunOnly.HasRelay,
            "members it does not know are ignored, and a set without TURN means no relay");
        foreach (var url in new[] { "turn:host:0", "turn:a.example.test:65536", "turn:A.example.test", "turn:a.example.test?transport=sctp", "turn:localhost:3478", "turn:1.2.3.4:3478", "turn:a.example.test:" })
        {
            That(!IceServerSet.TryParseUrl(url, out _, out _, out _), "the URL grammar refuses " + url);
        }

        That(IceServerSet.TryParseUrl("stun:a.example.test", out _, out var defaulted, out var udp) && defaulted.Port == 3478 && udp, "an absent port is 3478");
    }

    internal static void KeepalivesHoldTheServerMappingOpen()
    {
        var harness = new TurnHarness(12);
        harness.Start();
        harness.RunFor(1_000_000);
        harness.RunFor(61_000_000);
        That(harness.Server.Keepalives >= 3 && harness.Server.Keepalives <= 5, "an idle allocation sends a Binding indication every 15 seconds (" + harness.Server.Keepalives + ")");
    }
}
