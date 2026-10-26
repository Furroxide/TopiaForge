using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class PresenceHost
    {
        private string? worldSeen;
        private int sceneSeen;

        internal bool IsCurrentWorld => string.Equals(worldSeen, presence.RunningWorld?.Invoke(), StringComparison.Ordinal)
            && sceneSeen == (presence.SceneVersion?.Invoke() ?? 0);

        /// <summary>Host world changes invalidate every old pose before any new packet is used, including before arrival.</summary>
        private void FollowWorld(long nowMicros)
        {
            var world = presence.RunningWorld?.Invoke();
            var scene = presence.SceneVersion?.Invoke() ?? 0;
            var changed = !string.Equals(world, worldSeen, StringComparison.Ordinal) || scene != sceneSeen;
            worldSeen = world;
            sceneSeen = scene;
            var spawn = world == null ? null : presence.RunningSpawn?.Invoke();
            var hasSpawn = TrySpawnPosition(spawn, out var point);
            var all = presence.Peers;
            for (var index = 0; index < all.Count; index++)
            {
                var peer = all[index];
                if (peer.IsLocal) continue;
                if (changed)
                {
                    peer.Plausibility?.Restart(nowMicros);
                    peer.ClearPose();
                    presence.DisposeAvatar(peer);
                    peer.PlacedWorld = null;
                    peer.Slot = -1;
                    peer.PlacementDue = false;
                    peer.CorrectionDue = false;
                }

                if (hasSpawn) peer.Plausibility?.SetInitialPosition(NetQuantization.QuantizePosition(point), nowMicros);
            }
        }

        private bool TrySpawnPosition(TransformState? spawn, out NetVector3 point)
        {
            point = default;
            if (!spawn.HasValue) return false;
            var value = spawn.Value;
            point = new NetVector3(value.Position.X, value.Position.Y, value.Position.Z);
            return IsRepresentable(point) && value.Rotation.IsFinite && value.Rotation.LengthSquared > 0.000001f
                && value.Scale.IsFinite && value.Scale.X != 0f && value.Scale.Y != 0f && value.Scale.Z != 0f
                && (!presence.Backend.TryGetWorldBounds(out var bounds) || bounds.Contains(NetQuantization.QuantizePosition(point)));
        }

        private static bool IsRepresentable(NetVector3 point)
        {
            const double minimum = (double)NetQuantization.PositionMinimumUnits / NetQuantization.PositionUnitsPerMeter;
            const double maximum = (double)NetQuantization.PositionMaximumUnits / NetQuantization.PositionUnitsPerMeter;
            return point.IsFinite && point.X >= minimum && point.X <= maximum && point.Y >= minimum && point.Y <= maximum
                && point.Z >= minimum && point.Z <= maximum;
        }
    }
}
