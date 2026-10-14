using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.NatTraversal.Ice;
using TopiaForge.Multiplayer.NatTraversal.Stun;
using TopiaForge.Multiplayer.NatTraversal.Turn;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal
{
    /// <summary>
    /// The relay. Relay credentials last ten minutes, so the next one is minted ninety seconds before they lapse: a new
    /// allocation comes up on another server address when there is one (make before break), or replaces the old one
    /// on the same address (break before make), and every session moves its connection over through the transport's
    /// path validation. A released allocation's pairs are forgotten once no connection runs through it.
    /// </summary>
    internal sealed partial class NatAgent
    {
        private const long RetireMarginMicros = 5_000_000;
        private const long RenewRetryMicros = 15_000_000;
        private const long ReallocateMicros = 30_000_000;
        private const long ReplaceDelayMicros = 500_000;
        private const long RetiredBaseMicros = 30_000_000;

        private readonly List<KeyValuePair<LocalBase, long>> retiredBases = new List<KeyValuePair<LocalBase, long>>();
        private Task<IceServerSet?>? renewal;
        private Task<IPAddress[]>? renewalLookup;
        private IceServerSet? renewed;
        private TurnClient? relay;
        private TurnClient? retiring;
        private LocalBase? relayBase;
        private LocalBase? retiringBase;
        private IceCandidate? relayCandidate;
        private int allocations;
        private int rotations;
        private long credentialExpiresAt = long.MaxValue;
        private long nextRenewalAt = long.MaxValue;
        private long retireBy = long.MaxValue;
        private long nextReallocateAt;
        private long relayStartAt;

        internal bool RelayUp => relay != null && relay.State == TurnAllocationState.Ready;

        internal TurnClient? Relay => relay;

        internal LocalBase? RelayBase => relayBase;

        /// <summary>Gives the path from a base to a remote address, or null when that base cannot reach it.</summary>
        internal TransportPath? PathFor(LocalBase local, IPEndPoint remote)
        {
            if (!local.IsRelay) return options.RelayOnly ? null : UdpPaths.For(remote);
            var client = RelayNumbered(local.Allocation);
            if (client == null || client.RelayedAddress == null || !RelayablePeer(remote.Address)) return null;
            if (NatAddresses.Unmap(remote.Address).AddressFamily != client.RelayedAddress.Address.AddressFamily) return null;
            return client.PathTo(remote);
        }

        /// <summary>Gets whether a TURN relay may send to <paramref name="address"/>: a public address, or loopback in single-machine tests.</summary>
        internal bool RelayablePeer(IPAddress address) => NatAddresses.IsPublic(address) || (options.AcceptLoopback && IPAddress.IsLoopback(address));

        /// <summary>Asks the current allocation to let <paramref name="candidate"/> reach this side.</summary>
        internal void PermitRemote(IceCandidate candidate, long now)
        {
            if (relay != null && relay.State == TurnAllocationState.Ready && RelayablePeer(candidate.EndPoint.Address)) relay.Permit(candidate.EndPoint.Address, now);
        }

        private void StartRelay(TransportPath server, IceServerSet set, long now)
        {
            var client = new TurnClient(++allocations, server, set.Username!, set.Credential!, stun);
            relay = client;
            client.Start(now);
        }

        /// <summary>A fresh allocation is ready: it becomes a base in every session, its peers are permitted, and it is trickled.</summary>
        private void OnRelayReady(long now)
        {
            var client = relay!;
            relayBase = LocalBase.Relay(client.Allocation, IceCandidate.ComputePriority(IceCandidateKind.Relayed, 65535));
            relayCandidate = new IceCandidate(IceCandidateKind.Relayed, client.RelayedAddress!, relayBase.Priority, candidateOrder++);
            if (!options.RelayOnly && client.MappedAddress != null) AddReflexive(client.MappedAddress, 65533);
            if (gathered && gatheringOutcome != NatGathering.Complete) gatheringOutcome = NatGathering.Complete;
            Report(NatDiagnosticKind.RelayReady, "The relay is ready.");
            foreach (var session in sessions)
            {
                foreach (var remote in session.Checker.RemoteCandidates) PermitRemote(remote, now);
                session.AddRelayBase(relayBase, relayCandidate, now);
            }
        }

        private void PollRelays(long now)
        {
            if (relay != null && relay.State == TurnAllocationState.Idle && now >= relayStartAt) relay.Start(now);
            PollRelay(relay, now);
            PollRelay(retiring, now);
            if (relay != null && relay.State == TurnAllocationState.Failed && relayBase != null)
            {
                // The allocation was lost mid-session: its base retires, and a new one is tried with the same credential,
                // at most once every 30 seconds so a server that keeps refusing is never hammered.
                RetireBase(relayBase);
                retiredBases.Add(new KeyValuePair<LocalBase, long>(relayBase, now + RetiredBaseMicros));
                relayBase = null;
                relayCandidate = null;
                Report(NatDiagnosticKind.RelayFailed, "The relay allocation was lost.");
                if (servers != null && now >= nextReallocateAt && now < credentialExpiresAt - RetireMarginMicros)
                {
                    nextReallocateAt = now + ReallocateMicros;
                    StartRelay(relay.Server, servers, now);
                }
                else
                {
                    relay = null;
                }
            }
        }

        private void PollRelay(TurnClient? client, long now)
        {
            if (client == null) return;
            client.Poll(now);
            if (client.TryWriteKeepalive(now, scratch, out var length)) SendRaw(client.Server, scratch, length);
        }

        private void PollRotation(NetworkStack stack, long now)
        {
            if (plan?.Renew != null && renewal == null && renewed == null && renewalLookup == null && RelayUp && now >= nextRenewalAt)
            {
                nextRenewalAt = now + RenewRetryMicros;
                try
                {
                    renewal = plan.Renew();
                }
                catch (Exception)
                {
                    renewal = null;
                }
            }

            if (renewal != null && renewal.IsCompleted)
            {
                if (renewal.Status == TaskStatus.RanToCompletion)
                {
                    renewed = renewal.Result;
                }
                else
                {
                    Observe(renewal);
                    renewed = null;
                }

                renewal = null;
                if (renewed != null && renewed.HasRelay) renewalLookup = Lookup(renewed.TurnServers[0].Host);
                if (renewalLookup == null) renewed = null;
            }

            if (renewalLookup != null && renewalLookup.IsCompleted)
            {
                var addresses = Usable(renewalLookup);
                renewalLookup = null;
                if (addresses.Count > 0 && renewed != null) Rotate(addresses, now);
                renewed = null;
            }

            if (retiring != null && (now >= retireBy || !AllocationInUse(stack, retiring.Allocation)))
            {
                retiring.Release(now);
                if (retiringBase != null) retiredBases.Add(new KeyValuePair<LocalBase, long>(retiringBase, now + RetiredBaseMicros));
                retiring = null;
                retiringBase = null;
            }

            // A released base's pairs go once no connection runs through its allocation any more, or after a grace.
            for (var index = retiredBases.Count - 1; index >= 0; index--)
            {
                var retired = retiredBases[index];
                if (now < retired.Value && AllocationInUse(stack, retired.Key.Allocation)) continue;
                foreach (var session in sessions) session.Checker.RemoveBase(retired.Key);
                retiredBases.RemoveAt(index);
            }
        }

        /// <summary>Moves to a new allocation under the renewed credential, on another server address when one exists.</summary>
        private void Rotate(List<IPAddress> addresses, long now)
        {
            var next = renewed!;
            var current = relay;
            TransportPath? target = null;
            foreach (var server in next.TurnServers)
            {
                foreach (var address in addresses)
                {
                    var path = UdpPaths.For(new IPEndPoint(address, server.Port));
                    if (current == null || !path.Equals(current.Server))
                    {
                        target = path;
                        break;
                    }
                }

                if (target != null) break;
            }

            var sameServer = target == null;
            if (sameServer) target = current?.Server ?? UdpPaths.For(new IPEndPoint(addresses[0], next.TurnServers[0].Port));
            if (current != null && relayBase != null)
            {
                RetireBase(relayBase);
                if (retiring != null) retiring.Release(now);
                if (sameServer)
                {
                    // One allocation per 5-tuple: the old one must go before the new one can come up. Its pairs stay a
                    // while, so each session can still move its connection from there once the new allocation answers.
                    current.Release(now);
                    retiredBases.Add(new KeyValuePair<LocalBase, long>(relayBase, now + RetiredBaseMicros));
                    retiring = null;
                    retiringBase = null;
                }
                else
                {
                    retiring = current;
                    retiringBase = relayBase;
                    retireBy = credentialExpiresAt - RetireMarginMicros;
                }
            }

            servers = next;
            relayBase = null;
            relayCandidate = null;

            // On the same 5-tuple the release must reach the server before the new Allocate, or it draws a 437.
            relay = new TurnClient(++allocations, target!, next.Username!, next.Credential!, stun);
            relayStartAt = sameServer ? now + ReplaceDelayMicros : now;
            if (!sameServer) relay.Start(now);
            credentialExpiresAt = now + (next.TtlSeconds * 1_000_000L);
            nextRenewalAt = credentialExpiresAt - options.RenewMarginMicros;
            rotations++;
            Report(NatDiagnosticKind.RelayRotated, "The relay moved to a fresh credential.");
        }

        private void RetireBase(LocalBase local)
        {
            foreach (var session in sessions) session.Checker.RetireBase(local);
        }

        /// <summary>Gets whether the transport still has a connection running through allocation <paramref name="allocation"/>.</summary>
        private static bool AllocationInUse(NetworkStack stack, int allocation) =>
            stack.Endpoint.CountConnections(path => TurnRelayPath.TryGetAllocation(path, out var number) && number == allocation) > 0;

        private void ReleaseRelays(long now)
        {
            relay?.Release(now);
            retiring?.Release(now);
        }

        private void ForgetPermissions(NatSession leaving)
        {
            foreach (var remote in leaving.Checker.RemoteCandidates)
            {
                var shared = false;
                foreach (var session in sessions)
                {
                    if (ReferenceEquals(session, leaving)) continue;
                    foreach (var other in session.Checker.RemoteCandidates)
                    {
                        if (NatAddresses.SameAddress(other.EndPoint.Address, remote.EndPoint.Address)) shared = true;
                    }
                }

                if (shared) continue;
                relay?.Forget(remote.EndPoint.Address);
                retiring?.Forget(remote.EndPoint.Address);
            }
        }

        private TurnClient? RelayAt(TransportPath from)
        {
            if (relay != null && relay.Server.Equals(from)) return relay;
            if (retiring != null && retiring.Server.Equals(from)) return retiring;
            return null;
        }

        private TurnClient? RelayNumbered(int allocation)
        {
            if (relay != null && relay.Allocation == allocation) return relay;
            if (retiring != null && retiring.Allocation == allocation) return retiring;
            return null;
        }

        private LocalBase? BaseNumbered(int allocation)
        {
            if (relayBase != null && relayBase.Allocation == allocation) return relayBase;
            if (retiringBase != null && retiringBase.Allocation == allocation) return retiringBase;
            return null;
        }

        private long RelayTimeout()
        {
            var next = long.MaxValue;
            if (relay != null) next = Math.Min(next, relay.NextTimeoutMicros());
            if (retiring != null) next = Math.Min(next, Math.Min(retiring.NextTimeoutMicros(), retireBy));
            foreach (var retired in retiredBases) next = Math.Min(next, retired.Value);
            if (relay != null && relay.State == TurnAllocationState.Ready && relayBase == null) next = currentTime;
            if (relay != null && relay.State == TurnAllocationState.Idle) next = Math.Min(next, relayStartAt);
            if (plan?.Renew != null && RelayUp && renewal == null && renewed == null && renewalLookup == null) next = Math.Min(next, nextRenewalAt);
            if (renewal != null || renewalLookup != null) next = Math.Min(next, currentTime + PendingTaskPollMicros);
            return next;
        }
    }
}
