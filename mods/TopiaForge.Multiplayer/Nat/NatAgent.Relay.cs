using System;
using System.Collections.Generic;
using System.Net;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.NatTraversal.Ice;
using TopiaForge.Multiplayer.NatTraversal.Stun;
using TopiaForge.Multiplayer.NatTraversal.Turn;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal
{
    /// <summary>
    /// The relay's lifecycle. One allocation is in use, or on its way, whenever the relay is wanted; while a rotation moves
    /// connections off the one before, that one stays up too. A relay that is wanted but not ready always has its next
    /// attempt scheduled: after a failure it tries again with backoff, one second doubling to thirty, on the server
    /// address that last worked. An allocation that is lost is released first, since the server may still hold its
    /// 5-tuple; a release is a transaction of its own, and nothing allocates on a 5-tuple until the release there settled.
    /// </summary>
    internal sealed partial class NatAgent
    {
        internal const long ReallocateFirstMicros = 1_000_000;
        internal const long ReallocateLongestMicros = 30_000_000;
        private const long RetireMarginMicros = 5_000_000;
        private const long RetiredBaseMicros = 30_000_000;
        private const int MaximumRemembered = 8;

        private readonly List<KeyValuePair<LocalBase, long>> retiredBases = new List<KeyValuePair<LocalBase, long>>();
        private readonly List<TurnClient> releasing = new List<TurnClient>();
        private List<RelayTarget> relayTargets = new List<RelayTarget>();
        private TurnClient? relay;
        private TurnClient? retiring;
        private LocalBase? relayBase;
        private LocalBase? retiringBase;
        private IceCandidate? relayCandidate;
        private TransportPath? lastWorked;
        private int allocations;
        private int rotations;
        private long retireBy = long.MaxValue;
        private long relayStartAt;
        private long reallocateDelay = ReallocateFirstMicros;
        private long relayChangeSince = long.MinValue / 2;
        private int relayFailures;
        private TurnFailure lastRelayFailure;
        private bool relayHeard;
        private bool relayRefused;

        internal bool RelayUp => relay != null && relay.State == TurnAllocationState.Ready;

        internal TurnClient? Relay => relay;

        internal LocalBase? RelayBase => relayBase;

        /// <summary>Gets how many allocations this agent started, counting every retry and rotation.</summary>
        internal int AllocationsStarted => allocations;

        /// <summary>
        /// Gets whether the relay should be up: a session runs with relay credentials, the host's or a join that has not
        /// ended, and the service did not refuse the credential with no way to mint another.
        /// </summary>
        internal bool RelayWanted => !disposed && Role != NatRole.None && servers != null && servers.HasRelay && !relayRefused && (Role == NatRole.Host || LiveJoin() != null);

        /// <summary>Gives the path from a base to a remote address, or null when that base cannot reach it.</summary>
        internal TransportPath? PathFor(LocalBase local, IPEndPoint remote)
        {
            if (!local.IsRelay) return RelayOnly ? null : UdpPaths.For(remote);
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

        /// <summary>Puts the next allocation in place under <paramref name="set"/>'s credential; it starts at <paramref name="startAt"/>, once no release on its 5-tuple is settling.</summary>
        private void ScheduleRelay(TransportPath target, IceServerSet set, long startAt)
        {
            relay = new TurnClient(++allocations, target, set.Username!, set.Credential!, stun);
            relayStartAt = startAt;
        }

        /// <summary>A fresh allocation is ready: it becomes a base in every session, its peers are permitted, and it is trickled.</summary>
        private void OnRelayReady(long now)
        {
            var client = relay!;
            lastWorked = client.Server;
            relayHeard = true;
            relayFailures = 0;
            reallocateDelay = ReallocateFirstMicros;
            relayBase = LocalBase.Relay(client.Allocation, IceCandidate.ComputePriority(IceCandidateKind.Relayed, 65535));
            relayCandidate = new IceCandidate(IceCandidateKind.Relayed, client.RelayedAddress!, relayBase.Priority, candidateOrder++);
            if (!RelayOnly && client.MappedAddress != null) AddReflexive(client.MappedAddress, 65533);
            if (gathered && gatheringOutcome != NatGathering.Complete) gatheringOutcome = NatGathering.Complete;
            Report(NatDiagnosticKind.RelayReady, "The relay is ready.");
            foreach (var session in sessions)
            {
                foreach (var remote in session.Checker.RemoteCandidates) PermitRemote(remote, now);
                session.AddRelayBase(relayBase, relayCandidate, now);
            }
        }

        private void CatchRelayReady(long now)
        {
            if (relay != null && relay.State == TurnAllocationState.Ready && relayBase == null) OnRelayReady(now);
        }

        private void PollRelays(long now)
        {
            PollReleases();
            if (relay != null && relay.State == TurnAllocationState.Idle && now >= relayStartAt && !ReleasePending(relay.Server)) relay.Start(now);
            PollRelay(relay, now);
            PollRelay(retiring, now);
            CatchRelayReady(now);
            if (relay != null && relay.State == TurnAllocationState.Failed) OnRelayFailed(now);

            // The allocation being replaced lapsed before every connection moved off it: it goes now.
            if (retiring != null && retiring.State == TurnAllocationState.Failed) DropRetiring(now);
        }

        private void PollRelay(TurnClient? client, long now)
        {
            if (client == null) return;
            client.Poll(now);
            if (client.TryWriteKeepalive(now, scratch, out var length)) SendRaw(client.Server, scratch, length);
        }

        /// <summary>
        /// An allocation failed, on its way up or after it came up. It is released, the next one is scheduled with
        /// backoff on the address that last worked, and a rotation that could not come up elsewhere falls back to that
        /// address by releasing the allocation there first. A credential the server refused waits for a fresh one.
        /// </summary>
        private void OnRelayFailed(long now)
        {
            var failed = relay!;
            relay = null;
            relayHeard |= failed.Answered;
            relayFailures++;
            lastRelayFailure = failed.Failure;
            if (relayBase != null && relayBase.Allocation == failed.Allocation)
            {
                // It was up and is lost: its base retires, and its pairs go once nothing runs through them.
                RetireBase(relayBase);
                retiredBases.Add(new KeyValuePair<LocalBase, long>(relayBase, now + RetiredBaseMicros));
                relayBase = null;
                relayCandidate = null;
                relayChangeSince = now;
                Report(NatDiagnosticKind.RelayFailed, "The relay allocation was lost.");
            }

            // The server may still hold what this client allocated; releasing it frees the 5-tuple for the next attempt,
            // and a 437 means an earlier release there went unanswered, so that one goes again.
            Release(failed, now);
            if (failed.Failure == TurnFailure.AllocationMismatch) ReleaseAgainOn(failed.Server, now);
            if (!RelayWanted) return;
            if (failed.Failure == TurnFailure.Unauthorized)
            {
                // The credential itself was refused: only a fresh one can bring the relay back.
                if (plan?.Renew != null) nextRenewalAt = Math.Min(nextRenewalAt, now);
                else relayRefused = true;
                return;
            }

            var target = RetryTarget(failed);
            var startAt = now + reallocateDelay;
            reallocateDelay = Math.Min(reallocateDelay * 2, ReallocateLongestMicros);
            if (retiring != null && target.Equals(retiring.Server))
            {
                // A rotation could not come up on another server address, so it falls back to the one that worked. A
                // 5-tuple holds one allocation: the old one there is released, and the new one takes its place as soon as
                // the release settles, while each session's pairs through the old one stay a while to move from.
                RememberFailedTarget(failed.Server, now);
                DropRetiring(now);
                startAt = now;
            }

            ScheduleRelay(target, servers!, startAt);
        }

        /// <summary>
        /// Where the next attempt goes: the server address that last worked; before any has, the same address after an
        /// answer, or the next one after silence, since an address that never answers may be one the network blocks.
        /// </summary>
        private TransportPath RetryTarget(TurnClient failed)
        {
            if (lastWorked != null && IndexOfTarget(lastWorked) >= 0) return lastWorked;
            var index = IndexOfTarget(failed.Server);
            if (failed.Answered || index < 0 || relayTargets.Count == 0) return failed.Server;
            return relayTargets[(index + 1) % relayTargets.Count].Path;
        }

        private int IndexOfTarget(TransportPath path)
        {
            for (var index = 0; index < relayTargets.Count; index++)
            {
                if (relayTargets[index].Path.Equals(path)) return index;
            }

            return -1;
        }

        private void RetireBase(LocalBase local)
        {
            foreach (var session in sessions) session.Checker.RetireBase(local);
        }

        /// <summary>The allocation a rotation replaced goes: it is released, and its pairs stay a while for connections still on them.</summary>
        private void DropRetiring(long now)
        {
            if (retiring == null) return;
            Release(retiring, now);
            if (retiringBase != null) retiredBases.Add(new KeyValuePair<LocalBase, long>(retiringBase, now + RetiredBaseMicros));
            retiring = null;
            retiringBase = null;
            retireBy = long.MaxValue;
        }

        /// <summary>Releases an allocation, keeping track of a release in flight, or one the server never answered, on its 5-tuple.</summary>
        private void Release(TurnClient client, long now)
        {
            client.Release(now);
            if ((client.State != TurnAllocationState.Releasing && !client.ReleaseUnconfirmed) || releasing.Contains(client)) return;
            if (releasing.Count >= MaximumRemembered) releasing.RemoveAt(0);
            releasing.Add(client);
        }

        private void ReleaseAgainOn(TransportPath server, long now)
        {
            foreach (var client in releasing)
            {
                if (client.Server.Equals(server)) client.ReleaseAgain(now);
            }
        }

        private void PollReleases()
        {
            for (var index = releasing.Count - 1; index >= 0; index--)
            {
                var client = releasing[index];
                if (client.State == TurnAllocationState.Released && !client.ReleaseUnconfirmed) releasing.RemoveAt(index);
            }
        }

        /// <summary>Gets whether a release on <paramref name="server"/>'s 5-tuple is still waiting for its answer.</summary>
        private bool ReleasePending(TransportPath server)
        {
            foreach (var client in releasing)
            {
                if (client.State == TurnAllocationState.Releasing && client.Server.Equals(server)) return true;
            }

            return false;
        }

        private bool ReleasesInFlight()
        {
            foreach (var client in releasing)
            {
                if (client.State == TurnAllocationState.Releasing) return true;
            }

            return false;
        }

        /// <summary>Gets whether the transport still has a connection running through allocation <paramref name="allocation"/>.</summary>
        private static bool AllocationInUse(NetworkStack stack, int allocation) =>
            stack.Endpoint.CountConnections(path => TurnRelayPath.TryGetAllocation(path, out var number) && number == allocation) > 0;

        /// <summary>Stopping: every allocation is released, and the releases settle on their own while turns still run.</summary>
        private void ReleaseRelays(long now)
        {
            if (relay != null) Release(relay, now);
            if (retiring != null) Release(retiring, now);
            relay = null;
            retiring = null;
            relayBase = null;
            retiringBase = null;
            relayCandidate = null;
            retireBy = long.MaxValue;
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

        /// <summary>Gets whether a path leads to one of this side's TURN servers: the allocation in use, the one it replaces, or one still being released.</summary>
        private bool IsRelayServer(TransportPath path)
        {
            if (RelayAt(path) != null) return true;
            foreach (var client in releasing)
            {
                if (client.Server.Equals(path)) return true;
            }

            return false;
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
            if (relay != null)
            {
                next = Math.Min(next, relay.NextTimeoutMicros());
                if (relay.State == TurnAllocationState.Idle && !ReleasePending(relay.Server)) next = Math.Min(next, relayStartAt);
                if (relay.State == TurnAllocationState.Ready && relayBase == null) next = currentTime;
            }

            if (retiring != null) next = Math.Min(next, Math.Min(retiring.NextTimeoutMicros(), retireBy));
            foreach (var retired in retiredBases) next = Math.Min(next, retired.Value);
            return Math.Min(next, RenewalTimeout());
        }
    }
}
