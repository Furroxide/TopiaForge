using System;
using TopiaForge.Chronos;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    // Unit tests for the Unity-free core of the Chronos time-control framework: the leak-proof lease derivation
    // (LeaseLedger), the fixedDeltaTime co-scale math (TimeMath), the Superhot ramp (SuperhotTimeDriver), and the
    // turn initiative/energy order (TurnOrder). No UnityEngine and no timeScale writes — these compile into the
    // net8.0 test assembly via the csproj Compile includes, like the OVERRIDE/conversation tests.
    internal static class ChronosTests
    {
        public static void Run()
        {
            TestLeaseDerivation();
            TestLeaseOwnerScopedRelease();
            TestDriverBaseTimesSlow();
            TestFixedDeltaNoDrift();
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
            var fast = new object();
            var slow = new object();
            order.Register(fast, 2f);
            order.Register(slow, 1f);

            order.AddEnergy(0.6f); // fast=1.2 (ready), slow=0.6 (not)
            Assert(ReferenceEquals(order.NextReady(), fast), "the actor over threshold with most energy acts");

            order.SpendTurn(fast); // fast=0.2 (carryover kept)
            Assert(order.NextReady() == null, "after spending, nobody is ready yet");

            order.AddEnergy(1f); // fast=2.2, slow=1.6 — both ready, fast has more
            Assert(ReferenceEquals(order.NextReady(), fast), "the faster actor comes up again first");
        }

        private static void TestTurnOrderTieBreakAndUnregister()
        {
            var order = new TurnOrder(energyPerTurn: 1f);
            var first = new object();
            var second = new object();
            order.Register(first, 1f);
            order.Register(second, 1f);
            order.AddEnergy(1f); // both exactly at threshold ⇒ tie
            Assert(ReferenceEquals(order.NextReady(), first), "equal energy tie-breaks to the earliest registered");

            Assert(order.Unregister(first) && order.Count == 1, "unregister removes an actor");
            Assert(ReferenceEquals(order.NextReady(), second), "the remaining actor is next");
            Assert(!order.Unregister(new object()), "unregistering an unknown token is a no-op");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
