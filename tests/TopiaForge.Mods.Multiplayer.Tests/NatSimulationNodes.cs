extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.NatTraversal.Stun;
using provider::TopiaForge.Multiplayer.NatTraversal.Turn;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A player's machine on the virtual network: a transport stack and the NAT agent on one shared socket, glued exactly as
/// <c>NatDatagramSocket</c> and <c>NatNetworkService</c> glue them on the network thread. Every datagram it sends and
/// every relayed datagram its transport hands the agent is recorded for the privacy assertions.
/// </summary>
internal sealed class NatPeerNode : IVirtualAttachment
{
    private readonly byte[] wire = new byte[StunMessage.MaximumBytes];
    private readonly Queue<(TransportPath Destination, byte[] Bytes)> pending = new();

    internal NatPeerNode(string name, NetworkStack stack, NatAgent agent)
    {
        Name = name;
        Stack = stack;
        Agent = agent;
    }

    internal string Name { get; }

    internal NetworkStack Stack { get; }

    internal NatAgent Agent { get; }

    internal VirtualNode Node { get; set; } = null!;

    internal RecordingNatDiagnostics Diagnostics { get; init; } = new();

    /// <summary>Gets the machine's own address on its site, and its game port.</summary>
    internal IPEndPoint Address { get; init; } = new(IPAddress.Any, 0);

    internal bool RelayOnly { get; init; }

    /// <summary>Gets every destination this machine sent a datagram to, as the wire saw it.</summary>
    internal List<IPEndPoint> SentTo { get; } = new();

    /// <summary>Gets the sources of every datagram that reached this machine.</summary>
    internal List<IPEndPoint> HeardFrom { get; } = new();

    internal long RelayedIn { get; private set; }

    internal long HeldRelayed { get; private set; }

    public void Receive(TransportPath source, TransportPath destination, byte[] datagram, long now)
    {
        if (UdpPaths.TryGetEndPoint(source, out var from)) HeardFrom.Add(from);
        switch (Agent.Receive(source, datagram, 0, datagram.Length, now, out var relayed, out var offset, out var count))
        {
            case NatReceiveResult.Transport:
                if (datagram.Length <= TransportOptions.MaximumDatagramBytes) Stack.Receive(source, datagram, 0, datagram.Length, now);
                break;
            case NatReceiveResult.Relayed:
                RelayedIn++;
                Stack.Receive(relayed, datagram, offset, count, now);
                break;
        }
    }

    public void Poll(long now)
    {
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
                else HeldRelayed++;
            }
            finally
            {
                Stack.Release(outgoing);
            }
        }
    }

    public long NextTimeoutMicros() => Math.Min(Agent.NextTimeoutMicros(), Stack.NextTimeoutMicros());

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

/// <summary>The fake STUN and TURN server as a node: it answers on its listening ports and relays from its relayed ones.</summary>
internal sealed class TurnServerNode : IVirtualAttachment
{
    internal TurnServerNode(FakeTurnServer server)
    {
        Server = server;
    }

    internal FakeTurnServer Server { get; }

    public void Receive(TransportPath source, TransportPath destination, byte[] datagram, long now)
    {
        if (!UdpPaths.TryGetEndPoint(source, out var from) || !UdpPaths.TryGetEndPoint(destination, out var to)) return;
        var plainTo = new IPEndPoint(NatAddresses.Unmap(to.Address), to.Port);
        if (Server.Owns(plainTo)) Server.Receive(new IPEndPoint(NatAddresses.Unmap(from.Address), from.Port), plainTo, datagram, now);
    }

    public void Poll(long now) => Server.Poll(now);

    public long NextTimeoutMicros() => Server.NextTimeoutMicros();

    public bool TryDequeue(out TransportPath? source, out TransportPath destination, out byte[] datagram)
    {
        if (Server.Outbox.Count == 0)
        {
            source = null;
            destination = null!;
            datagram = null!;
            return false;
        }

        var (from, to, bytes) = Server.Outbox.Dequeue();
        source = UdpPaths.For(from);
        destination = UdpPaths.For(to);
        datagram = bytes;
        return true;
    }
}
