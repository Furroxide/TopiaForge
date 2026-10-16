using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>A planned spawn position: the ring slot and the ground point found for it.</summary>
    internal readonly struct SpawnSlot
    {
        public SpawnSlot(int index, NetVector3 position)
        {
            Index = index;
            Position = position;
        }

        public int Index { get; }

        /// <summary>Where the player's feet go.</summary>
        public NetVector3 Position { get; }
    }

    /// <summary>
    /// Places joining players on rings around the world's readiness spawn point, which the host's own player keeps.
    /// Slots 0-7 lie 2.5 m out at 45 degree steps, starting along +Z and turning toward +X; slots 8-23 lie 5 m out at
    /// 22.5 degree steps, offset by half a step so they sit between the inner ones. Each slot must find ground within
    /// 1.5 m of the spawn point's height and be clear of obstacles. The world queries are injected, so this stays free
    /// of Unity.
    /// </summary>
    internal sealed class SpawnSlotPlanner
    {
        public const int InnerRingSlots = 8;
        public const int OuterRingSlots = 16;
        public const int SlotCount = InnerRingSlots + OuterRingSlots;
        public const double InnerRingRadiusMeters = 2.5;
        public const double OuterRingRadiusMeters = 5.0;

        /// <summary>How far above the spawn point's height the ground probe starts.</summary>
        public const float ProbeHeightMeters = 2f;

        /// <summary>The largest height difference between the found ground and the spawn point.</summary>
        public const float MaximumStepMeters = 1.5f;

        // Unit directions as (x, z), written out so every platform uses the same values: no trigonometry at runtime.
        private const double S45 = 0.7071067811865476;
        private const double Sin11 = 0.19509032201612825;
        private const double Cos11 = 0.9807852804032304;
        private const double Sin33 = 0.5555702330196022;
        private const double Cos33 = 0.8314696123025452;

        private static readonly double[] DirectionX =
        {
            0.0, S45, 1.0, S45, 0.0, -S45, -1.0, -S45,
            Sin11, Sin33, Cos33, Cos11, Cos11, Cos33, Sin33, Sin11,
            -Sin11, -Sin33, -Cos33, -Cos11, -Cos11, -Cos33, -Sin33, -Sin11
        };

        private static readonly double[] DirectionZ =
        {
            1.0, S45, 0.0, -S45, -1.0, -S45, 0.0, S45,
            Cos11, Cos33, Sin33, Sin11, -Sin11, -Sin33, -Cos33, -Cos11,
            -Cos11, -Cos33, -Sin33, -Sin11, Sin11, Sin33, Cos33, Cos11
        };

        private readonly Func<NetVector3, NetVector3?> findGround;
        private readonly Func<NetVector3, bool> isClear;

        /// <param name="findGround">
        /// Casts down from the given point and returns the ground hit, or null when there is none in reach.
        /// </param>
        /// <param name="isClear">Whether a standing player fits with its feet at the given point.</param>
        public SpawnSlotPlanner(Func<NetVector3, NetVector3?> findGround, Func<NetVector3, bool> isClear)
        {
            this.findGround = findGround ?? throw new ArgumentNullException(nameof(findGround));
            this.isClear = isClear ?? throw new ArgumentNullException(nameof(isClear));
        }

        /// <summary>The horizontal offset of a slot from the spawn point, in metres.</summary>
        public static NetVector3 SlotOffset(int slot)
        {
            if (slot < 0 || slot >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
            var radius = slot < InnerRingSlots ? InnerRingRadiusMeters : OuterRingRadiusMeters;
            return new NetVector3((float)(DirectionX[slot] * radius), 0f, (float)(DirectionZ[slot] * radius));
        }

        /// <summary>
        /// Finds the lowest-numbered free slot that has ground and room. Returns false when none does; the caller then
        /// uses the spawn point itself, as single player does.
        /// </summary>
        /// <param name="spawnPoint">The world's readiness spawn point.</param>
        /// <param name="occupied">Slots already given to connected participants.</param>
        /// <param name="slot">The planned slot.</param>
        public bool TryPlan(NetVector3 spawnPoint, ICollection<int> occupied, out SpawnSlot slot)
        {
            if (!spawnPoint.IsFinite) throw new ArgumentException("The spawn point must be finite.", nameof(spawnPoint));
            if (occupied == null) throw new ArgumentNullException(nameof(occupied));
            for (var index = 0; index < SlotCount; index++)
            {
                if (occupied.Contains(index)) continue;
                var radius = index < InnerRingSlots ? InnerRingRadiusMeters : OuterRingRadiusMeters;
                var x = (float)(spawnPoint.X + DirectionX[index] * radius);
                var z = (float)(spawnPoint.Z + DirectionZ[index] * radius);
                var ground = findGround(new NetVector3(x, spawnPoint.Y + ProbeHeightMeters, z));
                if (!ground.HasValue || !ground.Value.IsFinite) continue;
                if (Math.Abs((double)ground.Value.Y - spawnPoint.Y) > MaximumStepMeters) continue;
                if (!isClear(ground.Value)) continue;
                slot = new SpawnSlot(index, ground.Value);
                return true;
            }

            slot = default;
            return false;
        }
    }
}
