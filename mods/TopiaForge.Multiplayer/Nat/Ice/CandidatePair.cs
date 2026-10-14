using TopiaForge.Multiplayer.NatTraversal.Stun;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal.Ice
{
    /// <summary>
    /// A local base: where this side's checks and datagrams leave from. The socket is one base; each TURN allocation is
    /// another, numbered as its relayed paths are. A relay-only side has no socket base at all.
    /// </summary>
    internal sealed class LocalBase
    {
        private LocalBase(bool isRelay, int allocation, uint priority)
        {
            IsRelay = isRelay;
            Allocation = allocation;
            Priority = priority;
        }

        internal bool IsRelay { get; }

        /// <summary>Gets the allocation number of a relay base; 0 for the socket.</summary>
        internal int Allocation { get; }

        /// <summary>Gets the priority this side's candidate at the base carries, for pair priorities.</summary>
        internal uint Priority { get; }

        /// <summary>Gets or sets whether the base is being replaced: nothing new is checked from it.</summary>
        internal bool Retiring { get; set; }

        internal static LocalBase Socket(uint priority) => new LocalBase(false, 0, priority);

        internal static LocalBase Relay(int allocation, uint priority) => new LocalBase(true, allocation, priority);

        public override string ToString() => IsRelay ? "Relay base" : "Socket base";
    }

    internal enum CandidatePairState
    {
        Waiting = 0,
        InProgress = 1,
        Succeeded = 2,
        Failed = 3
    }

    /// <summary>How a pair's datagrams travel: directly, or through one side's relay or both.</summary>
    internal enum PairRoute
    {
        Direct = 0,
        HostRelay = 1,
        JoinerRelay = 2,
        BothRelays = 3
    }

    /// <summary>
    /// One local base and one remote candidate, and the transport path between them. Pairs are network-layer data: the
    /// route keeper reads them, and <see cref="ToString"/> names only the route.
    /// </summary>
    internal sealed class CandidatePair
    {
        internal CandidatePair(LocalBase local, IceCandidate remote, TransportPath path, ulong priority, int order)
        {
            Local = local;
            Remote = remote;
            Path = path;
            Priority = priority;
            Order = order;
        }

        internal LocalBase Local { get; }

        internal IceCandidate Remote { get; }

        internal TransportPath Path { get; }

        internal ulong Priority { get; }

        /// <summary>Gets the pair's creation order: among relayed pairs of one route, the newest wins.</summary>
        internal int Order { get; }

        internal CandidatePairState State { get; set; }

        internal StunTransaction? Check { get; set; }

        /// <summary>Gets or sets whether a triggered check waits for this pair at the front of the queue.</summary>
        internal bool Triggered { get; set; }

        internal long SucceededAt { get; set; }

        /// <summary>Gets or sets whether consent freshness runs on the pair: it carries, or stands by for, the session.</summary>
        internal bool ConsentWanted { get; set; }

        internal long ConsentExpiresAt { get; set; }

        internal long NextConsentAt { get; set; }

        internal StunTransaction? ConsentCheck { get; set; }

        /// <summary>Gets or sets how often the route keeper asked the transport to move onto this pair.</summary>
        internal int Probes { get; set; }

        internal long LastProbeAt { get; set; }

        internal bool IsRelayed => Local.IsRelay || Remote.Kind == IceCandidateKind.Relayed;

        /// <summary>Gets the pair's route as both sides see it, which is why the side that hosts must be named.</summary>
        internal PairRoute Route(bool localIsHost)
        {
            var localRelay = Local.IsRelay;
            var remoteRelay = Remote.Kind == IceCandidateKind.Relayed;
            if (localRelay && remoteRelay) return PairRoute.BothRelays;
            if (!localRelay && !remoteRelay) return PairRoute.Direct;
            return localRelay == localIsHost ? PairRoute.HostRelay : PairRoute.JoinerRelay;
        }

        public override string ToString() => (IsRelayed ? "Relayed" : "Direct") + " pair";
    }
}
