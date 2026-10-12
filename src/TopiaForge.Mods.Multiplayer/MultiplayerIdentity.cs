using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>Identifies the optional multiplayer contract and loopback runtime provider.</summary>
    public static class MultiplayerModule
    {
        /// <summary>Gets the manifest id used to declare the multiplayer runtime provider.</summary>
        public const string Id = "io.github.furroxide.topiaforge.multiplayer";

        /// <summary>
        /// Gets the live-provider protocol version implemented by this contract line. It is a static field rather than
        /// a constant so compiled mods read the installed SDK's value instead of one baked in when they were built.
        /// </summary>
        public static readonly string ProtocolVersion = "1.0.0";

        /// <summary>Gets the mutually negotiated peer range accepted by this contract line.</summary>
        public static readonly string PeerVersionRange = ">=1.0.0 <2.0.0";
    }

    /// <summary>Describes whether the current process has interactive presentation systems.</summary>
    public enum MultiplayerProcessKind
    {
        /// <summary>A process with local input and presentation services.</summary>
        Interactive = 0,

        /// <summary>A process without a local player, input, rendering, or audio.</summary>
        Headless = 1
    }

    /// <summary>Identifies the logical multiplayer sides active in one process.</summary>
    [Flags]
    public enum MultiplayerExecutionSide
    {
        /// <summary>No multiplayer side is active.</summary>
        None = 0,

        /// <summary>The local presentation and input side is active.</summary>
        Client = 1,

        /// <summary>The canonical simulation side is active.</summary>
        Server = 2
    }

    /// <summary>Describes admission and snapshot readiness for a multiplayer session.</summary>
    public enum MultiplayerSessionState
    {
        /// <summary>The transport or loopback provider is establishing the session.</summary>
        Connecting = 0,

        /// <summary>Canonical state is being applied before callbacks may run.</summary>
        Synchronizing = 1,

        /// <summary>The canonical snapshot is complete and session APIs are usable.</summary>
        Ready = 2,

        /// <summary>The session has ended and no further work may be submitted.</summary>
        Ended = 3,

        /// <summary>
        /// The connection to the host was interrupted and is being resumed within the reconnect grace window.
        /// Canonical state is frozen until the session returns to <see cref="Ready"/> or ends.
        /// </summary>
        Reconnecting = 4
    }

    /// <summary>An opaque identity that remains stable for one multiplayer session.</summary>
    public readonly struct MultiplayerSessionId : IEquatable<MultiplayerSessionId>
    {
        /// <summary>Creates a session identity from a provider-owned opaque value.</summary>
        public MultiplayerSessionId(string value)
        {
            Value = MultiplayerIdentityValidation.Require(value, nameof(value));
        }

        /// <summary>Gets the opaque identity value.</summary>
        public string Value { get; }

        /// <summary>Gets whether this value contains a provider-assigned identity rather than the default struct value.</summary>
        public bool IsValid => !string.IsNullOrEmpty(Value);

        /// <inheritdoc/>
        public bool Equals(MultiplayerSessionId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is MultiplayerSessionId other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);

        /// <inheritdoc/>
        public override string ToString() => Value ?? string.Empty;
    }

    /// <summary>An opaque participant identity scoped to one multiplayer session.</summary>
    public readonly struct ParticipantId : IEquatable<ParticipantId>
    {
        /// <summary>Creates a participant identity from a provider-owned opaque value.</summary>
        public ParticipantId(string value)
        {
            Value = MultiplayerIdentityValidation.Require(value, nameof(value));
        }

        /// <summary>Gets the opaque identity value.</summary>
        public string Value { get; }

        /// <summary>Gets whether this value contains a provider-assigned identity rather than the default struct value.</summary>
        public bool IsValid => !string.IsNullOrEmpty(Value);

        /// <inheritdoc/>
        public bool Equals(ParticipantId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is ParticipantId other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);

        /// <inheritdoc/>
        public override string ToString() => Value ?? string.Empty;
    }

    /// <summary>An opaque server-assigned identity for one replicated session object.</summary>
    public readonly struct NetworkObjectId : IEquatable<NetworkObjectId>
    {
        /// <summary>Creates a network object identity from a provider-owned opaque value.</summary>
        public NetworkObjectId(string value)
        {
            Value = MultiplayerIdentityValidation.Require(value, nameof(value));
        }

        /// <summary>Gets the opaque identity value.</summary>
        public string Value { get; }

        /// <summary>Gets whether this value contains a provider-assigned identity rather than the default struct value.</summary>
        public bool IsValid => !string.IsNullOrEmpty(Value);

        /// <inheritdoc/>
        public bool Equals(NetworkObjectId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is NetworkObjectId other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);

        /// <inheritdoc/>
        public override string ToString() => Value ?? string.Empty;
    }

    /// <summary>A monotonically increasing canonical simulation tick.</summary>
    public readonly struct NetworkTick : IEquatable<NetworkTick>, IComparable<NetworkTick>
    {
        /// <summary>Creates a network tick.</summary>
        public NetworkTick(ulong value)
        {
            Value = value;
        }

        /// <summary>Gets the unsigned tick value.</summary>
        public ulong Value { get; }

        /// <inheritdoc/>
        public int CompareTo(NetworkTick other) => Value.CompareTo(other.Value);

        /// <inheritdoc/>
        public bool Equals(NetworkTick other) => Value == other.Value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is NetworkTick other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => Value.GetHashCode();

        /// <inheritdoc/>
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A provider-selected deterministic seed scoped to one session.</summary>
    public readonly struct SessionSeed : IEquatable<SessionSeed>
    {
        /// <summary>Creates a deterministic session seed.</summary>
        public SessionSeed(ulong value)
        {
            Value = value;
        }

        /// <summary>Gets the unsigned seed value.</summary>
        public ulong Value { get; }

        /// <inheritdoc/>
        public bool Equals(SessionSeed other) => Value == other.Value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is SessionSeed other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => Value.GetHashCode();

        /// <inheritdoc/>
        public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Immutable display and ownership information for one admitted participant.</summary>
    public sealed class MultiplayerParticipant
    {
        /// <summary>Gets the maximum player-facing display-name length accepted from a provider.</summary>
        public const int MaximumDisplayNameLength = 128;

        /// <summary>Gets the largest ping a provider may report.</summary>
        public const int MaximumPingMilliseconds = 60000;

        /// <summary>Creates a participant snapshot.</summary>
        public MultiplayerParticipant(ParticipantId id, string displayName, bool isLocal, bool isConnected)
            : this(id, displayName, isLocal, isConnected, false, MultiplayerConnectionQuality.Unknown, null, default)
        {
        }

        /// <summary>Creates a participant snapshot with host, connection, and player-key details.</summary>
        /// <param name="id">The session-scoped identity.</param>
        /// <param name="displayName">The bounded player-facing name.</param>
        /// <param name="isLocal">Whether the participant belongs to the current process.</param>
        /// <param name="isConnected">Whether the participant remains connected.</param>
        /// <param name="isHost">Whether the participant hosts the session.</param>
        /// <param name="quality">The bucketed connection quality.</param>
        /// <param name="pingMilliseconds">The measured ping, rounded to the nearest 10 ms, or null when unknown.</param>
        /// <param name="playerKey">The player's key on this host, or the default value when unknown.</param>
        public MultiplayerParticipant(
            ParticipantId id,
            string displayName,
            bool isLocal,
            bool isConnected,
            bool isHost,
            MultiplayerConnectionQuality quality,
            int? pingMilliseconds,
            MultiplayerPlayerKey playerKey)
        {
            if (!id.IsValid)
            {
                throw new ArgumentException("A participant snapshot requires a valid participant identity.", nameof(id));
            }

            if (!Enum.IsDefined(typeof(MultiplayerConnectionQuality), quality))
            {
                throw new ArgumentOutOfRangeException(nameof(quality));
            }

            if (pingMilliseconds.HasValue &&
                (pingMilliseconds.Value < 0 || pingMilliseconds.Value > MaximumPingMilliseconds))
            {
                throw new ArgumentOutOfRangeException(nameof(pingMilliseconds));
            }

            Id = id;
            DisplayName = MultiplayerIdentityValidation.RequireDisplayName(displayName, nameof(displayName));
            IsLocal = isLocal;
            IsConnected = isConnected;
            IsHost = isHost;
            Quality = quality;
            // Coarse buckets keep ping useful for presentation without exposing fine-grained timing.
            PingMilliseconds = pingMilliseconds.HasValue ? (pingMilliseconds.Value + 5) / 10 * 10 : (int?)null;
            PlayerKey = playerKey;
        }

        /// <summary>Gets the session-scoped identity.</summary>
        public ParticipantId Id { get; }

        /// <summary>Gets the bounded player-facing name supplied by the provider.</summary>
        public string DisplayName { get; }

        /// <summary>Gets whether this participant belongs to the current process.</summary>
        public bool IsLocal { get; }

        /// <summary>Gets whether the participant remains connected.</summary>
        public bool IsConnected { get; }

        /// <summary>Gets whether the participant hosts the session.</summary>
        public bool IsHost { get; }

        /// <summary>Gets the bucketed connection quality.</summary>
        public MultiplayerConnectionQuality Quality { get; }

        /// <summary>Gets the ping rounded to 10 ms, or null when unknown.</summary>
        public int? PingMilliseconds { get; }

        /// <summary>Gets the player's key on this host, or the default value when the provider has none.</summary>
        public MultiplayerPlayerKey PlayerKey { get; }
    }

    /// <summary>Immutable readiness, side, participant, and clock state for a session.</summary>
    public sealed class MultiplayerSessionSnapshot
    {
        /// <summary>Gets the maximum number of participant records accepted in one bounded snapshot.</summary>
        public const int MaximumParticipantCount = 256;

        /// <summary>Creates a session snapshot.</summary>
        public MultiplayerSessionSnapshot(
            MultiplayerSessionId id,
            MultiplayerSessionState state,
            MultiplayerProcessKind processKind,
            MultiplayerExecutionSide executionSides,
            ParticipantId? localParticipantId,
            IReadOnlyList<MultiplayerParticipant> participants,
            NetworkTick tick,
            SessionSeed seed)
            : this(
                id,
                state,
                processKind,
                executionSides,
                localParticipantId,
                participants,
                tick,
                seed,
                null,
                MultiplayerSessionEndReason.None)
        {
        }

        /// <summary>Creates a session snapshot that also names the host and, once ended, why the session ended.</summary>
        public MultiplayerSessionSnapshot(
            MultiplayerSessionId id,
            MultiplayerSessionState state,
            MultiplayerProcessKind processKind,
            MultiplayerExecutionSide executionSides,
            ParticipantId? localParticipantId,
            IReadOnlyList<MultiplayerParticipant> participants,
            NetworkTick tick,
            SessionSeed seed,
            ParticipantId? hostParticipantId,
            MultiplayerSessionEndReason endReason)
        {
            if (!id.IsValid)
            {
                throw new ArgumentException("A session snapshot requires a valid session identity.", nameof(id));
            }

            if (!Enum.IsDefined(typeof(MultiplayerSessionState), state)) throw new ArgumentOutOfRangeException(nameof(state));
            if (!Enum.IsDefined(typeof(MultiplayerProcessKind), processKind)) throw new ArgumentOutOfRangeException(nameof(processKind));
            if ((executionSides & ~(MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(executionSides));
            }

            if (participants == null) throw new ArgumentNullException(nameof(participants));
            if (participants.Count > MaximumParticipantCount)
            {
                throw new ArgumentException(
                    "A session snapshot cannot contain more than " + MaximumParticipantCount + " participants.",
                    nameof(participants));
            }

            if (localParticipantId.HasValue && !localParticipantId.Value.IsValid)
            {
                throw new ArgumentException("The local participant identity must be valid when present.", nameof(localParticipantId));
            }

            if (processKind == MultiplayerProcessKind.Headless && localParticipantId.HasValue)
            {
                throw new ArgumentException("A headless process cannot expose a local participant.", nameof(localParticipantId));
            }

            if (processKind == MultiplayerProcessKind.Headless &&
                (executionSides & MultiplayerExecutionSide.Client) != 0)
            {
                throw new ArgumentException(
                    "A headless process cannot expose the local presentation and input side.",
                    nameof(executionSides));
            }

            if (localParticipantId.HasValue &&
                (processKind != MultiplayerProcessKind.Interactive ||
                 (executionSides & MultiplayerExecutionSide.Client) == 0))
            {
                throw new ArgumentException(
                    "A local participant requires an interactive logical client side.",
                    nameof(localParticipantId));
            }

            var participantCopy = new List<MultiplayerParticipant>(participants.Count);
            var participantIds = new HashSet<ParticipantId>();
            var matchingLocalCount = 0;
            foreach (var participant in participants)
            {
                if (participant == null)
                {
                    throw new ArgumentException("Participant snapshots cannot contain null entries.", nameof(participants));
                }

                if (!participantIds.Add(participant.Id))
                {
                    throw new ArgumentException(
                        "Participant snapshots cannot contain duplicate participant identities.",
                        nameof(participants));
                }

                if (participant.IsLocal)
                {
                    if (!localParticipantId.HasValue || !participant.Id.Equals(localParticipantId.Value))
                    {
                        throw new ArgumentException(
                            "The local participant marker must match LocalParticipantId.",
                            nameof(participants));
                    }

                    matchingLocalCount++;
                }
                else if (localParticipantId.HasValue && participant.Id.Equals(localParticipantId.Value))
                {
                    throw new ArgumentException(
                        "The LocalParticipantId record must be marked local.",
                        nameof(participants));
                }

                participantCopy.Add(participant);
            }

            if (localParticipantId.HasValue && matchingLocalCount != 1)
            {
                throw new ArgumentException(
                    "A local participant identity must have exactly one matching participant record.",
                    nameof(participants));
            }

            ValidateHost(hostParticipantId, participantCopy);
            if (!Enum.IsDefined(typeof(MultiplayerSessionEndReason), endReason))
            {
                throw new ArgumentOutOfRangeException(nameof(endReason));
            }

            if (endReason != MultiplayerSessionEndReason.None && state != MultiplayerSessionState.Ended)
            {
                throw new ArgumentException("Only an ended session can report an end reason.", nameof(endReason));
            }

            Id = id;
            State = state;
            ProcessKind = processKind;
            ExecutionSides = executionSides;
            LocalParticipantId = localParticipantId;
            Participants = participantCopy.AsReadOnly();
            Tick = tick;
            Seed = seed;
            HostParticipantId = hostParticipantId;
            EndReason = endReason;
        }

        /// <summary>Gets the opaque session identity.</summary>
        public MultiplayerSessionId Id { get; }

        /// <summary>Gets the admission and snapshot readiness state.</summary>
        public MultiplayerSessionState State { get; }

        /// <summary>Gets whether this process is interactive or headless.</summary>
        public MultiplayerProcessKind ProcessKind { get; }

        /// <summary>Gets the logical sides active in this process.</summary>
        public MultiplayerExecutionSide ExecutionSides { get; }

        /// <summary>Gets the local participant, or null on a headless server.</summary>
        public ParticipantId? LocalParticipantId { get; }

        /// <summary>Gets admitted participants in provider-defined stable order.</summary>
        public IReadOnlyList<MultiplayerParticipant> Participants { get; }

        /// <summary>Gets the latest canonical simulation tick.</summary>
        public NetworkTick Tick { get; }

        /// <summary>Gets the deterministic session seed.</summary>
        public SessionSeed Seed { get; }

        /// <summary>Gets whether the canonical server side is active in this process.</summary>
        public bool HasWorldAuthority => (ExecutionSides & MultiplayerExecutionSide.Server) != 0;

        /// <summary>Gets whether local presentation is available in this process.</summary>
        public bool HasPresentation => ProcessKind == MultiplayerProcessKind.Interactive
            && (ExecutionSides & MultiplayerExecutionSide.Client) != 0;

        /// <summary>Gets the participant hosting the session, or null when there is none or the provider did not say.</summary>
        public ParticipantId? HostParticipantId { get; }

        /// <summary>Gets why the session ended, or <see cref="MultiplayerSessionEndReason.None"/> while it is live.</summary>
        public MultiplayerSessionEndReason EndReason { get; }

        /// <summary>Tries to find an admitted participant by identity.</summary>
        public bool TryGetParticipant(ParticipantId id, out MultiplayerParticipant? participant)
        {
            foreach (var candidate in Participants)
            {
                if (candidate.Id.Equals(id))
                {
                    participant = candidate;
                    return true;
                }
            }

            participant = null;
            return false;
        }

        private static void ValidateHost(ParticipantId? hostParticipantId, IReadOnlyList<MultiplayerParticipant> participants)
        {
            var markedHosts = 0;
            var hostMarkerMatches = false;
            foreach (var participant in participants)
            {
                if (!participant.IsHost) continue;
                markedHosts++;
                hostMarkerMatches = hostParticipantId.HasValue && participant.Id.Equals(hostParticipantId.Value);
            }

            if (markedHosts > 1)
            {
                throw new ArgumentException("A session snapshot can mark at most one host.", nameof(participants));
            }

            if (!hostParticipantId.HasValue)
            {
                if (markedHosts != 0)
                {
                    throw new ArgumentException(
                        "A participant marked as host requires HostParticipantId.",
                        nameof(hostParticipantId));
                }

                return;
            }

            if (!hostParticipantId.Value.IsValid)
            {
                throw new ArgumentException("The host participant identity must be valid when present.", nameof(hostParticipantId));
            }

            if (!hostMarkerMatches)
            {
                throw new ArgumentException(
                    "HostParticipantId must name the one participant marked as host.",
                    nameof(hostParticipantId));
            }
        }
    }

    internal static class MultiplayerIdentityValidation
    {
        internal static string Require(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            {
                throw new ArgumentException("A non-empty opaque identity of at most 128 characters is required.", parameterName);
            }

            foreach (var character in value)
            {
                if (char.IsControl(character))
                {
                    throw new ArgumentException("Opaque identities cannot contain control characters.", parameterName);
                }
            }

            return value;
        }

        internal static string RequireDisplayName(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MultiplayerParticipant.MaximumDisplayNameLength)
            {
                throw new ArgumentException(
                    "A non-empty display name of at most " + MultiplayerParticipant.MaximumDisplayNameLength +
                    " characters is required.",
                    parameterName);
            }

            foreach (var character in value)
            {
                if (char.IsControl(character))
                {
                    throw new ArgumentException("Display names cannot contain control characters.", parameterName);
                }
            }

            return value;
        }
    }
}
