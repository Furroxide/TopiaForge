using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Griefing and steals over ordered, delayed links (WS3.5): an honest player drives a real <see cref="OwnershipClaim"/>
    /// at round trips from 50 to 500 ms, so it names the epochs it has actually heard, never the host's live one.
    /// </summary>
    internal static class MultiplayerOwnershipGriefingTests
    {
        private const int Griefer = 2;
        private const int Honest = 3;
        private const int OtherGriefer = 4;
        private static readonly long[] RoundTrips = { 50_000, 100_000, 200_000, 300_000, 400_000, 500_000 };

        public static void Run()
        {
            QuietOrShortHoldsCannotKeepABodyFromOthers();
            WithoutYieldingTheGriefersKeepTheBody();
            TwoGriefersCannotKeepABodyBetweenThem();
            MoreGriefersOnlyDelayTheHonestPlayer();
            WithoutDoublingTwoGriefersKeepTheBody();
            AClosedLinkCannotKeepABodyEither();
            AStealStreamsInTimeAtAnyRoundTrip();
            Console.WriteLine("MultiplayerOwnershipGriefingTests passed.");
        }

        private static void QuietOrShortHoldsCannotKeepABodyFromOthers()
        {
            // A griefer 20 ms from the host sends a burst every 200 ms, within the five grabs a second the request policy
            // allows: a grab alone, a grab and one sample, a grab and a hand-back, a grab, a sample and a hand-back, or a
            // grab and a sample far enough off to be rejected. The last two end the hold before any request can reach
            // it, so nothing contested it; at a round trip of 200 ms or more the honest player's grabs always arrived
            // stale and it lost 50 of 50. Now its stale grab makes the griefer yield, and its next grab wins. The griefer
            // can still win one grant a burst until that grab arrives, about a round trip after the griefer's first win.
            foreach (var grief in AllGriefs)
            {
                foreach (var roundTrip in RoundTrips)
                {
                    var name = grief + " at a " + roundTrip / 1_000 + " ms round trip";
                    var rig = Race(OwnershipPolicy.Default, roundTrip, 10_000_000, grief);
                    var honestGrants = rig.Grantees.FindAll(grantee => grantee == Honest).Count;
                    True(honestGrants >= 4, name + ": the honest player gets the body (" + honestGrants + " of " + rig.Grantees.Count + " grants)");
                    True(rig.Grantees.IndexOf(Honest) is >= 0 and <= 2, name + ": it wins one of the first three grants");
                    var run = LongestRun(rig.Grantees, Griefer);
                    var allowed = 1 + (int)(roundTrip / GriefPlayer.PeriodMicros);
                    True(run <= allowed, name + ": the griefer won at most " + allowed + " grants in a row, here " + run);
                    NeverStruck(rig, name);
                }
            }
        }

        private static void WithoutYieldingTheGriefersKeepTheBody()
        {
            // The control: with the cooldown off, every one of those loops keeps the body from an honest player whose
            // round trip is 200 ms or more.
            var noYield = new OwnershipPolicy(yieldCooldownMicros: 0, maximumYieldCooldownMicros: 0);
            foreach (var grief in AllGriefs)
            {
                foreach (var roundTrip in new[] { 200_000L, 300_000L, 400_000L, 500_000L })
                {
                    var rig = Race(noYield, roundTrip, 10_000_000, grief);
                    True(rig.Grantees.Count > 5 && !rig.Grantees.Contains(Honest),
                        grief + " at " + roundTrip / 1_000 + " ms without yielding: the griefer keeps the body (" + rig.Grantees.Count + " grants)");
                }
            }
        }

        private static void TwoGriefersCannotKeepABodyBetweenThem()
        {
            // Two griefers 20 ms from the host take turns, each bursting every 200 ms, 100 ms apart. One cooldown slot,
            // which each new yield moved, let them free each other: two that never streamed won 34 of 34 grants in 10 s
            // at every round trip, one sample a grant 10 of 10, and a grab, a sample and a hand-back 300 of 300 over a
            // minute at 200 ms and more. Each now keeps its own cooldown, and a strike within 8 s of its last cooldown
            // doubles it, so their turns thin out. Over 20 s the honest player holds the body within 6 s and at least five
            // times in every loop, and it never strikes: each of its yields is taking turns.
            foreach (var grief in AllGriefs)
            {
                foreach (var roundTrip in RoundTrips)
                {
                    var name = "two griefers, " + grief + ", at a " + roundTrip / 1_000 + " ms round trip";
                    var rig = Race(OwnershipPolicy.Default, roundTrip, 20_000_000, grief, grief);
                    var honestGrants = rig.Grantees.FindAll(grantee => grantee == Honest).Count;
                    True(honestGrants >= 5, name + ": the honest player gets the body (" + honestGrants + " of " + rig.Grantees.Count + " grants)");
                    var first = rig.Grantees.IndexOf(Honest);
                    True(first >= 0 && rig.GrantTimes[first] <= 6_000_000, name + ": first within 6 s, at " + (first < 0 ? -1 : rig.GrantTimes[first]) + " us");
                    NeverStruck(rig, name);
                }
            }
        }

        private static void MoreGriefersOnlyDelayTheHonestPlayer()
        {
            // Three or four griefers bursting in turn, spread evenly over the period, slow the honest player's first grant
            // as their strikes build up, but within a minute it holds the body again and again in every loop.
            foreach (var count in new[] { 3, 4 })
            {
                foreach (var grief in AllGriefs)
                {
                    foreach (var roundTrip in RoundTrips)
                    {
                        var name = count + " griefers, " + grief + ", at a " + roundTrip / 1_000 + " ms round trip";
                        var rig = new OwnershipRig();
                        for (var index = 0; index < count; index++)
                        {
                            rig.Add(new GriefPlayer(10 + index, 20_000, grief, index * GriefPlayer.PeriodMicros / count));
                        }

                        rig.Add(new ClaimPlayer(Honest, roundTrip));
                        rig.Run(60_000_000);
                        var honestGrants = rig.Grantees.FindAll(grantee => grantee == Honest).Count;
                        True(honestGrants >= 10, name + ": the honest player gets the body (" + honestGrants + " of " + rig.Grantees.Count + " grants)");
                        NeverStruck(rig, name);
                    }
                }
            }
        }

        private static void WithoutDoublingTwoGriefersKeepTheBody()
        {
            // The control: with every strike costing the base second, two griefers each sending one sample a grant hold
            // the body a second apiece and are always ready again when the other is reclaimed.
            var noDoubling = new OwnershipPolicy(maximumYieldCooldownMicros: OwnershipPolicy.DefaultYieldCooldownMicros);
            foreach (var roundTrip in RoundTrips)
            {
                var rig = Race(noDoubling, roundTrip, 20_000_000, Grief.OneSample, Grief.OneSample);
                True(rig.Grantees.Count > 10 && !rig.Grantees.Contains(Honest),
                    "two griefers at " + roundTrip / 1_000 + " ms without doubling: they keep the body (" + rig.Grantees.Count + " grants)");
            }
        }

        private static void AClosedLinkCannotKeepABodyEither()
        {
            // A griefer grabs, never streams, drops its link 200 ms later and rejoins 50 ms after that, within the
            // engine's grace period. When a closed link only reclaimed the body, nobody yielded, and the honest player got
            // nothing at a round trip of 100 ms or more. A closed link now ends a quiet hold as going quiet does.
            foreach (var roundTrip in RoundTrips)
            {
                var name = "a dropped link at a " + roundTrip / 1_000 + " ms round trip";
                var rig = new OwnershipRig();
                rig.Add(new LinkDropPlayer(Griefer, 20_000, 0));
                rig.Add(new ClaimPlayer(Honest, roundTrip));
                rig.Run(10_000_000);
                var honestGrants = rig.Grantees.FindAll(grantee => grantee == Honest).Count;
                True(honestGrants >= 4, name + ": the honest player gets the body (" + honestGrants + " of " + rig.Grantees.Count + " grants)");
                True(rig.Grantees.IndexOf(Honest) is >= 0 and <= 2, name + ": it wins one of the first three grants");
                True(rig.Reclaims.Exists(reclaim => reclaim.Participant == Griefer && reclaim.Reason == OwnershipReclaimReason.Disconnected), name + ": the griefer's links closed");
                NeverStruck(rig, name);

                var two = new OwnershipRig();
                two.Add(new LinkDropPlayer(Griefer, 20_000, 0));
                two.Add(new LinkDropPlayer(OtherGriefer, 20_000, 100_000));
                two.Add(new ClaimPlayer(Honest, roundTrip));
                two.Run(10_000_000);
                var shared = two.Grantees.FindAll(grantee => grantee == Honest).Count;
                True(shared >= 4, name + ", two griefers: the honest player gets the body (" + shared + " of " + two.Grantees.Count + " grants)");
                NeverStruck(two, name + ", two griefers");
            }

            foreach (var roundTrip in new[] { 100_000L, 300_000L, 500_000L })
            {
                var old = new OwnershipRig { LinkCloseOnlyReclaims = true };
                old.Add(new LinkDropPlayer(Griefer, 20_000, 0));
                old.Add(new ClaimPlayer(Honest, roundTrip));
                old.Run(10_000_000);
                True(old.Grantees.Count > 20 && !old.Grantees.Contains(Honest),
                    "a dropped link at " + roundTrip / 1_000 + " ms, when a closed link only reclaimed: the griefer keeps the body (" + old.Grantees.Count + " grants)");
            }
        }

        private static void AStealStreamsInTimeAtAnyRoundTrip()
        {
            // Alice holds a body and throws it; Bob catches it in her release window. His grab of a body Alice held used
            // to wait for the grant before simulating, so his first sample came a round trip after it, and at 300 ms or
            // more the grant lapsed and Bob was made to yield. Now he simulates the grab at once, under the epoch a grant
            // will assign, and his first sample arrives with the grant.
            foreach (var roundTrip in RoundTrips)
            {
                var name = "a steal at a " + roundTrip / 1_000 + " ms round trip";
                var rig = new OwnershipRig();
                var alice = new ClaimPlayer(Griefer, 40_000, holdMicros: 300_000) { GrabsAgain = false };
                var bob = new ClaimPlayer(Honest, roundTrip, holdMicros: 500_000, grabFromMicros: 400_000 + roundTrip / 2) { GrabsAgain = false };
                rig.Add(alice);
                rig.Add(bob);
                rig.Run(4_000_000);
                Sequence(new[] { Griefer, Honest }, rig.Grantees, name + ": Alice's grant, then Bob's steal");
                True(!rig.Reclaims.Exists(reclaim => reclaim.Participant == Honest), name + ": Bob's catch never lapsed");
                True(!rig.Yields.Contains(Honest), name + ": and Bob never had to yield");
                Equal(1, bob.ConfirmedHolds, name + ": Bob held the body");
                True(rig.Host.State == OwnershipState.Server && rig.Host.Epoch == 3, name + ": and handed it back");
                True(alice.Claim.State == OwnershipState.Server && bob.Claim.State == OwnershipState.Server, name + ": both claims agree");
                True(alice.Claim.Epoch == 3 && bob.Claim.Epoch == 3, name + ": on the epoch too");
            }
        }

        private static readonly Grief[] AllGriefs =
        {
            Grief.Silent, Grief.OneSample, Grief.GrabAndHandBack, Grief.SampleAndHandBack, Grief.RejectedSample
        };

        private enum Grief
        {
            Silent,
            OneSample,
            GrabAndHandBack,
            SampleAndHandBack,
            RejectedSample
        }

        /// <summary>
        /// The griefers racing an honest player for the body for <paramref name="durationMicros"/>; everyone stands
        /// beside it. A second griefer bursts half a period after the first.
        /// </summary>
        private static OwnershipRig Race(OwnershipPolicy policy, long roundTrip, long durationMicros, Grief grief, Grief? second = null)
        {
            var rig = new OwnershipRig(policy);
            rig.Add(new GriefPlayer(Griefer, 20_000, grief, 0));
            if (second.HasValue) rig.Add(new GriefPlayer(OtherGriefer, 20_000, second.Value, GriefPlayer.PeriodMicros / 2));
            rig.Add(new ClaimPlayer(Honest, roundTrip));
            rig.Run(durationMicros);
            return rig;
        }

        /// <summary>The honest player's yields are all taking turns: it never earns a strike of its own.</summary>
        private static void NeverStruck(OwnershipRig rig, string name) =>
            Equal(0L, rig.Host.CooldownOf(Honest).StrikeMicros, name + ": the honest player never struck");

        private static int LongestRun(List<int> grantees, int participant)
        {
            var longest = 0;
            var run = 0;
            foreach (var grantee in grantees)
            {
                run = grantee == participant ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }

            return longest;
        }

        /// <summary>
        /// A griefer that sends one burst every 200 ms under the newest epoch it has heard, stamping its sample and
        /// hand-back with the epoch its grab would get.
        /// </summary>
        private sealed class GriefPlayer : RigPlayer
        {
            public const long PeriodMicros = 200_000;
            private static readonly NetQuantizedPosition Teleported = new NetQuantizedPosition(20 * NetQuantization.PositionUnitsPerMeter, 0, 0);
            private readonly Grief grief;
            private long nextBurst;
            private uint known;

            public GriefPlayer(int participant, long roundTripMicros, Grief grief, long firstBurstMicros)
                : base(participant, roundTripMicros)
            {
                this.grief = grief;
                nextBurst = firstBurstMicros;
            }

            public override void Act(long now)
            {
                if (now < nextBurst) return;
                nextBurst = now + PeriodMicros;
                Send(RigMessage.Of(RigMessageKind.Request, known), now);
                var held = unchecked(known + 1);
                if (grief == Grief.OneSample || grief == Grief.SampleAndHandBack) Send(RigMessage.Of(RigMessageKind.Sample, held), now);
                if (grief == Grief.RejectedSample) Send(new RigMessage(RigMessageKind.Sample, held, Teleported, OwnershipRig.Near), now);
                if (grief == Grief.GrabAndHandBack || grief == Grief.SampleAndHandBack) Send(RigMessage.Of(RigMessageKind.Handback, held), now);
            }

            protected override void Receive(RigReply reply, long now)
            {
                if (unchecked((int)(reply.Epoch - known)) > 0) known = reply.Epoch;
            }
        }

        /// <summary>
        /// A griefer that grabs under the newest epoch it has heard, never streams, drops its link 200 ms later, rejoins
        /// 50 ms after that, and grabs again as soon as the baseline has told it the epoch.
        /// </summary>
        private sealed class LinkDropPlayer : RigPlayer
        {
            private const long HoldMicros = 200_000;
            private const long AwayMicros = 50_000;
            private long nextStep;
            private int phase;
            private uint known;

            public LinkDropPlayer(int participant, long roundTripMicros, long firstGrabMicros)
                : base(participant, roundTripMicros)
            {
                nextStep = firstGrabMicros;
            }

            public override void Act(long now)
            {
                if (now < nextStep) return;
                switch (phase)
                {
                    case 0:
                        Send(RigMessage.Of(RigMessageKind.Request, known), now);
                        nextStep = now + HoldMicros;
                        break;
                    case 1:
                        Rig.CloseLink(this);
                        nextStep = now + AwayMicros;
                        break;
                    default:
                        Rig.Rejoin(this);
                        nextStep = now + OneWayMicros;
                        break;
                }

                phase = (phase + 1) % 3;
            }

            protected override void Receive(RigReply reply, long now)
            {
                if (unchecked((int)(reply.Epoch - known)) > 0) known = reply.Epoch;
            }
        }
    }
}
