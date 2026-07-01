using System;
using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.Chronos
{
    // The single, leak-proof authority over Time.timeScale / Time.fixedDeltaTime. Effects are ref-counted leases in a
    // pure LeaseLedger; the effective scale is DERIVED every frame (any freeze ⇒ 0, else driver-base × slow-product),
    // never last-writer-wins. fixedDeltaTime is co-scaled off a baseline captured ONCE so the timestep can't drift
    // across gamemode loads. Force-reset on scene change / owner teardown / dispose / a thrown frame, so a held scale
    // can never leak. Coexists with a native pause (FreezeGame): if it sees an external timeScale==0 it didn't set, it
    // yields rather than fights. Drives Unity time; the derivation/ordering live in Unity-free files (TimeMath/
    // LeaseLedger/TurnOrder) so they unit-test.
    internal sealed class TimeControlService : ITimeControlService, IDisposable
    {
        private const float FixedFloor = 0.1f; // co-scale floor for fixedDeltaTime (keeps the physics step affordable)

        private readonly string ownerModId;
        private readonly IModLogger logger;
        private readonly LeaseLedger ledger = new LeaseLedger();
        private readonly PlayerTimeExemption player;

        private float baseFixedDelta = 0.02f;
        private bool baseFixedCaptured;
        private float ownedScale = 1f;     // the timeScale value WE last wrote
        private bool hasWritten;           // we've taken control of timeScale at least since the last full release
        private bool exemptApplied;
        private int suspendRefCount;
        private bool disposed;

        private int driverLeaseId;
        private ITimeDriver? driver;

        private TurnScheduler? turnScheduler;

        public TimeControlService(string ownerModId, IModLogger logger)
        {
            this.ownerModId = ownerModId ?? "robotopia.chronos";
            this.logger = logger;
            player = new PlayerTimeExemption(logger);
            CaptureBaseFixed();
        }

        public bool IsAvailable => !disposed;

        public float WorldScale { get; private set; } = 1f;
        public float WorldDeltaTime => Time.deltaTime;
        public float WorldTime => Time.time;
        public float ControlDeltaTime => Time.unscaledDeltaTime;
        public float ControlTime => Time.unscaledTime;
        public bool IsFrozen => WorldScale <= 0f;
        public TimeMode Mode { get; private set; } = TimeMode.Realtime;

        // --- leases ---------------------------------------------------------------------------------------------

        public ITimeLease Freeze(string usage, bool suspendPlayer = false)
        {
            if (disposed)
            {
                return DeadLease.Instance;
            }

            var id = ledger.Add(LeaseKind.Freeze, ownerModId, usage ?? "freeze");
            if (suspendPlayer)
            {
                if (suspendRefCount++ == 0)
                {
                    player.Suspend();
                }
            }

            ApplyDiscrete();
            return new TimeLease(this, id, suspendPlayer);
        }

        public ITimeLease Slow(string usage, float scale)
        {
            if (disposed)
            {
                return DeadLease.Instance;
            }

            var id = ledger.Add(LeaseKind.Slow, ownerModId, usage ?? "slow", TimeMath.Clamp01(scale));
            ApplyDiscrete();
            return new TimeLease(this, id, false);
        }

        public ITimeLease ExemptPlayer(string usage)
        {
            if (disposed)
            {
                return DeadLease.Instance;
            }

            var id = ledger.Add(LeaseKind.ExemptPlayer, ownerModId, usage ?? "exempt-player");
            return new TimeLease(this, id, false);
        }

        public ITimeLease SetDriver(string usage, ITimeDriver newDriver)
        {
            if (disposed || newDriver == null)
            {
                return DeadLease.Instance;
            }

            // One driver at a time: drop the previous driver lease so the latest wins.
            if (driverLeaseId != 0)
            {
                ledger.Remove(driverLeaseId);
            }

            driver = newDriver;
            driverLeaseId = ledger.Add(LeaseKind.Driver, ownerModId, usage ?? "driver");
            ApplyDiscrete();
            return new TimeLease(this, driverLeaseId, false);
        }

        public void Step(float seconds)
        {
            StepInternal(Mathf.Clamp(seconds, 0f, 0.5f), 0);
        }

        public void StepFixed(int ticks)
        {
            StepInternal(0f, Mathf.Clamp(ticks, 0, 20));
        }

        public ITurnScheduler BeginTurnBased(string usage, TurnSchedulerOptions options)
        {
            if (disposed)
            {
                return new TurnScheduler(null, null, new TurnSchedulerOptions());
            }

            turnScheduler?.Dispose();
            var freeze = Freeze(usage ?? "turn-based"); // hard-freeze the world; the scheduler lifts time per actor
            turnScheduler = new TurnScheduler(this, freeze, options ?? new TurnSchedulerOptions());
            Mode = TimeMode.TurnBased;
            return turnScheduler;
        }

        public void ForceReset()
        {
            ledger.Clear();
            driver = null;
            driverLeaseId = 0;
            suspendRefCount = 0;
            if (turnScheduler != null)
            {
                var t = turnScheduler;
                turnScheduler = null;
                t.AbortFromService();
            }

            player.RestoreExemption();
            player.ReleaseSuspend();
            exemptApplied = false;
            RestoreBaseline();
            WorldScale = 1f;
            Mode = TimeMode.Realtime;
            hasWritten = false;
        }

        // --- per-frame tick (driven by ChronosMod) --------------------------------------------------------------

        public void Tick(float unscaledDeltaTime)
        {
            if (disposed)
            {
                return;
            }

            try
            {
                // Coexist with a native pause (FreezeGame/pause menu): if something else forced timeScale to 0 and we
                // didn't, stand down — never fight it. Resume when it lifts.
                if (hasWritten && ownedScale != 0f && Time.timeScale == 0f)
                {
                    turnScheduler?.Tick(unscaledDeltaTime); // the scheduler runs on unscaled time and is fine to advance
                    return;
                }

                if (!ledger.HasActiveLeases)
                {
                    if (hasWritten)
                    {
                        RestoreBaseline();
                        hasWritten = false;
                    }

                    WorldScale = 1f;
                    Mode = TimeMode.Realtime;
                    if (exemptApplied)
                    {
                        player.RestoreExemption();
                        exemptApplied = false;
                    }

                    return;
                }

                var driverScale = 1f;
                if (driver != null)
                {
                    var signal = SampleSignal(WorldScale, unscaledDeltaTime);
                    driverScale = TimeMath.Clamp01(driver.ComputeScale(signal));
                }

                ApplyComputed(driverScale);
                turnScheduler?.Tick(unscaledDeltaTime);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Chronos Tick threw; force-resetting time so a non-1 scale can't strand the game.");
                ForceReset();
            }
        }

        public void OnSceneChanged()
        {
            // A scene change releases everything; a consumer re-acquires what it needs in the new scene.
            ForceReset();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            ForceReset();
            disposed = true;
        }

        // --- internals ------------------------------------------------------------------------------------------

        // Called by a TimeLease on release.
        internal void ReleaseLease(int id, bool wasSuspend)
        {
            if (disposed)
            {
                return;
            }

            ledger.Remove(id);
            if (id == driverLeaseId)
            {
                driverLeaseId = 0;
                driver = null;
            }

            if (wasSuspend && suspendRefCount > 0 && --suspendRefCount == 0)
            {
                player.ReleaseSuspend();
            }

            ApplyDiscrete();
        }

        // Called by the TurnScheduler when it is disposed: clear our reference (its freeze lease is released by it).
        internal void OnTurnSchedulerEnded(TurnScheduler scheduler)
        {
            if (ReferenceEquals(turnScheduler, scheduler))
            {
                turnScheduler = null;
                if (Mode == TimeMode.TurnBased)
                {
                    Mode = TimeMode.Realtime;
                }
            }
        }

        // Re-derive and apply the scale after a discrete lease change (no driver sampling — the driver ramps in Tick).
        private void ApplyDiscrete()
        {
            if (disposed)
            {
                return;
            }

            if (!ledger.HasActiveLeases)
            {
                if (hasWritten)
                {
                    RestoreBaseline();
                    hasWritten = false;
                }

                WorldScale = 1f;
                Mode = TimeMode.Realtime;
                if (exemptApplied)
                {
                    player.RestoreExemption();
                    exemptApplied = false;
                }

                return;
            }

            // Keep the current driver-derived scale for the discrete recompute; the next Tick refreshes the ramp.
            var driverScale = driver != null ? Mathf.Clamp01(WorldScale <= 0f ? 1f : WorldScale) : 1f;
            ApplyComputed(driverScale);
        }

        private void ApplyComputed(float driverScale)
        {
            var scale = ledger.EffectiveScale(driverScale);
            WorldScale = scale;
            Mode = turnScheduler != null
                ? TimeMode.TurnBased
                : (ledger.AnyFreeze ? TimeMode.Paused : (scale < 1f ? TimeMode.Slowed : TimeMode.Realtime));

            ApplyScale(scale);

            if (ledger.AnyExemptPlayer && scale < 1f)
            {
                player.ApplyExemption(scale);
                exemptApplied = true;
            }
            else if (exemptApplied)
            {
                player.RestoreExemption();
                exemptApplied = false;
            }
        }

        private void ApplyScale(float scale)
        {
            Time.timeScale = scale;
            if (baseFixedCaptured)
            {
                Time.fixedDeltaTime = TimeMath.FixedDelta(baseFixedDelta, scale, FixedFloor);
            }

            ownedScale = scale;
            hasWritten = true;
        }

        private void RestoreBaseline()
        {
            Time.timeScale = 1f;
            if (baseFixedCaptured)
            {
                Time.fixedDeltaTime = baseFixedDelta;
            }

            ownedScale = 1f;
        }

        // Briefly lift the freeze to advance the frozen sim by a bounded slice (RTwP "advance a beat" / turn step).
        private void StepInternal(float seconds, int fixedTicks)
        {
            if (disposed || !IsFrozen)
            {
                return;
            }

            // Restore real time for exactly one frame's worth so FixedUpdate-driven motion advances, then the next
            // Tick re-applies the frozen scale. (A precise N-fixed-step advance would need a coroutine; this bounded
            // single-frame lift is the safe primitive — callers Step() once per beat.)
            Time.timeScale = 1f;
            if (baseFixedCaptured)
            {
                Time.fixedDeltaTime = baseFixedDelta;
            }

            ownedScale = 1f;
        }

        private TimeSignal SampleSignal(float currentScale, float dt)
        {
            float moveMag = 0f;
            float mouseMag = 0f;
            var acting = false;
            try
            {
                var h = Input.GetAxisRaw("Horizontal");
                var v = Input.GetAxisRaw("Vertical");
                moveMag = Mathf.Clamp01(new Vector2(h, v).magnitude);
                var mx = Input.GetAxisRaw("Mouse X");
                var my = Input.GetAxisRaw("Mouse Y");
                mouseMag = Mathf.Clamp01(new Vector2(mx, my).magnitude * 0.5f);
                acting = Input.GetMouseButton(0) || Input.GetMouseButton(1);
            }
            catch
            {
                // Input axes not configured on this build — degrade to a still signal (world eases toward the floor).
            }

            var magnitude = Mathf.Max(moveMag, mouseMag);
            return new TimeSignal(dt, currentScale, magnitude, acting);
        }

        private void CaptureBaseFixed()
        {
            try
            {
                baseFixedDelta = Time.fixedDeltaTime;
                baseFixedCaptured = baseFixedDelta > 0f;
            }
            catch
            {
                baseFixedCaptured = false;
            }
        }
    }

    // A ref-counted lease handle. Releasing routes back into the service; idempotent.
    internal sealed class TimeLease : ITimeLease
    {
        private TimeControlService? service;
        private readonly int id;
        private readonly bool suspend;

        public TimeLease(TimeControlService service, int id, bool suspend)
        {
            this.service = service;
            this.id = id;
            this.suspend = suspend;
        }

        public bool IsActive => service != null;

        public void Release()
        {
            var s = service;
            service = null;
            s?.ReleaseLease(id, suspend);
        }

        public void Dispose() => Release();
    }

    // Returned when the service is unavailable/disposed so callers never get null and never throw.
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
