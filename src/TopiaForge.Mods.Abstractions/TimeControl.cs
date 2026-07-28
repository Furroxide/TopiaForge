using System;

namespace TopiaForge.Mods
{
    /// <summary>
    /// Controls <b>game time</b> for the whole mod ecosystem through one coordinated, leak-proof authority.
    /// It is the reusable foundation for time-bending gamemodes:
    /// a hard <see cref="Freeze"/> (turn-based / RPG pause / freeze-to-talk), a continuous <see cref="Slow"/> or
    /// driver-ramped scale (Superhot "time moves only when you move"), bounded stepping (<see cref="Step"/>), and a
    /// full <see cref="BeginTurnBased"/> turn engine.
    /// </summary>
    /// <remarks>
    /// Published by the <c>TopiaForge.Chronos</c> framework mod and resolved with
    /// <c>context.RequireExtension&lt;ITimeControlService&gt;()</c>. Declare a dependency on
    /// <c>io.github.furroxide.topiaforge.chronos</c>.
    /// <para>
    /// <b>Two clocks.</b> The sim (native robots, physics, and any mod entity that should obey slow-mo) runs on the
    /// <i>scaled</i> world clock (<see cref="WorldDeltaTime"/>/<see cref="WorldTime"/>); the control plane
    /// (the local player when exempt, HUD, conversation UI,
    /// countdowns, and the drivers themselves) runs on the <i>unscaled</i> ControlClock
    /// (<see cref="ControlDeltaTime"/>/<see cref="ControlTime"/>). Read these service clocks so your code freezes
    /// (or does not) with the world correctly.
    /// </para>
    /// <para>
    /// <b>Leak-proof by construction.</b> Every effect is a ref-counted, owner-tagged <see cref="ITimeLease"/>; the
    /// effective scale is <i>derived</i> from all active leases (any freeze ⇒ 0, else the product of slow factors),
    /// never last-writer-wins. Releasing a lease restores the prior derived state, and the service resets native
    /// timing plus the player on scene change, on owner teardown, on dispose, and even if
    /// a frame throws — so a held scale can never leak into a menu or the next gamemode (the failure that got the
    /// studio's own slow-mo cut). It also yields to a native pause it did not request instead of fighting it.
    /// </para>
    /// </remarks>
    public interface ITimeControlService
    {
        /// <summary><c>true</c> when the service resolved the engine time hooks and can drive time. Cheap to poll.</summary>
        bool IsAvailable { get; }

        /// <summary>The effective world time scale right now: <c>0</c> = frozen, <c>1</c> = normal, in between = slow-mo.</summary>
        float WorldScale { get; }

        /// <summary>This frame's scaled delta time; read this in simulation/entity loops so they obey the scale.</summary>
        float WorldDeltaTime { get; }

        /// <summary>Accumulated scaled game time for simulation timers that should pause with the world.</summary>
        float WorldTime { get; }

        /// <summary>This frame's unscaled delta time — read this for the player (when exempt), UI, countdowns, and drivers.</summary>
        float ControlDeltaTime { get; }

        /// <summary>Accumulated unscaled (wall-clock) time — for deadlines/UI that must keep running while the world is frozen.</summary>
        float ControlTime { get; }

        /// <summary><c>true</c> when the effective world scale is zero (the sim is frozen).</summary>
        bool IsFrozen { get; }

        /// <summary>The current high-level mode, set by whatever effect is active (informational).</summary>
        TimeMode Mode { get; }

        /// <summary>
        /// Hard-freezes the world (scale 0): turn-based, an RPG pause, or a freeze-to-talk beat. Returns a lease;
        /// dispose it (or <see cref="ITimeLease.Release"/>) to lift the freeze. <paramref name="suspendPlayer"/> also
        /// acquires the shared player-control lease so movement/look suspension composes with other mods and the
        /// controller's prior state is restored only after the final lease releases. Cursor behavior remains UI-owned.
        /// </summary>
        OperationResult<ITimeLease> Freeze(string usage, bool suspendPlayer = false);

        /// <summary>
        /// Slows the world to <paramref name="scale"/> (0..1). Multiple slow leases multiply. Returns a lease;
        /// dispose to restore. Use for steady slow-mo; for input-driven ramps use <see cref="SetDriver"/>.
        /// </summary>
        OperationResult<ITimeLease> Slow(string usage, float scale);

        /// <summary>
        /// Keeps the local player running at full speed while the world is slowed/frozen (the Superhot exemption):
        /// the service scales the native FPS controller's move/look rates up by <c>1/WorldScale</c> each frame.
        /// Returns a lease; dispose to restore native rates. Degrades to a no-op (player slows with the world) when
        /// the controller fields can't be resolved.
        /// </summary>
        OperationResult<ITimeLease> ExemptPlayer(string usage);

        /// <summary>
        /// Installs a <see cref="ITimeDriver"/> that recomputes the world scale every control-clock tick (e.g. the
        /// Superhot ramp). The service feeds it a <see cref="TimeSignal"/> (player input magnitude + control delta).
        /// Returns a lease; dispose to remove the driver. One driver at a time per owner; the latest wins.
        /// </summary>
        OperationResult<ITimeLease> SetDriver(string usage, ITimeDriver driver);

        /// <summary>
        /// Advances the frozen/paused world by a bounded slice of <paramref name="seconds"/> of game time (briefly
        /// lifting the scale), for an RTwP "advance one beat" control. No-op when not frozen. The slice is capped so
        /// a caller can't run the sim away.
        /// </summary>
        OperationResult<bool> Step(float seconds);

        /// <summary>Advances the frozen world by <paramref name="ticks"/> bounded fixed-update steps. No-op when not frozen.</summary>
        OperationResult<bool> StepFixed(int ticks);

        /// <summary>
        /// Enters turn-based mode: hard-freezes the world and hands back a scheduler that runs registered actors in
        /// initiative/energy order, lifting time only for the actor that is acting. Dispose the returned scheduler to
        /// end turn-based mode and release the freeze.
        /// </summary>
        OperationResult<ITurnScheduler> BeginTurnBased(string usage, TurnSchedulerOptions options);

    }

    /// <summary>A ref-counted time effect. Dispose (or <see cref="Release"/>) to remove it and restore the prior state. Idempotent.</summary>
    public interface ITimeLease : IGameplayLease
    {
        /// <summary>Removes this effect. Same as <see cref="IDisposable.Dispose"/>; safe to call more than once.</summary>
        void Release();
    }

    /// <summary>
    /// Recomputes the world scale each control-clock tick from a <see cref="TimeSignal"/> — the pluggable brain of a
    /// time mode (e.g. the Superhot input-ramp). Pure and Unity-free so it unit-tests; the service owns the engine
    /// writes and feeds the signal.
    /// </summary>
    public interface ITimeDriver
    {
        /// <summary>Returns the new world scale (the service clamps it to its valid range and applies it).</summary>
        float ComputeScale(in TimeSignal signal);
    }

    /// <summary>The per-tick inputs a <see cref="ITimeDriver"/> reasons over. All on the unscaled control clock.</summary>
    public readonly struct TimeSignal
    {
        /// <summary>Creates a signal.</summary>
        public TimeSignal(float controlDeltaTime, float currentScale, float playerInputMagnitude, bool playerActing)
        {
            ControlDeltaTime = controlDeltaTime;
            CurrentScale = currentScale;
            PlayerInputMagnitude = playerInputMagnitude;
            PlayerActing = playerActing;
        }

        /// <summary>Unscaled delta time this frame (drive ramps with this, never the scaled clock).</summary>
        public float ControlDeltaTime { get; }

        /// <summary>The current effective world scale (so a driver can lerp from it).</summary>
        public float CurrentScale { get; }

        /// <summary>How much the player is moving/aiming this frame, normalised 0..1 (0 = perfectly still).</summary>
        public float PlayerInputMagnitude { get; }

        /// <summary><c>true</c> when the player took a discrete action this frame (fired/attacked) — a strong "advance time" signal.</summary>
        public bool PlayerActing { get; }
    }

    /// <summary>The high-level time mode an effect expresses (informational; read via <see cref="ITimeControlService.Mode"/>).</summary>
    public enum TimeMode
    {
        /// <summary>Normal play (scale 1, no active effect).</summary>
        Realtime,

        /// <summary>The world is slowed or driver-ramped (e.g. Superhot) but advancing.</summary>
        Slowed,

        /// <summary>The world is frozen but the control plane is live (freeze-to-talk / RPG pause).</summary>
        Paused,

        /// <summary>A turn scheduler owns the clock.</summary>
        TurnBased
    }

    /// <summary>
    /// Runs registered actors in initiative/energy order while the world is hard-frozen, lifting time only for the
    /// actor that is currently acting (others have no queued action and idle). Decoupled from any specific entity
    /// type: an actor is an SDK-native id the consumer owns. Drive it from your update loop with <see cref="Tick"/>;
    /// when <see cref="State"/> is <see cref="TurnState.AwaitingAction"/>, issue <see cref="CurrentActor"/>'s action,
    /// call <see cref="BeginAction"/>, then <see cref="EndAction"/> when it finishes. Dispose to end turn-based mode.
    /// </summary>
    public interface ITurnScheduler : IDisposable
    {
        /// <summary>Adds an actor with a relative <paramref name="speed"/> (higher values act more often).</summary>
        OperationResult<bool> Register(TurnActorId actor, float speed);

        /// <summary>Removes an actor. Returns false when it was not registered.</summary>
        OperationResult<bool> Unregister(TurnActorId actor);

        /// <summary>The current scheduler state.</summary>
        TurnState State { get; }

        /// <summary>The actor whose turn it is, when <see cref="State"/> is <see cref="TurnState.AwaitingAction"/>/<see cref="TurnState.Acting"/>; else <c>null</c>.</summary>
        TurnActorId? CurrentActor { get; }

        /// <summary>Number of registered actors.</summary>
        int ActorCount { get; }

        /// <summary>
        /// The consumer has issued the current actor's action (e.g. told it to walk/attack); lift time so its native
        /// locomotion runs. Valid only in <see cref="TurnState.AwaitingAction"/>.
        /// </summary>
        OperationResult<bool> BeginAction();

        /// <summary>The current actor's action finished; re-freeze, spend its energy, and advance to the next actor.</summary>
        OperationResult<bool> EndAction();

        /// <summary>Advances energy/initiative and the action safety-timeout. Call once per frame with the unscaled delta.</summary>
        void Tick(float controlDeltaTime);
    }

    /// <summary>Identifies one consumer-owned participant in a turn schedule.</summary>
    public readonly struct TurnActorId : IEquatable<TurnActorId>
    {
        /// <summary>Creates an actor id from a non-empty, consumer-stable value.</summary>
        public TurnActorId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A turn actor id is required.", nameof(value));
            }

            Value = value;
        }

        /// <summary>Gets the consumer-stable value.</summary>
        public string Value { get; }

        /// <summary>Compares actor ids using ordinal semantics.</summary>
        public bool Equals(TurnActorId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is TurnActorId other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value ?? string.Empty);

        /// <inheritdoc />
        public override string ToString() => Value ?? string.Empty;

        /// <summary>Compares two actor ids.</summary>
        public static bool operator ==(TurnActorId left, TurnActorId right) => left.Equals(right);

        /// <summary>Compares two actor ids.</summary>
        public static bool operator !=(TurnActorId left, TurnActorId right) => !left.Equals(right);
    }

    /// <summary>The phase a <see cref="ITurnScheduler"/> is in.</summary>
    public enum TurnState
    {
        /// <summary>No actor is ready yet (energy still accumulating), or there are no actors.</summary>
        Idle,

        /// <summary>An actor reached its turn; the consumer should issue its action and call <see cref="ITurnScheduler.BeginAction"/>.</summary>
        AwaitingAction,

        /// <summary>The current actor is acting with time lifted; the consumer calls <see cref="ITurnScheduler.EndAction"/> when done.</summary>
        Acting
    }

    /// <summary>Tuning for <see cref="ITimeControlService.BeginTurnBased"/>.</summary>
    public sealed class TurnSchedulerOptions
    {
        /// <summary>Creates immutable, validated turn-scheduler tuning.</summary>
        /// <param name="energyPerTurn">Positive energy threshold for selecting an actor.</param>
        /// <param name="maxActionSeconds">Positive unscaled timeout for one actor action.</param>
        public TurnSchedulerOptions(float energyPerTurn = 1f, float maxActionSeconds = 8f)
        {
            RequireFinitePositive(energyPerTurn, nameof(energyPerTurn));
            RequireFinitePositive(maxActionSeconds, nameof(maxActionSeconds));
            EnergyPerTurn = energyPerTurn;
            MaxActionSeconds = maxActionSeconds;
        }

        /// <summary>Energy an actor must accumulate (at speed 1) before it gets a turn. Higher = slower cadence.</summary>
        public float EnergyPerTurn { get; }

        /// <summary>
        /// Hard cap (unscaled seconds) on a single actor's action before the scheduler force-ends the turn, so a
        /// stuck/never-arriving action can't strand turn-based mode with time lifted.
        /// </summary>
        public float MaxActionSeconds { get; }

        private static void RequireFinitePositive(float value, string parameter)
        {
            if (value <= 0f || float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameter);
            }
        }
    }

    /// <summary>Adapts time control into the core <see cref="GameplayPause"/> primitive.</summary>
    public static class TimeControlPauseExtensions
    {
        /// <summary>
        /// Returns a pause source that hard-freezes the world, for use as the preferred hold of a
        /// <see cref="GameplayPause"/>.
        /// </summary>
        /// <param name="service">
        /// The time-control service, or <c>null</c> when Chronos is not a dependency. A null or unavailable service
        /// yields a typed failure, so the pause degrades to suspending player control instead.
        /// </param>
        /// <param name="suspendPlayer">
        /// Also take the shared player-control lease, so movement and look suspension composes with other mods.
        /// </param>
        /// <returns>A source usable as the preferred hold of a <see cref="GameplayPause"/>.</returns>
        /// <example>
        /// <code>
        /// var pause = new GameplayPause(Context, "mymod-shop", time.AsPauseSource(), "MYMOD_SHOP_PAUSE_FAILED");
        /// </code>
        /// </example>
        public static Func<string, OperationResult<IGameplayLease>> AsPauseSource(
            this ITimeControlService? service,
            bool suspendPlayer = true)
        {
            return usage =>
            {
                if (service == null || !service.IsAvailable)
                {
                    return OperationResult<IGameplayLease>.Failure(
                        ModErrorCode.Unavailable,
                        "Time control is unavailable.");
                }

                var frozen = service.Freeze(usage, suspendPlayer);
                return frozen.TryGetValue(out var lease)
                    ? OperationResult<IGameplayLease>.Success(lease)
                    : OperationResult<IGameplayLease>.Failure(frozen.ErrorCode, frozen.ErrorMessage);
            };
        }
    }
}
