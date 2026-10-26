using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.PresenceAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The spawn ring over generated ground (WS3.10). For seeded worlds of slopes, steps, holes and walls, every player
    /// that arrives in the host's world is placed on the lowest free slot whose ground lies within a step of the spawn and
    /// has room, as the rule says, never on another player's slot; where no slot fits it stays where its world put it.
    /// The slots each arrival takes are checked against the rule worked out here, not against the planner.
    /// </summary>
    internal static class PresenceSpawnRingTests
    {
        internal static void Run()
        {
            for (var seed = 1; seed <= 12; seed++) ArrivalsFollowTheRuleOn((ulong)seed);
            NoRoomLeavesPlayersWhereTheyAre();
            SlotsFreeWhenPlayersLeaveAndWorldsChange();
            Console.WriteLine("PresenceSpawnRingTests passed.");
        }

        /// <summary>A world made from a seed: rolling ground with terraces, a few holes and a few walls, around a spawn.</summary>
        private sealed class GeneratedWorld
        {
            private readonly double slopeX;
            private readonly double slopeZ;
            private readonly double wave;
            private readonly double terrace;
            private readonly List<(double X, double Z, double Radius)> holes = new List<(double, double, double)>();
            private readonly List<(double X, double Z, double HalfX, double HalfZ)> walls = new List<(double, double, double, double)>();

            internal GeneratedWorld(ulong seed)
            {
                var random = new SeededRandom(seed * 7919);
                slopeX = random.NextDouble(-0.25, 0.25);
                slopeZ = random.NextDouble(-0.25, 0.25);
                wave = random.NextDouble(0, 0.6);
                terrace = random.NextDouble() < 0.5 ? random.NextDouble(0.8, 2.5) : 0;
                for (var index = random.NextInt(0, 5); index > 0; index--) holes.Add((random.NextDouble(-6, 6), random.NextDouble(-6, 6), random.NextDouble(0.5, 1.5)));
                for (var index = random.NextInt(0, 4); index > 0; index--) walls.Add((random.NextDouble(-6, 6), random.NextDouble(-6, 6), random.NextDouble(0.2, 1.5), random.NextDouble(0.2, 1.5)));
                SpawnY = (float)Height(0, 0);
            }

            internal float SpawnY { get; }

            internal double Height(double x, double z)
            {
                var height = slopeX * x + slopeZ * z + wave * Math.Sin(x * 0.9) * Math.Cos(z * 0.7);
                if (terrace > 0 && x > 1.5) height += terrace;
                return height;
            }

            /// <summary>The ground under a probe, ignoring players, as the engine's ray finds it; null over a hole.</summary>
            internal NetVector3? Ground(NetVector3 from)
            {
                foreach (var hole in holes)
                {
                    var dx = from.X - hole.X;
                    var dz = from.Z - hole.Z;
                    if (dx * dx + dz * dz < hole.Radius * hole.Radius) return null;
                }

                var height = Height(from.X, from.Z);
                if (height > from.Y || from.Y - height > 4.0) return null;
                return new NetVector3(from.X, (float)height, from.Z);
            }

            /// <summary>Whether a standing player fits: not within half a metre of a wall.</summary>
            internal bool Clear(NetVector3 feet)
            {
                foreach (var wall in walls)
                {
                    if (Math.Abs(feet.X - wall.X) < wall.HalfX + 0.5 && Math.Abs(feet.Z - wall.Z) < wall.HalfZ + 0.5) return false;
                }

                return true;
            }

            /// <summary>The slot the rule gives the next arrival, worked out from the rule's words, or -1.</summary>
            internal int Expected(NetVector3 spawn, ICollection<int> taken)
            {
                for (var slot = 0; slot < SpawnSlotPlanner.SlotCount; slot++)
                {
                    if (taken.Contains(slot)) continue;
                    var inner = slot < 8;
                    var angle = inner ? slot * Math.PI / 4 : (slot - 8) * Math.PI / 8 + Math.PI / 16;
                    var radius = inner ? 2.5 : 5.0;
                    var x = spawn.X + Math.Sin(angle) * radius;
                    var z = spawn.Z + Math.Cos(angle) * radius;
                    var ground = Ground(new NetVector3((float)x, spawn.Y + 2f, (float)z));
                    if (ground == null || Math.Abs(ground.Value.Y - spawn.Y) > 1.5 || !Clear(ground.Value)) continue;
                    return slot;
                }

                return -1;
            }
        }

        private static void ArrivalsFollowTheRuleOn(ulong seed)
        {
            var world = new GeneratedWorld(seed);
            var rig = new PresenceRig();
            var spawn = new NetVector3(0f, world.SpawnY, 0f);
            rig.Host.Spawn = new TransformState(new Vec3(spawn.X, spawn.Y, spawn.Z), Quat.Identity, new Vec3(1f, 1f, 1f));
            rig.Host.Feet = spawn;
            rig.Host.Backend.Ground = world.Ground;
            rig.Host.Backend.Clear = world.Clear;
            rig.OpenToMultiplayer();
            var taken = new List<int>();
            for (var index = 1; index <= 6; index++)
            {
                var player = rig.AddPlayer("p" + index);
                player.Feet = spawn;
                var expected = world.Expected(spawn, taken);
                rig.Join(player);
                var peer = rig.Host.PeerOf(player)!;
                if (expected < 0)
                {
                    That(player.Backend.Teleports.Count == 0 && peer.Slot < 0, "seed " + seed + ": with no slot that fits, player " + index + " stays where its world put it");
                    continue;
                }

                That(peer.Slot == expected, "seed " + seed + ": player " + index + " takes slot " + expected + " (took " + peer.Slot + ")");
                taken.Add(expected);
                That(player.Backend.Teleports.Count == 1, "seed " + seed + ": and is moved there once");
                var placed = player.Backend.Teleports[0].Feet;
                var ground = world.Ground(new NetVector3(placed.X, spawn.Y + 2f, placed.Z));
                That(ground.HasValue && Math.Abs(ground.Value.Y - placed.Y) < 0.001f, "seed " + seed + ": on the ground");
                That(Math.Abs(placed.Y - spawn.Y) <= 1.5f && world.Clear(placed), "seed " + seed + ": within a step of the spawn, with room to stand");
                var offset = SpawnSlotPlanner.SlotOffset(expected);
                That(Math.Abs(placed.X - offset.X) < 0.001f && Math.Abs(placed.Z - offset.Z) < 0.001f, "seed " + seed + ": at the slot's place on the ring");
                That(Math.Abs(player.Backend.Teleports[0].Yaw) < 0.01f && !player.Backend.Teleports[0].KeepFacing, "seed " + seed + ": facing the way the world's spawn faces");
            }

            That(taken.Distinct().Count() == taken.Count, "seed " + seed + ": no two players share a slot");
            That(rig.Host.Counters.Corrections == 0, "seed " + seed + ": and no placement is taken for a cheat");
        }

        private static void NoRoomLeavesPlayersWhereTheyAre()
        {
            var rig = new PresenceRig();
            rig.Host.Backend.Ground = _ => null;
            rig.HostWith(1);
            var player = rig.Players[0];
            That(player.Backend.Teleports.Count == 0 && rig.Host.Counters.Placements == 0, "a world with no ground around its spawn places nobody");
            That(rig.Host.AvatarOf(player) != null && player.AvatarOf(rig.Host) != null, "and the players still see each other");

            var walled = new PresenceRig();
            walled.Host.Backend.Clear = _ => false;
            walled.HostWith(1);
            That(walled.Players[0].Backend.Teleports.Count == 0, "nor does one walled in all round");

            var unknown = new PresenceRig();
            unknown.Host.Spawn = null;
            unknown.HostWith(1);
            That(unknown.Players[0].Backend.Teleports.Count == 0, "nor a host that does not know where its world put its player");
        }

        private static void SlotsFreeWhenPlayersLeaveAndWorldsChange()
        {
            var rig = new PresenceRig();
            rig.HostWith(3);
            var first = rig.Players[0];
            var second = rig.Players[1];
            var third = rig.Players[2];
            That(rig.Host.PeerOf(first)!.Slot == 0 && rig.Host.PeerOf(second)!.Slot == 1 && rig.Host.PeerOf(third)!.Slot == 2, "three players take the first three slots");

            // The second leaves: its slot is the next arrival's.
            rig.Network.Leave(second.Control);
            rig.Frames(3);
            var fourth = rig.AddPlayer("p4");
            rig.Join(fourth);
            That(rig.Host.PeerOf(fourth)!.Slot == 1 && Near(fourth.Backend.Teleports.Single().Feet, SpawnSlotPlanner.SlotOffset(1), 0.001f), "a slot a player left is the next one's");

            // The host starts its world again: everyone arrives in the new world and is placed afresh, nearest slots first.
            foreach (var node in rig.All) node.World = "world-two";
            rig.Frames(12);
            That(first.Backend.Teleports.Count == 2 && third.Backend.Teleports.Count == 2 && fourth.Backend.Teleports.Count == 2, "every player is placed in the new world");
            var slots = new[] { rig.Host.PeerOf(first)!.Slot, rig.Host.PeerOf(third)!.Slot, rig.Host.PeerOf(fourth)!.Slot };
            That(slots.OrderBy(slot => slot).SequenceEqual(new[] { 0, 1, 2 }), "on the first three slots again (" + string.Join(", ", slots) + ")");
            That(rig.Host.Counters.Corrections == 0, "and none of those moves is taken for a cheat");

            // A player still in the old world when the host's changed is not placed until it arrives in the new one.
            var late = rig.AddPlayer("p5");
            late.World = "world-one";
            rig.Join(late);
            That(late.Backend.Teleports.Count == 0 && rig.Host.PeerOf(late)!.Slot < 0, "a player in another world is not placed in the host's");
            late.World = "world-two";
            rig.Frames(10);
            That(late.Backend.Teleports.Count == 1 && rig.Host.PeerOf(late)!.Slot == 3, "and is placed once it arrives");
        }
    }
}
