extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal.Ice;
using provider::TopiaForge.Multiplayer.NatTraversal.Signaling;
using provider::TopiaForge.Multiplayer.NatTraversal.Stun;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A hostile machine on the simulated Internet. It sends whatever a test queues, from any source it names, and it can keep
/// sessions it opened on a host alive: every interval it sends each one an authentic connectivity check, signed with the
/// password the host's answer gave it, from the port that identity named, to the host's current relayed address, as an
/// attacker learns it from the host's trickled candidates. It answers nothing.
/// </summary>
internal sealed class NatHostileSender : IVirtualAttachment
{
    private readonly List<Kept> kept = new();
    private readonly Random random = new(4242);

    internal NatHostileSender(IPAddress address)
    {
        Address = address;
    }

    /// <summary>Gets the address the attacker's own sockets send from.</summary>
    internal IPAddress Address { get; }

    internal Queue<(TransportPath Source, TransportPath Destination, byte[] Bytes)> Outbox { get; } = new();

    /// <summary>Gets how many checks it sent to keep sessions alive.</summary>
    internal int ChecksSent { get; private set; }

    /// <summary>Places the attacker on the scenario's Internet and returns it.</summary>
    internal static NatHostileSender On(NatScenario scenario, string address)
    {
        var sender = new NatHostileSender(IPAddress.Parse(address));
        var node = scenario.Network.AddAttachment("attacker " + address, sender);
        scenario.Router.Place(node, scenario.Router.Internet, new IPEndPoint(sender.Address, 41000));
        return sender;
    }

    /// <summary>
    /// Opens a signaling socket as a new identity, offers <paramref name="candidates"/>, and closes the socket once the
    /// offer is through, so it holds none of the room's places; returns the ICE credentials it offered.
    /// </summary>
    internal static IceCredentials OfferAndLeave(NatScenario scenario, string peerId, string prefix, IReadOnlyList<SignalCandidate> candidates, ITransportRandom random,
        IReadOnlyList<SignalCandidate>? trickled = null)
    {
        var socket = scenario.Signaling.ConnectPeer(peerId, false, prefix);
        scenario.Network.RunFor(100_000);
        var ice = IceCredentials.Generate(random);
        socket.TrySend(SignalMessage.Offer(ice, candidates));
        scenario.Network.RunFor(100_000);
        if (trickled != null)
        {
            foreach (var candidate in trickled) socket.TrySend(SignalMessage.Trickle(null, candidate));
        }

        socket.Close();
        scenario.Network.RunFor(100_000);
        return ice;
    }

    /// <summary>Sixteen candidates on addresses of a hostile identity's own: <paramref name="own"/> first when given, then distinct addresses.</summary>
    internal static List<SignalCandidate> Spread(int identity, int count = 16, string? own = null, int ownPort = 41000)
    {
        var candidates = new List<SignalCandidate>();
        if (own != null) candidates.Add(new SignalCandidate(IceCandidateKind.ServerReflexive, own, ownPort, 1000u));
        for (var index = candidates.Count; index < count; index++)
        {
            candidates.Add(new SignalCandidate(IceCandidateKind.ServerReflexive, "45." + (10 + identity) + "." + index + ".1", 40000, 1000u));
        }

        return candidates;
    }

    /// <summary>Keeps the host's session for <paramref name="peerId"/> alive with a check every <paramref name="interval"/> from <paramref name="port"/>.</summary>
    internal void Keep(NatPeerNode host, string peerId, IceCredentials own, int port, long interval) =>
        kept.Add(new Kept(host, peerId, own, port, interval));

    public void Receive(TransportPath source, TransportPath destination, byte[] datagram, long now)
    {
    }

    public void Poll(long now)
    {
        foreach (var item in kept)
        {
            if (now < item.NextAt) continue;
            item.NextAt = now + item.Interval;
            var session = item.Host.Agent.Sessions.LastOrDefault(candidate => candidate.PeerId == item.PeerId);
            var relayed = item.Host.Agent.Relay?.RelayedAddress;
            if (session == null || relayed == null) continue;
            var writer = new StunMessageWriter();
            var buffer = new byte[512];
            var id = new byte[12];
            random.NextBytes(id);
            writer.Begin(buffer, StunMethod.Binding, StunClass.Request, StunTransactionId.Read(id, 0));
            writer.AddText(StunAttribute.Username, session.Checker.Local.Ufrag + ":" + item.Own.Ufrag);
            writer.AddUInt32(StunAttribute.Priority, IceCandidate.ComputePriority(IceCandidateKind.PeerReflexive, 65535));
            writer.AddUInt64(StunAttribute.IceControlling, 7);
            var length = writer.Finish(session.Checker.Local.Key, true);
            Outbox.Enqueue((UdpPaths.For(new IPEndPoint(Address, item.Port)), UdpPaths.For(relayed), buffer.AsSpan(0, length).ToArray()));
            ChecksSent++;
        }
    }

    public long NextTimeoutMicros()
    {
        var next = long.MaxValue;
        foreach (var item in kept) next = Math.Min(next, item.NextAt);
        return next;
    }

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

    private sealed class Kept
    {
        internal Kept(NatPeerNode host, string peerId, IceCredentials own, int port, long interval)
        {
            Host = host;
            PeerId = peerId;
            Own = own;
            Port = port;
            Interval = interval;
        }

        internal NatPeerNode Host { get; }

        internal string PeerId { get; }

        internal IceCredentials Own { get; }

        internal int Port { get; }

        internal long Interval { get; }

        internal long NextAt { get; set; }
    }
}
