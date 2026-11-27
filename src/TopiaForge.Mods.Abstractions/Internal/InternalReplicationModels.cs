using System;

namespace TopiaForge.Mods.Internal
{
    /// <summary>The network role of this process, as the runtime's realm rules see it.</summary>
    internal enum InternalNetRealm
    {
        /// <summary>Single player: no session links, and this process is its own authority.</summary>
        Offline = 0,

        /// <summary>A player-hosted listen server: client and server in one process.</summary>
        ListenHost = 1,

        /// <summary>Joined to another player's listen server, following its world.</summary>
        Client = 2
    }

    /// <summary>How a mod takes part in sessions, read from its effective manifest.</summary>
    internal enum InternalNetworkClass
    {
        /// <summary>No multiplayer block, or a declared session mod: every peer runs the identical package.</summary>
        Shared = 0,

        /// <summary>Runs only on the peer that enables it, and nothing it does is networked.</summary>
        ClientLocal = 1,

        /// <summary>Runs where the session's server runs: offline or on the listen host.</summary>
        ServerOnly = 2,

        /// <summary>Runs offline only, and refuses every multiplayer session.</summary>
        Standalone = 3
    }

    /// <summary>Where a mod context's code runs.</summary>
    internal enum InternalExecutionContext
    {
        /// <summary>Process-wide mod code, on every peer.</summary>
        Process = 0,

        /// <summary>The world provider's scope, on every peer, loading the world deterministically.</summary>
        WorldOwner = 1,

        /// <summary>The gamemode factory's server controller, on the host only.</summary>
        GamemodeServer = 2,

        /// <summary>One per interactive peer, owning that player's tools and HUD.</summary>
        Participant = 3
    }

    /// <summary>Why mod code is running.</summary>
    internal enum InternalIntent
    {
        /// <summary>Scripted: timers, ticks, world events and anything else no player caused.</summary>
        None = 0,

        /// <summary>A frame in which the local player's input was active.</summary>
        InputActiveFrame = 1,

        /// <summary>A user-interface callback, such as a button press.</summary>
        UiCallback = 2,

        /// <summary>A command the local player issued.</summary>
        OwnerCommand = 3,

        /// <summary>An interaction the local player triggered.</summary>
        InteractionHandler = 4,

        /// <summary>Code driving a motion lease the local player holds.</summary>
        MotionLease = 5
    }

    /// <summary>Where a world-changing call takes effect.</summary>
    internal enum InternalRoute
    {
        /// <summary>The context never runs in this realm.</summary>
        NotRun = 0,

        /// <summary>The call takes effect here, and this result is the one every peer sees.</summary>
        Canonical = 1,

        /// <summary>Sent to the host as a request, predicted locally, then adopted or rolled back.</summary>
        Forwarded = 2,

        /// <summary>A local no-op bound to the host's result with the same origin, so nothing duplicates.</summary>
        Shadow = 3,

        /// <summary>Takes effect on this peer only and is never replicated or forwarded.</summary>
        LocalOnly = 4
    }

    /// <summary>Whether a context may change the session's time scale.</summary>
    internal enum InternalTimeAuthority
    {
        /// <summary>The change applies, and online it reaches every peer.</summary>
        Apply = 0,

        /// <summary>Refused: online, only the gamemode server on the host controls time, and players follow its scale.</summary>
        NotAuthoritative = 1
    }

    /// <summary>What the microphone is used for, in priority order: a lower value comes first.</summary>
    internal enum InternalMicrophoneUse
    {
        /// <summary>The game's own robot recording, which the runtime observes and nobody acquires.</summary>
        NativeRecording = 0,

        /// <summary>RobotKit's capture of the player's speech to a robot.</summary>
        RobotDialogue = 1,

        /// <summary>Voice chat, which yields to both of the others.</summary>
        VoiceChat = 2
    }

    /// <summary>What a canonical entity is.</summary>
    internal enum InternalSpawnSource
    {
        /// <summary>Spawned by mod code: a joining peer builds a replica from its description.</summary>
        SdkSpawn = 0,

        /// <summary>World content every peer builds alike, bound by its content key rather than spawned again.</summary>
        WorldContent = 1
    }

    /// <summary>What a network reference names.</summary>
    internal enum InternalNetRefKind
    {
        /// <summary>Nothing: the default reference.</summary>
        None = 0,

        /// <summary>A networked entity.</summary>
        Entity = 1,

        /// <summary>A player.</summary>
        Player = 2
    }

    /// <summary>The bounds the replication seam enforces on what it is given.</summary>
    internal static class InternalReplicationLimits
    {
        /// <summary>The longest replica or command kind.</summary>
        internal const int MaximumKindLength = 64;

        /// <summary>The largest description or command payload: 16 KiB, so either fits one system message with room to spare.</summary>
        internal const int MaximumPayloadBytes = 16 * 1024;

        /// <summary>The longest world content key.</summary>
        internal const int MaximumContentKeyLength = 256;

        /// <summary>The longest value a network reference holds.</summary>
        internal const int MaximumReferenceLength = 128;

        /// <summary>The longest reason a microphone lease gives.</summary>
        internal const int MaximumReasonLength = 128;

        /// <summary>Whether <paramref name="kind"/> is a lowercase token: a letter or digit, then up to 63 letters, digits, dots, dashes or underscores.</summary>
        internal static bool IsKind(string? kind)
        {
            if (string.IsNullOrEmpty(kind) || kind!.Length > MaximumKindLength) return false;
            for (var index = 0; index < kind.Length; index++)
            {
                var character = kind[index];
                var alphanumeric = (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9');
                if (alphanumeric) continue;
                if (index == 0 || (character != '.' && character != '-' && character != '_')) return false;
            }

            return true;
        }

        /// <summary>1 to <paramref name="maximum"/> UTF-16 units, no surrounding white space, no control characters and no lone surrogates.</summary>
        internal static bool IsText(string? value, int maximum)
        {
            if (value == null || value.Length == 0 || value.Length > maximum) return false;
            if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1])) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (char.IsControl(character)) return false;
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) return false;
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// How peers build one canonical entity: its replica kind, its transform when it was made, what it is, and what the
    /// kind's replica factory needs. Immutable: the payload is copied in.
    /// </summary>
    internal sealed class InternalSpawnDescriptor
    {
        /// <param name="kind">The replica kind, a lowercase token that names the factory peers build it with, such as <c>creator.content</c>.</param>
        /// <param name="transform">Where the entity was when it was made.</param>
        /// <param name="payload">What the replica factory needs, at most 16 KiB; none by default.</param>
        /// <param name="source">Whether mod code spawned it or a world provider built it as world content.</param>
        /// <param name="contentKey">World content only: the key every peer computes alike for it, 1 to 256 printable characters.</param>
        internal InternalSpawnDescriptor(
            string kind,
            TransformState transform,
            byte[]? payload = null,
            InternalSpawnSource source = InternalSpawnSource.SdkSpawn,
            string? contentKey = null)
        {
            if (!InternalReplicationLimits.IsKind(kind)) throw new ArgumentException("A replica kind is a lowercase token of at most 64 characters.", nameof(kind));
            if (payload != null && payload.Length > InternalReplicationLimits.MaximumPayloadBytes)
            {
                throw new ArgumentException("A description is at most 16 KiB.", nameof(payload));
            }

            if (source != InternalSpawnSource.SdkSpawn && source != InternalSpawnSource.WorldContent) throw new ArgumentOutOfRangeException(nameof(source));
            if (source == InternalSpawnSource.WorldContent)
            {
                if (!InternalReplicationLimits.IsText(contentKey, InternalReplicationLimits.MaximumContentKeyLength))
                {
                    throw new ArgumentException("World content needs a content key of 1 to 256 printable characters.", nameof(contentKey));
                }
            }
            else if (contentKey != null)
            {
                throw new ArgumentException("Only world content has a content key.", nameof(contentKey));
            }

            Kind = kind;
            Transform = transform;
            Source = source;
            ContentKey = contentKey;
            Payload = payload == null || payload.Length == 0 ? Array.Empty<byte>() : (byte[])payload.Clone();
        }

        /// <summary>Gets the replica kind.</summary>
        internal string Kind { get; }

        /// <summary>Gets where the entity was when it was made.</summary>
        internal TransformState Transform { get; }

        /// <summary>Gets what the replica factory needs.</summary>
        internal ReadOnlyMemory<byte> Payload { get; }

        /// <summary>Gets whether mod code spawned it or a world provider built it.</summary>
        internal InternalSpawnSource Source { get; }

        /// <summary>Gets the world content key, or null for a spawn.</summary>
        internal string? ContentKey { get; }
    }

    /// <summary>One routed command, as its handler sees it. Immutable: the payload is copied in.</summary>
    internal sealed class InternalCommand
    {
        internal InternalCommand(string kind, byte[] payload, string? sender)
        {
            if (!InternalReplicationLimits.IsKind(kind)) throw new ArgumentException("A command kind is a lowercase token of at most 64 characters.", nameof(kind));
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (payload.Length > InternalReplicationLimits.MaximumPayloadBytes) throw new ArgumentException("A command is at most 16 KiB.", nameof(payload));
            if (sender != null && !InternalReplicationLimits.IsText(sender, InternalReplicationLimits.MaximumReferenceLength))
            {
                throw new ArgumentException("A sender is a participant id of 1 to 128 printable characters.", nameof(sender));
            }

            Kind = kind;
            Payload = payload.Length == 0 ? Array.Empty<byte>() : (byte[])payload.Clone();
            Sender = sender;
        }

        /// <summary>Gets the command's kind.</summary>
        internal string Kind { get; }

        /// <summary>Gets what the command carries.</summary>
        internal ReadOnlyMemory<byte> Payload { get; }

        /// <summary>
        /// Gets the participant id the host gave the player who sent the command, or null for this process's own. Until
        /// authority routing (WS3.7) forwards commands, every command is this process's own.
        /// </summary>
        internal string? Sender { get; }

        /// <summary>Gets whether this process submitted the command itself.</summary>
        internal bool IsLocal => Sender == null;
    }

    /// <summary>A reference to an entity or a player that names it on every peer. It holds a runtime id, never an address.</summary>
    internal readonly struct InternalNetRef : IEquatable<InternalNetRef>
    {
        /// <param name="kind">What the reference names.</param>
        /// <param name="value">The runtime id it names that by, 1 to 128 printable characters.</param>
        internal InternalNetRef(InternalNetRefKind kind, string value)
        {
            if (kind != InternalNetRefKind.Entity && kind != InternalNetRefKind.Player) throw new ArgumentOutOfRangeException(nameof(kind));
            if (!InternalReplicationLimits.IsText(value, InternalReplicationLimits.MaximumReferenceLength))
            {
                throw new ArgumentException("A reference holds an id of 1 to 128 printable characters.", nameof(value));
            }

            Kind = kind;
            Value = value;
        }

        /// <summary>Gets what the reference names; <see cref="InternalNetRefKind.None"/> for the default reference.</summary>
        internal InternalNetRefKind Kind { get; }

        /// <summary>Gets the id the reference names it by, or null for the default reference.</summary>
        internal string? Value { get; }

        /// <summary>Gets whether the reference names anything.</summary>
        internal bool IsSpecified => Kind != InternalNetRefKind.None;

        public bool Equals(InternalNetRef other) => Kind == other.Kind && string.Equals(Value, other.Value, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is InternalNetRef other && Equals(other);

        public override int GetHashCode() => ((int)Kind * 397) ^ (Value == null ? 0 : StringComparer.Ordinal.GetHashCode(Value));

        public override string ToString() => IsSpecified ? Kind + ":" + Value : "none";
    }
}
