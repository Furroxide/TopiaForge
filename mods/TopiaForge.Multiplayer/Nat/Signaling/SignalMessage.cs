using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.NatTraversal.Ice;

namespace TopiaForge.Multiplayer.NatTraversal.Signaling
{
    internal enum SignalType
    {
        Offer = 0,
        Answer = 1,
        Candidate = 2,
        Punch = 3,
        Bye = 4,
        Welcome = 5,
        Joined = 6,
        Left = 7,
        HostStatus = 8,
        Notice = 9,
        Error = 10
    }

    internal enum SignalRole
    {
        Host = 0,
        Peer = 1
    }

    internal enum SignalByeReason
    {
        Done = 0,
        Cancelled = 1,
        Failed = 2,
        Timeout = 3
    }

    /// <summary>The fixed error codes the service sends; anything else reads as <see cref="Unknown"/>.</summary>
    internal enum SignalErrorCode
    {
        Unknown = 0,
        InvalidMessage = 1,
        HostOffline = 2,
        UnknownPeer = 3,
        RateLimited = 4
    }

    /// <summary>
    /// A candidate as the signaling wire carries it: kind, address in its canonical text, port and priority. Only the
    /// NAT layer builds or reads one, and only after the privacy rules have decided it may be sent.
    /// </summary>
    internal sealed class SignalCandidate
    {
        internal SignalCandidate(IceCandidateKind kind, string address, int port, uint priority)
        {
            if (kind == IceCandidateKind.PeerReflexive) throw new ArgumentException("Peer-reflexive candidates are never signaled.", nameof(kind));
            Kind = kind;
            Address = address ?? throw new ArgumentNullException(nameof(address));
            Port = port;
            Priority = priority;
        }

        internal IceCandidateKind Kind { get; }

        internal string Address { get; }

        internal int Port { get; }

        internal uint Priority { get; }

        internal static SignalCandidate From(IceCandidate candidate) =>
            new SignalCandidate(candidate.Kind, NatAddresses.Format(candidate.EndPoint.Address), candidate.EndPoint.Port, candidate.Priority);

        /// <summary>A candidate holds an address, so it prints only its kind.</summary>
        public override string ToString() => Kind + " candidate";
    }

    /// <summary>
    /// One signaling frame (ControlPlaneApi.md, section 8), either way. The fields a type does not use stay at their
    /// defaults. A message prints only its type: its candidates and peer ids stay out of every log.
    /// </summary>
    internal sealed class SignalMessage
    {
        private static readonly IReadOnlyList<SignalCandidate> NoCandidates = Array.Empty<SignalCandidate>();
        private static readonly IReadOnlyList<SignalPeer> NoPeers = Array.Empty<SignalPeer>();

        internal SignalMessage(SignalType type)
        {
            Type = type;
        }

        internal SignalType Type { get; }

        /// <summary>Gets the peer id a host-side frame names: 16 hexadecimal characters the service derives from a ticket.</summary>
        internal string? Peer { get; set; }

        internal IceCredentials? Ice { get; set; }

        internal IReadOnlyList<SignalCandidate> Candidates { get; set; } = NoCandidates;

        internal SignalCandidate? Candidate { get; set; }

        internal int DelayMs { get; set; }

        internal SignalByeReason Reason { get; set; }

        internal SignalRole Role { get; set; }

        /// <summary>Gets the peers a host's welcome lists.</summary>
        internal IReadOnlyList<SignalPeer> Peers { get; set; } = NoPeers;

        /// <summary>Gets whether the host is connected to signaling: the peer welcome's <c>hostOnline</c> or host-status <c>online</c>.</summary>
        internal bool Online { get; set; }

        /// <summary>Gets the relay-only choice a frame reports: the host's in a peer welcome or host-status, a peer's in joined.</summary>
        internal bool RelayOnly { get; set; }

        internal SignalErrorCode Error { get; set; }

        /// <summary>Gets how many candidates the service withheld, for a candidates-withheld notice.</summary>
        internal int Withheld { get; set; }

        internal static SignalMessage Offer(IceCredentials ice, IReadOnlyList<SignalCandidate> candidates) =>
            new SignalMessage(SignalType.Offer) { Ice = ice, Candidates = candidates };

        internal static SignalMessage Answer(string peer, IceCredentials ice, IReadOnlyList<SignalCandidate> candidates) =>
            new SignalMessage(SignalType.Answer) { Peer = peer, Ice = ice, Candidates = candidates };

        internal static SignalMessage Trickle(string? peer, SignalCandidate candidate) =>
            new SignalMessage(SignalType.Candidate) { Peer = peer, Candidate = candidate };

        internal static SignalMessage Punch(string? peer, int delayMs) => new SignalMessage(SignalType.Punch) { Peer = peer, DelayMs = delayMs };

        internal static SignalMessage Bye(string? peer, SignalByeReason reason) => new SignalMessage(SignalType.Bye) { Peer = peer, Reason = reason };

        public override string ToString() => Type + " signal";
    }

    /// <summary>A peer as the host's welcome lists it.</summary>
    internal readonly struct SignalPeer
    {
        internal SignalPeer(string peer, bool relayOnly)
        {
            Peer = peer;
            RelayOnly = relayOnly;
        }

        internal string Peer { get; }

        internal bool RelayOnly { get; }

        public override string ToString() => "Signaling peer";
    }
}
