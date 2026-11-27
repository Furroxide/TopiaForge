using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>The priority-accumulator scheduler: fairness, starvation, budget and rest suppression (WS3.5).</summary>
    internal static class MultiplayerSchedulerTests
    {
        public static void Run()
        {
            EqualPrioritiesShareInRotation();
            SharesFollowPriorities();
            TheLowestPriorityNeverStarves();
            KeepsWithinTheByteBudget();
            AnOversizedUpdateOverdrawsAndRepays();
            BodiesAtRestStopCostingBudget();
            ForgetsEntitiesThatAreNoLongerCandidates();
            RejectsBadInput();
            Console.WriteLine("MultiplayerSchedulerTests passed.");
        }

        private static void EqualPrioritiesShareInRotation()
        {
            var scheduler = new ReplicationScheduler(300);
            var candidates = new List<ReplicationCandidate>();
            for (uint id = 1; id <= 10; id++) candidates.Add(new ReplicationCandidate(Id(id), 1, 100, false));
            var selected = new List<NetworkEntityId>();
            scheduler.Schedule(candidates, selected);
            Sequence(new[] { Id(1), Id(2), Id(3) }, selected, "the first tick goes in id order");
            scheduler.Schedule(candidates, selected);
            Sequence(new[] { Id(4), Id(5), Id(6) }, selected, "then the ones that waited");

            var sends = new int[11];
            var lastSent = new int[11];
            var worstGap = 0;
            for (var tick = 3; tick <= 302; tick++)
            {
                scheduler.Schedule(candidates, selected);
                Equal(3, selected.Count, "three fit each tick");
                foreach (var id in selected)
                {
                    sends[id.Value]++;
                    if (lastSent[id.Value] > 0) worstGap = Math.Max(worstGap, tick - lastSent[id.Value]);
                    lastSent[id.Value] = tick;
                }
            }

            for (var id = 1; id <= 10; id++) Equal(90, sends[id], "equal priorities get equal shares (entity " + id + ")");
            True(worstGap <= 4, "nobody waits more than ceil(10 / 3) ticks; the worst gap was " + worstGap);
        }

        private static void SharesFollowPriorities()
        {
            var scheduler = new ReplicationScheduler(100);
            var candidates = new[]
            {
                new ReplicationCandidate(Id(1), 3, 100, false),
                new ReplicationCandidate(Id(2), 1, 100, false)
            };
            var selected = new List<NetworkEntityId>();
            var sends = new int[3];
            for (var tick = 0; tick < 400; tick++)
            {
                scheduler.Schedule(candidates, selected);
                Equal(1, selected.Count, "one fits each tick");
                sends[selected[0].Value]++;
            }

            Equal(300, sends[1], "priority 3 gets three quarters");
            Equal(100, sends[2], "priority 1 gets one quarter");
        }

        private static void TheLowestPriorityNeverStarves()
        {
            // Three entities at priority 1000 and one at priority 1 compete for one send a tick, and ties favour the
            // rivals' lower ids. Linear ageing still bounds the low one's wait by the sum of its rivals' priorities
            // over its own, plus one tick.
            const int high = 1000;
            var scheduler = new ReplicationScheduler(100);
            var candidates = new[]
            {
                new ReplicationCandidate(Id(1), high, 100, false),
                new ReplicationCandidate(Id(2), high, 100, false),
                new ReplicationCandidate(Id(3), high, 100, false),
                new ReplicationCandidate(Id(4), 1, 100, false)
            };
            var bound = 3 * high + 1;
            var selected = new List<NetworkEntityId>();
            var lastLow = 0;
            var lowSends = 0;
            for (var tick = 1; tick <= 3 * bound; tick++)
            {
                scheduler.Schedule(candidates, selected);
                if (!selected.Contains(Id(4))) continue;
                True(tick - lastLow <= bound, "the low-priority entity waited " + (tick - lastLow) + " ticks");
                lastLow = tick;
                lowSends++;
            }

            True(lowSends >= 2, "the low-priority entity is sent repeatedly, not once");
        }

        private static void KeepsWithinTheByteBudget()
        {
            const int budget = ReplicationScheduler.DefaultBudgetBytesPerTick;
            var random = new SeededRandom(41);
            var scheduler = new ReplicationScheduler(budget);
            var selected = new List<NetworkEntityId>();
            var sizes = new Dictionary<uint, int>();
            long total = 0;
            var largest = 0;
            for (var tick = 1; tick <= 2000; tick++)
            {
                var candidates = new List<ReplicationCandidate>();
                sizes.Clear();
                for (uint id = 1; id <= 40; id++)
                {
                    if (random.NextInt(0, 4) == 0) continue;
                    var size = random.NextInt(12, 400);
                    sizes[id] = size;
                    largest = Math.Max(largest, size);
                    candidates.Add(new ReplicationCandidate(Id(id), random.NextInt(1, 50), size, random.NextInt(0, 10) == 0));
                }

                scheduler.Schedule(candidates, selected);
                var bytes = 0;
                foreach (var id in selected) bytes += sizes[id.Value];
                True(bytes <= budget || selected.Count == 1, "a tick spends at most its budget unless one update overdraws");
                total += bytes;
                True(total <= (long)tick * budget + largest, "cumulative bytes stay within the budget plus one update");
            }

            True(total > 2000L * budget * 9 / 10, "a busy scheduler uses most of its budget: " + total);
        }

        private static void AnOversizedUpdateOverdrawsAndRepays()
        {
            var scheduler = new ReplicationScheduler(800);
            var candidates = new[] { new ReplicationCandidate(Id(1), 1, 2000, false) };
            var selected = new List<NetworkEntityId>();
            var sentTicks = new List<int>();
            for (var tick = 1; tick <= 11; tick++)
            {
                scheduler.Schedule(candidates, selected);
                if (selected.Count == 1) sentTicks.Add(tick);
            }

            Sequence(new[] { 1, 3, 6, 8, 11 }, sentTicks, "2000-byte updates at 800 bytes a tick: 2 sends every 5 ticks");
            Equal(-1200L, scheduler.Credit, "the debt after the last send");
        }

        private static void BodiesAtRestStopCostingBudget()
        {
            var scheduler = new ReplicationScheduler(100);
            var selected = new List<NetworkEntityId>();
            var resting = Id(1);
            var moving = Id(2);
            var aSends = new List<int>();
            for (var tick = 1; tick <= 30; tick++)
            {
                var atRest = tick != 15;
                scheduler.Schedule(new[]
                {
                    new ReplicationCandidate(resting, 5, 100, atRest),
                    new ReplicationCandidate(moving, 1, 100, false)
                }, selected);
                if (tick == 22) True(scheduler.Refresh(resting), "re-arm the confirmations");
                if (selected.Contains(resting)) aSends.Add(tick);
                if (tick > 3 && tick < 15) Sequence(new[] { moving }, selected, "a suppressed body costs nothing (tick " + tick + ")");
            }

            Sequence(new[] { 1, 2, 3, 15, 16, 17, 18, 23, 24, 25 }, aSends, "three confirmations at rest, every send while moving");
            True(scheduler.IsSuppressed(resting), "suppressed again at the end");
            Equal(0L, scheduler.AccumulatorOf(resting), "a suppressed body does not accumulate");
        }

        private static void ForgetsEntitiesThatAreNoLongerCandidates()
        {
            var scheduler = new ReplicationScheduler(100);
            var selected = new List<NetworkEntityId>();
            var both = new[] { new ReplicationCandidate(Id(1), 1, 100, false), new ReplicationCandidate(Id(2), 1, 100, false) };
            scheduler.Schedule(both, selected);
            Equal(2, scheduler.TrackedCount, "two tracked");
            Equal(1L, scheduler.AccumulatorOf(Id(2)), "the unsent one keeps its claim");
            scheduler.Schedule(new[] { both[0] }, selected);
            Equal(1, scheduler.TrackedCount, "an entity that left relevancy is forgotten");
            Equal(0L, scheduler.AccumulatorOf(Id(2)), "and its claim with it");
            True(scheduler.Forget(Id(1)) && scheduler.TrackedCount == 0, "explicit forget");
            True(!scheduler.Refresh(Id(1)), "refreshing an unknown entity");
        }

        private static void RejectsBadInput()
        {
            var scheduler = new ReplicationScheduler(100);
            var selected = new List<NetworkEntityId>();
            Throws<ArgumentException>(() => scheduler.Schedule(new[]
            {
                new ReplicationCandidate(Id(1), 1, 10, false),
                new ReplicationCandidate(Id(1), 1, 10, false)
            }, selected), "a candidate listed twice");
            Equal(0L, scheduler.Credit, "a rejected tick spends nothing");
            Throws<ArgumentOutOfRangeException>(() => new ReplicationCandidate(Id(1), 0, 10, false), "priority zero");
            Throws<ArgumentOutOfRangeException>(() => new ReplicationCandidate(Id(1), ReplicationScheduler.MaximumPriority + 1, 10, false), "priority");
            Throws<ArgumentOutOfRangeException>(() => new ReplicationCandidate(Id(1), 1, 0, false), "size zero");
            Throws<ArgumentException>(() => new ReplicationCandidate(default, 1, 10, false), "invalid id");
            Throws<ArgumentOutOfRangeException>(() => new ReplicationScheduler(0), "budget");
        }

        private static NetworkEntityId Id(uint value) => new NetworkEntityId(value);
    }
}
