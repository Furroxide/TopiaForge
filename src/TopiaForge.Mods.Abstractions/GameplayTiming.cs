using System;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>Identifies the game-loop phase that produced a time sample.</summary>
    public enum GameLoopPhase
    {
        /// <summary>The ordinary once-per-rendered-frame update.</summary>
        Frame = 0,

        /// <summary>The fixed-rate physics update.</summary>
        Fixed = 1,

        /// <summary>The update after ordinary frame subscribers and camera movement.</summary>
        Late = 2
    }

    /// <summary>An immutable, engine-independent sample of game-loop timing.</summary>
    public readonly struct GameTimeSample : IEquatable<GameTimeSample>
    {
        /// <summary>Creates a game-time sample.</summary>
        public GameTimeSample(
            GameLoopPhase phase,
            float deltaTime,
            float unscaledDeltaTime,
            double elapsedTime,
            long frameIndex)
        {
            Phase = phase;
            DeltaTime = deltaTime;
            UnscaledDeltaTime = unscaledDeltaTime;
            ElapsedTime = elapsedTime;
            FrameIndex = frameIndex;
        }

        /// <summary>Gets the loop phase.</summary>
        public GameLoopPhase Phase { get; }

        /// <summary>Gets scaled elapsed seconds since the previous callback in this phase.</summary>
        public float DeltaTime { get; }

        /// <summary>Gets unscaled elapsed seconds since the previous callback in this phase.</summary>
        public float UnscaledDeltaTime { get; }

        /// <summary>Gets unscaled seconds elapsed since the gameplay service started.</summary>
        public double ElapsedTime { get; }

        /// <summary>Gets the rendered-frame index associated with this sample.</summary>
        public long FrameIndex { get; }

        /// <inheritdoc/>
        public bool Equals(GameTimeSample other)
        {
            return Phase == other.Phase && DeltaTime.Equals(other.DeltaTime)
                && UnscaledDeltaTime.Equals(other.UnscaledDeltaTime)
                && ElapsedTime.Equals(other.ElapsedTime) && FrameIndex == other.FrameIndex;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is GameTimeSample other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)Phase;
                hash = (hash * 397) ^ DeltaTime.GetHashCode();
                hash = (hash * 397) ^ UnscaledDeltaTime.GetHashCode();
                hash = (hash * 397) ^ ElapsedTime.GetHashCode();
                return (hash * 397) ^ FrameIndex.GetHashCode();
            }
        }
    }

    /// <summary>Exposes the most recent sample from each game-loop phase.</summary>
    public interface IGameTime
    {
        /// <summary>Gets the most recent rendered-frame sample.</summary>
        GameTimeSample Frame { get; }

        /// <summary>Gets the most recent fixed-physics sample.</summary>
        GameTimeSample Fixed { get; }

        /// <summary>Gets the most recent late-frame sample.</summary>
        GameTimeSample Late { get; }
    }

    /// <summary>Schedules main-thread work owned by the current mod lifetime.</summary>
    public interface IModScheduler
    {
        /// <summary>Schedules an action for the next rendered frame.</summary>
        /// <returns>The lifetime-owned handle, or a stable failure when scheduling is unavailable.</returns>
        OperationResult<IDisposable> NextFrame(Action action);

        /// <summary>Schedules an action after at least the supplied unscaled duration.</summary>
        /// <returns>The lifetime-owned handle, or a stable failure when scheduling is unavailable.</returns>
        OperationResult<IDisposable> After(TimeSpan delay, Action action);

        /// <summary>Schedules a repeating action at an unscaled interval until its handle is disposed.</summary>
        /// <returns>The lifetime-owned handle, or a stable failure when scheduling is unavailable.</returns>
        OperationResult<IDisposable> Every(TimeSpan interval, Action action);

        /// <summary>Completes on the main thread after at least the supplied unscaled duration.</summary>
        /// <param name="delay">A non-negative duration.</param>
        /// <param name="cancellationToken">Optional caller cancellation, combined with the mod stopping token.</param>
        /// <returns>
        /// A successful result after the delay, or a <see cref="ModErrorCode.Cancelled"/> failure when
        /// caller cancellation or mod shutdown wins. Expected cancellation never faults or cancels the task.
        /// </returns>
        Task<OperationResult<bool>> DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default);
    }
}
