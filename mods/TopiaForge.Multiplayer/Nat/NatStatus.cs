using System.Globalization;

namespace TopiaForge.Multiplayer.NatTraversal
{
    /// <summary>How gathering went: whether any server answered, and whether the relay came up.</summary>
    internal enum NatGathering
    {
        Idle = 0,
        Gathering = 1,
        Complete = 2,

        /// <summary>The servers answered but the relay did not come up; direct paths may still work.</summary>
        RelayUnavailable = 3,

        /// <summary>No STUN or TURN server answered at all: the network blocks UDP, or the service's servers are unreachable.</summary>
        UdpBlocked = 4
    }

    /// <summary>Where a join stands. A host has no stage of its own; it reports per-peer counts instead.</summary>
    internal enum NatJoinStage
    {
        Idle = 0,
        Gathering = 1,
        Signaling = 2,
        Checking = 3,
        Connecting = 4,
        Connected = 5,
        Failed = 6,
        Ended = 7
    }

    /// <summary>Why traversal failed, in words a status line can carry. None of them names an address.</summary>
    internal enum NatFailure
    {
        None = 0,

        /// <summary>Gathering heard nothing from any server.</summary>
        UdpBlocked = 1,

        /// <summary>Relay-only needs the relay, and it did not come up.</summary>
        RelayUnavailable = 2,

        /// <summary>The host is not connected to signaling.</summary>
        HostOffline = 3,

        /// <summary>The host never answered the offer.</summary>
        NoAnswer = 4,

        /// <summary>Checks found no pair that works, or the handshake over it failed.</summary>
        NoRoute = 5,

        /// <summary>The signaling socket could not be opened or kept.</summary>
        SignalingFailed = 6,

        /// <summary>The host's session ended, or it said goodbye.</summary>
        HostEnded = 7,

        /// <summary>This side stopped the attempt.</summary>
        Cancelled = 8,

        /// <summary>The service stopped honouring the join ticket: its invite was withdrawn or expired, or the server stopped being public.</summary>
        TicketRevoked = 9
    }

    /// <summary>The route a session's connection takes now.</summary>
    internal enum NatRoute
    {
        None = 0,
        Relayed = 1,
        Direct = 2
    }

    /// <summary>
    /// The NAT layer as the main thread sees it: enums and counts, replaced whole on every change. It holds no address,
    /// candidate or peer id by construction, and <see cref="ToString"/> prints only what it holds.
    /// </summary>
    internal sealed class NatStatus
    {
        internal static readonly NatStatus Idle = new NatStatus(NatGathering.Idle, NatJoinStage.Idle, NatFailure.None, NatRoute.None, false, 0, 0, 0, 0, 0);

        internal NatStatus(NatGathering gathering, NatJoinStage stage, NatFailure failure, NatRoute route, bool relayReady, int sessions, int direct, int relayed, int rotations, int withheldByService)
        {
            Gathering = gathering;
            Stage = stage;
            Failure = failure;
            Route = route;
            RelayReady = relayReady;
            Sessions = sessions;
            DirectSessions = direct;
            RelayedSessions = relayed;
            Rotations = rotations;
            WithheldByService = withheldByService;
        }

        internal NatGathering Gathering { get; }

        internal NatJoinStage Stage { get; }

        internal NatFailure Failure { get; }

        /// <summary>Gets a join's route; a host leaves it at <see cref="NatRoute.None"/> and counts sessions instead.</summary>
        internal NatRoute Route { get; }

        internal bool RelayReady { get; }

        internal int Sessions { get; }

        internal int DirectSessions { get; }

        internal int RelayedSessions { get; }

        /// <summary>Gets how many times the relay moved to a fresh credential.</summary>
        internal int Rotations { get; }

        /// <summary>Gets how many candidates the service reported withholding: always 0 when this side withholds first.</summary>
        internal int WithheldByService { get; }

        public override string ToString() =>
            "NAT " + Gathering + ", " + Stage + ", " + Failure + ", " + Route + ", relay " + (RelayReady ? "ready" : "not ready") + ", " +
            Sessions.ToString(CultureInfo.InvariantCulture) + " sessions (" + DirectSessions.ToString(CultureInfo.InvariantCulture) + " direct, " +
            RelayedSessions.ToString(CultureInfo.InvariantCulture) + " relayed)";
    }
}
