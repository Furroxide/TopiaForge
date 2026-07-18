using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Mutable game-loop samples advanced explicitly by a test.</summary>
    public sealed class DeterministicGameTime : IGameTime
    {
        private double elapsed;
        private long frameIndex;

        /// <inheritdoc/>
        public GameTimeSample Frame { get; private set; }

        /// <inheritdoc/>
        public GameTimeSample Fixed { get; private set; }

        /// <inheritdoc/>
        public GameTimeSample Late { get; private set; }

        /// <summary>Advances and returns the rendered-frame sample.</summary>
        /// <param name="deltaTime">The non-negative scaled frame duration.</param>
        /// <param name="unscaledDeltaTime">The optional non-negative unscaled duration.</param>
        public GameTimeSample AdvanceFrame(float deltaTime, float? unscaledDeltaTime = null)
        {
            ValidateDelta(deltaTime, nameof(deltaTime));
            var unscaled = unscaledDeltaTime ?? deltaTime;
            ValidateDelta(unscaled, nameof(unscaledDeltaTime));
            elapsed += unscaled;
            frameIndex++;
            Frame = new GameTimeSample(GameLoopPhase.Frame, deltaTime, unscaled, elapsed, frameIndex);
            return Frame;
        }

        /// <summary>Sets and returns the fixed-physics sample without advancing rendered-frame time.</summary>
        public GameTimeSample StepFixed(float deltaTime, float? unscaledDeltaTime = null)
        {
            ValidateDelta(deltaTime, nameof(deltaTime));
            var unscaled = unscaledDeltaTime ?? deltaTime;
            ValidateDelta(unscaled, nameof(unscaledDeltaTime));
            Fixed = new GameTimeSample(GameLoopPhase.Fixed, deltaTime, unscaled, elapsed, frameIndex);
            return Fixed;
        }

        /// <summary>Sets and returns the late-frame sample for the current rendered frame.</summary>
        public GameTimeSample StepLate(float? deltaTime = null, float? unscaledDeltaTime = null)
        {
            var scaled = deltaTime ?? Frame.DeltaTime;
            var unscaled = unscaledDeltaTime ?? Frame.UnscaledDeltaTime;
            ValidateDelta(scaled, nameof(deltaTime));
            ValidateDelta(unscaled, nameof(unscaledDeltaTime));
            Late = new GameTimeSample(GameLoopPhase.Late, scaled, unscaled, elapsed, frameIndex);
            return Late;
        }

        private static void ValidateDelta(float value, string parameterName)
        {
            if (value < 0f || float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }

    /// <summary>Virtual-time scheduler whose work runs only when a test advances it.</summary>
    public sealed class DeterministicModScheduler : IModScheduler
    {
        private readonly FakeModLifetime lifetime;
        private readonly CapturedModLogger logger;
        private readonly List<ScheduledWork> work = new List<ScheduledWork>();
        private long nextId;
        private long frameIndex;
        private double elapsedSeconds;

        /// <summary>Creates a virtual-time scheduler.</summary>
        public DeterministicModScheduler(FakeModLifetime lifetime, CapturedModLogger logger)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Gets the virtual unscaled elapsed time.</summary>
        public TimeSpan Elapsed => TimeSpan.FromSeconds(elapsedSeconds);

        /// <summary>Gets the number of pending scheduled operations.</summary>
        public int PendingCount => work.Count;

        /// <inheritdoc/>
        public OperationResult<IDisposable> NextFrame(Action action)
        {
            return TryAdd(action, elapsedSeconds, frameIndex + 1, 0d);
        }

        /// <inheritdoc/>
        public OperationResult<IDisposable> After(TimeSpan delay, Action action)
        {
            ValidateDelay(delay, allowZero: true, nameof(delay));
            return TryAdd(action, elapsedSeconds + delay.TotalSeconds, 0, 0d);
        }

        /// <inheritdoc/>
        public OperationResult<IDisposable> Every(TimeSpan interval, Action action)
        {
            ValidateDelay(interval, allowZero: false, nameof(interval));
            return TryAdd(action, elapsedSeconds + interval.TotalSeconds, 0, interval.TotalSeconds);
        }

        /// <inheritdoc/>
        public Task<OperationResult<bool>> DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            ValidateDelay(delay, allowZero: true, nameof(delay));
            var completion = new TaskCompletionSource<OperationResult<bool>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ScheduledWork scheduled;
            try
            {
                scheduled = Add(
                    () => completion.TrySetResult(OperationResult<bool>.Success(true)),
                    elapsedSeconds + delay.TotalSeconds,
                    0,
                    0d,
                    () => completion.TrySetResult(OperationResult<bool>.Failure(
                        ModErrorCode.Cancelled,
                        "The scheduled delay was cancelled.")));
            }
            catch (ObjectDisposedException)
            {
                completion.TrySetResult(OperationResult<bool>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake mod stopped before the delay could be scheduled."));
                return completion.Task;
            }

            if (cancellationToken.CanBeCanceled)
            {
                scheduled.SetCancellation(cancellationToken.Register(scheduled.Dispose));
            }

            return completion.Task;
        }

        private OperationResult<IDisposable> TryAdd(
            Action action,
            double dueSeconds,
            long dueFrame,
            double intervalSeconds)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            if (lifetime.IsStopping)
            {
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake mod is stopping and cannot schedule more work.");
            }

            try
            {
                return OperationResult<IDisposable>.Success(
                    Add(action, dueSeconds, dueFrame, intervalSeconds, null));
            }
            catch (ObjectDisposedException)
            {
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake mod stopped before the work could be scheduled.");
            }
        }

        /// <summary>Advances one rendered frame and runs all work that becomes due.</summary>
        /// <param name="duration">The non-negative unscaled frame duration.</param>
        public void AdvanceFrame(TimeSpan duration)
        {
            ValidateDelay(duration, allowZero: true, nameof(duration));
            elapsedSeconds += duration.TotalSeconds;
            frameIndex++;
            RunDueWork();
        }

        /// <summary>Advances virtual time without incrementing the rendered-frame index.</summary>
        public void AdvanceBy(TimeSpan duration)
        {
            ValidateDelay(duration, allowZero: true, nameof(duration));
            elapsedSeconds += duration.TotalSeconds;
            RunDueWork();
        }

        private ScheduledWork Add(
            Action action,
            double dueSeconds,
            long dueFrame,
            double intervalSeconds,
            Action? cancelled)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            var item = new ScheduledWork(
                ++nextId,
                action,
                dueSeconds,
                dueFrame,
                intervalSeconds,
                cancelled,
                Remove);
            work.Add(item);
            item.SetLifetimeLease(lifetime.Track(item));
            return item;
        }

        private void RunDueWork()
        {
            while (true)
            {
                ScheduledWork? due = null;
                foreach (var candidate in work)
                {
                    if (!candidate.IsDue(elapsedSeconds, frameIndex))
                    {
                        continue;
                    }

                    if (due == null || candidate.CompareTo(due) < 0)
                    {
                        due = candidate;
                    }
                }

                if (due == null)
                {
                    return;
                }

                try
                {
                    due.Execute();
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "A deterministic scheduled callback threw.");
                }
            }
        }

        private void Remove(ScheduledWork item)
        {
            work.Remove(item);
        }

        private static void ValidateDelay(TimeSpan delay, bool allowZero, string parameterName)
        {
            var invalidZero = !allowZero && delay == TimeSpan.Zero;
            if (delay < TimeSpan.Zero || invalidZero || double.IsNaN(delay.TotalSeconds) || double.IsInfinity(delay.TotalSeconds))
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }

        private sealed class ScheduledWork : IDisposable, IComparable<ScheduledWork>
        {
            private readonly Action action;
            private readonly double intervalSeconds;
            private readonly Action? cancelled;
            private readonly Action<ScheduledWork> remove;
            private IDisposable? lifetimeLease;
            private CancellationTokenRegistration cancellation;
            private bool hasCancellation;
            private bool disposed;

            public ScheduledWork(
                long id,
                Action action,
                double dueSeconds,
                long dueFrame,
                double intervalSeconds,
                Action? cancelled,
                Action<ScheduledWork> remove)
            {
                Id = id;
                this.action = action;
                DueSeconds = dueSeconds;
                DueFrame = dueFrame;
                this.intervalSeconds = intervalSeconds;
                this.cancelled = cancelled;
                this.remove = remove;
            }

            public long Id { get; }
            public double DueSeconds { get; private set; }
            public long DueFrame { get; }

            public bool IsDue(double elapsed, long frame)
            {
                return !disposed && elapsed >= DueSeconds && (DueFrame == 0 || frame >= DueFrame);
            }

            public int CompareTo(ScheduledWork? other)
            {
                if (other == null)
                {
                    return 1;
                }

                var time = DueSeconds.CompareTo(other.DueSeconds);
                return time != 0 ? time : Id.CompareTo(other.Id);
            }

            public void SetLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease;
            }

            public void SetCancellation(CancellationTokenRegistration value)
            {
                if (disposed)
                {
                    value.Dispose();
                    return;
                }

                cancellation = value;
                hasCancellation = true;
            }

            public void Execute()
            {
                if (disposed)
                {
                    return;
                }

                try
                {
                    action();
                }
                finally
                {
                    if (intervalSeconds > 0d)
                    {
                        DueSeconds += intervalSeconds;
                    }
                    else
                    {
                        Complete();
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
                remove(this);
                if (hasCancellation)
                {
                    cancellation.Dispose();
                }

                cancelled?.Invoke();
                var lease = lifetimeLease;
                lifetimeLease = null;
                lease?.Dispose();
            }

            private void Complete()
            {
                disposed = true;
                remove(this);
                if (hasCancellation)
                {
                    cancellation.Dispose();
                }

                var lease = lifetimeLease;
                lifetimeLease = null;
                lease?.Dispose();
            }
        }
    }

    /// <summary>Manually completes an asynchronous SDK operation from test code.</summary>
    /// <typeparam name="T">The non-null successful value type.</typeparam>
    public sealed class ControlledOperation<T> : IDisposable where T : notnull
    {
        private readonly TaskCompletionSource<OperationResult<T>> completion =
            new TaskCompletionSource<OperationResult<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration cancellation;

        /// <summary>Creates a controlled operation optionally cancelled by a token.</summary>
        public ControlledOperation(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.CanBeCanceled)
            {
                cancellation = cancellationToken.Register(() => Cancel());
                if (completion.Task.IsCompleted)
                {
                    cancellation.Dispose();
                }
            }
        }

        /// <summary>Gets the task observed by the code under test.</summary>
        public Task<OperationResult<T>> Task => completion.Task;

        /// <summary>Gets whether the operation has reached any terminal state.</summary>
        public bool IsCompleted => completion.Task.IsCompleted;

        /// <summary>Completes the operation successfully.</summary>
        public bool Succeed(T value) => Complete(OperationResult<T>.Success(value));

        /// <summary>Completes the operation with a stable expected failure.</summary>
        public bool Fail(ModErrorCode errorCode, string message) =>
            Complete(OperationResult<T>.Failure(errorCode, message));

        /// <summary>Completes the operation with a stable cancellation failure if it is still pending.</summary>
        public bool Cancel() => Complete(OperationResult<T>.Failure(
            ModErrorCode.Cancelled,
            "The controlled operation was cancelled."));

        /// <inheritdoc/>
        public void Dispose()
        {
            cancellation.Dispose();
            Cancel();
        }

        private bool Complete(OperationResult<T> result)
        {
            var changed = completion.TrySetResult(result);
            if (changed)
            {
                cancellation.Dispose();
            }

            return changed;
        }
    }
}
