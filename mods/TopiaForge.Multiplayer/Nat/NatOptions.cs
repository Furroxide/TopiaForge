using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using TopiaForge.Multiplayer.NatTraversal.Ice;
using TopiaForge.Multiplayer.NatTraversal.Signaling;
using TopiaForge.Multiplayer.NatTraversal.Turn;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal
{
    /// <summary>What the NAT layer reports. No kind and no message ever names an address, a port or a peer.</summary>
    internal enum NatDiagnosticKind
    {
        GatheringComplete = 0,
        UdpBlocked = 1,
        RelayReady = 2,
        RelayFailed = 3,
        RelayRotated = 4,
        ChecksStarted = 5,
        StartedOnRelay = 6,
        StartedDirect = 7,
        UpgradedToDirect = 8,
        FellBackToRelay = 9,
        ConsentLost = 10,
        SignalingClosed = 11,
        ServiceWithheldCandidates = 12,
        SignalingRefusedFrame = 13,
        PortMapped = 14,
        PortMappingFailed = 15,
        TraversalFailed = 16,

        /// <summary>A connected joiner closed its signaling socket until it has something to say.</summary>
        SignalingParked = 17
    }

    /// <summary>Receives NAT-layer events. Messages are fixed text chosen here, never built from network data.</summary>
    internal interface INatDiagnostics
    {
        void Report(NatDiagnosticKind kind, string message);
    }

    /// <summary>Tuning for one game socket's NAT layer. The defaults are production's.</summary>
    internal sealed class NatAgentOptions
    {
        /// <summary>
        /// Gets or sets the player's "hide my IP" choice, which is on unless the player allowed direct play for this
        /// session: direct play is never a silent default, so a caller sets this false only for a player who chose it.
        /// Host and server-reflexive candidates are then never gathered, signaled or checked: the only thing that leaves
        /// the socket is traffic to the TURN server, and every path to the peer runs through this side's own allocation.
        /// </summary>
        internal bool RelayOnly { get; set; } = true;

        internal ITransportRandom Random { get; set; } = CryptoTransportRandom.Instance;

        internal INatDiagnostics? Diagnostics { get; set; }

        /// <summary>Gets or sets whether loopback addresses count as usable: only tests that run every side on one machine set it.</summary>
        internal bool AcceptLoopback { get; set; }

        internal long GatheringTimeoutMicros { get; set; } = 6_000_000;

        internal long AnswerTimeoutMicros { get; set; } = 10_000_000;

        /// <summary>Gets or sets how long checks may run without a usable pair before a join gives up.</summary>
        internal long ChecksTimeoutMicros { get; set; } = 10_000_000;

        /// <summary>Gets or sets how long a joiner waits for the host's punch after the answer before checking anyway.</summary>
        internal long PunchWaitMicros { get; set; } = 1_000_000;

        /// <summary>Gets or sets the punch delay a host asks for: both sides then check at about the same moment.</summary>
        internal int PunchDelayMs { get; set; } = 250;

        /// <summary>Gets or sets how long a join prefers to wait for a relayed pair once a direct one has succeeded.</summary>
        internal long RelayGraceMicros { get; set; } = 1_000_000;

        /// <summary>Gets or sets how long before the relay credential expires the next one is minted and moved to.</summary>
        internal long RenewMarginMicros { get; set; } = 90_000_000;

        internal void Validate()
        {
            if (Random == null) throw new ArgumentException("A random source is required.", nameof(Random));
            if (GatheringTimeoutMicros <= 0 || AnswerTimeoutMicros <= 0 || ChecksTimeoutMicros <= 0 || PunchWaitMicros < 0 || RelayGraceMicros < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(GatheringTimeoutMicros));
            }

            if (PunchDelayMs < 0 || PunchDelayMs > SignalCodec.MaximumPunchDelayMs) throw new ArgumentOutOfRangeException(nameof(PunchDelayMs));
            if (RenewMarginMicros < 10_000_000) throw new ArgumentOutOfRangeException(nameof(RenewMarginMicros));
        }
    }

    /// <summary>
    /// What one hosted or joined session needs from the control plane: relay credentials in the <c>iceServers</c> shape,
    /// a way to mint the next ones before they expire, a way to open the session's signaling socket, and DNS. The host
    /// and join flows build it once they hold a host token or a join ticket. Without one, the NAT layer contacts nothing.
    /// </summary>
    internal sealed class NatSessionPlan
    {
        internal NatSessionPlan(IceServerSet servers, Func<ISignalingClient> openSignaling, Func<string, Task<IPAddress[]>> resolve)
        {
            Servers = servers ?? throw new ArgumentNullException(nameof(servers));
            OpenSignaling = openSignaling ?? throw new ArgumentNullException(nameof(openSignaling));
            Resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        }

        internal IceServerSet Servers { get; }

        /// <summary>Opens a fresh signaling socket for the same server and credential: once at the start, again after each lifetime ends.</summary>
        internal Func<ISignalingClient> OpenSignaling { get; }

        /// <summary>Resolves a STUN or TURN server name. It runs off the network thread; the agent polls the task.</summary>
        internal Func<string, Task<IPAddress[]>> Resolve { get; }

        /// <summary>Gets or sets how the next relay credential is minted; null keeps the first one until it expires.</summary>
        internal Func<Task<IceServerSet?>>? Renew { get; set; }

        /// <summary>Gets or sets this machine's own addresses for host candidates, listed off the network thread. Relay-only ignores them.</summary>
        internal IReadOnlyList<IPAddress> LocalAddresses { get; set; } = Array.Empty<IPAddress>();

        /// <summary>Gets or sets the game socket's port, which host candidates carry.</summary>
        internal int LocalPort { get; set; }
    }

    /// <summary>
    /// The one place a candidate is cleared to leave this machine. Relay-only sends relay candidates only; nothing ever
    /// sends a peer-reflexive one, an unusable address, or more than the grammar's 16. Every offer, answer and trickled
    /// candidate passes through here, so the client never relies on the service's own stripping.
    /// </summary>
    internal static class NatPrivacy
    {
        internal static List<SignalCandidate> Signalable(IEnumerable<IceCandidate> candidates, bool relayOnly, bool acceptLoopback)
        {
            var result = new List<SignalCandidate>();
            foreach (var candidate in candidates)
            {
                if (!MaySignal(candidate, relayOnly, acceptLoopback)) continue;
                result.Add(SignalCandidate.From(candidate));
                if (result.Count == SignalCodec.MaximumCandidates) break;
            }

            return result;
        }

        internal static bool MaySignal(IceCandidate candidate, bool relayOnly, bool acceptLoopback)
        {
            if (candidate == null || candidate.Kind == IceCandidateKind.PeerReflexive) return false;
            if (relayOnly && candidate.Kind != IceCandidateKind.Relayed) return false;
            return NatAddresses.IsUsable(candidate.EndPoint.Address) || (acceptLoopback && IPAddress.IsLoopback(candidate.EndPoint.Address));
        }
    }
}
