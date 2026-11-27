using System;

namespace TopiaForge.Mods
{
    /// <summary>Explains why a multiplayer session reached <see cref="MultiplayerSessionState.Ended"/>.</summary>
    public enum MultiplayerSessionEndReason
    {
        /// <summary>The session has not ended, or the provider did not report a reason.</summary>
        None = 0,

        /// <summary>The local player left the session.</summary>
        Left = 1,

        /// <summary>The host ended the session or quit, so the session cannot continue.</summary>
        HostEnded = 2,

        /// <summary>The host removed the local player.</summary>
        Kicked = 3,

        /// <summary>The host removed the local player and refuses them on this server.</summary>
        Banned = 4,

        /// <summary>The connection was lost and did not resume within the reconnect grace window.</summary>
        TimedOut = 5,

        /// <summary>Another session replaced this one, for example after joining a different server.</summary>
        Replaced = 6,

        /// <summary>Admission rejected the local peer's content or build.</summary>
        AdmissionRejected = 7,

        /// <summary>The peers do not share a mutually compatible TopiaForge multiplayer protocol.</summary>
        ProtocolMismatch = 8,

        /// <summary>The transport failed in a way the session could not recover from.</summary>
        TransportFailed = 9
    }

    /// <summary>
    /// A coarse, privacy-preserving connection quality bucket. Providers derive it from round-trip time and loss;
    /// mods should treat it as presentation only.
    /// </summary>
    public enum MultiplayerConnectionQuality
    {
        /// <summary>The provider has not measured this connection yet.</summary>
        Unknown = 0,

        /// <summary>Round trips under about 60 ms with negligible loss, or the host's own process.</summary>
        Excellent = 1,

        /// <summary>Round trips under about 120 ms with little loss.</summary>
        Good = 2,

        /// <summary>Round trips under about 200 ms, or noticeable loss.</summary>
        Fair = 3,

        /// <summary>Slower or lossier than <see cref="Fair"/>.</summary>
        Poor = 4
    }

    /// <summary>
    /// An opaque player identity that is stable across sessions on one host and unlinkable across hosts. Hosts use it
    /// for per-player persistence and bans; it is derived from the player's install identity and never contains an
    /// address. There are no accounts, so a player who regenerates their identity receives a new key.
    /// </summary>
    public readonly struct MultiplayerPlayerKey : IEquatable<MultiplayerPlayerKey>
    {
        /// <summary>Creates a player key from a provider-owned opaque value.</summary>
        public MultiplayerPlayerKey(string value)
        {
            Value = MultiplayerIdentityValidation.Require(value, nameof(value));
        }

        /// <summary>Gets the opaque key value.</summary>
        public string Value { get; }

        /// <summary>Gets whether this value contains a provider-assigned key rather than the default struct value.</summary>
        public bool IsValid => !string.IsNullOrEmpty(Value);

        /// <inheritdoc/>
        public bool Equals(MultiplayerPlayerKey other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is MultiplayerPlayerKey other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);

        /// <inheritdoc/>
        public override string ToString() => Value ?? string.Empty;
    }
}
