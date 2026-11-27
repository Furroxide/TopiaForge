using System;
using TopiaForge.Mods;

namespace TopiaForge.RobotKit
{
    // Passive observations only: this state cannot call native navigation, change a brain or move a body.
    internal interface IRobotReplicaState
    {
        RobotReplicaState? ReplicaState { get; }
    }

    internal sealed class RobotReplicaState
    {
        private Guid pending, acknowledged;
        private bool moving, reached;
        internal RobotReplicaState(RobotMovementSettings settings)
        { Gait = settings.Gait; MoveSpeed = settings.MoveSpeed; TurnSpeed = settings.TurnSpeed; StopDistance = settings.StopDistance; }
        internal bool IsMoving => (pending == default || pending == acknowledged) && moving;
        internal bool HasReachedTarget => (pending == default || pending == acknowledged) && reached;
        internal float MoveSpeed { get; private set; }
        internal float TurnSpeed { get; private set; }
        internal float StopDistance { get; private set; }
        internal RobotGait Gait { get; private set; }
        internal void Await(Guid motion) => pending = motion;
        internal void ResetPending() => pending = default;
        internal void Apply(RobotAgentSpawnRequest request, bool isMoving, bool hasReachedTarget, Guid motion)
        {
            MoveSpeed = request.MoveSpeed; TurnSpeed = request.TurnSpeed;
            StopDistance = request.StopDistance; Gait = request.Gait;
            moving = isMoving; reached = hasReachedTarget; acknowledged = motion;
        }
    }
}
