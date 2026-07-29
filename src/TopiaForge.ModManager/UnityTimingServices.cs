using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed class UnityGameTime : IGameTime
    {
        public GameTimeSample Frame { get; private set; }
        public GameTimeSample Fixed { get; private set; }
        public GameTimeSample Late { get; private set; }

        public GameTimeSample Update(
            GameLoopPhase phase,
            float deltaTime,
            float unscaledDeltaTime,
            double elapsedTime,
            long frameIndex)
        {
            var sample = new GameTimeSample(phase, deltaTime, unscaledDeltaTime, elapsedTime, frameIndex);
            switch (phase)
            {
                case GameLoopPhase.Frame:
                    Frame = sample;
                    break;
                case GameLoopPhase.Fixed:
                    Fixed = sample;
                    break;
                case GameLoopPhase.Late:
                    Late = sample;
                    break;
            }

            return sample;
        }
    }

    internal sealed class OwnerScheduler : IModScheduler
    {
        private readonly IModLifetime lifetime;
        private readonly UnityScheduler scheduler;
        private readonly IModLogger logger;

        public OwnerScheduler(IModLifetime lifetime, UnityScheduler scheduler, IModLogger logger)
        {
            this.lifetime = lifetime;
            this.scheduler = scheduler;
            this.logger = logger;
        }

        public OperationResult<IDisposable> NextFrame(Action action)
        {
            var callback = Wrap(action);
            return Schedule(() => scheduler.ScheduleFrames(1, callback));
        }

        public OperationResult<IDisposable> After(TimeSpan delay, Action action)
        {
            ValidateDelay(delay, nameof(delay), allowZero: true);
            var callback = Wrap(action);
            return Schedule(() => scheduler.Schedule(delay.TotalSeconds, null, callback));
        }

        public OperationResult<IDisposable> Every(TimeSpan interval, Action action)
        {
            ValidateDelay(interval, nameof(interval), allowZero: false);
            var callback = Wrap(action);
            return Schedule(() => scheduler.Schedule(interval.TotalSeconds, interval.TotalSeconds, callback));
        }

        public Task<OperationResult<bool>> DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            ValidateDelay(delay, nameof(delay), allowZero: true);
            if (lifetime.IsStopping)
            {
                return Task.FromResult(OperationResult<bool>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot schedule more work."));
            }

            var state = new DelayState(lifetime.StoppingToken, cancellationToken);
            try
            {
                state.Attach(scheduler.Schedule(delay.TotalSeconds, null, state.Complete));
                state.AttachLifetimeLease(lifetime.Track(state));
            }
            catch (ObjectDisposedException)
            {
                state.Fail(
                    lifetime.IsStopping ? ModErrorCode.Cancelled : ModErrorCode.InvalidState,
                    lifetime.IsStopping
                        ? "The mod stopped before the delay could be scheduled."
                        : "The game scheduler is no longer available.");
            }

            return state.Task;
        }

        private OperationResult<IDisposable> Schedule(Func<IDisposable> create)
        {
            if (lifetime.IsStopping)
            {
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot schedule more work.");
            }

            try
            {
                return OperationResult<IDisposable>.Success(lifetime.Track(create()));
            }
            catch (ObjectDisposedException)
            {
                return OperationResult<IDisposable>.Failure(
                    lifetime.IsStopping ? ModErrorCode.Cancelled : ModErrorCode.InvalidState,
                    lifetime.IsStopping
                        ? "The mod stopped before the work could be scheduled."
                        : "The game scheduler is no longer available.");
            }
        }

        private Action Wrap(Action action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            return () =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "A scheduled mod action failed.");
                }
            };
        }

        private static void ValidateDelay(TimeSpan value, string parameterName, bool allowZero)
        {
            if (value < TimeSpan.Zero || (!allowZero && value == TimeSpan.Zero))
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }

        private sealed class DelayState : IDisposable
        {
            private readonly TaskCompletionSource<OperationResult<bool>> completion =
                new TaskCompletionSource<OperationResult<bool>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly CancellationTokenRegistration stoppingRegistration;
            private readonly CancellationTokenRegistration callerRegistration;
            private IDisposable? schedule;
            private IDisposable? lifetimeLease;
            private int finished;

            public DelayState(CancellationToken stoppingToken, CancellationToken callerToken)
            {
                stoppingRegistration = stoppingToken.Register(Cancel);
                if (Volatile.Read(ref finished) != 0)
                {
                    stoppingRegistration.Dispose();
                }

                callerRegistration = callerToken.CanBeCanceled ? callerToken.Register(Cancel) : default;
                if (Volatile.Read(ref finished) != 0)
                {
                    callerRegistration.Dispose();
                }
            }

            public Task<OperationResult<bool>> Task => completion.Task;

            public void Attach(IDisposable value)
            {
                schedule = value;
                if (Volatile.Read(ref finished) != 0)
                {
                    Interlocked.Exchange(ref schedule, null)?.Dispose();
                }
            }

            public void AttachLifetimeLease(IDisposable value)
            {
                lifetimeLease = value;
                if (Volatile.Read(ref finished) != 0)
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }

            public void Complete()
            {
                Finish(OperationResult<bool>.Success(true));
            }

            public void Fail(ModErrorCode errorCode, string message)
            {
                Finish(OperationResult<bool>.Failure(errorCode, message));
            }

            public void Dispose()
            {
                Cancel();
            }

            private void Cancel()
            {
                Finish(OperationResult<bool>.Failure(
                    ModErrorCode.Cancelled,
                    "The scheduled delay was cancelled."));
            }

            private void Finish(OperationResult<bool> result)
            {
                if (Interlocked.Exchange(ref finished, 1) != 0)
                {
                    return;
                }

                Interlocked.Exchange(ref schedule, null)?.Dispose();
                stoppingRegistration.Dispose();
                callerRegistration.Dispose();
                completion.TrySetResult(result);
                Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
            }
        }
    }

    internal sealed class UnityScheduler : IDisposable
    {
        private readonly object sync = new object();
        private readonly List<ScheduledAction> scheduled = new List<ScheduledAction>();
        private double elapsedTime;
        private long frameIndex;
        private bool disposed;

        public IDisposable Schedule(double delaySeconds, double? intervalSeconds, Action action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            lock (sync)
            {
                ThrowIfDisposed();
                var item = new ScheduledAction(this, elapsedTime + delaySeconds, null, intervalSeconds, action);
                scheduled.Add(item);
                return item;
            }
        }

        public IDisposable ScheduleFrames(long frames, Action action)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            lock (sync)
            {
                ThrowIfDisposed();
                var item = new ScheduledAction(this, null, frameIndex + frames, null, action);
                scheduled.Add(item);
                return item;
            }
        }

        public void Tick(double currentElapsedTime, long currentFrameIndex)
        {
            UnityMainThreadGuard.AssertCurrent();
            ScheduledAction[] due;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                elapsedTime = currentElapsedTime;
                frameIndex = currentFrameIndex;
                var found = new List<ScheduledAction>();
                for (var index = 0; index < scheduled.Count; index++)
                {
                    if (scheduled[index].IsDue(elapsedTime, frameIndex))
                    {
                        found.Add(scheduled[index]);
                    }
                }

                due = found.ToArray();
            }

            foreach (var item in due)
            {
                item.Invoke(currentElapsedTime);
            }
        }

        public void Dispose()
        {
            UnityMainThreadGuard.AssertCurrent();
            ScheduledAction[] snapshot;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                snapshot = scheduled.ToArray();
                scheduled.Clear();
            }

            foreach (var item in snapshot)
            {
                item.DisposeFromOwner();
            }
        }

        private void Remove(ScheduledAction item)
        {
            lock (sync)
            {
                scheduled.Remove(item);
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(UnityScheduler));
            }
        }

        private sealed class ScheduledAction : IDisposable
        {
            private UnityScheduler? owner;
            private readonly double? intervalSeconds;
            private Action? action;
            private double? dueTime;
            private readonly long? dueFrame;

            public ScheduledAction(
                UnityScheduler owner,
                double? dueTime,
                long? dueFrame,
                double? intervalSeconds,
                Action action)
            {
                this.owner = owner;
                this.dueTime = dueTime;
                this.dueFrame = dueFrame;
                this.intervalSeconds = intervalSeconds;
                this.action = action;
            }

            public bool IsDue(double time, long frame)
            {
                return owner != null && ((dueTime.HasValue && time >= dueTime.Value)
                    || (dueFrame.HasValue && frame >= dueFrame.Value));
            }

            public void Invoke(double time)
            {
                UnityMainThreadGuard.AssertCurrent();
                var callback = action;
                if (callback == null)
                {
                    return;
                }

                if (intervalSeconds.HasValue)
                {
                    dueTime = time + intervalSeconds.Value;
                }
                else
                {
                    var currentOwner = Interlocked.Exchange(ref owner, null);
                    currentOwner?.Remove(this);
                    action = null;
                }

                callback();
            }

            public void Dispose()
            {
                var currentOwner = Interlocked.Exchange(ref owner, null);
                currentOwner?.Remove(this);
                action = null;
            }

            public void DisposeFromOwner()
            {
                Interlocked.Exchange(ref owner, null);
                action = null;
            }
        }
    }
}
