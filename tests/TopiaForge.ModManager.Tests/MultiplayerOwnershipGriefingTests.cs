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
        private static readonly long[] RoundTrips = { 50_000, 100_000, 200_000, 300_000, 400_000, 500_000 };

        public static void Run()
        {
            QuietOrShortHoldsCannotKeepABodyFromOthers();
            WithoutYieldingTheGriefersKeepTheBody();
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
                    var rig = Race(OwnershipPolicy.Default, grief, roundTrip);
                    var honestGrants = rig.Grantees.FindAll(grantee => grantee == Honest).Count;
                    True(honestGrants >= 4, name + ": the honest player gets the body (" + honestGrants + " of " + rig.Grantees.Count + " grants)");
                    True(rig.Grantees.IndexOf(Honest) is >= 0 and <= 2, name + ": it wins one of the first three grants");
                    var run = LongestRun(rig.Grantees, Griefer);
                    var allowed = 1 + (int)(roundTrip / GriefPlayer.PeriodMicros);
                    True(run <= allowed, name + ": the griefer won at most " + allowed + " grants in a row, here " + run);
                }
            }
        }

        private static void WithoutYieldingTheGriefersKeepTheBody()
        {
            // The control: with the cooldown off, every one of those loops keeps the body from an honest player whose
            // round trip is 200 ms or more.
            var noYield = new OwnershipPolicy(yieldCooldownMicros: 0);
            foreach (var grief in AllGriefs)
            {
                foreach (var roundTrip in new[] { 200_000L, 300_000L, 400_000L, 500_000L })
                {
                    var rig = Race(noYield, grief, roundTrip);
                    True(rig.Grantees.Count > 5 && !rig.Grantees.Contains(Honest),
                        grief + " at " + roundTrip / 1_000 + " ms without yielding: the griefer keeps the body (" + rig.Grantees.Count + " grants)");
                }
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

        /// <summary>Ten seconds of the griefer racing an honest player for the body; both stand beside it.</summary>
        private static OwnershipRig Race(OwnershipPolicy policy, Grief grief, long roundTrip)
        {
            var rig = new OwnershipRig(policy);
            rig.Add(new GriefPlayer(Griefer, 20_000, grief));
            rig.Add(new ClaimPlayer(Honest, roundTrip));
            rig.Run(10_000_000);
            return rig;
        }

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

            public GriefPlayer(int participant, long roundTripMicros, Grief grief)
                : base(participant, roundTripMicros)
            {
                this.grief = grief;
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
    }
}
