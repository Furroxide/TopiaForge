using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;

namespace TopiaForge.Chronos
{
    // The single, leak-proof authority over Time.timeScale / Time.fixedDeltaTime. Effects are ref-counted leases in a
    // pure LeaseLedger; the effective scale is DERIVED every frame (any freeze ⇒ 0, else driver-base × slow-product),
    // never last-writer-wins. fixedDeltaTime is co-scaled off a baseline captured ONCE so the timestep can't drift
    // across gamemode loads. Force-reset on scene change / owner teardown / dispose / a thrown frame, so a held scale
    // can never leak. Coexists with native pause through a read-only pause-UI signal plus observed-zero ownership
    // fallback, so a pause layered over a Chronos freeze is never lifted. Drives Unity time; derivation/ordering live
    // in Unity-free files (TimeMath/LeaseLedger/TimeScalePlan/TurnOrder) so they unit-test.
    internal sealed partial class TimeControlService :
        ITimeControlService,
        ITimeLeaseHost,
        IOwnerBoundExtensionFactory,
        IDisposable
    {
        private const float FixedFloor = 0.1f; // co-scale floor for fixedDeltaTime (keeps the physics step affordable)

        private readonly string ownerModId;
        private readonly IModLogger logger;
        private readonly LeaseLedger ledger = new LeaseLedger();
        private readonly PlayerTimeExemption player;
        private readonly PlayerSuspendCoordinator playerSuspend;
        private readonly NativePauseSignal nativePause;

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

        public TimeControlService(string ownerModId, IModLogger logger, IPlayerService playerService)
        {
            this.ownerModId = ownerModId ?? "io.github.furroxide.topiaforge.chronos";
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            player = new PlayerTimeExemption(logger);
            playerSuspend = new PlayerSuspendCoordinator(playerService, logger);
            nativePause = new NativePauseSignal(logger);
            // Install the process-lifecycle guard while the mod is live. Initializing lazily from Dispose can be
            // too late when the manager unloads mods from its own shutdown callback.
            DeferredTimeScaleRestore.InitializeLifecycle();
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

        object IOwnerBoundExtensionFactory.CreateOwnerFacade(
            Type contractType,
            string consumerId,
            IModLifetime lifetime)
        {
            if (contractType != typeof(ITimeControlService))
            {
                throw new ArgumentException("Unsupported Chronos extension contract.", nameof(contractType));
            }

            return new OwnerFacade(this, consumerId, lifetime);
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
            playerSuspend.Release();
            exemptApplied = false;
            var explicitNativePause = hasWritten && nativePause.IsPaused();
            RestoreBaseline(explicitNativePause);
            WorldScale = 1f;
            Mode = TimeMode.Realtime;
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
                if (suspendRefCount > 0)
                {
                    playerSuspend.Tick(unscaledDeltaTime);
                }

                // Keep deriving logical state while Robotopia owns the native clock. This is important when a mod
                // acquires/releases a lease behind the pause menu: WorldScale/Mode/exemption stay truthful, while
                // the plan suppresses only the engine write until native pause releases.
                var explicitNativePause = false;
                var nativePaused = (ledger.HasActiveLeases || hasWritten)
                    && IsNativePaused(out explicitNativePause);

                if (!ledger.HasActiveLeases)
                {
                    if (hasWritten)
                    {
                        RestoreBaseline(explicitNativePause);
                    }

                    ApplyLogical(TimeScalePlan.Derive(
                        ledger,
                        driverScale: 1f,
                        turnBased: turnScheduler != null,
                        nativePaused: nativePaused));

                    turnScheduler?.Tick(unscaledDeltaTime);
                    return;
                }

                var driverScale = 1f;
                if (driver != null)
                {
                    var signal = SampleSignal(WorldScale, unscaledDeltaTime);
                    driverScale = TimeMath.Clamp01(driver.ComputeScale(signal));
                }

                ApplyComputed(driverScale, nativePaused);
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
            nativePause.ResetScene();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            ForceReset();
            if (TimeScaleOwnership.RestoreFixedOnAbandon(hasWritten, baseFixedCaptured))
            {
                // ForceReset deliberately leaves timeScale at zero while Robotopia's pause remains authoritative.
                // Chronos will no longer receive a Tick after disposal, so relinquish the independent fixed-step
                // setting now. The deferred handoff below restores timeScale only after native pause closes.
                Time.fixedDeltaTime = baseFixedDelta;
            }

            if (hasWritten)
            {
                var activePauseRoot = nativePause.CaptureActiveRoot();
                nativePause.Dispose();
                try
                {
                    if (DeferredTimeScaleRestore.Begin(activePauseRoot, ownedScale))
                    {
                        logger.Debug("Chronos deferred its final time-scale restore until native pause releases.");
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Chronos could not install its native-pause restore handoff.");
                }
            }
            else
            {
                nativePause.Dispose();
            }

            ownedScale = 1f;
            hasWritten = false;
            disposed = true;
        }

        // Re-derive and apply the scale after a discrete lease change (no driver sampling — the driver ramps in Tick).
        private void ApplyDiscrete()
        {
            if (disposed)
            {
                return;
            }

            var explicitNativePause = false;
            var nativePaused = (ledger.HasActiveLeases || hasWritten)
                && IsNativePaused(out explicitNativePause);
            if (!ledger.HasActiveLeases)
            {
                if (hasWritten)
                {
                    RestoreBaseline(explicitNativePause);
                }

                ApplyLogical(TimeScalePlan.Derive(
                    ledger,
                    driverScale: 1f,
                    turnBased: turnScheduler != null,
                    nativePaused: nativePaused));

                return;
            }

            // Keep the current driver-derived scale for the discrete recompute; the next Tick refreshes the ramp.
            var driverScale = driver != null ? Mathf.Clamp01(WorldScale <= 0f ? 1f : WorldScale) : 1f;
            ApplyComputed(driverScale, nativePaused);
        }

        private void ApplyComputed(float driverScale, bool nativePaused)
        {
            var plan = TimeScalePlan.Derive(
                ledger,
                driverScale,
                turnBased: turnScheduler != null,
                nativePaused: nativePaused);
            ApplyLogical(plan);

            if (plan.WriteNativeScale)
            {
                ApplyScale(plan.WorldScale);
            }
        }

        private void ApplyLogical(TimeScalePlan plan)
        {
            WorldScale = plan.WorldScale;
            Mode = plan.Mode;

            if (plan.ExemptPlayer)
            {
                player.ApplyExemption(plan.WorldScale);
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
            // A reloaded Chronos instance is now authoritative; prevent an older unload handoff from writing a
            // baseline over this new lease after the native pause closes.
            DeferredTimeScaleRestore.CancelForActiveOwner();
            Time.timeScale = scale;
            if (baseFixedCaptured)
            {
                Time.fixedDeltaTime = TimeMath.FixedDelta(baseFixedDelta, scale, FixedFloor);
            }

            ownedScale = scale;
            hasWritten = true;
        }

        private void RestoreBaseline(bool explicitNativePause)
        {
            if (!hasWritten)
            {
                return;
            }

            var plan = TimeScaleOwnership.PlanRestore(
                explicitNativePause,
                hasWritten,
                ownedScale,
                Time.timeScale);
            if (plan.RetainOwnership)
            {
                // Keep the last written scale/fixed-step ownership until the native pause actually releases.
                return;
            }

            if (plan.WriteBaseline)
            {
                Time.timeScale = 1f;
                if (baseFixedCaptured)
                {
                    Time.fixedDeltaTime = baseFixedDelta;
                }

                ownedScale = 1f;
                hasWritten = false;
            }
        }

        private bool IsNativePaused(out bool explicitNativePause)
        {
            explicitNativePause = nativePause.IsPaused();
            return TimeScaleOwnership.IsNativePaused(
                explicitNativePause,
                hasWritten,
                ownedScale,
                Time.timeScale);
        }

        // Briefly lift the freeze to advance the frozen sim by a bounded slice (RTwP "advance a beat" / turn step).
        private bool StepInternal(float seconds, int fixedTicks)
        {
            if (disposed)
            {
                return false;
            }

            var nativePaused = IsNativePaused(out _);
            if (!TimeScaleOwnership.CanStep(IsFrozen, nativePaused))
            {
                return false;
            }

            // Restore real time for exactly one frame's worth so FixedUpdate-driven motion advances, then the next
            // Tick re-applies the frozen scale. (A precise N-fixed-step advance would need a coroutine; this bounded
            // single-frame lift is the safe primitive — callers Step() once per beat.)
            DeferredTimeScaleRestore.CancelForActiveOwner();
            Time.timeScale = 1f;
            if (baseFixedCaptured)
            {
                Time.fixedDeltaTime = baseFixedDelta;
            }

            ownedScale = 1f;
            return true;
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
}
