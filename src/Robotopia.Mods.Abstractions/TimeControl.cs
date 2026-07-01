using System;

namespace Robotopia.Mods
{
    /// <summary>
    /// Controls <b>game time</b> for the whole mod ecosystem — the single, leak-proof authority over Unity's
    /// <c>Time.timeScale</c> / <c>Time.fixedDeltaTime</c>. It is the reusable foundation for time-bending gamemodes:
    /// a hard <see cref="Freeze"/> (turn-based / RPG pause / freeze-to-talk), a continuous <see cref="Slow"/> or
    /// driver-ramped scale (Superhot "time moves only when you move"), bounded stepping (<see cref="Step"/>), and a
    /// full <see cref="BeginTurnBased"/> turn engine.
    /// </summary>
    /// <remarks>
    /// Published by the <c>Robotopia.Chronos</c> framework mod and resolved with
    /// <c>context.GetService&lt;ITimeControlService&gt;()</c>. Declare a dependency on <c>robotopia.chronos</c>.
    /// <para>
    /// <b>Two clocks.</b> The sim (native robots, physics, and any mod entity that should obey slow-mo) runs on the
    /// <i>scaled</i> WorldClock (<see cref="WorldDeltaTime"/>/<see cref="WorldTime"/> = Unity's
    /// <c>Time.deltaTime</c>/scaled time); the control plane (the local player when exempt, HUD, conversation UI,
    /// countdowns, and the drivers themselves) runs on the <i>unscaled</i> ControlClock
    /// (<see cref="ControlDeltaTime"/>/<see cref="ControlTime"/>). Read <b>these</b>, not <c>Time.*</c> directly, so
    /// your code freezes (or doesn't) with the world correctly.
    /// </para>
    /// <para>
    /// <b>Leak-proof by construction.</b> Every effect is a ref-counted, owner-tagged <see cref="ITimeLease"/>; the
    /// effective scale is <i>derived</i> from all active leases (any freeze ⇒ 0, else the product of slow factors),
    /// never last-writer-wins. Releasing a lease restores the prior derived state, and the service force-resets
    /// <c>timeScale</c>/<c>fixedDeltaTime</c> + the player on scene change, on owner teardown, on dispose, and even if
    /// a frame throws — so a held scale can never leak into a menu or the next gamemode (the failure that got the
    /// studio's own slow-mo cut). It also <b>yields to a native pause</b>: if it detects an external
    /// <c>timeScale==0</c> it didn't request, it stands down until that clears rather than fighting it.
    /// </para>
    /// </remarks>
    public interface ITimeControlService
    {
        /// <summary><c>true</c> when the service resolved the engine time hooks and can drive time. Cheap to poll.</summary>
        bool IsAvailable { get; }

        /// <summary>The effective world time scale right now: <c>0</c> = frozen, <c>1</c> = normal, in between = slow-mo.</summary>
        float WorldScale { get; }

        /// <summary>This frame's scaled delta time (= Unity <c>Time.deltaTime</c>) — read this in sim/entity loops so they obey the scale.</summary>
        float WorldDeltaTime { get; }

        /// <summary>Accumulated scaled game time (= Unity scaled time) — for sim timers that should pause with the world.</summary>
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
        /// disables the player's first-person controller + frees the cursor for a modal UI (restored on release).
        /// </summary>
        ITimeLease Freeze(string usage, bool suspendPlayer = false);

        /// <summary>
        /// Slows the world to <paramref name="scale"/> (0..1). Multiple slow leases multiply. Returns a lease;
        /// dispose to restore. Use for steady slow-mo; for input-driven ramps use <see cref="SetDriver"/>.
        /// </summary>
        ITimeLease Slow(string usage, float scale);

        /// <summary>
        /// Keeps the local player running at full speed while the world is slowed/frozen (the Superhot exemption):
        /// the service scales the native FPS controller's move/look rates up by <c>1/WorldScale</c> each frame.
        /// Returns a lease; dispose to restore native rates. Degrades to a no-op (player slows with the world) when
        /// the controller fields can't be resolved.
        /// </summary>
        ITimeLease ExemptPlayer(string usage);

        /// <summary>
        /// Installs a <see cref="ITimeDriver"/> that recomputes the world scale every control-clock tick (e.g. the
        /// Superhot ramp). The service feeds it a <see cref="TimeSignal"/> (player input magnitude + control delta).
        /// Returns a lease; dispose to remove the driver. One driver at a time per owner; the latest wins.
        /// </summary>
        ITimeLease SetDriver(string usage, ITimeDriver driver);

        /// <summary>
        /// Advances the frozen/paused world by a bounded slice of <paramref name="seconds"/> of game time (briefly
        /// lifting the scale), for an RTwP "advance one beat" control. No-op when not frozen. The slice is capped so
        /// a caller can't run the sim away.
        /// </summary>
        void Step(float seconds);

        /// <summary>Advances the frozen world by <paramref name="ticks"/> bounded fixed-update steps. No-op when not frozen.</summary>
        void StepFixed(int ticks);

        /// <summary>
        /// Enters turn-based mode: hard-freezes the world and hands back a scheduler that runs registered actors in
        /// initiative/energy order, lifting time only for the actor that is acting. Dispose the returned scheduler to
        /// end turn-based mode and release the freeze.
        /// </summary>
        ITurnScheduler BeginTurnBased(string usage, TurnSchedulerOptions options);

        /// <summary>
        /// Releases <b>every</b> lease, removes drivers/exemptions, and restores <c>timeScale</c>=1 +
        /// <c>fixedDeltaTime</c> baseline + player controls. The big red button; teardown paths call it for you.
        /// </summary>
        void ForceReset();
    }

    /// <summary>A ref-counted time effect. Dispose (or <see cref="Release"/>) to remove it and restore the prior state. Idempotent.</summary>
    public interface ITimeLease : IDisposable
    {
        /// <summary><c>true</c> until released.</summary>
        bool IsActive { get; }

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
    /// type: an actor is an opaque token the consumer owns. Drive it from your update loop with <see cref="Tick"/>;
    /// when <see cref="State"/> is <see cref="TurnState.AwaitingAction"/>, issue <see cref="CurrentActor"/>'s action,
    /// call <see cref="BeginAction"/>, then <see cref="EndAction"/> when it finishes. Dispose to end turn-based mode.
    /// </summary>
    public interface ITurnScheduler : IDisposable
    {
        /// <summary>Adds an actor with a relative <paramref name="speed"/> (higher = acts more often). No-op if already registered.</summary>
        void Register(object actorToken, float speed);

        /// <summary>Removes an actor (e.g. it died). Safe if not registered. If it was the current actor, the turn is cancelled.</summary>
        void Unregister(object actorToken);

        /// <summary>The current scheduler state.</summary>
        TurnState State { get; }

        /// <summary>The actor whose turn it is, when <see cref="State"/> is <see cref="TurnState.AwaitingAction"/>/<see cref="TurnState.Acting"/>; else <c>null</c>.</summary>
        object? CurrentActor { get; }

        /// <summary>Number of registered actors.</summary>
        int ActorCount { get; }

        /// <summary>
        /// The consumer has issued the current actor's action (e.g. told it to walk/attack); lift time so its native
        /// locomotion runs. Valid only in <see cref="TurnState.AwaitingAction"/>.
        /// </summary>
        void BeginAction();

        /// <summary>The current actor's action finished; re-freeze, spend its energy, and advance to the next actor.</summary>
        void EndAction();

        /// <summary>Advances energy/initiative and the action safety-timeout. Call once per frame with the unscaled delta.</summary>
        void Tick(float controlDeltaTime);
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
        /// <summary>Energy an actor must accumulate (at speed 1) before it gets a turn. Higher = slower cadence.</summary>
        public float EnergyPerTurn { get; set; } = 1f;

        /// <summary>
        /// Hard cap (unscaled seconds) on a single actor's action before the scheduler force-ends the turn, so a
        /// stuck/never-arriving action can't strand turn-based mode with time lifted.
        /// </summary>
        public float MaxActionSeconds { get; set; } = 8f;
    }
}
