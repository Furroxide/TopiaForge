using System;

namespace TopiaForge.Mods
{
    /// <summary>Identifies which participants a <see cref="PresentationTarget"/> selects.</summary>
    public enum PresentationTargetKind
    {
        /// <summary>Every interactive participant.</summary>
        Everyone = 0,

        /// <summary>Only the participant whose command produced the event.</summary>
        Sender = 1,

        /// <summary>Every interactive participant except the command's sender.</summary>
        Others = 2,

        /// <summary>Exactly one named participant.</summary>
        Only = 3,

        /// <summary>Every interactive participant except one named participant.</summary>
        AllExcept = 4
    }

    /// <summary>
    /// Selects the recipients of a transient presentation event. <see cref="Sender"/> and <see cref="Others"/> are
    /// relative to the command being handled; outside a command they refer to the local participant.
    /// </summary>
    public readonly struct PresentationTarget : IEquatable<PresentationTarget>
    {
        private PresentationTarget(PresentationTargetKind kind, ParticipantId participant)
        {
            Kind = kind;
            Participant = participant;
        }

        /// <summary>Gets a target selecting every interactive participant.</summary>
        public static PresentationTarget Everyone => new PresentationTarget(PresentationTargetKind.Everyone, default);

        /// <summary>Gets a target selecting only the command's sender.</summary>
        public static PresentationTarget Sender => new PresentationTarget(PresentationTargetKind.Sender, default);

        /// <summary>Gets a target selecting everyone except the command's sender.</summary>
        public static PresentationTarget Others => new PresentationTarget(PresentationTargetKind.Others, default);

        /// <summary>Gets which participants this target selects.</summary>
        public PresentationTargetKind Kind { get; }

        /// <summary>Gets the named participant for <see cref="PresentationTargetKind.Only"/> and <see cref="PresentationTargetKind.AllExcept"/>.</summary>
        public ParticipantId Participant { get; }

        /// <summary>Creates a target selecting exactly one participant.</summary>
        public static PresentationTarget Only(ParticipantId participant) =>
            new PresentationTarget(PresentationTargetKind.Only, RequireParticipant(participant));

        /// <summary>Creates a target selecting everyone except one participant.</summary>
        public static PresentationTarget AllExcept(ParticipantId participant) =>
            new PresentationTarget(PresentationTargetKind.AllExcept, RequireParticipant(participant));

        /// <summary>Converts a legacy audience to the equivalent target.</summary>
        public static PresentationTarget FromAudience(MultiplayerAudience audience)
        {
            switch (audience)
            {
                case MultiplayerAudience.Everyone:
                    return Everyone;
                case MultiplayerAudience.Sender:
                    return Sender;
                case MultiplayerAudience.Others:
                    return Others;
                default:
                    throw new ArgumentOutOfRangeException(nameof(audience));
            }
        }

        /// <summary>
        /// Tries to express this target as a legacy audience, which is possible for every kind except
        /// <see cref="PresentationTargetKind.Only"/> and <see cref="PresentationTargetKind.AllExcept"/>.
        /// </summary>
        public bool TryGetAudience(out MultiplayerAudience audience)
        {
            switch (Kind)
            {
                case PresentationTargetKind.Everyone:
                    audience = MultiplayerAudience.Everyone;
                    return true;
                case PresentationTargetKind.Sender:
                    audience = MultiplayerAudience.Sender;
                    return true;
                case PresentationTargetKind.Others:
                    audience = MultiplayerAudience.Others;
                    return true;
                default:
                    audience = default;
                    return false;
            }
        }

        /// <summary>Returns whether this target selects <paramref name="participant"/> for an event from <paramref name="sender"/>.</summary>
        public bool Selects(ParticipantId participant, ParticipantId sender)
        {
            switch (Kind)
            {
                case PresentationTargetKind.Everyone:
                    return true;
                case PresentationTargetKind.Sender:
                    return participant.Equals(sender);
                case PresentationTargetKind.Others:
                    return !participant.Equals(sender);
                case PresentationTargetKind.Only:
                    return participant.Equals(Participant);
                case PresentationTargetKind.AllExcept:
                    return !participant.Equals(Participant);
                default:
                    return false;
            }
        }

        /// <inheritdoc/>
        public bool Equals(PresentationTarget other) => Kind == other.Kind && Participant.Equals(other.Participant);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is PresentationTarget other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => ((int)Kind * 397) ^ Participant.GetHashCode();

        /// <inheritdoc/>
        public override string ToString() =>
            Kind == PresentationTargetKind.Only || Kind == PresentationTargetKind.AllExcept
                ? Kind + "(" + Participant + ")"
                : Kind.ToString();

        private static ParticipantId RequireParticipant(ParticipantId participant)
        {
            if (!participant.IsValid)
            {
                throw new ArgumentException("A targeted presentation event requires a valid participant.", nameof(participant));
            }

            return participant;
        }
    }
}
