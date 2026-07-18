using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic Chronos fake with observable leases and a manually advanced control clock.</summary>
    public sealed partial class FakeTimeControlService : ITimeControlService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<Lease> leases = new List<Lease>();
        private ITimeDriver? driver;
        private FakeTurnScheduler? turnScheduler;

        /// <summary>Creates a fake time-control service owned by a mod lifetime.</summary>
        public FakeTimeControlService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <inheritdoc />
        public bool IsAvailable { get; set; } = true;

        /// <inheritdoc />
        public float WorldScale { get; private set; } = 1f;

        /// <inheritdoc />
        public float WorldDeltaTime { get; private set; }

        /// <inheritdoc />
        public float WorldTime { get; private set; }

        /// <inheritdoc />
        public float ControlDeltaTime { get; private set; }

        /// <inheritdoc />
        public float ControlTime { get; private set; }

        /// <inheritdoc />
        public bool IsFrozen => WorldScale <= 0f;

        /// <inheritdoc />
        public TimeMode Mode { get; private set; } = TimeMode.Realtime;

        /// <summary>Gets the number of active time effects.</summary>
        public int ActiveLeaseCount => leases.Count;

        /// <summary>Gets the total requested manual game-time slice.</summary>
        public float RequestedStepSeconds { get; private set; }

        /// <summary>Gets the total requested fixed-update slices.</summary>
        public int RequestedFixedSteps { get; private set; }

        /// <summary>Invalidates every active effect, mirroring an authoritative Chronos reset.</summary>
        public void ForceReset()
        {
            turnScheduler?.Dispose();
            turnScheduler = null;
            for (var index = leases.Count - 1; index >= 0; index--)
            {
                leases[index].Dispose();
            }

            driver = null;
            Recompute();
        }

        /// <inheritdoc />
        public OperationResult<ITimeLease> Freeze(string usage, bool suspendPlayer = false) =>
            AddLease(LeaseKind.Freeze, 0f, null);

        /// <inheritdoc />
        public OperationResult<ITimeLease> Slow(string usage, float scale)
        {
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0f || scale > 1f)
            {
                return OperationResult<ITimeLease>.Failure(
                    ModErrorCode.InvalidArgument,
                    "A slow scale must be finite and between zero and one.");
            }

            return AddLease(LeaseKind.Slow, scale, null);
        }

        /// <inheritdoc />
        public OperationResult<ITimeLease> ExemptPlayer(string usage) =>
            AddLease(LeaseKind.Exemption, 1f, null);

        /// <inheritdoc />
        public OperationResult<ITimeLease> SetDriver(string usage, ITimeDriver newDriver)
        {
            if (newDriver == null)
            {
                throw new ArgumentNullException(nameof(newDriver));
            }

            for (var index = leases.Count - 1; index >= 0; index--)
            {
                if (leases[index].Kind == LeaseKind.Driver)
                {
                    leases[index].Dispose();
                }
            }

            return AddLease(LeaseKind.Driver, 1f, newDriver);
        }

        /// <inheritdoc />
        public OperationResult<bool> Step(float seconds)
        {
            if (!IsAvailable)
            {
                return UnavailableBool();
            }

            if (seconds <= 0f || float.IsNaN(seconds) || float.IsInfinity(seconds))
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidArgument,
                    "Step duration must be finite and positive.");
            }

            var applied = IsFrozen;
            if (applied)
            {
                RequestedStepSeconds += Math.Min(seconds, 0.5f);
            }

            return OperationResult<bool>.Success(applied);
        }

        /// <inheritdoc />
        public OperationResult<bool> StepFixed(int ticks)
        {
            if (!IsAvailable)
            {
                return UnavailableBool();
            }

            if (ticks <= 0)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Fixed-step count must be positive.");
            }

            var applied = IsFrozen;
            if (applied)
            {
                RequestedFixedSteps += Math.Min(ticks, 20);
            }

            return OperationResult<bool>.Success(applied);
        }

        /// <inheritdoc />
        public OperationResult<ITurnScheduler> BeginTurnBased(string usage, TurnSchedulerOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (!IsAvailable)
            {
                return OperationResult<ITurnScheduler>.Failure(ModErrorCode.Unavailable, "Fake Chronos is unavailable.");
            }

            turnScheduler?.Dispose();
            var freezeResult = AddLease(LeaseKind.Freeze, 0f, null);
            if (!freezeResult.TryGetValue(out var freeze))
            {
                return OperationResult<ITurnScheduler>.Failure(
                    freezeResult.ErrorCode,
                    freezeResult.ErrorMessage);
            }

            var scheduler = new FakeTurnScheduler(
                options,
                freeze,
                AcquireTurnFreeze,
                OnTurnSchedulerDisposed);
            turnScheduler = scheduler;
            Mode = TimeMode.TurnBased;
            return lifetime.TrackResult<ITurnScheduler>(
                scheduler,
                scheduler.AttachLifetimeLease,
                "The fake mod stopped before turn-based time could begin.");
        }

        /// <summary>Advances the deterministic control and world clocks.</summary>
        public void Advance(float controlDeltaTime, float playerInputMagnitude = 0f, bool playerActing = false)
        {
            if (controlDeltaTime < 0f || float.IsNaN(controlDeltaTime) || float.IsInfinity(controlDeltaTime))
            {
                throw new ArgumentOutOfRangeException(nameof(controlDeltaTime));
            }

            ControlDeltaTime = controlDeltaTime;
            ControlTime += controlDeltaTime;
            if (driver != null && !Has(LeaseKind.Freeze))
            {
                var driven = driver.ComputeScale(new TimeSignal(
                    controlDeltaTime,
                    WorldScale,
                    playerInputMagnitude,
                    playerActing));
                WorldScale = Clamp01(driven);
            }
            else
            {
                Recompute();
            }

            WorldDeltaTime = controlDeltaTime * WorldScale;
            WorldTime += WorldDeltaTime;
        }

        private OperationResult<ITimeLease> AddLease(LeaseKind kind, float scale, ITimeDriver? leaseDriver)
        {
            if (!IsAvailable)
            {
                return OperationResult<ITimeLease>.Failure(ModErrorCode.Unavailable, "Fake Chronos is unavailable.");
            }

            var lease = new Lease(kind, scale, value =>
            {
                leases.Remove(value);
                if (value.Kind == LeaseKind.Driver && ReferenceEquals(driver, value.Driver))
                {
                    driver = null;
                }

                Recompute();
            }, leaseDriver);
            leases.Add(lease);
            if (kind == LeaseKind.Driver)
            {
                driver = leaseDriver;
            }

            var tracked = lifetime.TrackResult<ITimeLease>(
                lease,
                lease.AttachLifetimeLease,
                "The fake mod stopped before the time lease could be acquired.");
            Recompute();
            return tracked;
        }

        private ITimeLease? AcquireTurnFreeze()
        {
            var result = AddLease(LeaseKind.Freeze, 0f, null);
            return result.TryGetValue(out var lease) ? lease : null;
        }

        private void OnTurnSchedulerDisposed(FakeTurnScheduler scheduler)
        {
            if (ReferenceEquals(turnScheduler, scheduler))
            {
                turnScheduler = null;
                Recompute();
            }
        }

        private void Recompute()
        {
            if (Has(LeaseKind.Freeze))
            {
                WorldScale = 0f;
                Mode = turnScheduler != null ? TimeMode.TurnBased : TimeMode.Paused;
                return;
            }

            var scale = 1f;
            foreach (var lease in leases)
            {
                if (lease.Kind == LeaseKind.Slow)
                {
                    scale *= lease.Scale;
                }
            }

            WorldScale = Clamp01(scale);
            Mode = turnScheduler != null
                ? TimeMode.TurnBased
                : (driver != null || WorldScale < 1f ? TimeMode.Slowed : TimeMode.Realtime);
        }

        private bool Has(LeaseKind kind)
        {
            foreach (var lease in leases)
            {
                if (lease.Kind == kind)
                {
                    return true;
                }
            }

            return false;
        }

        private static float Clamp01(float value) =>
            float.IsNaN(value) ? 0f : Math.Max(0f, Math.Min(1f, value));

        private static OperationResult<bool> UnavailableBool() =>
            OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Fake Chronos is unavailable.");
    }
}
