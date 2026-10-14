using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.NatTraversal.Ice;
using TopiaForge.Multiplayer.NatTraversal.Signaling;

namespace TopiaForge.Multiplayer.NatTraversal
{
    /// <summary>
    /// Signaling. A joiner offers once gathering has settled and the host is online, then takes the host's answer,
    /// trickled candidates and punch. A host answers each offer with fresh credentials per peer and asks for a punch.
    /// Every candidate that leaves goes through <see cref="NatPrivacy"/>. A peer's socket lasts fifteen minutes, so it
    /// is opened again when its lifetime ends or the connection drops, with backoff, while the session still needs it.
    /// </summary>
    internal sealed partial class NatAgent
    {
        private const int FramesPerTurn = 64;
        private const long ReconnectFirstMicros = 1_000_000;
        private const long ReconnectLongestMicros = 30_000_000;
        private const int ReconnectsPerWindow = 6;
        private const long ReconnectWindowMicros = 600_000_000;

        private readonly Dictionary<string, bool> relayOnlyPeers = new Dictionary<string, bool>(StringComparer.Ordinal);
        private ISignalingClient? signaling;
        private long reconnectAt = long.MaxValue;
        private long reconnectDelay = ReconnectFirstMicros;
        private long reconnectWindowStart;
        private int reconnectsInWindow;
        private bool signalingDone;
        private int withheldByService;
        private NatJoinStage lastJoinStage = NatJoinStage.Idle;
        private NatFailure lastJoinFailure = NatFailure.None;

        /// <summary>Gets how many frames could not be queued for the service.</summary>
        internal int SignalsDropped { get; private set; }

        /// <summary>Starts a join: gathering, signaling, checks, then a connection over the best pair, filling in <paramref name="attempt"/>.</summary>
        internal void StartJoin(NatSessionPlan sessionPlan, ConnectAttempt attempt, byte[]? expectedKey, long now)
        {
            if (sessionPlan == null) throw new ArgumentNullException(nameof(sessionPlan));
            if (attempt == null) throw new ArgumentNullException(nameof(attempt));
            if (disposed || Role != NatRole.None) throw new InvalidOperationException("The NAT layer is already running a session.");
            Role = NatRole.Joiner;
            currentTime = now;
            BeginGathering(sessionPlan, now);
            var session = CreateSession(null, true, now);
            session.BeginJoin(attempt, expectedKey, now);
            OpenSignaling(now);
            Publish();
        }

        /// <summary>Starts hosting: gathering and signaling; each peer's offer then gets an answer and its own checks.</summary>
        internal void StartHost(NatSessionPlan sessionPlan, long now)
        {
            if (sessionPlan == null) throw new ArgumentNullException(nameof(sessionPlan));
            if (disposed || Role != NatRole.None) throw new InvalidOperationException("The NAT layer is already running a session.");
            Role = NatRole.Host;
            currentTime = now;
            BeginGathering(sessionPlan, now);
            OpenSignaling(now);
            Publish();
        }

        /// <summary>Queues a frame for the service. Losing one is survivable: checks retransmit and descriptions are resent.</summary>
        internal void Signal(SignalMessage message)
        {
            if (signaling == null || signaling.State == SignalingState.Closed || !signaling.TrySend(message)) SignalsDropped++;
        }

        /// <summary>The candidates this side may signal now: the privacy gate over everything it gathered.</summary>
        internal IReadOnlyList<SignalCandidate> SignalableCandidates() => NatPrivacy.Signalable(LocalCandidates(), options.RelayOnly, options.AcceptLoopback);

        /// <summary>Gets whether one candidate may be trickled.</summary>
        internal bool MaySignal(IceCandidate candidate) => NatPrivacy.MaySignal(candidate, options.RelayOnly, options.AcceptLoopback);

        /// <summary>Converts a signaled candidate into one this side will check: a canonical, usable address.</summary>
        internal bool TryAccept(SignalCandidate signaled, out IceCandidate candidate)
        {
            candidate = null!;
            if (signaled == null || !NatAddresses.TryParseCanonical(signaled.Address, out var address)) return false;
            if (!NatAddresses.IsUsable(address) && !(options.AcceptLoopback && System.Net.IPAddress.IsLoopback(address))) return false;
            candidate = new IceCandidate(signaled.Kind, new System.Net.IPEndPoint(address, signaled.Port), signaled.Priority);
            return true;
        }

        internal void RememberJoinOutcome(NatJoinStage stage, NatFailure failure)
        {
            lastJoinStage = stage;
            lastJoinFailure = failure;
        }

        private NatSession CreateSession(string? peer, bool controlling, long now)
        {
            IceCredentials local;
            do
            {
                local = IceCredentials.Generate(options.Random);
            }
            while (byUfrag.ContainsKey(local.Ufrag));

            var tieBreakerBytes = new byte[8];
            options.Random.Fill(tieBreakerBytes);
            var checker = new ConnectivityChecker(local, controlling, BitConverter.ToUInt64(tieBreakerBytes, 0), stun, options.Random, PathFor);
            if (socketBase != null) checker.AddBase(socketBase, now);
            if (relayBase != null) checker.AddBase(relayBase, now);
            var session = new NatSession(this, peer, checker, now);
            sessions.Add(session);
            byUfrag.Add(local.Ufrag, session);
            return session;
        }

        private void OpenSignaling(long now)
        {
            reconnectAt = long.MaxValue;
            try
            {
                signaling = plan!.OpenSignaling();
            }
            catch (Exception)
            {
                signaling = null;
                OnSignalingLost(SignalingCloseReason.Unreachable, now);
            }
        }

        private void PollSignaling(long now)
        {
            if (signaling == null)
            {
                if (now >= reconnectAt) OpenSignaling(now);
                return;
            }

            for (var count = 0; count < FramesPerTurn && signaling.TryReceive(out var message); count++) OnSignal(message, now);
            if (signaling.State == SignalingState.Closed)
            {
                var reason = signaling.CloseReason;
                signaling.Dispose();
                signaling = null;
                OnSignalingLost(reason, now);
            }
        }

        /// <summary>
        /// A socket closed. A peer whose socket reached its lifetime, or lost the connection, opens another while its
        /// session still needs signaling; a replaced socket, an ended host session or a refused upgrade does not.
        /// </summary>
        private void OnSignalingLost(SignalingCloseReason reason, long now)
        {
            Report(NatDiagnosticKind.SignalingClosed, "The signaling socket closed.");
            var retry = !signalingDone && (reason == SignalingCloseReason.Expired || reason == SignalingCloseReason.Unreachable || reason == SignalingCloseReason.RateLimited);
            if (retry && now - reconnectWindowStart > ReconnectWindowMicros)
            {
                reconnectWindowStart = now;
                reconnectsInWindow = 0;
            }

            if (retry && reconnectsInWindow < ReconnectsPerWindow)
            {
                reconnectsInWindow++;
                reconnectAt = now + (reason == SignalingCloseReason.Expired ? 0 : reconnectDelay);
                if (reason != SignalingCloseReason.Expired) reconnectDelay = Math.Min(reconnectDelay * 2, ReconnectLongestMicros);
                return;
            }

            signalingDone = true;
            var failure = reason == SignalingCloseReason.SessionEnded ? NatFailure.HostEnded : NatFailure.SignalingFailed;
            foreach (var session in sessions) session.OnSignalingGone(failure, now);
        }

        private void OnSignal(SignalMessage message, long now)
        {
            reconnectDelay = ReconnectFirstMicros;
            var target = Target(message);
            switch (message.Type)
            {
                case SignalType.Welcome:
                    if (Role == NatRole.Joiner && message.Role == SignalRole.Peer) target?.OnWelcome(message.Online, now);
                    break;
                case SignalType.HostStatus:
                    if (Role == NatRole.Joiner) target?.OnHostStatus(message.Online, now);
                    break;
                case SignalType.Joined:
                    if (Role == NatRole.Host && message.Peer != null && relayOnlyPeers.Count < MaximumSessions * 2) relayOnlyPeers[message.Peer] = message.RelayOnly;
                    break;
                case SignalType.Left:
                    if (Role == NatRole.Host && message.Peer != null) relayOnlyPeers.Remove(message.Peer);
                    target?.OnLeft(now);
                    break;
                case SignalType.Offer:
                    if (Role == NatRole.Host) OnOffer(message, now);
                    break;
                case SignalType.Answer:
                    if (Role == NatRole.Joiner && message.Ice != null) target?.OnAnswer(message.Ice, message.Candidates, now);
                    break;
                case SignalType.Candidate:
                    if (message.Candidate != null) target?.AddRemote(message.Candidate, now);
                    break;
                case SignalType.Punch:
                    target?.OnPunch(message.DelayMs, now);
                    break;
                case SignalType.Bye:
                    target?.OnBye(now);
                    break;
                case SignalType.Notice:
                    withheldByService += message.Withheld;
                    Report(NatDiagnosticKind.ServiceWithheldCandidates, "The service withheld candidates this side sent.");
                    break;
                case SignalType.Error:
                    Report(NatDiagnosticKind.SignalingRefusedFrame, "The service refused a signaling frame.");
                    if (Role == NatRole.Joiner && message.Error == SignalErrorCode.HostOffline) target?.OnHostStatus(false, now);
                    break;
            }
        }

        /// <summary>Gets the session a frame is for: the join for a joiner, the named peer's for a host.</summary>
        private NatSession? Target(SignalMessage message)
        {
            if (Role == NatRole.Joiner)
            {
                foreach (var session in sessions)
                {
                    if (session.IsJoin && session.State != NatSessionState.Ended && session.State != NatSessionState.Failed) return session;
                }

                return null;
            }

            if (message.Peer == null) return null;
            foreach (var session in sessions)
            {
                if (session.PeerId == message.Peer && session.State != NatSessionState.Ended && session.State != NatSessionState.Failed) return session;
            }

            return null;
        }

        /// <summary>A peer's offer replaces any earlier session for it, and is answered once gathering has settled.</summary>
        private void OnOffer(SignalMessage offer, long now)
        {
            if (offer.Peer == null || offer.Ice == null) return;
            var previous = Target(offer);
            previous?.End(NatFailure.None, now, false);
            var live = 0;
            foreach (var session in sessions)
            {
                if (session.State != NatSessionState.Ended && session.State != NatSessionState.Failed) live++;
            }

            if (live >= MaximumSessions) return;
            var created = CreateSession(offer.Peer, false, now);
            created.OnOffer(offer.Ice, offer.Candidates, relayOnlyPeers.TryGetValue(offer.Peer, out var relayOnly) && relayOnly, now);
        }

        /// <summary>Closes signaling for good. The socket sends what is queued, the goodbyes included, then releases itself.</summary>
        private void CloseSignaling()
        {
            signalingDone = true;
            reconnectAt = long.MaxValue;
            signaling?.Close();
            signaling = null;
        }

        private long SignalingTimeout() => signaling == null && reconnectAt != long.MaxValue ? reconnectAt : long.MaxValue;
    }
}
