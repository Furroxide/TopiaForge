using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Relevancy with 150 m enter and 170 m leave hysteresis (WS3.5).</summary>
    internal static class MultiplayerRelevancyTests
    {
        private const int Unit = NetQuantization.PositionUnitsPerMeter;

        public static void Run()
        {
            RadiiAreExactIntegers();
            EdgesAreInclusiveAndHysteretic();
            ReportsEntriesAndExitsInOrder();
            RejectsDuplicatesAndBadInput();
            Console.WriteLine("MultiplayerRelevancyTests passed.");
        }

        private static void RadiiAreExactIntegers()
        {
            Equal(5_898_240_000L, RelevancySet.EnterRadiusSquaredUnits, "150 m squared in grid units");
            Equal(7_575_961_600L, RelevancySet.LeaveRadiusSquaredUnits, "170 m squared in grid units");
            var far = new NetQuantizedPosition(NetQuantization.PositionMaximumUnits, NetQuantization.PositionMaximumUnits, NetQuantization.PositionMaximumUnits);
            var near = new NetQuantizedPosition(NetQuantization.PositionMinimumUnits, NetQuantization.PositionMinimumUnits, NetQuantization.PositionMinimumUnits);
            Equal(3L * 4_194_303L * 4_194_303L, far.DistanceSquaredTo(near), "the largest distance fits without overflow");
        }

        private static void EdgesAreInclusiveAndHysteretic()
        {
            var viewer = new NetQuantizedPosition(0, 0, 0);
            var set = new RelevancySet();
            var entered = new List<NetworkEntityId>();
            var left = new List<NetworkEntityId>();
            var id = new NetworkEntityId(1);

            Update(150 * Unit + 1);
            True(!set.Contains(id), "one grid unit past 150 m does not enter");
            Update(150 * Unit);
            True(set.Contains(id) && entered.Count == 1, "exactly 150 m enters");
            Update(160 * Unit);
            True(set.Contains(id) && entered.Count == 0 && left.Count == 0, "between the radii a member stays");
            Update(170 * Unit);
            True(set.Contains(id), "exactly 170 m stays");
            Update(170 * Unit + 1);
            True(!set.Contains(id) && left.Count == 1, "one grid unit past 170 m leaves");
            Update(160 * Unit);
            True(!set.Contains(id) && entered.Count == 0, "between the radii a non-member stays out");
            Update(150 * Unit);
            True(set.Contains(id), "re-enters at 150 m");

            // Diagonal distances use the exact squared sum: (90, 0, 120) m is exactly 150 m.
            var diagonal = new RelevancySet();
            diagonal.Update(viewer, new[] { new RelevancyCandidate(id, new NetQuantizedPosition(90 * Unit, 0, 120 * Unit)) }, entered, left);
            True(diagonal.Contains(id), "a 3-4-5 diagonal at 150 m enters");
            diagonal.Update(viewer, new[] { new RelevancyCandidate(id, new NetQuantizedPosition(90 * Unit, 0, 120 * Unit + 1)) }, entered, left);
            True(diagonal.Contains(id), "and stays just past it");

            True(RelevancySet.IsRelevant(false, RelevancySet.EnterRadiusSquaredUnits), "rule: enter edge");
            True(!RelevancySet.IsRelevant(false, RelevancySet.EnterRadiusSquaredUnits + 1), "rule: past enter");
            True(RelevancySet.IsRelevant(true, RelevancySet.LeaveRadiusSquaredUnits), "rule: leave edge");
            True(!RelevancySet.IsRelevant(true, RelevancySet.LeaveRadiusSquaredUnits + 1), "rule: past leave");

            void Update(int x)
            {
                set.Update(viewer, new[] { new RelevancyCandidate(id, new NetQuantizedPosition(x, 0, 0)) }, entered, left);
            }
        }

        private static void ReportsEntriesAndExitsInOrder()
        {
            var viewer = new NetQuantizedPosition(1000, 0, -1000);
            var set = new RelevancySet();
            var entered = new List<NetworkEntityId> { new NetworkEntityId(999) };
            var left = new List<NetworkEntityId>();
            set.Update(viewer, new[]
            {
                Candidate(9, 10),
                Candidate(3, 10),
                Candidate(5, 500),
                new RelevancyCandidate(new NetworkEntityId(7), new NetQuantizedPosition(2_000_000, 0, 0), alwaysRelevant: true),
                Candidate(1, 149)
            }, entered, left);
            Sequence(new[] { Id(1), Id(3), Id(7), Id(9) }, entered, "entries are cleared first and come back ascending");
            Equal(0, left.Count, "nothing left");

            set.Update(viewer, new[] { Candidate(9, 10), Candidate(3, 300) }, entered, left);
            Sequence(new[] { Id(1), Id(3), Id(7) }, left, "missing and distant members leave, ascending");
            Equal(1, set.Count, "one member remains");

            True(set.Remove(Id(9)) && set.Count == 0, "a despawn drops a member silently");
            set.Update(viewer, new[] { Candidate(4, 1), Candidate(2, 1) }, entered, left);
            set.Clear(left);
            Sequence(new[] { Id(2), Id(4) }, left, "clear reports every member");
            Equal(0, set.Count, "cleared");

            RelevancyCandidate Candidate(uint id, int meters) =>
                new RelevancyCandidate(Id(id), new NetQuantizedPosition(viewer.X + meters * Unit, viewer.Y, viewer.Z));
        }

        private static void RejectsDuplicatesAndBadInput()
        {
            var set = new RelevancySet();
            var lists = new List<NetworkEntityId>();
            var origin = new NetQuantizedPosition(0, 0, 0);
            Throws<ArgumentException>(() => set.Update(origin, new[]
            {
                new RelevancyCandidate(Id(1), origin),
                new RelevancyCandidate(Id(1), origin)
            }, lists, new List<NetworkEntityId>()), "a candidate listed twice");
            Equal(0, set.Count, "a rejected update changes nothing");
            Throws<ArgumentException>(() => new RelevancyCandidate(default, origin), "invalid id");
            Throws<ArgumentOutOfRangeException>(() => RelevancySet.IsRelevant(true, -1), "negative distance");
            Throws<ArgumentNullException>(() => set.Update(origin, null!, lists, lists), "null candidates");
        }

        private static NetworkEntityId Id(uint value) => new NetworkEntityId(value);
    }
}
