using System;

namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// The player intent code acts on, as one context's mod sees it (WS3.4): what caused it, and whose it is. It names a
    /// player by the participant id the host gave it, never by an address. The default record is scripted: no intent.
    /// </summary>
    internal readonly struct InternalIntentRecord : IEquatable<InternalIntentRecord>
    {
        /// <param name="source">What caused it; <see cref="InternalIntent.None"/> for scripted code.</param>
        /// <param name="participantId">
        /// Null for this process's own player; otherwise the participant a listen host acts for (WS3.7), 1 to 128 printable
        /// characters. Scripted code has none.
        /// </param>
        internal InternalIntentRecord(InternalIntent source, string? participantId)
        {
            if (source < InternalIntent.None || source > InternalIntent.MotionLease) throw new ArgumentOutOfRangeException(nameof(source));
            if (participantId != null)
            {
                if (source == InternalIntent.None) throw new ArgumentException("Scripted code acts for nobody.", nameof(participantId));
                if (!InternalReplicationLimits.IsText(participantId, InternalReplicationLimits.MaximumReferenceLength))
                {
                    throw new ArgumentException("A participant id is 1 to 128 printable characters.", nameof(participantId));
                }
            }

            Source = source;
            ParticipantId = participantId;
        }

        /// <summary>Gets what caused the intent; <see cref="InternalIntent.None"/> for scripted code.</summary>
        internal InternalIntent Source { get; }

        /// <summary>Gets the participant the host acts for, or null for this process's own player and for scripted code.</summary>
        internal string? ParticipantId { get; }

        /// <summary>Gets whether the code acts on a player's intent at all.</summary>
        internal bool IsPlayerIntent => Source != InternalIntent.None;

        /// <summary>Gets whether this process's own player is the one acting.</summary>
        internal bool IsLocalPlayer => IsPlayerIntent && ParticipantId == null;

        public bool Equals(InternalIntentRecord other) =>
            Source == other.Source && string.Equals(ParticipantId, other.ParticipantId, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is InternalIntentRecord other && Equals(other);

        public override int GetHashCode() => ((int)Source * 397) ^ (ParticipantId == null ? 0 : StringComparer.Ordinal.GetHashCode(ParticipantId));

        /// <summary>Names the source and whose intent it is, never a participant's id: logs must not carry one.</summary>
        public override string ToString() =>
            !IsPlayerIntent ? "scripted" : Source + (IsLocalPlayer ? " of the local player" : " of a participant");
    }
}
