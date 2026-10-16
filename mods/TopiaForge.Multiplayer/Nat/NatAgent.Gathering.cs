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
    /// Gathering. Host candidates come from this machine's own addresses, a server-reflexive one from a Binding request
    /// to the STUN server, and the relay candidate from the TURN allocation, which also reports the address the relay
    /// saw. Relay-only gathers the allocation alone: it never lists its addresses or asks a STUN server anything.
    /// Gathering settles once every server answered or timed out, or after six seconds; no answer at all means UDP is
    /// blocked. Server names resolve off the network thread, and the agent polls the lookups.
    /// </summary>
    internal sealed partial class NatAgent
    {
        private const long PendingTaskPollMicros = 2_000;
        private const int MaximumHostCandidates = 8;

        private readonly List<IceCandidate> hostCandidates = new List<IceCandidate>();
        private readonly List<IceCandidate> reflexiveCandidates = new List<IceCandidate>();
        private BindingOwner? bindingOwner;
        private NatSessionPlan? plan;
        private IceServerSet? servers;
        private Task<IPAddress[]>? stunLookup;
        private RelayServers? initialResolving;
        private bool stunAttempted;
        private bool turnAttempted;
        private bool bindingSettled;
        private bool bindingAnswered;
        private bool relayLookupFailed;
        private bool gathered;
        private long gatherDeadline = long.MaxValue;
        private NatGathering gatheringOutcome = NatGathering.Idle;
        private LocalBase? socketBase;
        private int candidateOrder;

        /// <summary>Gets whether gathering has settled, so descriptions may go out.</summary>
        internal bool Gathered => gathered;

        internal NatGathering GatheringOutcome => gatheringOutcome;

        internal LocalBase? SocketBase => socketBase;

        /// <summary>Gets every candidate this side has, before the privacy rules decide what may be signaled.</summary>
        internal IEnumerable<IceCandidate> LocalCandidates()
        {
            foreach (var candidate in hostCandidates) yield return candidate;
            foreach (var candidate in reflexiveCandidates) yield return candidate;
            if (relayCandidate != null) yield return relayCandidate;
        }

        /// <summary>Adds a candidate a port mapping produced: a server-reflexive address of this host's own making.</summary>
        internal void AddMappedCandidate(IPEndPoint endpoint, long now)
        {
            if (endpoint == null || RelayOnly || Role != NatRole.Host || !NatAddresses.IsPublic(endpoint.Address)) return;
            var candidate = AddReflexive(endpoint, 65535);
            if (candidate == null) return;
            foreach (var session in sessions) session.TrickleLocal(candidate);
        }

        /// <summary>Forgets the last session's candidates and lookups; a lookup still running is left to finish unobserved.</summary>
        private void ResetGathering()
        {
            Abandon(stunLookup);
            initialResolving?.Abandon();
            hostCandidates.Clear();
            reflexiveCandidates.Clear();
            bindingOwner = null;
            plan = null;
            servers = null;
            stunLookup = null;
            initialResolving = null;
            stunAttempted = false;
            turnAttempted = false;
            bindingSettled = false;
            bindingAnswered = false;
            relayLookupFailed = false;
            gathered = false;
            gatherDeadline = long.MaxValue;
            gatheringOutcome = NatGathering.Idle;
            socketBase = null;
        }

        private void BeginGathering(NatSessionPlan sessionPlan, long now)
        {
            plan = sessionPlan;
            servers = sessionPlan.Servers;
            bindingOwner = new BindingOwner(this);
            gatheringOutcome = NatGathering.Gathering;
            gatherDeadline = now + options.GatheringTimeoutMicros;
            credentialExpiresAt = now + (servers.TtlSeconds * 1_000_000L);
            if (sessionPlan.Renew != null) nextRenewalAt = credentialExpiresAt - options.RenewMarginMicros;
            if (!RelayOnly)
            {
                socketBase = LocalBase.Socket(IceCandidate.ComputePriority(IceCandidateKind.Host, 65535));
                AddHostCandidates(sessionPlan.LocalAddresses, sessionPlan.LocalPort);
                if (servers.StunServers.Count > 0)
                {
                    stunAttempted = true;
                    stunLookup = Lookup(servers.StunServers[0].Host);
                    if (stunLookup == null) bindingSettled = true;
                }
            }

            if (servers.HasRelay)
            {
                // Every TURN server's own name is looked up, so a rotation or a retry can move to another address.
                turnAttempted = true;
                initialResolving = new RelayServers(servers, Lookup);
            }
        }

        private void PollGathering(long now)
        {
            if (stunLookup != null && stunLookup.IsCompleted)
            {
                var address = Pick(stunLookup);
                stunLookup = null;
                if (address == null) bindingSettled = true;
                else StartBinding(new IPEndPoint(address, servers!.StunServers[0].Port), now);
            }

            if (initialResolving != null && initialResolving.IsCompleted)
            {
                var targets = initialResolving.Targets(null, options.AcceptLoopback);
                initialResolving = null;
                if (targets.Count == 0)
                {
                    relayLookupFailed = true;
                }
                else
                {
                    relayTargets = targets;
                    if (RelayWanted) ScheduleRelay(targets[0].Path, servers!, now);
                }
            }

            CatchRelayReady(now);
            if (!gathered && gatheringOutcome == NatGathering.Gathering && (GatheringSettled() || now >= gatherDeadline)) CompleteGathering(now);
        }

        private bool GatheringSettled()
        {
            if (stunLookup != null || initialResolving != null) return false;
            var stunDone = !stunAttempted || bindingSettled;
            return stunDone && (!turnAttempted || relayLookupFailed || RelayUp || RelayGaveUp());
        }

        /// <summary>
        /// Gets whether gathering stops waiting for the relay: an attempt failed, and either nothing ever answered (the
        /// network may block UDP), no attempt is pending, or the next one starts only after the deadline.
        /// </summary>
        private bool RelayGaveUp()
        {
            if (relayFailures == 0) return false;
            if (relay == null) return true;
            if (relay.State != TurnAllocationState.Idle) return false;
            return relayStartAt >= gatherDeadline || (!relayHeard && lastRelayFailure == TurnFailure.Timeout);
        }

        private void CompleteGathering(long now)
        {
            gathered = true;
            var relayUp = RelayUp;
            var relayAnswered = relayHeard || (relay != null && relay.Answered);
            if (!bindingAnswered && !relayAnswered && (stunAttempted || turnAttempted))
            {
                gatheringOutcome = NatGathering.UdpBlocked;
                Report(NatDiagnosticKind.UdpBlocked, "No STUN or TURN server answered.");
            }
            else if (turnAttempted && !relayUp)
            {
                gatheringOutcome = NatGathering.RelayUnavailable;
                Report(NatDiagnosticKind.RelayFailed, "The relay did not come up.");
            }
            else
            {
                gatheringOutcome = NatGathering.Complete;
                Report(NatDiagnosticKind.GatheringComplete, "Gathering finished.");
            }

            foreach (var session in sessions) session.OnGathered(now);
        }

        private void StartBinding(IPEndPoint server, long now)
        {
            var path = UdpPaths.For(server);
            var id = stun.NewTransactionId();
            writer.Begin(scratch, StunMethod.Binding, StunClass.Request, id);
            var length = writer.Finish(null, true);
            if (stun.Begin(path, scratch, length, id, StunTiming.Gathering, bindingOwner!, null, now) == null) bindingSettled = true;
        }

        private long GatheringTimeout()
        {
            if (stunLookup != null || initialResolving != null) return currentTime + PendingTaskPollMicros;
            return gathered ? long.MaxValue : gatherDeadline;
        }

        private void AddHostCandidates(IReadOnlyList<IPAddress> addresses, int port)
        {
            if (port < 1 || port > 65535) return;
            foreach (var raw in addresses)
            {
                if (raw == null || hostCandidates.Count >= MaximumHostCandidates) continue;
                var address = NatAddresses.Unmap(raw);
                var usable = NatAddresses.IsUsable(address) || (options.AcceptLoopback && IPAddress.IsLoopback(address));
                if (!usable || hostCandidates.Exists(existing => NatAddresses.SameAddress(existing.EndPoint.Address, address))) continue;
                var priority = IceCandidate.ComputePriority(IceCandidateKind.Host, 65535 - hostCandidates.Count);
                hostCandidates.Add(new IceCandidate(IceCandidateKind.Host, new IPEndPoint(address, port), priority, candidateOrder++));
            }
        }

        private IceCandidate? AddReflexive(IPEndPoint endpoint, int localPreference)
        {
            if (RelayOnly) return null;
            foreach (var existing in reflexiveCandidates)
            {
                if (existing.SameAddress(endpoint)) return null;
            }

            foreach (var existing in hostCandidates)
            {
                if (existing.SameAddress(endpoint)) return null;
            }

            var candidate = new IceCandidate(IceCandidateKind.ServerReflexive, endpoint, IceCandidate.ComputePriority(IceCandidateKind.ServerReflexive, localPreference), candidateOrder++);
            reflexiveCandidates.Add(candidate);
            return candidate;
        }

        private Task<IPAddress[]>? Lookup(string host)
        {
            try
            {
                return plan!.Resolve(host);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Takes the first usable IPv4 address a lookup found, else the first usable IPv6 one; null when it failed.</summary>
        private IPAddress? Pick(Task<IPAddress[]> lookup)
        {
            var addresses = Usable(lookup);
            foreach (var address in addresses)
            {
                if (address.AddressFamily == AddressFamily.InterNetwork) return address;
            }

            return addresses.Count > 0 ? addresses[0] : null;
        }

        private List<IPAddress> Usable(Task<IPAddress[]> lookup)
        {
            var result = new List<IPAddress>();
            if (lookup.Status != TaskStatus.RanToCompletion)
            {
                Observe(lookup);
                return result;
            }

            foreach (var raw in lookup.Result ?? Array.Empty<IPAddress>())
            {
                if (raw == null) continue;
                var address = NatAddresses.Unmap(raw);
                if (NatAddresses.IsUsable(address) || (options.AcceptLoopback && IPAddress.IsLoopback(address))) result.Add(address);
                if (result.Count == RelayServers.AddressesPerName) break;
            }

            return result;
        }

        /// <summary>Reads a failed task's exception so it never surfaces as unobserved; its message is never used.</summary>
        private static void Observe(Task task) => _ = task.Exception;

        /// <summary>Lets go of a task nothing will wait for: whenever it fails, its exception is observed and never read.</summary>
        internal static void Abandon(Task? task)
        {
            if (task == null) return;
            if (task.IsCompleted) Observe(task);
            else task.ContinueWith(done => _ = done.Exception, System.Threading.CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        /// <summary>The gathering Binding request's owner: an answer is a server-reflexive candidate; any answer proves UDP gets out.</summary>
        private sealed class BindingOwner : IStunTransactionOwner
        {
            private readonly NatAgent agent;

            internal BindingOwner(NatAgent agent)
            {
                this.agent = agent;
            }

            public bool Accepts(StunTransaction transaction, StunMessage response) => true;

            public void OnResponse(StunTransaction transaction, StunMessage response, long now)
            {
                agent.bindingSettled = true;
                agent.bindingAnswered = true;
                if (response.Class != StunClass.Success || !response.TryGetAddress(StunAttribute.XorMappedAddress, out var mapped)) return;
                var candidate = agent.AddReflexive(new IPEndPoint(NatAddresses.Unmap(mapped.Address), mapped.Port), 65534);
                if (candidate != null && agent.gathered)
                {
                    foreach (var session in agent.sessions) session.TrickleLocal(candidate);
                }
            }

            public void OnTimeout(StunTransaction transaction, long now) => agent.bindingSettled = true;
        }
    }
}
