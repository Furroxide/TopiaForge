using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPresence : IInternalCommunicationPositionSource
    {
        internal bool IsVoiceActive(PresencePeer peer) => peer.IsConnected && !peer.Removed && !peer.NeedsWorldArrival
            && CanPresentWorld && Host.IsCurrentWorld
            && control is IInternalCommunicationActivityControl activity && activity.IsVoiceActive(peer.Id);

        internal PresenceFlags CanonicalActivityFlags(PresencePeer peer) => peer.NeedsWorldArrival
            ? peer.Flags & ~(PresenceFlags.Talking | PresenceFlags.Typing)
            : IsVoiceActive(peer) ? peer.Flags | PresenceFlags.Talking : peer.Flags & ~PresenceFlags.Talking;

        internal PresenceFlags ActivityFlagsFor(PresencePeer peer, ParticipantId listener)
        {
            var flags = peer.Flags & ~PresenceFlags.Talking;
            if (!peer.IsConnected || peer.Removed || peer.NeedsWorldArrival || !CanPresentWorld || !Host.IsCurrentWorld)
                return flags & ~PresenceFlags.Typing;
            if (control is IInternalCommunicationControl routing && !routing.CanCommunicate(peer.Id, listener))
                return flags & ~PresenceFlags.Typing;
            if (control is IInternalCommunicationActivityControl activity && activity.HasVoiceActivity(peer.Id, listener))
                flags |= PresenceFlags.Talking;
            return flags;
        }

        public bool TryGetCommunicationPosition(ParticipantId participant, out Vec3 position)
        {
            position = default;
            if (!CanPresentWorld || !Host.IsCurrentWorld) return false;
            var validated = ValidatedPosition(participant);
            if (!validated.HasValue) return false;
            var value = validated.Value;
            position = new Vec3(value.X, value.Y, value.Z);
            return true;
        }
    }
}
