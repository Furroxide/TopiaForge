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
    /// Every candidate that leaves goes through <see cref="NatPrivacy"/>. The room counts every open peer socket against
    /// its places (ControlPlaneApi.md, section 8.4), so a joiner closes its socket once its connection is up and opens it
    /// again only when it has something to say: while its relay changes, so the new relay candidate reaches the host,
    /// or once its path is lost, until a new one validates. The host keeps its socket, and sends a returning peer its
    /// candidates again, since whatever it trickled while that peer's socket was closed went nowhere.
    /// </summary>
    internal sealed partial class NatAgent
    {
        private const int FramesPerTurn = 64;
        private const long ReconnectFirstMicros = 1_000_000;
        private const long ReconnectLongestMicros = 30_000_000;
        private const int ReconnectsPerWindow = 6;
        private const long ReconnectWindowMicros = 600_000_000;

        /// <summary>How soon a connected joiner may open its socket again after the last time it did.</summary>
        private const long ReopenSpacingMicros = 1_000_000;

        private readonly Dictionary<string, bool> relayOnlyPeers = new Dictionary<string, bool>(StringComparer.Ordinal);
        private ISignalingClient? signaling;
        private long reconnectAt = long.MaxValue;
        private long reconnectDelay = ReconnectFirstMicros;
        private long reconnectWindowStart;
        private int reconnectsInWindow;
        private bool signalingDone;
        private bool signalingParked;
        private long lastOpenedAt = long.MinValue / 2;
        private int withheldByService;
        private NatJoinStage lastJoinStage = NatJoinStage.Idle;
        private NatFailure lastJoinFailure = NatFailure.None;

        private enum SignalingNeed
        {
            /// <summary>Nothing more will ever be said: the session ended.</summary>
            Done,

            /// <summary>A connected joiner with nothing to say: its socket stays closed.</summary>
            Idle,

            Needed
        }

        /// <summary>Gets how many frames could not be queued for the service.</summary>
        internal int SignalsDropped { get; private set; }

        /// <summary>Gets how many signaling sockets this agent opened, counting every reopening.</summary>
        internal int SignalingOpens { get; private set; }

        /// <summary>Gets whether a signaling socket is open or opening now.</summary>
        internal bool SignalingOpen => signaling != null && signaling.State != SignalingState.Closed;

        /// <summary>Starts a join: gathering, signaling, checks, then a connection over the best pair, filling in <paramref name="attempt"/>.</summary>
        /// <param name="relayOnly">Whether the stack the join runs on is relay-only; the join then is too, whatever the agent's option.</param>
        internal void StartJoin(NatSessionPlan sessionPlan, ConnectAttempt attempt, byte[]? expectedKey, long now, bool relayOnly = false)
        {
            if (sessionPlan == null) throw new ArgumentNullException(nameof(sessionPlan));
            if (attempt == null) throw new ArgumentNullException(nameof(attempt));
            if (disposed || Role != NatRole.None) throw new InvalidOperationException("The NAT layer is already running a session.");
            forcedRelayOnly = relayOnly;
            Role = NatRole.Joiner;
            currentTime = now;
            BeginGathering(sessionPlan, now);
            var session = CreateSession(null, true, now);
            session.BeginJoin(attempt, expectedKey, now);
            OpenSignaling(now);
            Publish();
        }

        /// <summary>Starts hosting: gathering and signaling; each peer's offer then gets an answer and its own checks.</summary>
        /// <param name="relayOnly">Whether the stack the host runs on is relay-only; hosting then is too, whatever the agent's option.</param>
        internal void StartHost(NatSessionPlan sessionPlan, long now, bool relayOnly = false)
        {
            if (sessionPlan == null) throw new ArgumentNullException(nameof(sessionPlan));
            if (disposed || Role != NatRole.None) throw new InvalidOperationException("The NAT layer is already running a session.");
            forcedRelayOnly = relayOnly;
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
        internal IReadOnlyList<SignalCandidate> SignalableCandidates() => NatPrivacy.Signalable(LocalCandidates(), RelayOnly, options.AcceptLoopback);

        /// <summary>Gets whether one candidate may be trickled.</summary>
        internal bool MaySignal(IceCandidate candidate) => NatPrivacy.MaySignal(candidate, RelayOnly, options.AcceptLoopback);

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

        /// <summary>Gets the join's session while it runs; null for a host, or once the join has ended.</summary>
        private NatSession? LiveJoin()
        {
            foreach (var session in sessions)
            {
                if (session.IsJoin && session.State != NatSessionState.Ended && session.State != NatSessionState.Failed) return session;
            }

            return null;
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
            lastOpenedAt = now;
            SignalingOpens++;
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

        /// <summary>
        /// Gets what signaling is for now. A host always needs it. A joiner needs it until its connection is up, while
        /// its relay changes, and while its connection is stalled; once none of that holds it has nothing to say.
        /// </summary>
        private SignalingNeed NeedNow(long now)
        {
            if (Role == NatRole.Host) return SignalingNeed.Needed;
            var join = Role == NatRole.Joiner ? LiveJoin() : null;
            if (join == null) return SignalingNeed.Done;
            if (join.State != NatSessionState.Connected || join.PathTrouble || RelayChanging(now)) return SignalingNeed.Needed;
            return SignalingNeed.Idle;
        }

        private void PollSignaling(long now)
        {
            if (signalingDone) return;
            var need = NeedNow(now);
            if (need == SignalingNeed.Done)
            {
                // The join ended: whatever it queued (its goodbye) goes, and signaling is over.
                CloseSignaling();
                return;
            }

            if (signaling == null)
            {
                // A connected joiner with nothing to say stays closed. One that needs its socket opens it once its backoff
                // and spacing allow. Opening for a change of relay is never held back further: the new relay candidate
                // must reach the host, and changes come no faster than credentials expire. Any other reopening counts
                // toward the reconnection window.
                if (need == SignalingNeed.Idle)
                {
                    signalingParked = true;
                    return;
                }

                if (now < reconnectAt || (signalingParked && !RelayChanging(now) && !WindowAllows(now))) return;
                signalingParked = false;
                OpenSignaling(now);
                return;
            }

            for (var count = 0; count < FramesPerTurn && signaling.TryReceive(out var message); count++) OnSignal(message, now);
            if (signaling.State == SignalingState.Closed)
            {
                var reason = signaling.CloseReason;
                signaling.Dispose();
                signaling = null;
                OnSignalingLost(reason, now);
                return;
            }

            // A connected joiner with nothing to say closes its socket: the room counts it against its places.
            if (need == SignalingNeed.Idle && signaling.State == SignalingState.Open) Park();
        }

        /// <summary>Closes a connected joiner's socket until it is needed again; the frames already queued go first.</summary>
        private void Park()
        {
            signaling!.Close();
            signaling = null;
            signalingParked = true;
            reconnectAt = lastOpenedAt + ReopenSpacingMicros;
            Report(NatDiagnosticKind.SignalingParked, "The signaling socket closed until it is needed again.");
        }

        /// <summary>Takes a place in the reconnection window: six openings in ten minutes; false, and the next turn waits for the window, once they are used.</summary>
        private bool WindowAllows(long now)
        {
            if (now - reconnectWindowStart > ReconnectWindowMicros)
            {
                reconnectWindowStart = now;
                reconnectsInWindow = 0;
            }

            if (reconnectsInWindow >= ReconnectsPerWindow)
            {
                reconnectAt = reconnectWindowStart + ReconnectWindowMicros + 1;
                return false;
            }

            reconnectsInWindow++;
            return true;
        }

        /// <summary>
        /// A socket closed. One whose lifetime ended, whose connection broke or that was rate limited is opened again
        /// while it is still needed, with backoff, at most six times in ten minutes. A connected joiner does not depend
        /// on signaling, so it never gives up on it: it parks, and opens again once it needs to and its backoff allows,
        /// even after a refused upgrade, since a full room refuses it too. A replaced socket, an ended session, a
        /// withdrawn ticket or a goodbye is final, and so is a refused upgrade before the connection is up.
        /// </summary>
        private void OnSignalingLost(SignalingCloseReason reason, long now)
        {
            Report(NatDiagnosticKind.SignalingClosed, "The signaling socket closed.");
            if (signalingDone) return;
            var need = NeedNow(now);
            var connected = Role == NatRole.Joiner && LiveJoin()?.State == NatSessionState.Connected;
            var retry = need != SignalingNeed.Done &&
                        (reason == SignalingCloseReason.Expired || reason == SignalingCloseReason.Unreachable || reason == SignalingCloseReason.RateLimited ||
                         (reason == SignalingCloseReason.Refused && connected));
            if (retry)
            {
                var delay = reason == SignalingCloseReason.Expired ? 0 : reconnectDelay;
                if (reason != SignalingCloseReason.Expired) reconnectDelay = Math.Min(reconnectDelay * 2, ReconnectLongestMicros);
                if (connected)
                {
                    signalingParked = true;
                    reconnectAt = now + delay;
                    return;
                }

                if (now - reconnectWindowStart > ReconnectWindowMicros)
                {
                    reconnectWindowStart = now;
                    reconnectsInWindow = 0;
                }

                if (reconnectsInWindow < ReconnectsPerWindow)
                {
                    reconnectsInWindow++;
                    reconnectAt = now + delay;
                    return;
                }
            }

            signalingDone = true;
            var failure = reason == SignalingCloseReason.SessionEnded ? NatFailure.HostEnded :
                reason == SignalingCloseReason.Revoked ? NatFailure.TicketRevoked : NatFailure.SignalingFailed;
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

                    // A peer back in the room missed what was trickled while its socket was closed: it hears it again.
                    target?.OnRejoined(now);
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
            if (Role == NatRole.Joiner) return LiveJoin();
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
            signalingParked = false;
            reconnectAt = long.MaxValue;
            signaling?.Close();
            signaling = null;
        }

        /// <summary>When signaling next needs a turn: a reconnection, or a parked joiner's backoff running out while it needs the socket.</summary>
        private long SignalingTimeout()
        {
            if (signalingDone || signaling != null) return long.MaxValue;
            if (signalingParked && NeedNow(currentTime) != SignalingNeed.Needed) return long.MaxValue;
            return reconnectAt;
        }
    }
}
