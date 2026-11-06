using System;
using System.Collections.Generic;
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
            for (var index = 0; index < snapshot.Participants.Count; index++)
            {
                var player = snapshot.Participants[index];
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
            for (var index = 0; index < snapshot.Participants.Count; index++)
            {
                var player = snapshot.Participants[index];
                if (!player.IsConnected || player.LifetimeId == Guid.Empty || !string.Equals(ReferenceValue(snapshot, player), reference.Value, StringComparison.Ordinal)) continue;
                entity = PlayerEntity(player.Id);
                return entity?.IsAlive == true;
            }
            return false;
        }

        private readonly Dictionary<ParticipantId, CachedPlayerReference> referenceValues = new Dictionary<ParticipantId, CachedPlayerReference>();
        private readonly List<ParticipantId> staleReferences = new List<ParticipantId>();
        private MultiplayerSessionSnapshot? referenceRoster;

        private string ReferenceValue(MultiplayerSessionSnapshot snapshot, MultiplayerParticipant player)
        {
            if (!ReferenceEquals(referenceRoster, snapshot))
            {
                staleReferences.Clear();
                foreach (var pair in referenceValues)
                    if (!snapshot.TryGetParticipant(pair.Key, out var current) || current == null || !current.IsConnected
                        || !pair.Value.Matches(snapshot, current)) staleReferences.Add(pair.Key);
                for (var index = 0; index < staleReferences.Count; index++) referenceValues.Remove(staleReferences[index]);
                referenceRoster = snapshot;
            }
            if (referenceValues.TryGetValue(player.Id, out var cached) && cached.Matches(snapshot, player)) return cached.Value;
            using var hash = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(snapshot.Id.Value + ":" + player.LifetimeId.ToString("N"));
            var value = "p2:" + Convert.ToBase64String(hash.ComputeHash(bytes));
            referenceValues[player.Id] = new CachedPlayerReference(snapshot.Id, player.PlayerKey, player.LifetimeId, value);
            return value;
        }

        private sealed class CachedPlayerReference
        {
            private readonly MultiplayerSessionId session;
            private readonly MultiplayerPlayerKey key;
            private readonly Guid lifetime;
            internal readonly string Value;
            internal CachedPlayerReference(MultiplayerSessionId session, MultiplayerPlayerKey key, Guid lifetime, string value)
            { this.session = session; this.key = key; this.lifetime = lifetime; Value = value; }
            internal bool Matches(MultiplayerSessionSnapshot snapshot, MultiplayerParticipant player) =>
                session.Equals(snapshot.Id) && key.Equals(player.PlayerKey) && lifetime == player.LifetimeId;
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
