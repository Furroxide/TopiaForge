using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.NatTraversal.Ice;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal
{
    /// <summary>
    /// Route keeping. The transport decides every move: this side only asks it to validate a path, and a connection moves
    /// when the peer answers there. Upgrades to a direct pair come from the joiner, which controls the checks; moves back
    /// to a relay, and off a retiring allocation, come from whichever side notices. Relayed pairs rank the same way on both
    /// sides, so the two never pull a connection in opposite directions: through the host's relay, then the joiner's,
    /// then both, and the newest allocation first. The loops index the checker's pairs rather than enumerate the
    /// read-only list, whose boxed enumerator would allocate on every route tick.
    /// </summary>
    internal sealed partial class NatSession
    {
        private static readonly System.Predicate<TransportPath> AnyPath = _ => true;

        private void PollJoin(NetworkStack stack, long now)
        {
            if (attempt == null) return;
            if (connectPair == null)
            {
                if (!offered && welcomed && !hostOnline && now - hostOfflineSince >= options.AnswerTimeoutMicros)
                {
                    Fail(NatFailure.HostOffline, now);
                    return;
                }

                if (offered && Checker.Remote == null && now - offeredAt >= options.AnswerTimeoutMicros)
                {
                    Fail(NatFailure.NoAnswer, now);
                    return;
                }

                var start = StartPair(now);
                if (start == null)
                {
                    if (Checker.Remote != null && now >= checksDeadline) Fail(NatFailure.NoRoute, now);
                    return;
                }

                connectPair = start;
                State = NatSessionState.Connecting;
                stack.Connect(attempt, start.Path, expectedKey, now);
                agent.Report(start.IsRelayed ? NatDiagnosticKind.StartedOnRelay : NatDiagnosticKind.StartedDirect,
                    start.IsRelayed ? "The connection starts on the relay." : "The connection starts on a direct path.");
                return;
            }

            if (attempt.State == ConnectAttemptState.Failed)
            {
                Fail(NatFailure.NoRoute, now);
                return;
            }

            if (attempt.State != ConnectAttemptState.Established) return;
            State = NatSessionState.Connected;
            if (attempt.Link != null && attempt.Link.IsClosed) End(NatFailure.None, now, true);
        }

        /// <summary>
        /// Gets whether the join's connection has stalled, or gone, on whatever path the transport has it: a joiner's
        /// endpoint carries that one connection, so this needs no pair, and a path the transport moved to itself counts.
        /// </summary>
        private static bool JoinStalled(NetworkStack stack)
        {
            var usage = stack.Endpoint.UsageWhere(AnyPath);
            return usage.Connections == 0 || usage.Stalled > 0;
        }

        /// <summary>A host's session ends when its peer never connected, or its connection has been gone a while.</summary>
        private void PollHost(long now)
        {
            if (Current != null)
            {
                lastUsedAt = now;
                State = NatSessionState.Connected;
                return;
            }

            if (now - lastUsedAt >= IdleHostSessionMicros && (State == NatSessionState.Connected || now >= checksDeadline || signalingGone)) End(NatFailure.None, now, false);
        }

        /// <summary>
        /// The pair a join connects over: the best relayed pair that succeeded, so the session starts on the relay; a direct
        /// one when no relayed pair exists or none can still succeed, or once one has waited out the relay grace.
        /// </summary>
        private CandidatePair? StartPair(long now)
        {
            CandidatePair? relayed = null;
            CandidatePair? direct = null;
            var relayedPending = false;
            for (var index = 0; index < Checker.Pairs.Count; index++)
            {
                var pair = Checker.Pairs[index];
                if (pair.IsRelayed && (pair.State == CandidatePairState.Waiting || pair.State == CandidatePairState.InProgress)) relayedPending = true;
                if (pair.State != CandidatePairState.Succeeded || pair.Local.Retiring) continue;
                if (pair.IsRelayed)
                {
                    if (relayed == null || RelayedBefore(pair, relayed)) relayed = pair;
                }
                else if (direct == null || pair.Priority > direct.Priority)
                {
                    direct = pair;
                }
            }

            if (relayed != null) return relayed;
            if (direct == null) return null;
            if (!relayedPending || now - direct.SucceededAt >= options.RelayGraceMicros) return direct;
            return null;
        }

        private bool HasSucceededPair()
        {
            for (var index = 0; index < Checker.Pairs.Count; index++)
            {
                var pair = Checker.Pairs[index];
                if (pair.State == CandidatePairState.Succeeded) return true;
            }

            return false;
        }

        /// <summary>Finds the pair the connection is on now; null while the transport has none on a pair this session knows.</summary>
        private void FindCurrent(NetworkStack stack)
        {
            var previous = Current;
            if (previous != null && stack.Endpoint.UsageOf(previous.Path).Connections > 0) return;
            Current = null;
            for (var index = 0; index < Checker.Pairs.Count; index++)
            {
                var pair = Checker.Pairs[index];
                if (stack.Endpoint.UsageOf(pair.Path).Connections == 0) continue;
                Current = pair;
                break;
            }

            if (previous == null || Current == null || previous.IsRelayed == Current.IsRelayed) return;
            agent.Report(Current.IsRelayed ? NatDiagnosticKind.FellBackToRelay : NatDiagnosticKind.UpgradedToDirect,
                Current.IsRelayed ? "The connection moved back to the relay." : "The connection moved to a direct path.");
        }

        private void KeepRoute(NetworkStack stack, long now)
        {
            var current = Current;
            if (current == null) return;
            var usage = stack.Endpoint.UsageOf(current.Path);
            if (usage.Validating > 0) return;

            // This side's allocation is being replaced: move to the best pair that does not use it.
            if (current.Local.Retiring)
            {
                Probe(stack, current, Best(current.IsRelayed, true) ?? Best(!current.IsRelayed, true), now);
                return;
            }

            // A direct path that stalled or lost consent falls back to the relay; a relayed one to any other relay.
            if (current.State == CandidatePairState.Failed || usage.Stalled > 0)
            {
                if (!current.IsRelayed && usage.Stalled > 0) agent.Report(NatDiagnosticKind.ConsentLost, "A direct path stopped answering.");
                Probe(stack, current, Best(true, false, current), now);
                return;
            }

            if (Checker.Controlling && current.IsRelayed)
            {
                var direct = Best(false, false);
                if (direct != null && direct.Probes < UpgradeProbesPerPair) Probe(stack, current, direct, now);
            }
        }

        private void Probe(NetworkStack stack, CandidatePair from, CandidatePair? to, long now)
        {
            if (to == null || ReferenceEquals(to, from) || now - to.LastProbeAt < ProbeSpacingMicros) return;

            // A pair that twice failed to take the connection is checked again before it is tried a third time: a path
            // that died since its last answer, such as one through a relay the remote released, fails, and the next is tried.
            if (to.Probes >= UpgradeProbesPerPair && to.AnsweredAt <= to.LastProbeAt)
            {
                Checker.Recheck(to, now);
                to.LastProbeAt = now;
                return;
            }

            if (to.AnsweredAt > to.LastProbeAt) to.Probes = 0;
            to.Probes++;
            to.LastProbeAt = now;
            stack.Endpoint.ProbeConnections(from.Path, to.Path, now);
        }

        /// <summary>The pair in use keeps consent fresh, and so does the best relay standing by behind a direct one.</summary>
        private void UpdateConsent(long now)
        {
            var current = Current;
            var standby = current != null && !current.IsRelayed ? Best(true, false) : null;
            for (var index = 0; index < Checker.Pairs.Count; index++)
            {
                var pair = Checker.Pairs[index];
                var wanted = ReferenceEquals(pair, current) || ReferenceEquals(pair, standby);
                Checker.WantConsent(pair, wanted && pair.State == CandidatePairState.Succeeded, now);
            }
        }

        /// <summary>
        /// The best succeeded pair of a kind, off retiring bases and the remote's replaced relay candidates, other than
        /// <paramref name="except"/>: nothing moves onto an allocation either side is giving up.
        /// </summary>
        private CandidatePair? Best(bool relayed, bool offRetiring, CandidatePair? except = null)
        {
            CandidatePair? best = null;
            for (var index = 0; index < Checker.Pairs.Count; index++)
            {
                var pair = Checker.Pairs[index];
                if (pair.State != CandidatePairState.Succeeded || pair.IsRelayed != relayed || ReferenceEquals(pair, except)) continue;
                if (pair.Local.Retiring && (offRetiring || relayed)) continue;
                if (Checker.IsSuperseded(pair.Remote)) continue;
                if (best == null || (relayed ? RelayedBefore(pair, best) : pair.Priority > best.Priority)) best = pair;
            }

            return best;
        }

        /// <summary>Ranks relayed pairs identically on both sides: by route, then the newest allocation, then the newest candidate.</summary>
        private bool RelayedBefore(CandidatePair candidate, CandidatePair best)
        {
            var hosting = !IsJoin;
            var left = candidate.Route(hosting);
            var right = best.Route(hosting);
            if (left != right) return left < right;
            if (candidate.Local.Allocation != best.Local.Allocation) return candidate.Local.Allocation > best.Local.Allocation;
            if (candidate.Remote.Order != best.Remote.Order) return candidate.Remote.Order > best.Remote.Order;
            return candidate.Priority > best.Priority;
        }
    }
}
