using System;
using System.Net;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.NatTraversal.PortMapping;

namespace TopiaForge.Multiplayer.NatTraversal
{
    /// <summary>
    /// What a join flow calls when the session is reached through the control plane: instead of
    /// <see cref="INetworkControl.Connect"/> on a known path, it starts a NAT join and polls the same kind of
    /// <see cref="ConnectAttempt"/>. The NAT layer finds the path itself, so no address ever reaches the main thread.
    /// </summary>
    internal sealed class NatJoin : IDisposable
    {
        private readonly INetworkControl network;
        private readonly NatAgent agent;
        private int stopped;

        private NatJoin(INetworkControl network, NatAgent agent, ConnectAttempt attempt)
        {
            this.network = network;
            this.agent = agent;
            Attempt = attempt;
        }

        /// <summary>Gets the connection attempt: established with its link, or failed; <see cref="Status"/> says why.</summary>
        internal ConnectAttempt Attempt { get; }

        internal NatStatus Status => agent.Status;

        /// <param name="network">The network built on <paramref name="nat"/>'s socket, with its service attached.</param>
        /// <param name="expectedHostKey">The host's transport key from its verified descriptor; the handshake refuses any other.</param>
        internal static NatJoin Start(INetworkControl network, NatPlumbing nat, NatSessionPlan plan, byte[]? expectedHostKey)
        {
            if (network == null) throw new ArgumentNullException(nameof(network));
            if (nat == null) throw new ArgumentNullException(nameof(nat));
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var attempt = new ConnectAttempt();
            var key = expectedHostKey == null ? null : (byte[])expectedHostKey.Clone();
            var agent = nat.Agent;
            agent.Adopt(attempt);
            network.Post((_, now) =>
            {
                try
                {
                    agent.StartJoin(plan, attempt, key, now);
                }
                catch (InvalidOperationException)
                {
                    attempt.Fail(Transport.TransportCloseReason.Local);
                }
            });
            return new NatJoin(network, agent, attempt);
        }

        /// <summary>Says goodbye and releases the relay. The connection, if one was made, closes with the session as usual.</summary>
        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref stopped, 1) != 0) return;
            network.Post((_, now) => agent.Stop(now));
        }
    }

    /// <summary>
    /// What a host flow calls once it has registered with the control plane: peers that join through signaling then reach
    /// this host's transport as ordinary inbound links, on whatever path the NAT layer found. A host that is not relay-only
    /// also maps its port on the router, best effort, for as long as it hosts.
    /// </summary>
    internal sealed class NatHost : IDisposable
    {
        private readonly INetworkControl network;
        private readonly NatAgent agent;
        private readonly PortMapper? mapper;
        private int stopped;

        private NatHost(INetworkControl network, NatAgent agent, PortMapper? mapper)
        {
            this.network = network;
            this.agent = agent;
            this.mapper = mapper;
        }

        internal NatStatus Status => agent.Status;

        internal PortMappingState PortMapping => mapper?.State ?? PortMappingState.Off;

        /// <param name="mapPorts">Whether to try UPnP and NAT-PMP; ignored, and nothing is mapped, for a relay-only host.</param>
        /// <param name="mapper">The port mapper to use; null builds the production one.</param>
        internal static NatHost Start(INetworkControl network, NatPlumbing nat, NatSessionPlan plan, bool mapPorts, PortMapper? mapper = null)
        {
            if (network == null) throw new ArgumentNullException(nameof(network));
            if (nat == null) throw new ArgumentNullException(nameof(nat));
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var agent = nat.Agent;
            network.Post((_, now) =>
            {
                try
                {
                    agent.StartHost(plan, now);
                }
                catch (InvalidOperationException)
                {
                    // Already running a session: the earlier one stands.
                }
            });

            PortMapper? active = null;
            if (mapPorts && !agent.Options.RelayOnly && plan.LocalPort > 0)
            {
                active = mapper ?? PortMapper.CreateDefault();
                active.Start(plan.LocalPort, endpoint => network.Post((_, now) => agent.AddMappedCandidate(endpoint, now)));
            }

            return new NatHost(network, agent, active);
        }

        /// <summary>Stops hosting through the NAT layer: goodbyes, relay release, and the port mappings are deleted.</summary>
        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref stopped, 1) != 0) return;
            network.Post((_, now) => agent.Stop(now));
            mapper?.Stop();
        }
    }
}
