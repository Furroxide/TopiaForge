extern alias provider;

using System.Net;
using provider::TopiaForge.Multiplayer.NatTraversal;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>NAT behaviours from RFC 4787: how a mapping is chosen, and what may come back through it.</summary>
internal enum NatBehaviour
{
    /// <summary>One mapping per inside endpoint; anyone may send to it.</summary>
    FullCone,

    /// <summary>One mapping per inside endpoint; only addresses it sent to may answer.</summary>
    AddressRestricted,

    /// <summary>One mapping per inside endpoint; only address and port pairs it sent to may answer.</summary>
    PortRestricted,

    /// <summary>A new mapping for every destination, and only that destination may answer.</summary>
    Symmetric
}

/// <summary>An address space: the Internet, a carrier's shared network, or a home behind its router.</summary>
internal sealed class SimulatedSite
{
    internal SimulatedSite(string name, SimulatedNat? gateway)
    {
        Name = name;
        Gateway = gateway;
    }

    internal string Name { get; }

    /// <summary>Gets the NAT this site sits behind; null for the Internet.</summary>
    internal SimulatedNat? Gateway { get; }

    /// <summary>Gets the nodes with an address here, by address; a node decides itself which of its ports answer.</summary>
    internal Dictionary<IPAddress, VirtualNode> Hosts { get; } = new();

    /// <summary>Gets the NATs whose public side has an address here, by that address.</summary>
    internal Dictionary<IPAddress, SimulatedNat> Nats { get; } = new();

    public override string ToString() => Name;
}

/// <summary>
/// One NAT box, deterministic: mappings take external ports in order from 40000, expire after an idle time that traffic
/// either way refreshes, and filter inbound traffic by the behaviour. Hairpinning, sending to the NAT's own public address
/// from inside, works only when allowed. A NAT that blocks UDP passes nothing at all.
/// </summary>
internal sealed class SimulatedNat
{
    private readonly Dictionary<string, Mapping> byKey = new();
    private readonly Dictionary<int, Mapping> byPort = new();
    private int nextPort = 40_000;

    internal SimulatedNat(string name, IPAddress publicAddress, NatBehaviour behaviour, SimulatedSite outside)
    {
        Name = name;
        PublicAddress = publicAddress;
        Behaviour = behaviour;
        Outside = outside;
        Inside = new SimulatedSite(name + " inside", this);
        outside.Nats.Add(publicAddress, this);
    }

    internal string Name { get; }

    internal IPAddress PublicAddress { get; }

    internal NatBehaviour Behaviour { get; }

    internal SimulatedSite Outside { get; }

    internal SimulatedSite Inside { get; }

    internal bool Hairpin { get; set; }

    internal bool BlocksUdp { get; set; }

    internal long MappingIdleMicros { get; set; } = 120_000_000;

    internal int Filtered { get; private set; }

    internal int Mappings => byPort.Count;

    /// <summary>Maps an inside endpoint toward <paramref name="destination"/> and records that it sent there.</summary>
    internal IPEndPoint Outbound(IPEndPoint inside, IPEndPoint destination, long now)
    {
        Expire(now);
        var key = Behaviour == NatBehaviour.Symmetric ? Key(inside) + ">" + Key(destination) : Key(inside);
        if (!byKey.TryGetValue(key, out var mapping))
        {
            mapping = new Mapping(key, inside, nextPort++);
            byKey.Add(key, mapping);
            byPort.Add(mapping.Port, mapping);
        }

        mapping.LastUsed = now;
        mapping.Addresses.Add(NatAddresses.Unmap(destination.Address));
        mapping.EndPoints.Add(Key(destination));
        return new IPEndPoint(PublicAddress, mapping.Port);
    }

    /// <summary>Finds the inside endpoint for traffic to <paramref name="port"/> from <paramref name="from"/>, or null when filtered.</summary>
    internal IPEndPoint? Inbound(int port, IPEndPoint from, long now)
    {
        Expire(now);
        if (!byPort.TryGetValue(port, out var mapping))
        {
            Filtered++;
            return null;
        }

        var allowed = Behaviour switch
        {
            NatBehaviour.FullCone => true,
            NatBehaviour.AddressRestricted => mapping.Addresses.Contains(NatAddresses.Unmap(from.Address)),
            _ => mapping.EndPoints.Contains(Key(from))
        };
        if (!allowed)
        {
            Filtered++;
            return null;
        }

        mapping.LastUsed = now;
        return mapping.Inside;
    }

    private void Expire(long now)
    {
        foreach (var mapping in byPort.Values.Where(mapping => now - mapping.LastUsed > MappingIdleMicros).ToList())
        {
            byPort.Remove(mapping.Port);
            byKey.Remove(mapping.Key);
        }
    }

    internal static string Key(IPEndPoint endpoint) => NatAddresses.Format(endpoint.Address) + "/" + endpoint.Port;

    public override string ToString() => Name;

    private sealed class Mapping
    {
        internal Mapping(string key, IPEndPoint inside, int port)
        {
            Key = key;
            Inside = inside;
            Port = port;
        }

        internal string Key { get; }

        internal IPEndPoint Inside { get; }

        internal int Port { get; }

        internal long LastUsed { get; set; }

        internal HashSet<IPAddress> Addresses { get; } = new();

        internal HashSet<string> EndPoints { get; } = new();
    }
}

/// <summary>
/// Routes UDP paths through sites and NATs: up through each gateway, translating the source, until the destination's
/// site, then down through each NAT on the way in, filtering. Relayed paths never reach the wire; the NAT layer wraps
/// them first. Anything without a route is lost, as on any network.
/// </summary>
internal sealed class NatRouter : IVirtualRouter
{
    private const int MaximumHops = 16;
    private readonly Dictionary<VirtualNode, (SimulatedSite Site, IPEndPoint Address)> homes = new();

    internal SimulatedSite Internet { get; } = new("Internet", null);

    internal int Undeliverable { get; private set; }

    /// <summary>Gets or sets a filter that loses datagrams by the address they were sent to, as a network blocking one port would.</summary>
    internal Func<IPEndPoint, bool>? Blackhole { get; set; }

    /// <summary>Gets how many datagrams <see cref="Blackhole"/> lost.</summary>
    internal int Blackholed { get; private set; }

    internal void Place(VirtualNode node, SimulatedSite site, IPEndPoint address)
    {
        homes[node] = (site, address);
        site.Hosts[NatAddresses.Unmap(address.Address)] = node;
    }

    internal IPEndPoint AddressOf(VirtualNode node) => homes[node].Address;

    public (VirtualNode To, TransportPath Source, TransportPath Destination)? Route(VirtualNode from, TransportPath? source, TransportPath destination, long now)
    {
        if (!homes.TryGetValue(from, out var home) || !UdpPaths.TryGetEndPoint(destination, out var target))
        {
            Undeliverable++;
            return null;
        }

        var at = source != null && UdpPaths.TryGetEndPoint(source, out var claimed) ? Plain(claimed) : home.Address;
        var to = Plain(target);
        if (Blackhole != null && Blackhole(to))
        {
            Blackholed++;
            return Lose();
        }

        var site = home.Site;
        SimulatedNat? cameThrough = null;
        for (var hop = 0; hop < MaximumHops; hop++)
        {
            if (site.Hosts.TryGetValue(to.Address, out var node)) return (node, UdpPaths.For(at), UdpPaths.For(to));
            if (site.Nats.TryGetValue(to.Address, out var nat))
            {
                // In through a NAT: its own inside traffic hairpins only when the NAT allows it.
                if (nat.BlocksUdp || (ReferenceEquals(nat, cameThrough) && !nat.Hairpin)) return Lose();
                var inside = nat.Inbound(to.Port, at, now);
                if (inside == null) return Lose();
                to = inside;
                site = nat.Inside;
                cameThrough = null;
                continue;
            }

            var gateway = site.Gateway;
            if (gateway == null || gateway.BlocksUdp) return Lose();
            at = gateway.Outbound(at, to, now);
            cameThrough = gateway;
            site = gateway.Outside;
        }

        return Lose();
    }

    private (VirtualNode, TransportPath, TransportPath)? Lose()
    {
        Undeliverable++;
        return null;
    }

    private static IPEndPoint Plain(IPEndPoint endpoint) => new(NatAddresses.Unmap(endpoint.Address), endpoint.Port);
}
