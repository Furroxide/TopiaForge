using TopiaForge.Mods;

namespace TopiaForge.Chronos
{
    // Turn-based runner: the world is hard-frozen between turns (the scheduler holds a Freeze lease); when an actor's
    // turn comes up the consumer issues its action and the scheduler LIFTS the freeze for the duration (so the active
    // actor's native locomotion runs) then re-freezes — "active unit animates, others idle, freeze, next unit". The
    // initiative/energy ordering is the pure TurnOrder; this just wires it to the world clock with a safety timeout.
    //
    // Consumer contract: while State == AwaitingAction, command CurrentActor (and ensure no OTHER actor has a queued
    // move — they'd advance during the lift), then BeginAction(); when the action finishes (e.g. the agent reached
    // its target), EndAction(). Tick(controlDeltaTime) once per frame.
    internal sealed class TurnScheduler : ITurnScheduler
    {
        private TimeControlService? service;
        private ITimeLease? freezeLease;     // held while NOT acting (world frozen); released during an action
        private readonly TurnOrder order;
        private readonly TurnSchedulerOptions options;
        private readonly string ownerModId;

        private TurnActorId? currentActor;
        private TurnState state = TurnState.Idle;
        private float actionTimer;
        private bool ended;

        public TurnScheduler(
            TimeControlService? service,
            ITimeLease? freezeLease,
            string ownerModId,
            TurnSchedulerOptions options)
        {
            this.service = service;
            this.freezeLease = freezeLease;
            this.ownerModId = ownerModId;
            this.options = options ?? new TurnSchedulerOptions();
            order = new TurnOrder(this.options.EnergyPerTurn);
            if (service == null)
            {
                ended = true; // dead scheduler (service unavailable)
            }
        }

        public TurnState State => state;
        public TurnActorId? CurrentActor => currentActor;
        public int ActorCount => order.Count;

        public OperationResult<bool> Register(TurnActorId actor, float speed)
        {
            if (ended)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Turn scheduler has ended.");
            }

            if (speed <= 0f || float.IsNaN(speed) || float.IsInfinity(speed))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Actor speed must be finite and positive.");
            }

            return order.Register(actor, speed)
                ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(ModErrorCode.Conflict, "Actor is already registered.");
        }

        public OperationResult<bool> Unregister(TurnActorId actor)
        {
            if (ended)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Turn scheduler has ended.");
            }

            var removed = order.Unregister(actor);
            if (currentActor == actor)
            {
                // The acting/awaiting actor left — cancel its turn and re-freeze if we'd lifted.
                if (state == TurnState.Acting)
                {
                    ReFreeze();
                }

                currentActor = null;
                state = TurnState.Idle;
            }

            return OperationResult<bool>.Success(removed);
        }

        public OperationResult<bool> BeginAction()
        {
            if (ended || state != TurnState.AwaitingAction || !currentActor.HasValue)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidState,
                    "No actor is awaiting an action.");
            }

            state = TurnState.Acting;
            actionTimer = 0f;
            // Lift the freeze so the active actor's native locomotion runs this turn.
            freezeLease?.Release();
            freezeLease = null;
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> EndAction()
        {
            if (ended || state != TurnState.Acting)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidState,
                    "No actor action is currently running.");
            }

            if (currentActor.HasValue)
            {
                order.SpendTurn(currentActor.Value);
            }

            ReFreeze();
            currentActor = null;
            state = TurnState.Idle;
            return OperationResult<bool>.Success(true);
        }

        public void Tick(float controlDeltaTime)
        {
            if (ended)
            {
                return;
            }

            switch (state)
            {
                case TurnState.Idle:
                    order.AddEnergy(controlDeltaTime);
                    var next = order.NextReady();
                    if (next != null)
                    {
                        currentActor = next;
                        state = TurnState.AwaitingAction;
                    }

                    break;

                case TurnState.Acting:
                    actionTimer += controlDeltaTime;
                    if (actionTimer >= options.MaxActionSeconds)
                    {
                        // Safety: the action never reported done — force-end so the world can't stay lifted forever.
                        EndAction();
                    }

                    break;
            }
        }

        public void Dispose()
        {
            if (ended && service == null)
            {
                return;
            }

            ended = true;
            // Release the freeze (if still held) so ending turn-based mode resumes normal time.
            freezeLease?.Release();
            freezeLease = null;
            currentActor = null;
            state = TurnState.Idle;
            service?.OnTurnSchedulerEnded(this);
            service = null;
        }

        // Called by the service's ForceReset so the scheduler tears down without re-entrancy.
        internal void AbortFromService()
        {
            ended = true;
            freezeLease?.Release();
            freezeLease = null;
            currentActor = null;
            state = TurnState.Idle;
            service = null;
        }

        private void ReFreeze()
        {
            if (freezeLease == null && service != null)
            {
                freezeLease = service.Freeze(ownerModId, "turn-based:between-turns");
            }
        }
    }
}
