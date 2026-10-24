using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed class HostedMotion
    {
        internal HostedMotion(HostWorldEntity entity, INetworkMotionBody body)
        {
            Entity = entity;
            Body = body;
            ResumeKinematic = body.IsKinematic;
            body.TryReadMotion(out Pose, out AngularVelocity);
        }

        internal readonly HostWorldEntity Entity;
        internal readonly INetworkMotionBody Body;
        internal readonly OwnershipStateMachine Rules = new OwnershipStateMachine(MotionRules.Policy);
        internal readonly List<ParticipantId> Skipped = new List<ParticipantId>();
        internal ParticipantId Participant;
        internal ulong Generation;
        internal bool ResumeKinematic;
        internal bool Dirty;
        internal long NextBroadcast;
        internal NetPose Pose;
        internal NetVector3 AngularVelocity;

        internal void Restore()
        {
            if (!Body.IsAlive) return;
            Body.IsKinematic = ResumeKinematic;
            Body.WriteMotion(Pose, AngularVelocity);
        }
    }
}
