using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Chronos
{
    internal sealed partial class TimeControlService
    {
        // The facade tags every effect with its consumer and binds cleanup to that mod's lifetime. In a multiplayer session
        // it also answers for where its consumer's code runs: only the gamemode's server controller on the host may freeze,
        // slow or step the shared world's time, and a change of realm ends what its consumer may no longer hold there.
        private sealed class OwnerFacade : ITimeControlService
        {
            private readonly TimeControlService service;
            private readonly string consumerId;
            private readonly IModLifetime lifetime;
            private readonly ChronosNetBridge? net;
            private readonly IRuntimeReplication? consumer;

            public OwnerFacade(TimeControlService service, string consumerId, IModLifetime lifetime)
                : this(service, consumerId, lifetime, null, null)
            {
            }

            public OwnerFacade(
                TimeControlService service,
                string consumerId,
                IModLifetime lifetime,
                ChronosNetBridge? net,
                IRuntimeReplication? consumer)
            {
                this.service = service;
                this.consumerId = consumerId;
                this.lifetime = lifetime;
                this.net = net;
                this.consumer = consumer;
            }

            public bool IsAvailable => service.IsAvailable && !lifetime.IsStopping;
            public float WorldScale => service.WorldScale;
            public float WorldDeltaTime => service.WorldDeltaTime;
            public float WorldTime => service.WorldTime;
            public float ControlDeltaTime => service.ControlDeltaTime;
            public float ControlTime => service.ControlTime;
            public bool IsFrozen => service.IsFrozen;
            public TimeMode Mode => service.Mode;

            public OperationResult<ITimeLease> Freeze(string usage, bool suspendPlayer = false)
            {
                if (lifetime.IsStopping) return Stopping<ITimeLease>();
                return NotAuthoritative<ITimeLease>() ?? Track(service.Freeze(consumerId, usage, suspendPlayer), followRealm: true);
            }

            public OperationResult<ITimeLease> Slow(string usage, float scale)
            {
                if (lifetime.IsStopping) return Stopping<ITimeLease>();
                var refused = NotAuthoritative<ITimeLease>();
                if (refused != null) return refused;
                if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0f || scale > 1f)
                {
                    return OperationResult<ITimeLease>.Failure(
                        ModErrorCode.InvalidArgument,
                        "A slow scale must be finite and between zero and one.");
                }

                return Track(service.Slow(consumerId, usage, scale), followRealm: true);
            }

            // Exempting this process's own player changes no one's time, so every context may, in every realm.
            public OperationResult<ITimeLease> ExemptPlayer(string usage) =>
                lifetime.IsStopping ? Stopping<ITimeLease>() : Track(service.ExemptPlayer(consumerId, usage), followRealm: false);

            public OperationResult<ITimeLease> SetDriver(string usage, ITimeDriver driver)
            {
                if (lifetime.IsStopping) return Stopping<ITimeLease>();
                var refused = NotAuthoritative<ITimeLease>();
                if (refused != null) return refused;
                if (driver == null)
                {
                    throw new ArgumentNullException(nameof(driver));
                }

                return Track(service.SetDriver(consumerId, usage, driver), followRealm: true);
            }

            public OperationResult<bool> Step(float seconds)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The mod lifetime is stopping.");
                }

                return NotAuthoritative<bool>() ?? service.Step(seconds);
            }

            public OperationResult<bool> StepFixed(int ticks)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The mod lifetime is stopping.");
                }

                return NotAuthoritative<bool>() ?? service.StepFixed(ticks);
            }

            public OperationResult<ITurnScheduler> BeginTurnBased(string usage, TurnSchedulerOptions options)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<ITurnScheduler>.Failure(ModErrorCode.InvalidState, "The mod lifetime is stopping.");
                }

                var refused = NotAuthoritative<ITurnScheduler>();
                if (refused != null) return refused;
                var scheduler = service.BeginTurnBased(consumerId, usage, options);
                net?.Track(scheduler, consumer);
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

            // Null when this consumer's code may change time now; otherwise the refusal, in the game's own words.
            private OperationResult<T>? NotAuthoritative<T>() where T : notnull
            {
                var reason = ChronosNetRules.Refusal(consumer);
                return reason == null ? null : OperationResult<T>.Failure(ModErrorCode.NotAuthoritative, reason);
            }

            private OperationResult<ITimeLease> Track(ITimeLease lease, bool followRealm)
            {
                if (followRealm)
                {
                    net?.Track(lease, consumer);
                }

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
