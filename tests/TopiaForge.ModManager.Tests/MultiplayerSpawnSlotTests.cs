using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>The spawn ring with injected ground and clearance probes (WS3.5).</summary>
    internal static class MultiplayerSpawnSlotTests
    {
        private static readonly NetVector3 Spawn = new NetVector3(10f, 4f, -20f);

        public static void Run()
        {
            RingsAreEvenlySpacedAndOrdered();
            PlansTheLowestFreeSlotWithGroundAndRoom();
            RejectsSlotsThatStepTooFar();
            FailsWhenEverySlotIsBlocked();
            ValidatesInputs();
            Console.WriteLine("MultiplayerSpawnSlotTests passed.");
        }

        private static void RingsAreEvenlySpacedAndOrdered()
        {
            Equal(24, SpawnSlotPlanner.SlotCount, "24 slots for up to 16 players");
            for (var slot = 0; slot < SpawnSlotPlanner.SlotCount; slot++)
            {
                var offset = SpawnSlotPlanner.SlotOffset(slot);
                var inner = slot < SpawnSlotPlanner.InnerRingSlots;
                var radius = inner ? SpawnSlotPlanner.InnerRingRadiusMeters : SpawnSlotPlanner.OuterRingRadiusMeters;
                Near(radius, Math.Sqrt(offset.LengthSquared), 1e-5, "slot " + slot + " radius");
                Equal(0f, offset.Y, "slots are horizontal");
                var expected = inner ? slot * 45.0 : 11.25 + (slot - SpawnSlotPlanner.InnerRingSlots) * 22.5;
                var angle = Math.Atan2(offset.X, offset.Z) * 180.0 / Math.PI;
                if (angle < 0) angle += 360.0;
                Near(expected, angle, 1e-4, "slot " + slot + " turns from +Z toward +X");
            }

            var closest = double.MaxValue;
            for (var left = 0; left < SpawnSlotPlanner.SlotCount; left++)
            {
                for (var right = left + 1; right < SpawnSlotPlanner.SlotCount; right++)
                {
                    var a = SpawnSlotPlanner.SlotOffset(left);
                    var b = SpawnSlotPlanner.SlotOffset(right);
                    var dx = (double)a.X - b.X;
                    var dz = (double)a.Z - b.Z;
                    closest = Math.Min(closest, Math.Sqrt(dx * dx + dz * dz));
                }
            }

            True(closest > 1.9, "slots are at least 1.9 m apart; the closest pair is " + closest);
            Throws<ArgumentOutOfRangeException>(() => SpawnSlotPlanner.SlotOffset(24), "slot range");
        }

        private static void PlansTheLowestFreeSlotWithGroundAndRoom()
        {
            var probes = new List<NetVector3>();
            var planner = new SpawnSlotPlanner(origin =>
            {
                probes.Add(origin);
                return new NetVector3(origin.X, Spawn.Y, origin.Z);
            }, feet => true);
            True(planner.TryPlan(Spawn, new HashSet<int>(), out var first), "flat open ground");
            Equal(0, first.Index, "the first player gets slot 0");
            Equal(new NetVector3(10f, 4f, -17.5f), first.Position, "2.5 m along +Z, on the ground");
            Equal(new NetVector3(10f, 6f, -17.5f), probes[0], "the probe starts 2 m above the spawn height");

            True(planner.TryPlan(Spawn, new List<int> { 0, 1 }, out var third), "occupied slots are skipped");
            Equal(2, third.Index, "the next free slot");

            var blocked = new SpawnSlotPlanner(origin => new NetVector3(origin.X, Spawn.Y, origin.Z), feet => feet.X < 9.9f);
            True(blocked.TryPlan(Spawn, new HashSet<int>(), out var west), "only slots west of the spawn are clear");
            Equal(5, west.Index, "the first clear slot is the inner ring's south-west one");

            // Slots 0-2 sit at z = -17.5, -18.2 and -20; only slot 3, at -21.8, has ground below it.
            var holes = new SpawnSlotPlanner(origin => origin.Z > -20.5f ? (NetVector3?)null : new NetVector3(origin.X, Spawn.Y, origin.Z), feet => true);
            True(holes.TryPlan(Spawn, new HashSet<int>(), out var grounded), "slots without ground are skipped");
            Equal(3, grounded.Index, "the first slot with ground below it");

            var unstable = new SpawnSlotPlanner(origin => new NetVector3(float.NaN, Spawn.Y, origin.Z), feet => true);
            True(!unstable.TryPlan(Spawn, new HashSet<int>(), out _), "a non-finite ground hit is never used");
        }

        private static void RejectsSlotsThatStepTooFar()
        {
            var ledge = new SpawnSlotPlanner(origin => new NetVector3(origin.X, Spawn.Y + SpawnSlotPlanner.MaximumStepMeters, origin.Z), feet => true);
            True(ledge.TryPlan(Spawn, new HashSet<int>(), out var onLedge), "a 1.5 m step is allowed");
            Equal(Spawn.Y + SpawnSlotPlanner.MaximumStepMeters, onLedge.Position.Y, "standing on the ledge");

            var roof = new SpawnSlotPlanner(origin => new NetVector3(origin.X, Spawn.Y + 1.5001f, origin.Z), feet => true);
            True(!roof.TryPlan(Spawn, new HashSet<int>(), out _), "a step past 1.5 m is refused");
            var pit = new SpawnSlotPlanner(origin => new NetVector3(origin.X, Spawn.Y - 1.5001f, origin.Z), feet => true);
            True(!pit.TryPlan(Spawn, new HashSet<int>(), out _), "a drop past 1.5 m is refused");
        }

        private static void FailsWhenEverySlotIsBlocked()
        {
            var calls = 0;
            var walls = new SpawnSlotPlanner(origin => new NetVector3(origin.X, Spawn.Y, origin.Z), feet =>
            {
                calls++;
                return false;
            });
            True(!walls.TryPlan(Spawn, new HashSet<int>(), out var none), "no slot fits");
            Equal(default(SpawnSlot).Index, none.Index, "nothing is planned; the caller uses the spawn point");
            Equal(SpawnSlotPlanner.SlotCount, calls, "every slot was tried once, in order");
            var full = new HashSet<int>();
            for (var slot = 0; slot < SpawnSlotPlanner.SlotCount; slot++) full.Add(slot);
            True(!walls.TryPlan(Spawn, full, out _), "every slot is taken");
            Equal(SpawnSlotPlanner.SlotCount, calls, "taken slots are not probed");
        }

        private static void ValidatesInputs()
        {
            Throws<ArgumentNullException>(() => new SpawnSlotPlanner(null!, feet => true), "ground probe");
            Throws<ArgumentNullException>(() => new SpawnSlotPlanner(origin => origin, null!), "clearance probe");
            var planner = new SpawnSlotPlanner(origin => origin, feet => true);
            Throws<ArgumentException>(() => planner.TryPlan(new NetVector3(float.NaN, 0f, 0f), new HashSet<int>(), out _), "spawn point");
            Throws<ArgumentNullException>(() => planner.TryPlan(Spawn, null!, out _), "occupied");
        }
    }
}
