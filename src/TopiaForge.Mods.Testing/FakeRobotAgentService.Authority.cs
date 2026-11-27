using System;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeRobotAgentService
    {
        private readonly int ownerThread = Environment.CurrentManagedThreadId;

        /// <summary>
        /// Gets or sets whether this service models a guest process. Defaults to false (host or solo).
        /// Client spawns use Dormant brains with native talk disabled; clients cannot wake native brains.
        /// </summary>
        [RequiresTopiaForge("0.2.0")]
        public bool IsClient { get; set; }

        /// <summary>
        /// Runs a host callback on behalf of a guest. Remote fake player UI actions apply this scope automatically.
        /// Nested calls retain guest origin, and a captured execution context retains it on this service's owning
        /// thread. Returning or throwing restores the caller's scope. The thread that constructs this service owns
        /// authority: calling this method on another thread, or making a spawn or native-brain call on another
        /// thread while a guest scope is visible, throws <see cref="InvalidOperationException"/> instead of
        /// silently answering as the host. Use this helper explicitly for fake modal completions.
        /// </summary>
        [RequiresTopiaForge("0.2.0")]
        public void RunAsGuest(Action callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (Environment.CurrentManagedThreadId != ownerThread)
                throw new InvalidOperationException(FakeGuestOrigin.OwnerThreadRule);
            FakeGuestOrigin.Run(true, callback);
        }

        private bool IsGuestOrigin => FakeGuestOrigin.IsActiveFor(ownerThread);

        private OperationResult<IRobotAgent>? PrepareSpawn(ref RobotAgentSpawnRequest request)
        {
            if (IsClient && request.Interaction.CustomInteraction != null)
                return OperationResult<IRobotAgent>.Failure(ModErrorCode.NotAuthoritative,
                    "Custom robot interactions must be installed by the host.");

            // Validate the original brain/talk values before normalization can erase them.
            var distance = request.Interaction.NativeTalkDistance;
            if (!Enum.IsDefined(typeof(RobotBrainMode), request.BrainMode) ||
                float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0f || distance > 100f)
                return OperationResult<IRobotAgent>.Failure(ModErrorCode.InvalidArgument,
                    "Robot brain and native talk values must be valid and bounded.");

            var guestOrigin = !IsClient && IsGuestOrigin;
            if (guestOrigin && request.BrainMode == RobotBrainMode.Autonomous)
                return OperationResult<IRobotAgent>.Failure(ModErrorCode.NotAuthoritative,
                    "A guest-origin callback cannot spawn an Autonomous robot, even when it runs on the host. Request Dormant instead.");

            if (IsClient || guestOrigin)
                request = new RobotAgentSpawnRequest(request.Position, request.Facing, RobotBrainMode.Dormant,
                    request.Gait, request.MoveSpeed, request.TurnSpeed, request.StopDistance, request.Tint,
                    request.Name, request.Scale, request.Interaction.CustomInteraction != null
                        ? request.Interaction : RobotInteractionOptions.DisableNativeTalk(), request.RobotTypeId);
            return null;
        }
    }
}
