using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>Identifies the optional multiplayer contract and loopback runtime provider.</summary>
    public static class MultiplayerModule
    {
        /// <summary>Gets the manifest id used to declare the multiplayer runtime provider.</summary>
        public const string Id = "io.github.furroxide.topiaforge.multiplayer";

        /// <summary>Gets the live-provider protocol version implemented by this stable contract line.</summary>
        public const string ProtocolVersion = "1.0.0";

        /// <summary>Gets the mutually negotiated peer range accepted by this stable contract line.</summary>
        public const string PeerVersionRange = ">=1.0.0 <2.0.0";
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
        Ended = 3
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

        /// <summary>Creates a participant snapshot.</summary>
        public MultiplayerParticipant(ParticipantId id, string displayName, bool isLocal, bool isConnected)
        {
            if (!id.IsValid)
            {
                throw new ArgumentException("A participant snapshot requires a valid participant identity.", nameof(id));
            }

            Id = id;
            DisplayName = MultiplayerIdentityValidation.RequireDisplayName(displayName, nameof(displayName));
            IsLocal = isLocal;
            IsConnected = isConnected;
        }

        /// <summary>Gets the session-scoped identity.</summary>
        public ParticipantId Id { get; }

        /// <summary>Gets the bounded player-facing name supplied by the provider.</summary>
        public string DisplayName { get; }

        /// <summary>Gets whether this participant belongs to the current process.</summary>
        public bool IsLocal { get; }

        /// <summary>Gets whether the participant remains connected.</summary>
        public bool IsConnected { get; }
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

            Id = id;
            State = state;
            ProcessKind = processKind;
            ExecutionSides = executionSides;
            LocalParticipantId = localParticipantId;
            Participants = participantCopy.AsReadOnly();
            Tick = tick;
            Seed = seed;
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
