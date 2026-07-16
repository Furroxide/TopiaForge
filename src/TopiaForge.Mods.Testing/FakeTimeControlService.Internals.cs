using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeTimeControlService
    {
        private enum LeaseKind
        {
            Freeze,
            Slow,
            Exemption,
            Driver
        }

        private sealed class Lease : ITimeLease
        {
            private Action<Lease>? release;
            private IDisposable? lifetimeLease;

            public Lease(LeaseKind kind, float scale, Action<Lease> release, ITimeDriver? driver)
            {
                Kind = kind;
                Scale = scale;
                Driver = driver;
                this.release = release;
            }

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            public LeaseKind Kind { get; }
            public float Scale { get; }
            public ITimeDriver? Driver { get; }
            public bool IsActive => release != null;

            public void Release()
            {
                var callback = release;
                release = null;
                try
                {
                    callback?.Invoke(this);
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }

            public void Dispose() => Release();
        }

        private sealed class FakeTurnScheduler : ITurnScheduler
        {
            private readonly TurnSchedulerOptions options;
            private readonly Dictionary<TurnActorId, float> actors = new Dictionary<TurnActorId, float>();
            private readonly Func<ITimeLease?> acquireFreeze;
            private readonly Action<FakeTurnScheduler> onDisposed;
            private ITimeLease? freeze;
            private IDisposable? lifetimeLease;
            private bool disposed;
            private float elapsed;

            public FakeTurnScheduler(
                TurnSchedulerOptions options,
                ITimeLease freeze,
                Func<ITimeLease?> acquireFreeze,
                Action<FakeTurnScheduler> onDisposed)
            {
                this.options = options;
                this.freeze = freeze;
                this.acquireFreeze = acquireFreeze;
                this.onDisposed = onDisposed;
            }

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            public TurnState State { get; private set; }
            public TurnActorId? CurrentActor { get; private set; }
            public int ActorCount => actors.Count;

            public OperationResult<bool> Register(TurnActorId actor, float speed)
            {
                if (disposed)
                {
                    return InvalidState();
                }

                if (speed <= 0f || float.IsNaN(speed) || float.IsInfinity(speed))
                {
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Actor speed must be finite and positive.");
                }

                if (actors.ContainsKey(actor))
                {
                    return OperationResult<bool>.Failure(ModErrorCode.Conflict, "Actor is already registered.");
                }

                actors.Add(actor, speed);
                return OperationResult<bool>.Success(true);
            }

            public OperationResult<bool> Unregister(TurnActorId actor)
            {
                if (disposed)
                {
                    return InvalidState();
                }

                var removed = actors.Remove(actor);
                if (CurrentActor == actor)
                {
                    CurrentActor = null;
                    State = TurnState.Idle;
                }

                return OperationResult<bool>.Success(removed);
            }

            public OperationResult<bool> BeginAction()
            {
                if (disposed || State != TurnState.AwaitingAction)
                {
                    return InvalidState();
                }

                State = TurnState.Acting;
                elapsed = 0f;
                freeze?.Dispose();
                freeze = null;
                return OperationResult<bool>.Success(true);
            }

            public OperationResult<bool> EndAction()
            {
                if (disposed || State != TurnState.Acting)
                {
                    return InvalidState();
                }

                CurrentActor = null;
                State = TurnState.Idle;
                freeze = acquireFreeze();
                return OperationResult<bool>.Success(true);
            }

            public void Tick(float controlDeltaTime)
            {
                if (disposed || actors.Count == 0)
                {
                    return;
                }

                if (State == TurnState.Idle)
                {
                    foreach (var actor in actors.Keys)
                    {
                        CurrentActor = actor;
                        break;
                    }

                    State = TurnState.AwaitingAction;
                }
                else if (State == TurnState.Acting)
                {
                    elapsed += Math.Max(0f, controlDeltaTime);
                    if (elapsed >= options.MaxActionSeconds)
                    {
                        EndAction();
                    }
                }
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                try
                {
                    freeze?.Dispose();
                    freeze = null;
                    actors.Clear();
                    CurrentActor = null;
                    State = TurnState.Idle;
                    onDisposed(this);
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }

            private static OperationResult<bool> InvalidState() =>
                OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The fake turn scheduler is not in the required state.");
        }
    }
}
