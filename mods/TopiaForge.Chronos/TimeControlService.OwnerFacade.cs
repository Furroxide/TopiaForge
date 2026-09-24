using System;
using TopiaForge.Mods;

namespace TopiaForge.Chronos
{
    internal sealed partial class TimeControlService
    {
        // The facade tags every effect with its consumer and binds cleanup to that mod's lifetime.
        private sealed class OwnerFacade : ITimeControlService
        {
            private readonly TimeControlService service;
            private readonly string consumerId;
            private readonly IModLifetime lifetime;

            public OwnerFacade(TimeControlService service, string consumerId, IModLifetime lifetime)
            {
                this.service = service;
                this.consumerId = consumerId;
                this.lifetime = lifetime;
            }

            public bool IsAvailable => service.IsAvailable && !lifetime.IsStopping;
            public float WorldScale => service.WorldScale;
            public float WorldDeltaTime => service.WorldDeltaTime;
            public float WorldTime => service.WorldTime;
            public float ControlDeltaTime => service.ControlDeltaTime;
            public float ControlTime => service.ControlTime;
            public bool IsFrozen => service.IsFrozen;
            public TimeMode Mode => service.Mode;

            public OperationResult<ITimeLease> Freeze(string usage, bool suspendPlayer = false) =>
                lifetime.IsStopping ? Stopping<ITimeLease>() : Track(service.Freeze(consumerId, usage, suspendPlayer));

            public OperationResult<ITimeLease> Slow(string usage, float scale)
            {
                if (lifetime.IsStopping) return Stopping<ITimeLease>();
                if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0f || scale > 1f)
                {
                    return OperationResult<ITimeLease>.Failure(
                        ModErrorCode.InvalidArgument,
                        "A slow scale must be finite and between zero and one.");
                }

                return Track(service.Slow(consumerId, usage, scale));
            }

            public OperationResult<ITimeLease> ExemptPlayer(string usage) =>
                lifetime.IsStopping ? Stopping<ITimeLease>() : Track(service.ExemptPlayer(consumerId, usage));

            public OperationResult<ITimeLease> SetDriver(string usage, ITimeDriver driver)
            {
                if (lifetime.IsStopping) return Stopping<ITimeLease>();
                if (driver == null)
                {
                    throw new ArgumentNullException(nameof(driver));
                }

                return Track(service.SetDriver(consumerId, usage, driver));
            }

            public OperationResult<bool> Step(float seconds)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The mod lifetime is stopping.");
                }

                return service.Step(seconds);
            }

            public OperationResult<bool> StepFixed(int ticks)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The mod lifetime is stopping.");
                }

                return service.StepFixed(ticks);
            }

            public OperationResult<ITurnScheduler> BeginTurnBased(string usage, TurnSchedulerOptions options)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<ITurnScheduler>.Failure(ModErrorCode.InvalidState, "The mod lifetime is stopping.");
                }

                var scheduler = service.BeginTurnBased(consumerId, usage, options);
                try
                {
                    return OperationResult<ITurnScheduler>.Success(
                        new OwnerTurnScheduler(scheduler, lifetime.Track(scheduler), lifetime));
                }
                catch (ObjectDisposedException)
                {
                    return OperationResult<ITurnScheduler>.Failure(ModErrorCode.InvalidState, "The mod lifetime is stopping.");
                }
            }

            private OperationResult<ITimeLease> Track(ITimeLease lease)
            {
                try
                {
                    return OperationResult<ITimeLease>.Success(
                        new OwnerTimeLease(lease, lifetime.Track(lease)));
                }
                catch (ObjectDisposedException)
                {
                    return OperationResult<ITimeLease>.Failure(ModErrorCode.InvalidState, "The mod lifetime is stopping.");
                }
            }

            private static OperationResult<T> Stopping<T>() where T : notnull =>
                OperationResult<T>.Failure(ModErrorCode.InvalidState, "The owning context is stopping.");

            private sealed class OwnerTimeLease : ITimeLease
            {
                private readonly ITimeLease lease;
                private IDisposable? lifetimeLease;

                public OwnerTimeLease(ITimeLease lease, IDisposable lifetimeLease)
                {
                    this.lease = lease;
                    this.lifetimeLease = lifetimeLease;
                }

                public bool IsActive => lifetimeLease != null && lease.IsActive;

                public void Release()
                {
                    System.Threading.Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }

                public void Dispose() => Release();
            }

            private sealed class OwnerTurnScheduler : ITurnScheduler
            {
                private readonly ITurnScheduler scheduler;
                private readonly IModLifetime lifetime;
                private IDisposable? lifetimeLease;

                public OwnerTurnScheduler(ITurnScheduler scheduler, IDisposable lifetimeLease, IModLifetime lifetime)
                {
                    this.scheduler = scheduler;
                    this.lifetimeLease = lifetimeLease;
                    this.lifetime = lifetime;
                }

                public TurnState State => scheduler.State;
                public TurnActorId? CurrentActor => scheduler.CurrentActor;
                public int ActorCount => scheduler.ActorCount;
                public OperationResult<bool> Register(TurnActorId actor, float speed) =>
                    lifetime.IsStopping ? Stopping<bool>() : scheduler.Register(actor, speed);
                public OperationResult<bool> Unregister(TurnActorId actor) => lifetime.IsStopping ? Stopping<bool>() : scheduler.Unregister(actor);
                public OperationResult<bool> BeginAction() => lifetime.IsStopping ? Stopping<bool>() : scheduler.BeginAction();
                public OperationResult<bool> EndAction() => lifetime.IsStopping ? Stopping<bool>() : scheduler.EndAction();
                public void Tick(float controlDeltaTime) { if (!lifetime.IsStopping) scheduler.Tick(controlDeltaTime); }

                public void Dispose()
                {
                    System.Threading.Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
        }
    }
}
