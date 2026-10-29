using System;
using System.Security.Cryptography;
using System.Text;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPresence
    {
        // Session, participant and host-scoped key name a player lifetime, never an engine instance or avatar index.
        private static InternalNetRef Reference(MultiplayerSessionSnapshot snapshot, MultiplayerParticipant player)
        {
            var text = snapshot.Id.Value + "\n" + player.Id.Value + "\n" + player.PlayerKey.Value;
            using var hash = SHA256.Create();
            var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(text));
            return new InternalNetRef(InternalNetRefKind.Player, BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant());
        }
        private bool Current(MultiplayerParticipant player)
        {
            return Realm != NetRealm.Offline && player.IsConnected && reconciled != null
                && control != null && reconciled.Id.Equals(control.SessionSnapshot.Id)
                && peersById.TryGetValue(player.Id, out var peer) && peer.IsConnected && !peer.Removed
                && peer.PlayerKey.Equals(player.PlayerKey);
        }
        internal bool TryCreatePlayerRef(IEntity entity, out InternalNetRef reference)
        {
            reference = default;
            if (entity == null || !entity.IsAlive || control == null) return false;
            var snapshot = control.SessionSnapshot;
            foreach (var player in snapshot.Participants)
                if (Current(player) && TryGetAvatar(player.Id, out var avatar) && ReferenceEquals(entity, avatar))
                { reference = Reference(snapshot, player); return true; }
            return false;
        }
        internal bool TryResolvePlayerRef(InternalNetRef reference, out IEntity? entity)
        {
            entity = null;
            if (reference.Kind != InternalNetRefKind.Player || control == null) return false;
            var snapshot = control.SessionSnapshot;
            foreach (var player in snapshot.Participants)
                if (Current(player) && Reference(snapshot, player).Equals(reference))
                    return TryGetAvatar(player.Id, out entity) && entity?.IsAlive == true;
            return false;
        }
        internal bool IsCurrentViewAvatar(IEntity entity, string world)
        {
            if (string.IsNullOrEmpty(world) || RunningWorld?.Invoke() != world || !TryCreatePlayerRef(entity, out _)) return false;
            foreach (var peer in peers)
                if (!peer.IsLocal && peer.HasPose && peer.Avatar?.IsAlive == true && ReferenceEquals(peer.Avatar.Entity, entity))
                    return peer.AvatarWorld == world && peer.AvatarSceneVersion == (SceneVersion?.Invoke() ?? 0)
                        && (Realm != NetRealm.ListenHost || Host.IsCurrentWorld);
            return false;
        }
    }
}
