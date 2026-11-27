using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class NetworkWorldClient
    {
        internal Func<IEntity, bool>? IsSimulating { get; set; }

        internal bool TryMotionTarget(IEntity entity, out MotionTarget target, out INetworkMotionBody body)
        {
            target = default;
            body = null!;
            if (TryGetMirrored(entity, out var mirrored) && mirrored.Object is INetworkMotionBody mirroredBody)
            {
                target = new MotionTarget(mirrored.Id);
                body = mirroredBody;
                return true;
            }

            if (index.TryFind(entity, out var slot) && slot.Object is INetworkMotionBody sceneBody)
            {
                target = slot.IsBound ? new MotionTarget(slot.Bound) : new MotionTarget(slot.Key);
                body = sceneBody;
                return true;
            }

            return false;
        }

        internal void SetSimulated(IEntity entity, bool simulated)
        {
            if (!TryGetMirrored(entity, out var mirrored)) return;
            mirrored.SimulatedHere = simulated;
            if (!simulated) mirrored.Buffer?.Reset();
        }

        internal void ApplyMotion(MotionMessage message)
        {
            if (!entities.TryGetValue(message.Target.Id.Value, out var mirrored)) return;
            if (mirrored.SimulatedHere) return;
            driver.Offer(mirrored, message.TimeMicros, message.Pose, clock());
        }
    }
}
