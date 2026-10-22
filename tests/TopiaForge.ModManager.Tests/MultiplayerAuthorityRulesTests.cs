using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.WorldAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Authority routing's rules (WS3.7), Unity-free: how a request relates to its target, what a limiter's answer and a
    /// verdict on the wire mean, which spawns a joined player shadows, how fast an honest player sends, which bundle paths
    /// a player may name, what a refusal says, and both state machines, every state and event.
    /// </summary>
    internal static class MultiplayerAuthorityRulesTests
    {
        internal static void Run()
        {
            RelationFollowsTheOwnerSlot();
            LimiterAnswersBecomeRefusals();
            VerdictsReadFromTheWireAreBounded();
            OnlyScriptedCodeEveryPeerRunsIsShadowed();
            AnHonestPlayerNeverMeetsTheHostsLimits();
            BundlePathsArePlainPathsInsideAPackage();
            RefusalsSayWhatHappenedAndWhatToDo();
            PredictionsFollowTheirStateMachine();
            ShadowsFollowTheirStateMachine();
            ShadowKeysNumberEachModOnItsOwn();
        }

        private static void RelationFollowsTheOwnerSlot()
        {
            That(SpawnAuthorityRules.RelationOf(0, 1) == NetTargetRelation.Server && SpawnAuthorityRules.RelationOf(0, 255) == NetTargetRelation.Server,
                "an entity no player owns is the world's");
            That(SpawnAuthorityRules.RelationOf(3, 3) == NetTargetRelation.Self && SpawnAuthorityRules.RelationOf(255, 255) == NetTargetRelation.Self,
                "a player's own slot is its own");
            That(SpawnAuthorityRules.RelationOf(4, 3) == NetTargetRelation.OtherPlayer && SpawnAuthorityRules.RelationOf(1, 255) == NetTargetRelation.OtherPlayer,
                "another slot is another player's");
            Throws<ArgumentOutOfRangeException>(() => SpawnAuthorityRules.RelationOf(-1, 1), "an owner below zero is no owner");
            Throws<ArgumentOutOfRangeException>(() => SpawnAuthorityRules.RelationOf(1, 0), "slot zero is never a requester: the host's player is never limited");
        }

        private static void LimiterAnswersBecomeRefusals()
        {
            That(SpawnAuthorityRules.RefusalOf(NetPermission.Allowed) == SpawnRefusal.None, "allowed refuses nothing");
            That(SpawnAuthorityRules.RefusalOf(NetPermission.RateLimited) == SpawnRefusal.RateLimited, "too fast");
            That(SpawnAuthorityRules.RefusalOf(NetPermission.QuotaExceeded) == SpawnRefusal.QuotaExceeded, "too many");
            That(SpawnAuthorityRules.RefusalOf(NetPermission.NotPermitted) == SpawnRefusal.NotPermitted, "not allowed");
            Throws<ArgumentOutOfRangeException>(() => SpawnAuthorityRules.RefusalOf((NetPermission)9), "an answer the limiter never gives is refused");

            // The limiter the host applies: every request spends first, so one that floods forbidden requests is throttled too.
            var limiter = new PlayerRequestLimiter(PermissionPolicy.Default);
            var answers = Enumerable.Range(0, 10).Select(_ => limiter.Evaluate(1, false, NetRequestKind.Destroy, NetTargetRelation.OtherPlayer, 0, 1_000)).ToList();
            That(answers.Take(8).All(answer => answer == NetPermission.NotPermitted) && answers.Skip(8).All(answer => answer == NetPermission.RateLimited),
                "a player destroying another's objects is refused, then throttled once its burst is spent");
        }

        private static void VerdictsReadFromTheWireAreBounded()
        {
            That(SpawnAuthorityRules.FromWire(0) == SpawnRefusal.Unbuildable, "zero is no refusal a host sends");
            for (var value = 1; value <= 6; value++) That(SpawnAuthorityRules.FromWire((byte)value) == (SpawnRefusal)value, "refusal " + value + " reads as itself");
            That(SpawnAuthorityRules.FromWire((byte)SpawnRefusal.TimedOut) == SpawnRefusal.Unbuildable, "a timeout is the player's own, never the host's");
            for (var value = 8; value <= 255; value++) That(SpawnAuthorityRules.FromWire((byte)value) == SpawnRefusal.Unbuildable, "an unknown reason reads as unbuildable");
        }

        private static void OnlyScriptedCodeEveryPeerRunsIsShadowed()
        {
            var classes = (ModNetworkClass[])Enum.GetValues(typeof(ModNetworkClass));
            var contexts = (ModExecutionContext[])Enum.GetValues(typeof(ModExecutionContext));
            var sources = (IntentSource[])Enum.GetValues(typeof(IntentSource));
            var shadowed = new List<string>();
            foreach (var networkClass in classes)
            {
                foreach (var context in contexts)
                {
                    foreach (var source in sources)
                    {
                        var profile = new ModNetProfile(networkClass, context);
                        if (!SpawnAuthorityRules.IsShadowable(profile, source)) continue;
                        shadowed.Add(networkClass + "/" + context + "/" + source);
                    }
                }
            }

            That(shadowed.SequenceEqual(new[] { "Shared/Process/None", "Shared/WorldOwner/None" }),
                "a joined player shadows only a shared mod's scripted code in a context every peer runs (" + string.Join(", ", shadowed) + ")");
        }

        private static void AnHonestPlayerNeverMeetsTheHostsLimits()
        {
            foreach (NetRequestKind kind in Enum.GetValues(typeof(NetRequestKind)))
            {
                var host = PermissionPolicy.Default.LimitFor(kind);
                var pace = SpawnAuthorityRules.ClientPace(kind);
                That(pace.PerSecond == host.PerSecond && pace.Burst == Math.Max(1, host.Burst / 2), kind + " keeps the host's rate and half its burst");
            }

            // A player sends spawns as fast as its pace allows for half a minute; the network delays each by up to 0.9 s, in
            // order, as a stream does. The host's limit never refuses one.
            var random = new Random(37);
            var limit = SpawnAuthorityRules.ClientPace(NetRequestKind.Spawn);
            var client = new TokenBucket(limit.Burst, limit.PerSecond, 0);
            var arrivals = new List<long>();
            var last = 0L;
            for (long now = 0; now < 30_000_000; now += 16_000)
            {
                while (client.TryTake(now))
                {
                    last = Math.Max(last, now + random.Next(0, 900_000));
                    arrivals.Add(last);
                }
            }

            var limiter = new PlayerRequestLimiter(PermissionPolicy.Default);
            var refused = arrivals.Count(arrival => limiter.Evaluate(1, false, NetRequestKind.Spawn, NetTargetRelation.None, 0, arrival) != NetPermission.Allowed);
            That(arrivals.Count >= 120 && refused == 0, "an honest player's " + arrivals.Count + " spawns all pass the host's limit (" + refused + " refused)");

            // One that ignores the pace meets the limit at once.
            var flood = new PlayerRequestLimiter(PermissionPolicy.Default);
            var flooded = Enumerable.Range(0, 20).Count(_ => flood.Evaluate(1, false, NetRequestKind.Spawn, NetTargetRelation.None, 0, 5_000) == NetPermission.RateLimited);
            That(flooded == 12, "twenty spawns at once: the burst of eight passes and twelve are throttled (" + flooded + ")");
        }

        private static void BundlePathsArePlainPathsInsideAPackage()
        {
            foreach (var good in new[] { "assets/world.bundle", "world.bundle", "a/b/c/d.bundle", "assets\\world.bundle", "assets/.hidden/x.bundle", "assets/..x/y.bundle",
                         "assets/world..bundle", new string('a', 512) })
            {
                That(SpawnAuthorityRules.IsSafeBundlePath(good), "'" + good.Substring(0, Math.Min(40, good.Length)) + "' is a plain path inside a package");
            }

            foreach (var bad in new[] { null, string.Empty, "/assets/world.bundle", "\\\\server\\share\\x.bundle", "C:\\x.bundle", "c:x.bundle", "http://example.org/x.bundle",
                         "../x.bundle", "assets/../../x.bundle", "assets/./x.bundle", "assets//x.bundle", "assets/", "./x.bundle", "assets/x.bundle\0", "assets/\nx.bundle",
                         "..", ".", new string('a', 513) })
            {
                That(!SpawnAuthorityRules.IsSafeBundlePath(bad), "'" + (bad ?? "null").Replace("\0", "\\0").Replace("\n", "\\n") + "' is refused before a file is touched");
            }
        }

        private static void RefusalsSayWhatHappenedAndWhatToDo()
        {
            var refusals = ((SpawnRefusal[])Enum.GetValues(typeof(SpawnRefusal))).Where(refusal => refusal != SpawnRefusal.None).ToList();
            var texts = refusals.Select(SpawnAuthorityRules.RefusalText).ToList();
            That(texts.Distinct().Count() == texts.Count, "every refusal says something of its own");
            foreach (var text in texts)
            {
                That(text.Length <= 120 && text.EndsWith(".", StringComparison.Ordinal) && !text.Contains("Robotopia", StringComparison.Ordinal),
                    "'" + text + "' is one short plain sentence or two");
                That(text.Contains("removed", StringComparison.Ordinal), "'" + text + "' says the object is gone");
            }

            That(SpawnAuthorityRules.RefusalText(SpawnRefusal.RateLimited).Contains("Wait a moment", StringComparison.Ordinal) &&
                 SpawnAuthorityRules.RefusalText(SpawnRefusal.QuotaExceeded).Contains("Remove some", StringComparison.Ordinal) &&
                 SpawnAuthorityRules.RefusalText(SpawnRefusal.TimedOut).Contains("Try again", StringComparison.Ordinal),
                "a refusal the player can do something about says what");
        }

        private static void PredictionsFollowTheirStateMachine()
        {
            var expected = new Dictionary<(PredictionState, PredictionEvent), PredictionState>
            {
                { (PredictionState.Queued, PredictionEvent.Sent), PredictionState.Sent },
                { (PredictionState.Queued, PredictionEvent.Adopted), PredictionState.Adopted },
                { (PredictionState.Queued, PredictionEvent.Refused), PredictionState.Refused },
                { (PredictionState.Queued, PredictionEvent.TimedOut), PredictionState.Refused },
                { (PredictionState.Queued, PredictionEvent.StreamRestarted), PredictionState.Queued },
                { (PredictionState.Queued, PredictionEvent.Ended), PredictionState.Ended },
                { (PredictionState.Sent, PredictionEvent.Sent), PredictionState.Sent },
                { (PredictionState.Sent, PredictionEvent.Adopted), PredictionState.Adopted },
                { (PredictionState.Sent, PredictionEvent.Refused), PredictionState.Refused },
                { (PredictionState.Sent, PredictionEvent.TimedOut), PredictionState.Refused },
                { (PredictionState.Sent, PredictionEvent.StreamRestarted), PredictionState.Queued },
                { (PredictionState.Sent, PredictionEvent.Ended), PredictionState.Ended },
                { (PredictionState.Adopted, PredictionEvent.Sent), PredictionState.Adopted },
                { (PredictionState.Adopted, PredictionEvent.Adopted), PredictionState.Adopted },
                { (PredictionState.Adopted, PredictionEvent.Refused), PredictionState.Adopted },
                { (PredictionState.Adopted, PredictionEvent.TimedOut), PredictionState.Adopted },
                { (PredictionState.Adopted, PredictionEvent.StreamRestarted), PredictionState.Adopted },
                { (PredictionState.Adopted, PredictionEvent.Ended), PredictionState.Ended }
            };

            foreach (PredictionState state in Enum.GetValues(typeof(PredictionState)))
            {
                foreach (PredictionEvent happened in Enum.GetValues(typeof(PredictionEvent)))
                {
                    var want = expected.TryGetValue((state, happened), out var listed) ? listed : state;
                    if (state == PredictionState.Refused || state == PredictionState.Ended) want = state;
                    That(SpawnAuthorityRules.Next(state, happened) == want, state + " on " + happened + " reaches " + want);
                }
            }

            Throws<ArgumentOutOfRangeException>(() => SpawnAuthorityRules.Next(PredictionState.Sent, (PredictionEvent)99), "an event that is none is refused");
        }

        private static void ShadowsFollowTheirStateMachine()
        {
            var expected = new Dictionary<(ShadowState, ShadowEvent), ShadowState>
            {
                { (ShadowState.Waiting, ShadowEvent.Matched), ShadowState.Bound },
                { (ShadowState.Waiting, ShadowEvent.Mismatched), ShadowState.Ended },
                { (ShadowState.Waiting, ShadowEvent.Expired), ShadowState.Ended },
                { (ShadowState.Waiting, ShadowEvent.Ended), ShadowState.Ended },
                { (ShadowState.Bound, ShadowEvent.Matched), ShadowState.Bound },
                { (ShadowState.Bound, ShadowEvent.Mismatched), ShadowState.Bound },
                { (ShadowState.Bound, ShadowEvent.Expired), ShadowState.Bound },
                { (ShadowState.Bound, ShadowEvent.Ended), ShadowState.Ended }
            };

            foreach (ShadowState state in Enum.GetValues(typeof(ShadowState)))
            {
                foreach (ShadowEvent happened in Enum.GetValues(typeof(ShadowEvent)))
                {
                    var want = state == ShadowState.Ended ? ShadowState.Ended : expected[(state, happened)];
                    That(SpawnAuthorityRules.Next(state, happened) == want, state + " on " + happened + " reaches " + want);
                }
            }

            Throws<ArgumentOutOfRangeException>(() => SpawnAuthorityRules.Next(ShadowState.Waiting, (ShadowEvent)99), "an event that is none is refused");
        }

        private static void ShadowKeysNumberEachModOnItsOwn()
        {
            var numbering = new ShadowKeyNumbering();
            That(numbering.Next("io.example.a") == 1 && numbering.Next("io.example.a") == 2 && numbering.Next("io.example.b") == 1 && numbering.Next("IO.Example.A") == 3,
                "each mod counts from one, its id ignoring case as the runtime's do");
            That(numbering.Count("io.example.a") == 3 && numbering.Count("io.example.b") == 1 && numbering.Count("io.example.c") == 0, "and each knows its count");
            numbering.Restart();
            That(numbering.Starts == 1 && numbering.Count("io.example.a") == 0 && numbering.Next("io.example.b") == 1, "a restart starts every mod over");
            Throws<ArgumentException>(() => numbering.Next(string.Empty), "a key needs its mod");

            // Numbering a mod that has a number already allocates nothing.
            numbering.Next("io.example.a");
            var allocated = Allocated(() => numbering.Next("io.example.a"));
            That(allocated == 0, "numbering allocates nothing once a mod has a number (" + allocated + " bytes)");
        }
    }
}
