using System;
using TopiaForge.Chronos;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    // Unit tests for the Unity-free core of the Chronos time-control framework: the leak-proof lease derivation
    // (LeaseLedger), native-write suppression/logical state (TimeScaleOwnership/TimeScalePlan), fixedDeltaTime
    // co-scale math (TimeMath), the Superhot ramp (SuperhotTimeDriver), and turn initiative/energy order (TurnOrder).
    // No UnityEngine and no timeScale writes — these compile into the test assembly via explicit Compile includes.
    internal static class ChronosTests
    {
        public static void Run()
        {
            TestLeaseDerivation();
            TestLeaseOwnerScopedRelease();
            TestResetInvalidatesLeaseAndStaleReleaseIsHarmless();
            TestReplacedDriverLeaseCannotClearNewDriver();
            TestPlayerSuspensionComposesWithOtherControlLeases();
            TestPlayerSuspensionRetriesAfterTransientFailure();
            TestDriverBaseTimesSlow();
            TestFixedDeltaNoDrift();
            TestNativePauseOwnershipPolicy();
            TestNativePauseOverlayRelease();
            TestDeferredNativePauseRestore();
            TestLogicalStateDuringNativePause();
            TestSuperhotRamp();
            TestTurnOrderInitiative();
            TestTurnOrderTieBreakAndUnregister();
            Console.WriteLine("All Chronos tests passed.");
        }

        private static void TestLeaseDerivation()
        {
            var ledger = new LeaseLedger();
            Assert(Math.Abs(ledger.EffectiveScale(1f) - 1f) < 1e-6f, "no leases ⇒ scale 1");

            var slowA = ledger.Add(LeaseKind.Slow, "mod.a", "slow", 0.5f);
            Assert(Math.Abs(ledger.EffectiveScale(1f) - 0.5f) < 1e-6f, "one Slow(0.5) ⇒ 0.5");

            ledger.Add(LeaseKind.Slow, "mod.a", "slow2", 0.5f);
            Assert(Math.Abs(ledger.EffectiveScale(1f) - 0.25f) < 1e-6f, "two Slow(0.5) multiply ⇒ 0.25");

            var freeze = ledger.Add(LeaseKind.Freeze, "mod.b", "freeze");
            Assert(ledger.EffectiveScale(1f) == 0f, "any Freeze wins ⇒ 0 (never last-writer-wins)");

            ledger.Remove(freeze);
            Assert(Math.Abs(ledger.EffectiveScale(1f) - 0.25f) < 1e-6f, "removing the Freeze restores the derived 0.25");

            ledger.Remove(slowA);
            Assert(Math.Abs(ledger.EffectiveScale(1f) - 0.5f) < 1e-6f, "removing one Slow leaves the other");
        }

        private static void TestLeaseOwnerScopedRelease()
        {
            var ledger = new LeaseLedger();
            ledger.Add(LeaseKind.Slow, "mod.a", "a1", 0.5f);
            ledger.Add(LeaseKind.Freeze, "mod.b", "b1");
            ledger.Add(LeaseKind.Slow, "mod.a", "a2", 0.5f);

            var released = ledger.ReleaseOwner("mod.a");
            Assert(released == 2, "ReleaseOwner releases exactly that owner's leases");
            Assert(ledger.EffectiveScale(1f) == 0f, "mod.b's Freeze survives mod.a's teardown");
            Assert(ledger.ReleaseOwner("mod.b") == 1 && !ledger.HasActiveLeases, "releasing the last owner empties the ledger");
        }

        private static void TestDriverBaseTimesSlow()
        {
            var ledger = new LeaseLedger();
            ledger.Add(LeaseKind.Driver, "mod.a", "driver");
            ledger.Add(LeaseKind.Slow, "mod.a", "slow", 0.5f);
            // The driver supplies the base scale (e.g. Superhot 0.03); slow leases multiply on top.
            Assert(Math.Abs(ledger.EffectiveScale(0.03f) - 0.015f) < 1e-6f, "driver base × slow product");
            Assert(ledger.ActiveDriverId != 0, "the active driver is tracked");
        }

        private static void TestResetInvalidatesLeaseAndStaleReleaseIsHarmless()
        {
            var host = new TestLeaseHost();
            var staleId = host.AddFreeze(suspendPlayer: true);
            var stale = new TimeLease(host, staleId, suspend: true);
            Assert(stale.IsActive, "new lease reflects its live ledger entry");

            host.ForceReset();
            Assert(!stale.IsActive, "ForceReset invalidates an outstanding handle immediately");

            var currentId = host.AddFreeze(suspendPlayer: true);
            var current = new TimeLease(host, currentId, suspend: true);
            stale.Release();
            Assert(current.IsActive, "releasing the stale handle does not remove the newer freeze");
            Assert(host.SuspendRefCount == 1, "stale release does not decrement the newer suspension count");
            Assert(host.PlayerSuspendReleaseCount == 0, "stale release does not lift the newer player suspension");

            current.Release();
            Assert(!current.IsActive && host.SuspendRefCount == 0, "current release removes its own freeze");
            Assert(host.PlayerSuspendReleaseCount == 1, "the final live suspend release lifts player control once");
        }

        private static void TestReplacedDriverLeaseCannotClearNewDriver()
        {
            var host = new TestLeaseHost();
            var firstId = host.ReplaceDriver();
            var first = new TimeLease(host, firstId, suspend: false);
            var secondId = host.ReplaceDriver();
            var second = new TimeLease(host, secondId, suspend: false);

            Assert(!first.IsActive && second.IsActive, "replacing a driver invalidates only the prior handle");
            first.Release();
            Assert(host.DriverLeaseId == secondId, "releasing the replaced handle cannot clear the new driver");
            Assert(second.IsActive, "the replacement driver remains active after stale release");

            second.Release();
            Assert(host.DriverLeaseId == 0 && !second.IsActive, "the live driver clears on its own release");
        }

        private static void TestPlayerSuspensionComposesWithOtherControlLeases()
        {
            using var context = new FakeModContext();
            var externalResult = context.LocalPlayer.AcquireControl("another mod's modal");
            Assert(externalResult.TryGetValue(out _), "external control lease acquired");
            var external = externalResult.Value
                ?? throw new InvalidOperationException("The successful control result had no lease.");
            using var coordinator = new PlayerSuspendCoordinator(context.LocalPlayer, context.Logger);

            coordinator.Suspend("conversation");
            coordinator.Suspend("duplicate request");
            Assert(context.LocalPlayer.ActiveControlLeaseCount == 2, "Chronos acquires one shared control lease");

            coordinator.Release();
            Assert(context.LocalPlayer.ActiveControlLeaseCount == 1, "Chronos release preserves the other mod's lease");
            Assert(external.IsActive, "the other mod retains player control ownership");

            external.Dispose();
            Assert(context.LocalPlayer.ActiveControlLeaseCount == 0, "controls restore after the final owner releases");
        }

        private static void TestPlayerSuspensionRetriesAfterTransientFailure()
        {
            using var context = new FakeModContext();
            context.LocalPlayer.AcquireControlErrorCode = ModErrorCode.Unavailable;
            using var coordinator = new PlayerSuspendCoordinator(context.LocalPlayer, context.Logger);

            coordinator.Suspend("player not ready");
            Assert(!coordinator.IsSuspended && context.LocalPlayer.ActiveControlLeaseCount == 0,
                "a transient player-control failure leaves the coordinator eligible to retry");

            context.LocalPlayer.AcquireControlErrorCode = ModErrorCode.None;
            coordinator.Tick(0.5f);
            Assert(coordinator.IsSuspended && context.LocalPlayer.ActiveControlLeaseCount == 1,
                "the active hard freeze reacquires player control when the player becomes ready");
        }

        private static void TestFixedDeltaNoDrift()
        {
            const float baseFixed = 0.02f;
            Assert(Math.Abs(TimeMath.FixedDelta(baseFixed, 1f, 0.1f) - baseFixed) < 1e-7f, "scale 1 ⇒ baseline");
            Assert(Math.Abs(TimeMath.FixedDelta(baseFixed, 0f, 0.1f) - baseFixed) < 1e-7f, "scale 0 ⇒ baseline (FixedUpdate halts)");
            Assert(Math.Abs(TimeMath.FixedDelta(baseFixed, 0.5f, 0.1f) - 0.01f) < 1e-7f, "scale 0.5 ⇒ co-scaled");
            Assert(Math.Abs(TimeMath.FixedDelta(baseFixed, 0.03f, 0.1f) - (baseFixed * 0.1f)) < 1e-7f, "below the floor ⇒ floored");

            // No drift: always derived from the captured baseline, never the live value.
            var live = baseFixed;
            for (var i = 0; i < 100; i++)
            {
                live = TimeMath.FixedDelta(baseFixed, 0.25f, 0.1f);
            }

            Assert(Math.Abs(TimeMath.FixedDelta(baseFixed, 1f, 0.1f) - baseFixed) < 1e-7f, "repeated cycles can't drift the baseline");
        }

        private static void TestNativePauseOwnershipPolicy()
        {
            Assert(TimeScaleOwnership.IsNativePaused(
                    explicitNativePause: false,
                    hasWritten: false,
                    ownedScale: 1f,
                    observedScale: 0f),
                "a native pause that predates the first Chronos lease remains externally owned");
            Assert(TimeScaleOwnership.IsNativePaused(
                    explicitNativePause: false,
                    hasWritten: true,
                    ownedScale: 0.25f,
                    observedScale: 0f),
                "a native pause that overrides a Chronos slow remains externally owned");
            Assert(!TimeScaleOwnership.IsNativePaused(
                    explicitNativePause: false,
                    hasWritten: true,
                    ownedScale: 0f,
                    observedScale: 0f),
                "Chronos recognizes a zero scale that it wrote itself");
            Assert(TimeScaleOwnership.IsNativePaused(
                    explicitNativePause: true,
                    hasWritten: true,
                    ownedScale: 0f,
                    observedScale: 0f),
                "the explicit pause signal disambiguates a native overlay on a Chronos-owned freeze");
            Assert(!TimeScaleOwnership.CanStep(isFrozen: true, nativePaused: true),
                "bounded stepping cannot lift a native pause layered over a Chronos freeze");
            Assert(TimeScaleOwnership.CanStep(isFrozen: true, nativePaused: false),
                "bounded stepping remains available for a Chronos-only freeze");
            Assert(TimeScaleOwnership.RestoreFixedOnAbandon(hasWritten: true, baseFixedCaptured: true)
                && !TimeScaleOwnership.RestoreFixedOnAbandon(hasWritten: false, baseFixedCaptured: true),
                "disposing behind native pause restores Chronos' fixed-step ownership without lifting timeScale");
        }

        private static void TestNativePauseOverlayRelease()
        {
            var externalSlowPause = TimeScaleOwnership.PlanRestore(
                explicitNativePause: false,
                hasWritten: true,
                ownedScale: 0.25f,
                observedScale: 0f);
            Assert(!externalSlowPause.WriteBaseline && externalSlowPause.RetainOwnership,
                "disposing the final slow lease cannot restore scale one behind a native pause");

            var ownedFreeze = TimeScaleOwnership.PlanRestore(
                explicitNativePause: false,
                hasWritten: true,
                ownedScale: 0f,
                observedScale: 0f);
            Assert(ownedFreeze.WriteBaseline && !ownedFreeze.RetainOwnership,
                "disposing a Chronos-owned freeze restores the baseline");

            var nativeOverlay = TimeScaleOwnership.PlanRestore(
                explicitNativePause: true,
                hasWritten: true,
                ownedScale: 0f,
                observedScale: 0f);
            Assert(!nativeOverlay.WriteBaseline && nativeOverlay.RetainOwnership,
                "releasing or force-resetting a Chronos freeze behind the native pause overlay cannot lift it");

            var overlayClosed = TimeScaleOwnership.PlanRestore(
                explicitNativePause: false,
                hasWritten: nativeOverlay.RetainOwnership,
                ownedScale: 0f,
                observedScale: 0f);
            Assert(overlayClosed.WriteBaseline && !overlayClosed.RetainOwnership,
                "after the overlay closes, retained Chronos ownership safely restores its zero baseline");
        }

        private static void TestDeferredNativePauseRestore()
        {
            Assert(TimeScaleOwnership.PlanDeferredRestore(
                    hasExactPauseRoot: true,
                    exactNativePauseActive: true,
                    ownedScale: 0.5f,
                    observedScale: 0f) == DeferredScaleRestoreAction.Wait,
                "Chronos unload never lifts an explicitly active native pause");
            Assert(TimeScaleOwnership.PlanDeferredRestore(
                    hasExactPauseRoot: false,
                    exactNativePauseActive: false,
                    ownedScale: 0.5f,
                    observedScale: 0f) == DeferredScaleRestoreAction.Wait,
                "the scale-only fallback waits for an external zero pause to release");
            Assert(TimeScaleOwnership.PlanDeferredRestore(
                    hasExactPauseRoot: false,
                    exactNativePauseActive: false,
                    ownedScale: 0.5f,
                    observedScale: 0.5f) == DeferredScaleRestoreAction.RestoreBaseline,
                "after native pause restores Chronos' slow scale, the unload handoff restores baseline time");
            Assert(TimeScaleOwnership.PlanDeferredRestore(
                    hasExactPauseRoot: true,
                    exactNativePauseActive: false,
                    ownedScale: 0f,
                    observedScale: 0f) == DeferredScaleRestoreAction.RestoreBaseline,
                "after an explicit pause closes over a Chronos freeze, the stranded freeze is restorable");
            Assert(TimeScaleOwnership.PlanDeferredRestore(
                    hasExactPauseRoot: false,
                    exactNativePauseActive: false,
                    ownedScale: 0f,
                    observedScale: 0f) == DeferredScaleRestoreAction.Abandon,
                "a zero scale without an exact captured overlay is never lifted by inference");
            Assert(TimeScaleOwnership.PlanDeferredRestore(
                    hasExactPauseRoot: false,
                    exactNativePauseActive: false,
                    ownedScale: 0.5f,
                    observedScale: 0.25f) == DeferredScaleRestoreAction.Abandon,
                "a different non-zero scale is treated as a newer external owner and is never overwritten");
        }

        private static void TestLogicalStateDuringNativePause()
        {
            var ledger = new LeaseLedger();
            ledger.Add(LeaseKind.Slow, "mod.a", "cinematic", 0.5f);
            ledger.Add(LeaseKind.ExemptPlayer, "mod.a", "player");

            var slow = TimeScalePlan.Derive(
                ledger,
                driverScale: 1f,
                turnBased: false,
                nativePaused: true);
            Assert(Math.Abs(slow.WorldScale - 0.5f) < 1e-6f && slow.Mode == TimeMode.Slowed,
                "a lease acquired behind native pause still updates logical scale and mode");
            Assert(slow.ExemptPlayer,
                "logical player exemption remains derived while native pause owns the clock");
            Assert(!slow.WriteNativeScale,
                "native pause suppresses the engine write without suppressing logical derivation");

            ledger.Add(LeaseKind.Freeze, "mod.b", "overlay");
            var freeze = TimeScalePlan.Derive(
                ledger,
                driverScale: 1f,
                turnBased: false,
                nativePaused: true);
            Assert(freeze.WorldScale == 0f && freeze.Mode == TimeMode.Paused && freeze.ExemptPlayer,
                "freeze and exemption changes remain visible behind native pause");
            Assert(!freeze.WriteNativeScale,
                "Step and ordinary application cannot lift an explicit native pause");

            var turn = TimeScalePlan.Derive(
                ledger,
                driverScale: 1f,
                turnBased: true,
                nativePaused: true);
            Assert(turn.Mode == TimeMode.TurnBased,
                "turn-based mode remains authoritative logical state behind native pause");

            var resumed = TimeScalePlan.Derive(
                ledger,
                driverScale: 1f,
                turnBased: false,
                nativePaused: false);
            Assert(resumed.WriteNativeScale && resumed.WorldScale == 0f,
                "the accumulated logical lease state becomes writable when native pause releases");
        }

        private static void TestSuperhotRamp()
        {
            var driver = new SuperhotTimeDriver(idleScale: 0.03f, moveThreshold: 0.05f);

            // Holding still eases the world down toward the floor.
            var s = 1f;
            for (var i = 0; i < 40; i++)
            {
                s = driver.ComputeScale(new TimeSignal(0.1f, s, 0f, false));
            }

            Assert(s < 0.1f && s >= 0.03f, "still ⇒ eases toward the idle floor, clamped");

            // Moving ramps the world up toward full speed.
            var m = 0.03f;
            for (var i = 0; i < 40; i++)
            {
                m = driver.ComputeScale(new TimeSignal(0.1f, m, 1f, false));
            }

            Assert(m > 0.9f, "moving ⇒ ramps up toward 1");

            // Asymmetric: a discrete action ramps up at least as fast as merely moving, from the same start.
            var fromAction = driver.ComputeScale(new TimeSignal(0.05f, 0.03f, 1f, true));
            var fromMove = driver.ComputeScale(new TimeSignal(0.05f, 0.03f, 0.5f, false));
            Assert(fromAction >= fromMove, "acting snaps up at least as fast as moving");
            Assert(driver.ComputeScale(new TimeSignal(0.1f, 0.0f, 0f, false)) >= 0.03f, "never below the floor");
        }

        private static void TestTurnOrderInitiative()
        {
            var order = new TurnOrder(energyPerTurn: 1f);
            var fast = new TurnActorId("fast");
            var slow = new TurnActorId("slow");
            order.Register(fast, 2f);
            order.Register(slow, 1f);

            order.AddEnergy(0.6f); // fast=1.2 (ready), slow=0.6 (not)
            Assert(order.NextReady() == fast, "the actor over threshold with most energy acts");

            order.SpendTurn(fast); // fast=0.2 (carryover kept)
            Assert(order.NextReady() == null, "after spending, nobody is ready yet");

            order.AddEnergy(1f); // fast=2.2, slow=1.6 — both ready, fast has more
            Assert(order.NextReady() == fast, "the faster actor comes up again first");
        }

        private static void TestTurnOrderTieBreakAndUnregister()
        {
            var order = new TurnOrder(energyPerTurn: 1f);
            var first = new TurnActorId("first");
            var second = new TurnActorId("second");
            order.Register(first, 1f);
            order.Register(second, 1f);
            order.AddEnergy(1f); // both exactly at threshold ⇒ tie
            Assert(order.NextReady() == first, "equal energy tie-breaks to the earliest registered");

            Assert(order.Unregister(first) && order.Count == 1, "unregister removes an actor");
            Assert(order.NextReady() == second, "the remaining actor is next");
            Assert(!order.Unregister(new TurnActorId("missing")), "unregistering an unknown token is a no-op");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class TestLeaseHost : ITimeLeaseHost
        {
            private readonly LeaseLedger ledger = new LeaseLedger();
            private int driverLeaseId;
            private int suspendRefCount;

            public int DriverLeaseId => driverLeaseId;
            public int SuspendRefCount => suspendRefCount;
            public int PlayerSuspendReleaseCount { get; private set; }

            public int AddFreeze(bool suspendPlayer)
            {
                var id = ledger.Add(LeaseKind.Freeze, "test", "freeze");
                if (suspendPlayer)
                {
                    suspendRefCount++;
                }

                return id;
            }

            public int ReplaceDriver()
            {
                if (driverLeaseId != 0)
                {
                    ledger.Remove(driverLeaseId);
                }

                driverLeaseId = ledger.Add(LeaseKind.Driver, "test", "driver");
                return driverLeaseId;
            }

            public void ForceReset()
            {
                ledger.Clear();
                driverLeaseId = 0;
                suspendRefCount = 0;
                PlayerSuspendReleaseCount = 0;
            }

            public bool ContainsLease(int id) => ledger.Contains(id);

            public void ReleaseLease(int id, bool wasSuspend)
            {
                var effects = LeaseLifecycle.Release(
                    ledger,
                    id,
                    wasSuspend,
                    ref driverLeaseId,
                    ref suspendRefCount);
                if (effects.ReleasePlayerSuspend)
                {
                    PlayerSuspendReleaseCount++;
                }
            }
        }
    }
}
