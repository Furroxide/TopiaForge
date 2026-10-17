using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal.Turn
{
    /// <summary>One TURN server address the relay can allocate on: the path to it and its address family.</summary>
    internal readonly struct RelayTarget
    {
        internal RelayTarget(TransportPath path, AddressFamily family)
        {
            Path = path;
            Family = family;
        }

        internal TransportPath Path { get; }

        internal AddressFamily Family { get; }

        public override string ToString() => "TURN server address";
    }

    /// <summary>
    /// The TURN servers of one credential set, being resolved: each server's own host name is looked up off the network
    /// thread, once per distinct name, and the agent polls until every lookup has finished. The names are the ones the
    /// control plane chose, so nothing else is ever looked up. No address in here is ever printed.
    /// </summary>
    internal sealed class RelayServers
    {
        internal const int AddressesPerName = 4;

        private readonly List<string> hosts = new List<string>();
        private readonly List<Task<IPAddress[]>?> lookups = new List<Task<IPAddress[]>?>();

        internal RelayServers(IceServerSet set, Func<string, Task<IPAddress[]>?> lookup)
        {
            Set = set ?? throw new ArgumentNullException(nameof(set));
            if (lookup == null) throw new ArgumentNullException(nameof(lookup));
            foreach (var server in set.TurnServers)
            {
                if (hosts.Contains(server.Host)) continue;
                hosts.Add(server.Host);
                lookups.Add(lookup(server.Host));
            }
        }

        internal IceServerSet Set { get; }

        /// <summary>Lets go of the lookups: whichever fail later are observed, never read.</summary>
        internal void Abandon()
        {
            foreach (var lookup in lookups) NatAgent.Abandon(lookup);
        }

        /// <summary>Gets whether every lookup has finished, one way or the other.</summary>
        internal bool IsCompleted
        {
            get
            {
                foreach (var lookup in lookups)
                {
                    if (lookup != null && !lookup.IsCompleted) return false;
                }

                return true;
            }
        }

        /// <summary>
        /// The server addresses, in the order a relay tries them: the address family that worked last first, IPv4 when
        /// none has worked yet, then the other family; within a family the set's servers in the control plane's order,
        /// each with its own name's addresses in the order DNS gave them, at most four per name.
        /// </summary>
        internal List<RelayTarget> Targets(AddressFamily? preferred, bool acceptLoopback)
        {
            var first = preferred ?? AddressFamily.InterNetwork;
            var resolved = new List<List<IPAddress>>(hosts.Count);
            for (var index = 0; index < hosts.Count; index++) resolved.Add(Usable(lookups[index], acceptLoopback));
            var targets = new List<RelayTarget>();
            foreach (var family in new[] { first, first == AddressFamily.InterNetwork ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork })
            {
                foreach (var server in Set.TurnServers)
                {
                    foreach (var address in resolved[hosts.IndexOf(server.Host)])
                    {
                        if (address.AddressFamily != family) continue;
                        var path = UdpPaths.For(new IPEndPoint(address, server.Port));
                        if (!Contains(targets, path)) targets.Add(new RelayTarget(path, family));
                    }
                }
            }

            return targets;
        }

        /// <summary>Gets the family of a UDP path's address: IPv4 for a mapped one.</summary>
        internal static AddressFamily FamilyOf(TransportPath path)
        {
            var key = path.KeyBytes;
            if (key.Length != UdpPaths.KeySize || key[0] != UdpPaths.UdpKeyType) return AddressFamily.Unspecified;
            for (var index = 1; index <= 10; index++)
            {
                if (key[index] != 0) return AddressFamily.InterNetworkV6;
            }

            return key[11] == 0xFF && key[12] == 0xFF ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        }

        private static bool Contains(List<RelayTarget> targets, TransportPath path)
        {
            foreach (var target in targets)
            {
                if (target.Path.Equals(path)) return true;
            }

            return false;
        }

        /// <summary>A lookup's usable addresses, at most four; none when it failed. A failed lookup's exception is observed, never read.</summary>
        private static List<IPAddress> Usable(Task<IPAddress[]>? lookup, bool acceptLoopback)
        {
            var result = new List<IPAddress>();
            if (lookup == null) return result;
            if (lookup.Status != TaskStatus.RanToCompletion)
            {
                _ = lookup.Exception;
                return result;
            }

            foreach (var raw in lookup.Result ?? Array.Empty<IPAddress>())
            {
                if (raw == null) continue;
                var address = NatAddresses.Unmap(raw);
                if (NatAddresses.IsUsable(address) || (acceptLoopback && IPAddress.IsLoopback(address))) result.Add(address);
                if (result.Count == AddressesPerName) break;
            }

            return result;
        }
    }
}
