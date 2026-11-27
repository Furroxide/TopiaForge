using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// A joined player's transform changes and destroys, on the listen host (WS3.7). A request names its target by network
    /// id, or the player's own spawn by provisional id. It spends from the player's budget for its kind first, and the rules
    /// decide by how the player relates to the target: by default a player moves and destroys only its own objects, never
    /// the world's or another player's. What a request asks of a spawn still being made applies once it is made.
    /// </summary>
    internal sealed partial class NetworkWorldProxies
    {
        private void Move(ParticipantId sender, AuthorityTarget target, NetVector3 scale, in NetPose pose)
        {
            // A player the host made nothing for owns nothing here, so it may move nothing.
            if (!byParticipant.TryGetValue(sender, out var proxy))
            {
                counters.ChangesRefused++;
                return;
            }

            var now = clock();
            if (!TryResolve(proxy, target, out var spawn, out var hosted, out var relation))
            {
                limiter.Evaluate(proxy.Slot, false, NetRequestKind.SetTransform, NetTargetRelation.Server, 0, now);
                counters.ChangesRefused++;
                return;
            }

            if (limiter.Evaluate(proxy.Slot, false, NetRequestKind.SetTransform, relation, 0, now) != NetPermission.Allowed)
            {
                counters.ChangesRefused++;
                return;
            }

            if (spawn != null && spawn.State == ProxySpawnState.Building)
            {
                spawn.Transform = new TransformState(
                    new Vec3(pose.Position.X, pose.Position.Y, pose.Position.Z),
                    new Quat(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W),
                    new Vec3(scale.X, scale.Y, scale.Z));
                counters.TransformsApplied++;
                return;
            }

            if (hosted?.Object == null)
            {
                counters.ChangesRefused++;
                return;
            }

            try
            {
                hosted.Object.WritePose(pose);
                counters.TransformsApplied++;
            }
            catch (System.Exception)
            {
                counters.ChangesRefused++;
            }
        }

        private void Destroy(ParticipantId sender, AuthorityTarget target)
        {
            if (!byParticipant.TryGetValue(sender, out var proxy))
            {
                counters.ChangesRefused++;
                return;
            }

            var now = clock();
            if (!TryResolve(proxy, target, out var spawn, out _, out var relation))
            {
                limiter.Evaluate(proxy.Slot, false, NetRequestKind.Destroy, NetTargetRelation.Server, 0, now);
                counters.ChangesRefused++;
                return;
            }

            // Only what the player's own scope holds ends through it; a rule that let players remove more would need the
            // host to end what it never made for them.
            if (limiter.Evaluate(proxy.Slot, false, NetRequestKind.Destroy, relation, 0, now) != NetPermission.Allowed || spawn == null)
            {
                counters.ChangesRefused++;
                return;
            }

            proxy.Spawns.Remove(spawn.Provisional);
            End(spawn);
            if (spawn.Record != null) ledger.Remove(spawn.Record);
            counters.DestroysApplied++;
        }

        /// <summary>
        /// Finds what a request names, and how the sender relates to it: its own spawn by provisional id, building or made,
        /// or an entity by network id, which is the sender's own when its scope made it.
        /// </summary>
        private bool TryResolve(ProxyParticipant proxy, AuthorityTarget target, out ProxySpawn? spawn, out HostWorldEntity? hosted, out NetTargetRelation relation)
        {
            spawn = null;
            hosted = null;
            relation = NetTargetRelation.Server;
            if (target.IsProvisional)
            {
                if (!proxy.Spawns.TryGetValue(target.Value, out var own) || !own.Counts) return false;
                spawn = own;
                relation = NetTargetRelation.Self;
                if (own.State == ProxySpawnState.Building) return true;
                return host.TryGetHosted(own.Id, out hosted!);
            }

            if (!host.TryGetHosted(new NetworkEntityId(target.Value), out var found)) return false;
            hosted = found;
            relation = SpawnAuthorityRules.RelationOf(found.OwnerSlot, proxy.Slot);
            if (relation != NetTargetRelation.Self) return true;
            foreach (var own in proxy.Spawns.Values)
            {
                if (own.State == ProxySpawnState.Live && own.Id.Equals(found.Id))
                {
                    spawn = own;
                    break;
                }
            }

            return true;
        }
    }
}
