using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPlayers
    {
        // Use the provider's lifetime key, not a slot or process-local entity id. A successor occupying
        // the same participant slot cannot become the target of an old robot command.
        internal bool TryCreateReference(IEntity entity, out InternalNetRef reference)
        {
            reference = default;
            var snapshot = Snapshot;
            if (snapshot == null || !entity.IsAlive) return false;
            foreach (var player in snapshot.Participants)
            {
                if (!player.IsConnected || !player.PlayerKey.IsValid) continue;
                var target = PlayerEntity(player.Id);
                if (target == null || !target.IsAlive || !SameEntity(target, entity)) continue;
                reference = new InternalNetRef(InternalNetRefKind.Player, player.PlayerKey.Value);
                return true;
            }
            return false;
        }

        internal bool TryResolveReference(InternalNetRef reference, out IEntity? entity)
        {
            entity = null;
            var snapshot = Snapshot;
            if (reference.Kind != InternalNetRefKind.Player || !reference.IsSpecified || snapshot == null) return false;
            foreach (var player in snapshot.Participants)
            {
                if (!player.IsConnected || !player.PlayerKey.IsValid || !string.Equals(player.PlayerKey.Value, reference.Value, StringComparison.Ordinal)) continue;
                entity = PlayerEntity(player.Id);
                return entity?.IsAlive == true;
            }
            return false;
        }

        private static bool SameEntity(IEntity first, IEntity second)
        {
            var firstNative = (first as IInternalNativeObjectEntity)?.NativeObject;
            var secondNative = (second as IInternalNativeObjectEntity)?.NativeObject;
            return firstNative != null && secondNative != null ? ReferenceEquals(firstNative, secondNative)
                : string.Equals(first.Id, second.Id, StringComparison.Ordinal);
        }

        private IEntity? PlayerEntity(ParticipantId id) => IsLocal(id) ? Presence.Backend.LocalPlayerEntity
            : Presence.TryGetAvatar(id, out var entity) ? entity : null;
    }
}
