using System;
using System.Security.Cryptography;
using System.Text;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPlayers
    {
        // Persistence keys and slots survive reconnects. References bind the host-issued connection lifetime
        // to the session, so neither a rejoin nor a replacement can revive a retired target.
        internal bool TryCreateReference(IEntity entity, out InternalNetRef reference)
        {
            reference = default;
            var snapshot = Snapshot;
            if (snapshot == null || !entity.IsAlive) return false;
            foreach (var player in snapshot.Participants)
            {
                if (!player.IsConnected || player.LifetimeId == Guid.Empty) continue;
                var target = PlayerEntity(player.Id);
                if (target == null || !target.IsAlive || !SameEntity(target, entity)) continue;
                reference = new InternalNetRef(InternalNetRefKind.Player, ReferenceValue(snapshot, player));
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
                if (!player.IsConnected || player.LifetimeId == Guid.Empty || !string.Equals(ReferenceValue(snapshot, player), reference.Value, StringComparison.Ordinal)) continue;
                entity = PlayerEntity(player.Id);
                return entity?.IsAlive == true;
            }
            return false;
        }

        private static string ReferenceValue(MultiplayerSessionSnapshot snapshot, MultiplayerParticipant player)
        {
            using var hash = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(snapshot.Id.Value + ":" + player.LifetimeId.ToString("N"));
            return "p2:" + Convert.ToBase64String(hash.ComputeHash(bytes));
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
