namespace TopiaForge.ModManager
{
    /// <summary>What presence did, for diagnostics and tests. Counts only: no player, id or address. Main thread only.</summary>
    internal sealed class PresenceCounters
    {
        /// <summary>Gets or sets the poses this player sent its host.</summary>
        internal long PosesSent { get; set; }

        /// <summary>Gets or sets the poses a host took from its players, before its checks.</summary>
        internal long PosesReceived { get; set; }

        internal long PosesAccepted { get; set; }

        /// <summary>Gets or sets the poses sent before their sender heard a correction, dropped without a strike.</summary>
        internal long PosesIgnored { get; set; }

        /// <summary>Gets or sets the poses that arrived after a newer one from the same player.</summary>
        internal long PosesStale { get; set; }

        /// <summary>Gets or sets the poses that broke the host's rules and were corrected.</summary>
        internal long Corrections { get; set; }

        /// <summary>Gets or sets the players removed for breaking the host's rules once too often.</summary>
        internal long Kicks { get; set; }

        /// <summary>Gets or sets the payloads that could not be read: a version, a type or a field out of bounds.</summary>
        internal long Malformed { get; set; }

        /// <summary>
        /// Gets or sets what a peer sent in another's voice: a player's datagram or message in the host's format, which would
        /// speak for other players, or anything from a participant the session does not list.
        /// </summary>
        internal long Forged { get; set; }

        /// <summary>Gets or sets the states whose health a host refused in part or whole.</summary>
        internal long StatesRefused { get; set; }

        /// <summary>Gets or sets the claims a host refused: talking without voice, away while moving.</summary>
        internal long FlagsRefused { get; set; }

        /// <summary>Gets or sets the datagrams of relayed poses a host sent, one per player.</summary>
        internal long RelaysSent { get; set; }

        /// <summary>Gets or sets the poses those datagrams carried.</summary>
        internal long RelayedPoses { get; set; }

        /// <summary>Gets or sets the bytes of every datagram presence sent.</summary>
        internal long DatagramBytes { get; set; }

        /// <summary>Gets or sets the players a host placed on the spawn ring.</summary>
        internal long Placements { get; set; }

        /// <summary>Gets or sets the corrections and placements this player took from its host.</summary>
        internal long MovesTaken { get; set; }

        /// <summary>Gets or sets the avatars made, and the bodies worn on them.</summary>
        internal long AvatarsMade { get; set; }

        internal long BodiesWorn { get; set; }
    }
}
