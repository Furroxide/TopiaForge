using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    internal sealed partial class RobotAgent : TopiaForge.Mods.Internal.IInternalNativeObjectEntity, IRobotReplicaState
    {
        public object? NativeObject => go != null ? go : null;
        public RobotReplicaState? ReplicaState { get; private set; }
        private RobotCosmeticBody? replicaAnimation;
        private Vector3 previousReplicaPosition;

        internal void MakeReplica(Animator? animator, string? walkParameter)
        {
            Stop();
            SetBrainMode(RobotBrainMode.Dormant);
            SetInteraction(RobotInteractionOptions.DisableNativeTalk());
            RobotAgentService.Quieten(go, cosmetic: false);
            // NetworkWorld owns pose; kinematic colliders remain targetable. RobotKit animates the body.
            replicaAnimation = new RobotCosmeticBody(go, id, animator, walkParameter);
            ReplicaState = new RobotReplicaState(new RobotMovementSettings(gait, moveSpeed, turnSpeed, stopDistance));
            previousReplicaPosition = transform.position;
        }

        private static OperationResult<bool> ReplicaRefusal() =>
            OperationResult<bool>.Failure(ModErrorCode.NotAuthoritative, "Only the host drives a robot replica.");

        private void AnimateReplica()
        {
            if (go == null || despawned) return;
            var position = transform.position;
            var speed = Time.unscaledDeltaTime > 0 ? (position - previousReplicaPosition).magnitude / Time.unscaledDeltaTime : 0;
            previousReplicaPosition = position;
            replicaAnimation!.Animate(Mathf.Min(speed, 100), false);
        }
    }
}
