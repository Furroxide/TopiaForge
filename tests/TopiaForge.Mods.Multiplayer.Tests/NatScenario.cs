extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Signaling;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using ProviderChannel = provider::TopiaForge.Multiplayer.Engine.EngineChannel;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal sealed class RecordingNatDiagnostics : INatDiagnostics
{
    internal List<(NatDiagnosticKind Kind, string Message)> Reports { get; } = new();

    public void Report(NatDiagnosticKind kind, string message) => Reports.Add((kind, message));

    internal bool Saw(NatDiagnosticKind kind) => Reports.Any(report => report.Kind == kind);
}

/// <summary>
/// A whole Internet session on the virtual network: sites and NATs, a fake STUN and TURN server on the Internet, the
/// fake signaling room, and players whose NAT agents run the real gathering, signaling, checks and route keeping. The
/// servers answer on documentation addresses, which count as public here.
/// </summary>
internal sealed class NatScenario
{
    internal const string StunHost = "stun.relay.example.test";
    internal const string TurnHost = "turn.relay.example.test";
    internal const string PeerId = "74aea50c644d302f";
    internal static readonly IPAddress ServerAddress = IPAddress.Parse("203.0.113.1");

    private readonly Dictionary<SimulatedSite, SimulatedNat> nats = new();
    private int credentials;

    internal NatScenario(int seed, long ttlSeconds = 600)
    {
        Seed = seed;
        TtlSeconds = ttlSeconds;
        Network = new VirtualDatagramNetwork(seed) { MaximumDatagramBytes = 1500 };
        Router = new NatRouter();
        Network.Router = Router;

        // Signaling first: its deliveries must land before the players poll in the same instant.
        Signaling = new FakeSignalingService();
        Network.AddAttachment("signaling", Signaling);
        Turn = new FakeTurnServer(ServerAddress, seed, 3478, 3479);
        TurnNode = Network.AddAttachment("turn", new TurnServerNode(Turn));
        Router.Place(TurnNode, Router.Internet, new IPEndPoint(ServerAddress, 3478));
    }

    internal int Seed { get; }

    internal long TtlSeconds { get; }

    internal VirtualDatagramNetwork Network { get; }

    internal NatRouter Router { get; }

    internal FakeSignalingService Signaling { get; }

    internal FakeTurnServer Turn { get; }

    internal VirtualNode TurnNode { get; }

    /// <summary>Gets every server name a player's agent asked DNS for.</summary>
    internal List<(string Peer, string Host)> Lookups { get; } = new();

    /// <summary>Gets or sets whether a plan can mint the next relay credential, so allocations rotate before they expire.</summary>
    internal bool Renewable { get; set; }

    /// <summary>Gets or sets whether the TURN server set offers a second UDP port, so a rotation can make before it breaks.</summary>
    internal bool SecondTurnPort { get; set; } = true;

    /// <summary>Gets or sets what DNS answers for the TURN server's name; the server's own address by default.</summary>
    internal IPAddress[] TurnAddresses { get; set; } = { ServerAddress };

    /// <summary>Gets or sets whether minting works; when it does not, the control plane answers with an error and the plan yields nothing.</summary>
    internal Func<bool> MintWorks { get; set; } = () => true;

    /// <summary>Gets how many relay credentials were minted, the first one of every plan included.</summary>
    internal int Mints => credentials;

    /// <summary>Gets when each credential was minted, on the virtual clock, the first one of every plan included.</summary>
    internal List<long> MintedAt { get; } = new();

    /// <summary>Ends every credential minted so far now, as a revocation does: the server refuses each from here on.</summary>
    internal void RevokeCredentials()
    {
        foreach (var name in Turn.Users.Keys.ToList()) Turn.Users[name] = (Turn.Users[name].Password, Network.Now);
    }

    /// <summary>
    /// Gives every allocation a relayed address of its own, 203.0.113.100 and on, as a relay service may: a permission for
    /// one of a peer's relays then never admits the next, so only signaling can introduce it.
    /// </summary>
    internal void DistinctRelayAddresses()
    {
        Turn.RelayAddresses = index => IPAddress.Parse("203.0.113." + (100 + (index % 100)).ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var index = 0; index < 100; index++) Router.Place(TurnNode, Router.Internet, new IPEndPoint(IPAddress.Parse("203.0.113." + (100 + index).ToString(System.Globalization.CultureInfo.InvariantCulture)), 3478));
        Router.Place(TurnNode, Router.Internet, new IPEndPoint(ServerAddress, 3478));
    }

    /// <summary>Puts a site behind a NAT on <paramref name="outside"/> (the Internet by default); returns the inside site.</summary>
    internal SimulatedSite Behind(string name, NatBehaviour behaviour, string publicAddress, SimulatedSite? outside = null)
    {
        var nat = new SimulatedNat(name, IPAddress.Parse(publicAddress), behaviour, outside ?? Router.Internet);
        nats.Add(nat.Inside, nat);
        return nat.Inside;
    }

    internal SimulatedNat NatOf(SimulatedSite site) => nats[site];

    internal NatPeerNode AddPeer(string name, SimulatedSite site, string address, int port, bool relayOnly, int role)
    {
        var transport = new TransportOptions { Random = new SeededTransportRandom((Seed * 97) + role), RelayOnly = relayOnly };
        var diagnostics = new RecordingNatDiagnostics();
        return Place(name, site, address, port, transport, new NatAgentOptions { RelayOnly = relayOnly, Random = new SeededTransportRandom((Seed * 89) + role), Diagnostics = diagnostics }, diagnostics, relayOnly, role);
    }

    /// <summary>Adds a player whose agent and transport keep their defaults, as when nobody chose direct play; its ticket says relay-only.</summary>
    internal NatPeerNode AddDefaultPeer(string name, SimulatedSite site, string address, int port, int role)
    {
        var diagnostics = new RecordingNatDiagnostics();
        var transport = new TransportOptions { Random = new SeededTransportRandom((Seed * 97) + role) };
        return Place(name, site, address, port, transport, new NatAgentOptions { Random = new SeededTransportRandom((Seed * 89) + role), Diagnostics = diagnostics }, diagnostics, true, role);
    }

    private NatPeerNode Place(string name, SimulatedSite site, string address, int port, TransportOptions transport, NatAgentOptions options, RecordingNatDiagnostics diagnostics, bool relayOnly, int role)
    {
        var stack = new NetworkStack(new TransportEndpoint(TransportPair.Key(Seed, role), transport, Network.Now));
        var peer = new NatPeerNode(name, stack, new NatAgent(options)) { Diagnostics = diagnostics, Address = new IPEndPoint(IPAddress.Parse(address), port), RelayOnly = relayOnly };
        peer.Node = Network.AddAttachment(name, peer);
        Router.Place(peer.Node, site, peer.Address);
        return peer;
    }

    internal NatSessionPlan Plan(NatPeerNode peer, Func<ISignalingClient> signaling)
    {
        var plan = new NatSessionPlan(Credentials(), signaling, host =>
        {
            Lookups.Add((peer.Name, host));
            return host == StunHost ? Task.FromResult(new[] { ServerAddress })
                : host == TurnHost ? Task.FromResult(TurnAddresses)
                : Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException(11001));
        })
        {
            LocalAddresses = new[] { peer.Address.Address },
            LocalPort = peer.Address.Port
        };
        if (Renewable) plan.Renew = () => Task.FromResult(MintWorks() ? Credentials() : null);
        return plan;
    }

    internal void StartHost(NatPeerNode host) => host.Agent.StartHost(Plan(host, () => Signaling.ConnectHost(host.RelayOnly)), Network.Now);

    /// <param name="prefix">The address prefix the joiner reaches the control plane from, which the room counts its sockets under.</param>
    internal ConnectAttempt StartJoin(NatPeerNode joiner, int hostRole = 1, string peerId = PeerId, bool? ticketRelayOnly = null, string prefix = "")
    {
        var attempt = new ConnectAttempt();
        var relayOnly = ticketRelayOnly ?? joiner.RelayOnly;
        joiner.Agent.StartJoin(Plan(joiner, () => Signaling.ConnectPeer(peerId, relayOnly, prefix)), attempt, X25519Public(hostRole), Network.Now);
        return attempt;
    }

    /// <summary>Runs until the join is connected and the host has accepted it; returns both links.</summary>
    internal (NetworkLink Joiner, NetworkLink Host) Connect(NatPeerNode host, NatPeerNode joiner, ConnectAttempt attempt, long maximumMicros = 30_000_000)
    {
        NetworkLink? accepted = null;
        var done = Network.RunUntil(() => attempt.State == ConnectAttemptState.Established && (accepted != null || host.Stack.TryAccept(out accepted)), maximumMicros);
        TransportAssert.That(done && accepted != null,
            "the join connects (" + attempt.State + ", " + joiner.Agent.Status + "; host " + host.Agent.Status + ")");
        return (attempt.Link!, accepted!);
    }

    /// <summary>Sends a frame each way and runs until both arrive.</summary>
    internal bool Exchange(NetworkLink joiner, NetworkLink host, byte tag, long maximumMicros = 5_000_000)
    {
        joiner.Send(ProviderChannel.Session, new ArraySegment<byte>(new byte[] { 1, tag }));
        host.Send(ProviderChannel.Session, new ArraySegment<byte>(new byte[] { 2, tag }));
        var atHost = false;
        var atJoiner = false;
        return Network.RunUntil(() =>
        {
            while (host.TryReceive(out _, out var frame)) atHost |= frame.Length == 2 && frame[0] == 1 && frame[1] == tag;
            while (joiner.TryReceive(out _, out var frame)) atJoiner |= frame.Length == 2 && frame[0] == 2 && frame[1] == tag;
            return atHost && atJoiner;
        }, maximumMicros);
    }

    internal static NatRoute RouteOf(NatPeerNode peer) => peer.Agent.Sessions.FirstOrDefault(session => session.Current != null)?.Route ?? NatRoute.None;

    /// <summary>Mints a fresh TURN account on the fake server, the way the control plane mints a credential.</summary>
    private IceServerSet Credentials()
    {
        var name = "topiaforge-" + (++credentials).ToString(System.Globalization.CultureInfo.InvariantCulture);
        MintedAt.Add(Network.Now);
        Turn.Users[name] = ("relay-secret-" + credentials, Network.Now + (TtlSeconds * 1_000_000L));
        var turn = new List<IceServerAddress> { new(TurnHost, 3478) };
        if (SecondTurnPort) turn.Add(new IceServerAddress(TurnHost, 3479));
        return IceServerSet.Create(new[] { new IceServerAddress(StunHost, 3478) }, turn, name, "relay-secret-" + credentials, TtlSeconds);
    }

    private byte[] X25519Public(int role) => provider::TopiaForge.Multiplayer.Transport.Crypto.X25519.DerivePublicKey(TransportPair.Key(Seed, role));
}
