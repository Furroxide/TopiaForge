using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    public sealed partial class RobotKitMod
    {
        private void OfferPersistentBodies(RobotBodyService bodies)
        {
            var seam = (Context as IInternalReplicationContext)?.Replication;
            if (seam == null) return;
            var factory = seam.RegisterReplicaFactory(RobotBodyWire.Kind, bodies);
            if (!factory.Succeeded) throw new InvalidOperationException(factory.ErrorMessage);
            var handler = seam.RegisterSpawnHandler(RobotBodyWire.Kind, bodies);
            if (!handler.Succeeded) throw new InvalidOperationException(handler.ErrorMessage);
        }

        private void OfferRobotReplication(RobotAgentService agents)
        {
            var seam = (Context as IInternalReplicationContext)?.Replication;
            if (seam == null) return;
            var provider = new RobotReplication(agents.SpawnNative, entity =>
            {
                if (entity is INativeEntityAdapter native)
                {
                    var t = native.NativeGameObject.transform;
                    var p = t.position; var q = t.rotation; var s = t.localScale;
                    return new TransformState(new Vec3(p.x, p.y, p.z), new Quat(q.x, q.y, q.z, q.w), new Vec3(s.x, s.y, s.z));
                }
                return TransformState.Identity;
            }, seam);
            var factory = seam.RegisterReplicaFactory(RobotReplicationWire.Kind, provider);
            if (!factory.Succeeded) throw new InvalidOperationException(factory.ErrorMessage);
            var handler = seam.RegisterSpawnHandler(RobotReplicationWire.Kind, provider);
            if (!handler.Succeeded) throw new InvalidOperationException(handler.ErrorMessage);
            agents.Replication = provider;
        }
    }
}
