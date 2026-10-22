extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Stun;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// One player machine's network on the NAT simulator, behind the control the game's network thread offers. The stack
/// and the NAT agent share one socket, glued as <c>NatDatagramSocket</c> and <c>NatNetworkService</c> glue them, and
/// commands run at once on the virtual clock. Stopping does what the network thread does: the links close gracefully
/// first, and only once the grace period is over is the NAT layer disposed, its goodbyes and releases sent. Every
/// datagram the machine sends or hears is recorded for the privacy checks.
/// </summary>
internal sealed class SteppedTraversalNetwork : INetworkControl, IVirtualAttachment
{
    private const long ReleaseSettleMicros = 6_000_000;
    private readonly VirtualDatagramNetwork network;
    private readonly byte[] wire = new byte[StunMessage.MaximumBytes];
    private readonly Queue<(TransportPath Destination, byte[] Bytes)> pending = new();
    private long disposeAgentAt = long.MaxValue;
    private bool agentDisposed;

    internal SteppedTraversalNetwork(VirtualDatagramNetwork network, string name, NetworkStack stack, NatAgent agent, IPEndPoint address)
    {
        this.network = network;
        Name = name;
        Stack = stack;
        Agent = agent;
        Address = address;
        LocalPort = address.Port;
    }

    internal string Name { get; }

    internal NetworkStack Stack { get; }

    internal NatAgent Agent { get; }

    internal IPEndPoint Address { get; }

    internal VirtualNode Node { get; set; } = null!;

    internal bool Stopped { get; private set; }

    /// <summary>Gets every destination this machine sent a datagram to, as the wire saw it.</summary>
    internal List<IPEndPoint> SentTo { get; } = new();

    /// <summary>Gets the sources of every datagram that reached this machine.</summary>
    internal List<IPEndPoint> HeardFrom { get; } = new();

    public int LocalPort { get; }

    public ConnectAttempt Connect(TransportPath path, byte[]? expectedKey)
    {
        var attempt = new ConnectAttempt();
        Stack.Connect(attempt, path, expectedKey, network.Now);
        return attempt;
    }

    public void Post(Action<NetworkStack, long> command) => command(Stack, network.Now);

    public bool TryTakeEvent(out NetworkEvent networkEvent)
    {
        if (Stack.TryAccept(out var link))
        {
            networkEvent = NetworkEvent.Accepted(link);
            return true;
        }

        if (Stack.Endpoint.TryReceiveInfo(out var reply))
        {
            networkEvent = NetworkEvent.Received(reply);
            return true;
        }

        networkEvent = null!;
        return false;
    }

    /// <summary>The descriptor endpoint and LAN services have no place on the simulated Internet; they are let go at once.</summary>
    public void Attach(INetworkService service) => service.Dispose();

    public void Detach(INetworkService service) => service.Dispose();

    public void Stop(long gracefulMicros)
    {
        if (Stopped) return;
        Stopped = true;
        Stack.CloseAll();
        disposeAgentAt = network.Now + Math.Max(0, gracefulMicros);
        network.Retire(Node, disposeAgentAt + ReleaseSettleMicros);
    }

    public void Dispose() => Stop(0);

    public void Receive(TransportPath source, TransportPath destination, byte[] datagram, long now)
    {
        if (UdpPaths.TryGetEndPoint(source, out var from)) HeardFrom.Add(from);
        switch (Agent.Receive(source, datagram, 0, datagram.Length, now, out var relayed, out var offset, out var count))
        {
            case NatReceiveResult.Transport:
                if (datagram.Length <= TransportOptions.MaximumDatagramBytes) Stack.Receive(source, datagram, 0, datagram.Length, now);
                break;
            case NatReceiveResult.Relayed:
                Stack.Receive(relayed, datagram, offset, count, now);
                break;
        }
    }

    public void Poll(long now)
    {
        if (!agentDisposed && now >= disposeAgentAt)
        {
            // As the network thread disposes its services once the links have closed: goodbyes and releases go out last.
            agentDisposed = true;
            Agent.Dispose();
        }

        Agent.Turn(Stack, now);
        Stack.Poll(now);
        while (Agent.TryDequeueDatagram(out var own))
        {
            pending.Enqueue((own.Path, own.Buffer.AsSpan(0, own.Length).ToArray()));
            Agent.Release(own);
        }

        while (Stack.TryDequeueDatagram(out var outgoing))
        {
            try
            {
                if (!TurnRelayPath.IsRelayed(outgoing.Path))
                {
                    pending.Enqueue((outgoing.Path, outgoing.Buffer.AsSpan(0, outgoing.Length).ToArray()));
                    continue;
                }

                var length = Agent.Wrap(outgoing.Path, outgoing.Buffer, 0, outgoing.Length, wire, now, out var server);
                if (length > 0) pending.Enqueue((server, wire.AsSpan(0, length).ToArray()));
            }
            finally
            {
                Stack.Release(outgoing);
            }
        }
    }

    public long NextTimeoutMicros() => Math.Min(agentDisposed ? long.MaxValue : disposeAgentAt, Math.Min(Agent.NextTimeoutMicros(), Stack.NextTimeoutMicros()));

    public bool TryDequeue(out TransportPath? source, out TransportPath destination, out byte[] datagram)
    {
        source = null;
        if (pending.Count == 0)
        {
            destination = null!;
            datagram = null!;
            return false;
        }

        (destination, datagram) = pending.Dequeue();
        if (UdpPaths.TryGetEndPoint(destination, out var to)) SentTo.Add(to);
        return true;
    }

    public override string ToString() => Name;
}

/// <summary>
/// Places one player machine's networks on the NAT simulator: each network the live flows start gets a port of the
/// machine's address on its site, with seeded randomness. Only traversal networks exist here, since Internet sessions
/// are all these tests hold.
/// </summary>
internal sealed class TraversalNetworkFactory : INetworkFactory
{
    private readonly NatScenario scenario;
    private readonly SimulatedSite site;
    private readonly IPAddress address;
    private readonly int seed;
    private int created;

    internal TraversalNetworkFactory(NatScenario scenario, SimulatedSite site, IPAddress address, int seed)
    {
        this.scenario = scenario;
        this.site = site;
        this.address = address;
        this.seed = seed;
    }

    internal List<SteppedTraversalNetwork> Networks { get; } = new();

    /// <summary>Gets every destination any of this machine's networks sent to, as the wire saw it.</summary>
    internal IEnumerable<IPEndPoint> SentTo => Networks.SelectMany(network => network.SentTo);

    /// <summary>Gets the sources of everything that reached this machine.</summary>
    internal IEnumerable<IPEndPoint> HeardFrom => Networks.SelectMany(network => network.HeardFrom);

    internal RecordingNatDiagnostics Diagnostics { get; } = new();

    /// <summary>A network without the NAT layer, as a LAN host binds: it sits on the virtual network, but no route reaches it here.</summary>
    public INetworkControl Start(int port, NetworkStack stack)
    {
        ++created;
        var network = new SteppedNetwork(scenario.Network, address + "/lan-" + created, stack, port == 0 ? 41_000 + created : port);
        Plain.Add(network);
        return network;
    }

    /// <summary>Gets the networks started without the NAT layer.</summary>
    internal List<SteppedNetwork> Plain { get; } = new();

    public TraversalNetwork StartTraversal(int port, NetworkStack stack, NatAgentOptions options)
    {
        ++created;
        var bound = port == 0 ? 41_000 + created : port;
        options.Random = new SeededTransportRandom((seed * 131) + created);
        options.Diagnostics = Diagnostics;
        var endpoint = new IPEndPoint(address, bound);
        var network = new SteppedTraversalNetwork(scenario.Network, address + ":" + bound, stack, new NatAgent(options), endpoint);
        network.Node = scenario.Network.AddAttachment(network.Name, network);
        scenario.Router.Place(network.Node, site, endpoint);
        Networks.Add(network);
        return new TraversalNetwork(network, network.Agent);
    }

    public DescriptorEndpoint? CreateDescriptorEndpoint(int port, bool relayOnly) => null;

    public LanAnnouncer? CreateAnnouncer(LanBeacon beacon) => null;

    public LanBrowser? CreateBrowser(int discoveryPort) => null;

    public string? SourceOf(TransportPath path) => UdpPaths.SourceOf(path);

    public byte[]? NetworkOf(TransportPath path) => UdpPaths.NetworkOf(path);

    /// <summary>Gets whether a game socket this factory opened runs over IPv4 alone; the simulated sites never fall back.</summary>
    public bool OpenedIPv4Only => false;
}
