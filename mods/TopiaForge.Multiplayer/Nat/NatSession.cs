using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.NatTraversal.Ice;
using TopiaForge.Multiplayer.NatTraversal.Signaling;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal
{
    internal enum NatSessionState
    {
        Describing = 0,
        Checking = 1,
        Connecting = 2,
        Connected = 3,
        Ended = 4,
        Failed = 5
    }

    /// <summary>
    /// One remote side: a joiner's session with its host, or one of a host's sessions with a peer. It carries the
    /// description exchange, runs the connectivity checks, and keeps the route. A join connects over the first relayed pair
    /// that succeeds, or a direct one when no relay exists, and then upgrades to a direct pair through the transport's
    /// own path validation. Either side moves back to the relay when a direct path stalls or loses consent, and the side
    /// whose allocation is being replaced moves its connections to the new one.
    /// </summary>
    internal sealed partial class NatSession
    {
        internal const long RouteIntervalMicros = 200_000;
        internal const long ProbeSpacingMicros = 2_000_000;
        internal const int UpgradeProbesPerPair = 2;
        internal const long IdleHostSessionMicros = 30_000_000;
        internal const long ForgetMicros = 30_000_000;

        private readonly NatAgent agent;
        private readonly NatAgentOptions options;
        private ConnectAttempt? attempt;
        private byte[]? expectedKey;
        private CandidatePair? connectPair;
        private bool welcomed;
        private bool hostOnline;
        private bool offered;
        private bool answered;
        private bool answerPending;
        private bool signalingGone;
        private long hostOfflineSince = long.MaxValue;
        private long offeredAt = long.MaxValue;
        private long punchAt = long.MaxValue;
        private long checksDeadline = long.MaxValue;
        private long nextRouteAt;
        private long lastUsedAt;
        private long endedAt = long.MaxValue;

        internal NatSession(NatAgent agent, string? peerId, ConnectivityChecker checker, long now)
        {
            this.agent = agent;
            options = agent.Options;
            PeerId = peerId;
            Checker = checker;
            CreatedAt = now;
            lastUsedAt = now;
        }

        /// <summary>Gets the signaling peer id a host's session answers; a joiner's session has none.</summary>
        internal string? PeerId { get; }

        internal ConnectivityChecker Checker { get; }

        internal bool IsJoin => PeerId == null;

        internal long CreatedAt { get; }

        internal NatSessionState State { get; private set; }

        internal NatFailure Failure { get; private set; }

        /// <summary>Gets whether the remote side chose relay-only, as the service reported it.</summary>
        internal bool RemoteRelayOnly { get; private set; }

        /// <summary>Gets the pair the session's connection uses now, if the transport has one on a known pair.</summary>
        internal CandidatePair? Current { get; private set; }

        /// <summary>
        /// Gets whether a connected join's connection is in trouble: it has heard nothing authenticated for the stall time,
        /// on whatever path it runs. The joiner then needs signaling again, until a path validates.
        /// </summary>
        internal bool PathTrouble { get; private set; }

        internal NatRoute Route => Current == null ? NatRoute.None : Current.IsRelayed ? NatRoute.Relayed : NatRoute.Direct;

        internal NatJoinStage Stage
        {
            get
            {
                if (State == NatSessionState.Failed) return NatJoinStage.Failed;
                if (State == NatSessionState.Ended) return NatJoinStage.Ended;
                if (connectPair != null) return attempt?.State == ConnectAttemptState.Established ? NatJoinStage.Connected : NatJoinStage.Connecting;
                if (Checker.Remote != null) return NatJoinStage.Checking;
                if (offered || welcomed) return NatJoinStage.Signaling;
                return NatJoinStage.Gathering;
            }
        }

        internal void BeginJoin(ConnectAttempt joinAttempt, byte[]? key, long now)
        {
            attempt = joinAttempt;
            expectedKey = key == null ? null : (byte[])key.Clone();
        }

        internal void OnWelcome(bool online, long now)
        {
            // A joiner back in the room after closing its socket says again what the host may have missed.
            if (welcomed && offered) ResendCandidates();
            welcomed = true;
            OnHostStatus(online, now);
        }

        /// <summary>The peer's socket is back in the room: whatever the host trickled while it was closed went nowhere, so it goes again.</summary>
        internal void OnRejoined(long now)
        {
            signalingGone = false;
            if (answered) ResendCandidates();
        }

        internal void OnHostStatus(bool online, long now)
        {
            hostOnline = online;
            if (!online && hostOfflineSince == long.MaxValue) hostOfflineSince = now;
            if (online) hostOfflineSince = long.MaxValue;
            TryOffer(now);
        }

        /// <summary>Gathering settled: a joiner offers, a host answers offers that arrived first, and nothing goes out when UDP is blocked.</summary>
        internal void OnGathered(long now)
        {
            if (State == NatSessionState.Ended || State == NatSessionState.Failed) return;
            if (IsJoin && agent.GatheringOutcome == NatGathering.UdpBlocked)
            {
                Fail(NatFailure.UdpBlocked, now);
                return;
            }

            if (IsJoin && agent.RelayOnly && !agent.RelayUp)
            {
                Fail(NatFailure.RelayUnavailable, now);
                return;
            }

            if (IsJoin) TryOffer(now);
            else if (answerPending) Answer(now);
        }

        internal void OnAnswer(IceCredentials ice, IReadOnlyList<SignalCandidate> candidates, long now)
        {
            if (!offered || Checker.Remote != null || State != NatSessionState.Describing) return;
            Describe(ice, candidates, now);
            punchAt = now + options.PunchWaitMicros;
            checksDeadline = punchAt + options.ChecksTimeoutMicros;
        }

        internal void OnOffer(IceCredentials ice, IReadOnlyList<SignalCandidate> candidates, bool remoteRelayOnly, long now)
        {
            RemoteRelayOnly = remoteRelayOnly;
            Describe(ice, candidates, now);
            if (agent.Gathered) Answer(now);
            else answerPending = true;
        }

        internal void OnPunch(int delayMs, long now)
        {
            if (Checker.Remote == null || Checker.Started) return;
            punchAt = Math.Min(punchAt, now + (Math.Max(0, delayMs) * 1000L));
        }

        internal void AddRemote(SignalCandidate signaled, long now)
        {
            if (Checker.Remote == null || !agent.TryAccept(signaled, out var candidate)) return;
            if (Checker.AddRemote(candidate, now)) agent.PermitRemote(candidate, now);
        }

        /// <summary>A new allocation came up: it becomes a base here, and the relay candidate goes to the remote once described.</summary>
        internal void AddRelayBase(LocalBase local, IceCandidate candidate, long now)
        {
            Checker.AddBase(local, now);
            TrickleLocal(candidate);
        }

        /// <summary>Sends a candidate learned after the description, through the privacy gate.</summary>
        internal void TrickleLocal(IceCandidate candidate)
        {
            if (State == NatSessionState.Ended || State == NatSessionState.Failed || !(offered || answered) || !agent.MaySignal(candidate)) return;
            agent.Signal(SignalMessage.Trickle(PeerId, SignalCandidate.From(candidate)));
        }

        /// <summary>Trickles every candidate this side may signal now, through the privacy gate; the remote ignores those it holds.</summary>
        private void ResendCandidates()
        {
            if (State == NatSessionState.Ended || State == NatSessionState.Failed) return;
            foreach (var candidate in agent.SignalableCandidates()) agent.Signal(SignalMessage.Trickle(PeerId, candidate));
        }

        internal void OnBye(long now)
        {
            signalingGone = true;
            if (IsJoin && connectPair == null) Fail(NatFailure.HostEnded, now);
        }

        internal void OnLeft(long now) => signalingGone = true;

        /// <summary>Signaling is gone for good. A join still waiting fails; a connected session carries on without it.</summary>
        internal void OnSignalingGone(NatFailure failure, long now)
        {
            signalingGone = true;
            if (IsJoin && connectPair == null) Fail(failure, now);
        }

        internal void Touch(long now) => lastUsedAt = now;

        internal void Turn(NetworkStack stack, long now)
        {
            if (State == NatSessionState.Ended || State == NatSessionState.Failed) return;
            if (!Checker.Started && now >= punchAt)
            {
                Checker.Start(now);
                agent.Report(NatDiagnosticKind.ChecksStarted, "Connectivity checks started.");
            }

            Checker.Poll(now);
            if (IsJoin) PollJoin(stack, now);
            if (State == NatSessionState.Ended || State == NatSessionState.Failed || now < nextRouteAt) return;
            nextRouteAt = now + RouteIntervalMicros;
            FindCurrent(stack);
            KeepRoute(stack, now);
            UpdateConsent(now);
            Checker.Prune(now, Current);
            if (IsJoin) PathTrouble = State == NatSessionState.Connected && JoinStalled(stack);
            else PollHost(now);
        }

        internal long NextTimeoutMicros()
        {
            if (State == NatSessionState.Ended || State == NatSessionState.Failed) return long.MaxValue;
            var next = Checker.NextTimeoutMicros();
            if (!Checker.Started) next = Math.Min(next, punchAt);
            if (IsJoin && connectPair == null)
            {
                // Each deadline counts only while it can still fire, so a passed one never keeps the thread awake.
                if (Checker.Remote != null) next = Math.Min(next, checksDeadline);
                else if (offered) next = Math.Min(next, offeredAt + options.AnswerTimeoutMicros);
                else if (welcomed && !hostOnline && hostOfflineSince != long.MaxValue) next = Math.Min(next, hostOfflineSince + options.AnswerTimeoutMicros);
                if (HasSucceededPair()) next = Math.Min(next, agent.CurrentTime + RouteIntervalMicros);
            }

            if (connectPair != null || answered) next = Math.Min(next, nextRouteAt);
            return next;
        }

        /// <summary>Ends the session; a goodbye goes to the remote when asked and signaling still runs.</summary>
        internal void End(NatFailure failure, long now, bool sayGoodbye)
        {
            if (State == NatSessionState.Ended || State == NatSessionState.Failed) return;
            if (sayGoodbye && (offered || answered)) agent.Signal(SignalMessage.Bye(PeerId, failure == NatFailure.None || failure == NatFailure.Cancelled ? SignalByeReason.Done : SignalByeReason.Failed));
            State = failure == NatFailure.None || failure == NatFailure.Cancelled ? NatSessionState.Ended : NatSessionState.Failed;
            Failure = failure;
            endedAt = now;
            Checker.Stop();
            if (attempt != null && connectPair == null && attempt.State == ConnectAttemptState.Pending) attempt.Fail(TransportCloseReason.Local);
            if (IsJoin) agent.RememberJoinOutcome(Stage, failure);
        }

        internal bool CanForget(long now) => (State == NatSessionState.Ended || State == NatSessionState.Failed) && now - endedAt >= ForgetMicros;

        private void Fail(NatFailure failure, long now)
        {
            agent.Report(NatDiagnosticKind.TraversalFailed, "Traversal failed.");
            End(failure, now, true);
        }

        private void TryOffer(long now)
        {
            if (!IsJoin || offered || !welcomed || !hostOnline || !agent.Gathered || State != NatSessionState.Describing) return;
            if (agent.GatheringOutcome == NatGathering.UdpBlocked) return;
            agent.Signal(SignalMessage.Offer(Checker.Local, agent.SignalableCandidates()));
            offered = true;
            offeredAt = now;
        }

        private void Answer(long now)
        {
            answerPending = false;
            answered = true;
            agent.Signal(SignalMessage.Answer(PeerId!, Checker.Local, agent.SignalableCandidates()));
            agent.Signal(SignalMessage.Punch(PeerId, options.PunchDelayMs));
            punchAt = now + (options.PunchDelayMs * 1000L);
            checksDeadline = punchAt + options.ChecksTimeoutMicros;
            State = NatSessionState.Checking;
        }

        private void Describe(IceCredentials ice, IReadOnlyList<SignalCandidate> candidates, long now)
        {
            Checker.SetRemote(ice);
            foreach (var signaled in candidates)
            {
                if (!agent.TryAccept(signaled, out var candidate)) continue;
                if (Checker.AddRemote(candidate, now)) agent.PermitRemote(candidate, now);
            }

            if (State == NatSessionState.Describing) State = NatSessionState.Checking;
        }
    }
}
