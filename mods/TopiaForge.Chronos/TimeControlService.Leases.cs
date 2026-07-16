using System;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Chronos
{
    internal sealed partial class TimeControlService
    {
        // Each effect is represented by a disposable lease. Consumers compose effects instead of competing writes.
        public OperationResult<ITimeLease> Freeze(string usage, bool suspendPlayer = false)
        {
            return disposed
                ? OperationResult<ITimeLease>.Failure(ModErrorCode.Unavailable, "Chronos is unavailable.")
                : OperationResult<ITimeLease>.Success(Freeze(ownerModId, usage, suspendPlayer));
        }

        internal ITimeLease Freeze(string consumerId, string usage, bool suspendPlayer = false)
        {
            if (disposed)
            {
                return DeadLease.Instance;
            }

            var id = ledger.Add(LeaseKind.Freeze, consumerId, usage ?? "freeze");
            if (suspendPlayer)
            {
                suspendRefCount++;
                playerSuspend.Suspend(usage ?? "freeze");
            }

            ApplyDiscrete();
            return new TimeLease(this, id, suspendPlayer);
        }

        public OperationResult<ITimeLease> Slow(string usage, float scale)
        {
            if (disposed)
            {
                return OperationResult<ITimeLease>.Failure(ModErrorCode.Unavailable, "Chronos is unavailable.");
            }

            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0f || scale > 1f)
            {
                return OperationResult<ITimeLease>.Failure(
                    ModErrorCode.InvalidArgument,
                    "A slow scale must be finite and between zero and one.");
            }

            return OperationResult<ITimeLease>.Success(Slow(ownerModId, usage, scale));
        }

        internal ITimeLease Slow(string consumerId, string usage, float scale)
        {
            if (disposed)
            {
                return DeadLease.Instance;
            }

            var id = ledger.Add(LeaseKind.Slow, consumerId, usage ?? "slow", TimeMath.Clamp01(scale));
            ApplyDiscrete();
            return new TimeLease(this, id, false);
        }

        public OperationResult<ITimeLease> ExemptPlayer(string usage)
        {
            return disposed
                ? OperationResult<ITimeLease>.Failure(ModErrorCode.Unavailable, "Chronos is unavailable.")
                : OperationResult<ITimeLease>.Success(ExemptPlayer(ownerModId, usage));
        }

        internal ITimeLease ExemptPlayer(string consumerId, string usage)
        {
            if (disposed)
            {
                return DeadLease.Instance;
            }

            var id = ledger.Add(LeaseKind.ExemptPlayer, consumerId, usage ?? "exempt-player");
            ApplyDiscrete();
            return new TimeLease(this, id, false);
        }

        public OperationResult<ITimeLease> SetDriver(string usage, ITimeDriver newDriver)
        {
            if (newDriver == null)
            {
                throw new ArgumentNullException(nameof(newDriver));
            }

            return disposed
                ? OperationResult<ITimeLease>.Failure(ModErrorCode.Unavailable, "Chronos is unavailable.")
                : OperationResult<ITimeLease>.Success(SetDriver(ownerModId, usage, newDriver));
        }

        internal ITimeLease SetDriver(string consumerId, string usage, ITimeDriver newDriver)
        {
            if (disposed || newDriver == null)
            {
                return DeadLease.Instance;
            }

            if (driverLeaseId != 0)
            {
                ledger.Remove(driverLeaseId);
            }

            driver = newDriver;
            driverLeaseId = ledger.Add(LeaseKind.Driver, consumerId, usage ?? "driver");
            ApplyDiscrete();
            return new TimeLease(this, driverLeaseId, false);
        }

        public OperationResult<bool> Step(float seconds)
        {
            if (disposed)
            {
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Chronos is unavailable.");
            }

            if (seconds <= 0f || float.IsNaN(seconds) || float.IsInfinity(seconds))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Step duration must be finite and positive.");
            }

            return OperationResult<bool>.Success(StepInternal(Mathf.Clamp(seconds, 0f, 0.5f), 0));
        }

        public OperationResult<bool> StepFixed(int ticks)
        {
            if (disposed)
            {
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Chronos is unavailable.");
            }

            if (ticks <= 0)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Fixed-step count must be positive.");
            }

            return OperationResult<bool>.Success(StepInternal(0f, Mathf.Clamp(ticks, 0, 20)));
        }

        public OperationResult<ITurnScheduler> BeginTurnBased(string usage, TurnSchedulerOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            return disposed
                ? OperationResult<ITurnScheduler>.Failure(ModErrorCode.Unavailable, "Chronos is unavailable.")
                : OperationResult<ITurnScheduler>.Success(BeginTurnBased(ownerModId, usage, options));
        }

        internal ITurnScheduler BeginTurnBased(string consumerId, string usage, TurnSchedulerOptions options)
        {
            if (disposed)
            {
                return new TurnScheduler(null, null, consumerId, new TurnSchedulerOptions());
            }

            turnScheduler?.Dispose();
            var freeze = Freeze(consumerId, usage ?? "turn-based");
            turnScheduler = new TurnScheduler(this, freeze, consumerId, options ?? new TurnSchedulerOptions());
            Mode = TimeMode.TurnBased;
            return turnScheduler;
        }

        bool ITimeLeaseHost.ContainsLease(int id) => !disposed && ledger.Contains(id);

        void ITimeLeaseHost.ReleaseLease(int id, bool wasSuspend) => ReleaseLease(id, wasSuspend);

        internal void ReleaseLease(int id, bool wasSuspend)
        {
            if (disposed)
            {
                return;
            }

            var effects = LeaseLifecycle.Release(
                ledger,
                id,
                wasSuspend,
                ref driverLeaseId,
                ref suspendRefCount);
            if (!effects.Removed)
            {
                return;
            }

            if (effects.ReleasedDriver)
            {
                driver = null;
            }

            if (effects.ReleasePlayerSuspend)
            {
                playerSuspend.Release();
            }

            ApplyDiscrete();
        }

        internal void OnTurnSchedulerEnded(TurnScheduler scheduler)
        {
            if (ReferenceEquals(turnScheduler, scheduler))
            {
                turnScheduler = null;
                ApplyDiscrete();
            }
        }
    }

    // Returned when the service is unavailable so callers never need null checks.
    internal sealed class DeadLease : ITimeLease
    {
        public static readonly DeadLease Instance = new DeadLease();

        public bool IsActive => false;

        public void Release()
        {
        }

        public void Dispose()
        {
        }
    }
}
