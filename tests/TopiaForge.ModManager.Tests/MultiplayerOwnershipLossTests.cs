using System;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Requests the host never answers (WS3.5). A system channel past its bounds drops new messages without telling the
    /// sender, so a client's request can vanish on a link that stays open. The claim takes a request unanswered for the
    /// answer timeout as lost, a later answer shows an earlier request lost at once, and an answer that comes after the
    /// claim gave up settles nothing it should not.
    /// </summary>
    internal static class MultiplayerOwnershipLossTests
    {
        private const int Alice = 2;
        private const int Bob = 3;
        private const long Timeout = OwnershipPolicy.DefaultAnswerTimeoutMicros;
        private static readonly NetQuantizedPosition Origin = new NetQuantizedPosition(0, 0, 0);
        private static readonly MotionHandback Rest = new MotionHandback(Origin, NetVector3.Zero, NetVector3.Zero);
        private static readonly long[] RoundTrips = { 50_000, 100_000, 200_000, 300_000, 400_000, 500_000 };

        public static void Run()
        {
            ARequestTheHostNeverSawIsTakenAsLost();
            AHandbackBehindALostCatchTakesEffect();
            ALaterAnswerShowsAnEarlierRequestLost();
            ALostCatchEndsWhereTheHostIs();
            ALateAnswerSettlesNothingItShouldNot();
            ADroppedRequestNoLongerWedgesTheClaim();
            Console.WriteLine("MultiplayerOwnershipLossTests passed.");
        }

        private static void ARequestTheHostNeverSawIsTakenAsLost()
        {
            // The verification's case: one grab dropped at the host's inbox left the claim Requested, simulating the body
            // and unable to grab, for good.
            var host = new OwnershipStateMachine();
            var alice = new OwnershipClaim(Alice);
            True(alice.Grab(100_000, out var lost) && alice.State == OwnershipState.Requested, "a grab the host never receives");
            True(!alice.Poll(100_000 + Timeout) && alice.OutstandingRequests == 1 && alice.IsSimulating, "exactly at the timeout it still waits");
            True(alice.Poll(100_000 + Timeout + 1), "one microsecond later it gives up");
            True(alice.State == OwnershipState.Server && !alice.IsSimulating && alice.OutstandingRequests == 0 && !alice.GrantPending, "and stops simulating");
            True(alice.LostRequests == 1 && alice.LastRecovery == OwnershipClaimRecovery.AnswerTimeout, "recording why");
            True(!alice.Poll(10_000_000), "nothing else is due");

            True(alice.Grab(2_200_000, out var retry) && retry.Epoch == host.Epoch && retry.Number == lost.Number + 1, "it may grab again at once");
            var grant = host.Request(Alice, retry.Epoch, Origin, Origin, 2_250_000);
            alice.OnGranted(retry.Number, grant.Epoch);
            Equal(OwnershipState.Owned, alice.State, "and the retry is granted");

            // A throw sent behind the lost grab took effect only if the host had granted it, which it never did.
            var thrower = new OwnershipClaim(Alice);
            thrower.Grab(0, out _);
            True(thrower.BeginRelease(50_000) && thrower.State == OwnershipState.ReleaseWindow, "a throw before the answer");
            thrower.Poll(Timeout + 1);
            True(thrower.State == OwnershipState.Server && thrower.OutstandingRequests == 0, "is dropped with the lost grab");
        }

        private static void AHandbackBehindALostCatchTakesEffect()
        {
            // Alice throws, catches (the host's inbox loses the catch), throws again inside the host's window and hands
            // back when her second window closes, before the host's hand-back timeout. The host took the hand-back. When
            // the claim takes the catch as lost, what she sent behind it takes effect as the host applied it, so the
            // claim is back with the server under the host's epoch before any announcement says so.
            var host = new OwnershipStateMachine();
            var alice = new OwnershipClaim(Alice);
            alice.Grab(0, out var grab);
            alice.OnGranted(grab.Number, host.Request(Alice, grab.Epoch, Origin, Origin, 0).Epoch);
            host.Stream(Alice, alice.StreamEpoch, Origin, Origin, 10_000);
            alice.BeginRelease(100_000);
            host.BeginRelease(Alice, alice.StreamEpoch, Origin, 100_000);
            True(alice.Grab(150_000, out _), "a catch the host's inbox loses");
            True(alice.BeginRelease(200_000) && alice.State == OwnershipState.ReleaseWindow, "a second throw");
            Equal(OwnershipResult.Releasing, host.BeginRelease(Alice, alice.StreamEpoch, Origin, 200_000).Result, "the host keeps its window");
            True(alice.TryCompleteHandback(1_200_000, out var epoch), "Alice hands back when her second window closes");
            var handback = host.Handback(Alice, epoch, Rest, Origin, 1_200_000, out _);
            Equal(OwnershipResult.HandedBack, handback.Result, "inside the host's hand-back timeout");
            True(alice.OutstandingRequests == 1 && alice.State == OwnershipState.Server, "the claim shows the hand-back, the catch still due");
            True(alice.Poll(150_000 + Timeout + 1), "the catch is taken as lost");
            True(alice.State == OwnershipState.Server && alice.Epoch == host.Epoch && alice.KnownOwner.IsServer, "the hand-back took effect, epoch and all");
            True(alice.Grab(2_200_000, out var again) && again.Epoch == host.Epoch, "so a grab names the host's epoch");
            Equal(OwnershipResult.Granted, host.Request(Alice, again.Epoch, Origin, Origin, 2_200_000).Result, "and is granted");
        }

        private static void ALaterAnswerShowsAnEarlierRequestLost()
        {
            // Alice throws and catches twice inside her window; the host's inbox loses the first catch and refuses the
            // second from too far. The second answer names a later request, so the first never reached the host: the
            // claim settles both at once, back in the window with the host's deadline, without waiting for the timeout.
            var host = new OwnershipStateMachine();
            var alice = new OwnershipClaim(Alice);
            alice.Grab(0, out var grab);
            alice.OnGranted(grab.Number, host.Request(Alice, grab.Epoch, Origin, Origin, 0).Epoch);
            host.Stream(Alice, alice.StreamEpoch, Origin, Origin, 10_000);
            alice.BeginRelease(100_000);
            host.BeginRelease(Alice, alice.StreamEpoch, Origin, 100_000);
            True(alice.Grab(150_000, out var lostCatch), "a catch the host's inbox loses");
            alice.BeginRelease(200_000);
            host.BeginRelease(Alice, alice.StreamEpoch, Origin, 200_000);
            True(alice.Grab(250_000, out var farCatch), "another, from too far");
            var refusal = host.Request(Alice, farCatch.Epoch, Origin, new NetQuantizedPosition(200 * NetQuantization.PositionUnitsPerMeter, 0, 0), 250_000);
            Equal(OwnershipResult.OutOfRange, refusal.Result, "the host refuses it");
            alice.OnRefused(farCatch.Number, refusal.Epoch, refusal.Owner);
            True(alice.OutstandingRequests == 0 && alice.LostRequests == 1 && alice.LastRecovery == OwnershipClaimRecovery.AnswerSkipped, "the lost catch is settled with it");
            True(alice.State == OwnershipState.ReleaseWindow && alice.ReleaseDeadlineMicros == host.ReleaseDeadlineMicros, "the window goes on with the host's deadline");
            Equal(lostCatch.Number + 1, farCatch.Number, "the two catches were numbered in order");
            True(alice.TryCompleteHandback(host.ReleaseDeadlineMicros, out var handbackEpoch), "Alice hands back on time");
            Equal(OwnershipResult.HandedBack, host.Handback(Alice, handbackEpoch, Rest, Origin, host.ReleaseDeadlineMicros, out _).Result, "and the host takes it");
            True(!host.IsYielding(Alice, host.ReleaseDeadlineMicros), "with nobody yielding");
        }

        private static void ALostCatchEndsWhereTheHostIs()
        {
            // A catch the host never receives leaves Alice simulating a held body while the host waits in her window. The
            // host's hand-back timeout, a second after the window, comes before the claim's answer timeout, so the host
            // takes the body back and Alice yields; the claim stops simulating when it hears that, and its timeout then
            // frees it to grab again. A shorter answer timeout gives up first and hands back in time.
            var host = new OwnershipStateMachine();
            var alice = new OwnershipClaim(Alice);
            alice.Grab(0, out var grab);
            alice.OnGranted(grab.Number, host.Request(Alice, grab.Epoch, Origin, Origin, 0).Epoch);
            host.Stream(Alice, alice.StreamEpoch, Origin, Origin, 10_000);
            alice.BeginRelease(100_000);
            host.BeginRelease(Alice, alice.StreamEpoch, Origin, 100_000);
            True(alice.Grab(200_000, out _) && alice.State == OwnershipState.Owned, "a catch the host's inbox loses");
            for (var now = 233_333L; now < 2_100_000; now += 33_333) host.Stream(Alice, alice.StreamEpoch, Origin, Origin, now);
            var reclaim = host.Poll(2_100_001);
            True(reclaim.Result == OwnershipResult.Reclaimed && reclaim.ReclaimReason == OwnershipReclaimReason.HandbackTimeout, "the host's hand-back timeout comes first");
            alice.OnOwnerChanged(reclaim.Owner, reclaim.Epoch);
            True(alice.State == OwnershipState.Server && alice.OutstandingRequests == 1, "the claim stops simulating, its catch still due");
            True(!alice.Grab(2_150_000, out _), "and cannot grab yet");
            True(alice.Poll(200_000 + Timeout + 1) && alice.OutstandingRequests == 0 && alice.Epoch == host.Epoch, "the timeout frees it");
            True(alice.Grab(2_300_000, out _), "to grab again");

            var quick = new OwnershipPolicy(answerTimeoutMicros: 500_000);
            var host2 = new OwnershipStateMachine(quick);
            var bob = new OwnershipClaim(Bob, quick);
            bob.Grab(0, out var bobGrab);
            bob.OnGranted(bobGrab.Number, host2.Request(Bob, bobGrab.Epoch, Origin, Origin, 0).Epoch);
            host2.Stream(Bob, bob.StreamEpoch, Origin, Origin, 10_000);
            bob.BeginRelease(100_000);
            host2.BeginRelease(Bob, bob.StreamEpoch, Origin, 100_000);
            bob.Grab(200_000, out _);
            True(bob.Poll(700_001) && bob.State == OwnershipState.ReleaseWindow && bob.ReleaseDeadlineMicros == host2.ReleaseDeadlineMicros, "with 500 ms the window resumes");
            for (var now = 733_333L; now < 1_100_000; now += 33_333) host2.Stream(Bob, bob.StreamEpoch, Origin, Origin, now);
            True(bob.TryCompleteHandback(1_100_000, out var epoch), "Bob hands back on time");
            Equal(OwnershipResult.HandedBack, host2.Handback(Bob, epoch, Rest, Origin, 1_100_000, out _).Result, "and the host takes it");
            True(!host2.IsYielding(Bob, 1_100_000), "with no yield");
        }

        private static void ALateAnswerSettlesNothingItShouldNot()
        {
            // The host is slow, not lossy: Alice's grab reaches it after her claim gave up on it and grabbed again. Its
            // answer names the forgotten request, so it settles nothing; a grant among such answers means the host holds
            // the body for her, and the claim takes that hold, so it never goes unstreamed.
            var host = new OwnershipStateMachine();
            var alice = new OwnershipClaim(Alice);
            alice.Grab(0, out var slow);
            alice.Poll(Timeout + 1);
            True(alice.Grab(Timeout + 100_000, out var again) && again.Epoch == slow.Epoch, "a second grab under the same epoch");
            var late = host.Request(Alice, slow.Epoch, Origin, Origin, Timeout + 500_000);
            var second = host.Request(Alice, again.Epoch, Origin, Origin, Timeout + 500_000);
            True(late.Result == OwnershipResult.Granted && second.Result == OwnershipResult.StaleEpoch, "the host grants the first and refuses the second");
            alice.OnGranted(slow.Number, late.Epoch);
            True(alice.State == OwnershipState.Owned && alice.OutstandingRequests == 1 && alice.StreamEpoch == late.Epoch, "the late grant is taken as the host's hold");
            alice.OnRefused(again.Number, second.Epoch, second.Owner);
            True(alice.State == OwnershipState.Owned && alice.OutstandingRequests == 0 && alice.Epoch == host.Epoch, "and the refusal leaves it held, as the host has it");
            Equal(OwnershipResult.Accepted, host.Stream(Alice, alice.StreamEpoch, Origin, Origin, Timeout + 600_000).Result, "Alice streams under the host's epoch");

            // The other way round: Bob holds the body, so the slow grab is denied, but he throws before Carol's second
            // grab arrives, and that one steals his throw. The host answers both in order. The late refusal names the
            // forgotten request and settles nothing; without numbers it would have settled the steal and rolled it back,
            // leaving the body Carol's on the host and nobody streaming it.
            var host2 = new OwnershipStateMachine();
            var carol = new OwnershipClaim(Alice);
            carol.Grab(0, out var first);
            var bobs = host2.Request(Bob, 0, Origin, Origin, 1_000);
            host2.Stream(Bob, 1, Origin, Origin, 10_000);
            carol.OnOwnerChanged(bobs.Owner, bobs.Epoch);
            True(carol.Poll(Timeout + 1) && carol.State == OwnershipState.Server, "Carol gives up on her slow grab");
            True(carol.Grab(Timeout + 100_000, out var next) && next.Epoch == 1 && carol.IsSimulating, "and grabs the body Bob holds");
            var refused = host2.Request(Alice, first.Epoch, Origin, Origin, Timeout + 200_000);
            Equal(OwnershipResult.Denied, refused.Result, "the slow grab finds Bob holding it");
            host2.BeginRelease(Bob, 1, Origin, Timeout + 250_000);
            var steal = host2.Request(Alice, next.Epoch, Origin, Origin, Timeout + 300_000);
            True(steal.Result == OwnershipResult.Granted && steal.Epoch == 2, "the second steals his throw");
            carol.OnRefused(first.Number, refused.Epoch, refused.Owner);
            True(carol.State == OwnershipState.Requested && carol.OutstandingRequests == 1 && carol.IsSimulating, "the late refusal settles nothing");
            carol.OnGranted(next.Number, steal.Epoch);
            True(carol.State == OwnershipState.Owned && carol.Epoch == host2.Epoch && carol.StreamEpoch == 2, "and the grant settles the grab it answers");
        }

        private static void ADroppedRequestNoLongerWedgesTheClaim()
        {
            // On the rig, the host's inbox drops the honest player's first request. Its claim used to stay Requested for
            // 30 s and more; now it gives up after the answer timeout, grabs again, and holds the body within a round
            // trip of that, at every round trip from 50 to 500 ms. Losing its first three requests costs it three
            // timeouts and nothing more.
            foreach (var roundTrip in RoundTrips)
            {
                foreach (var dropped in new[] { 1, 3 })
                {
                    var name = dropped + " dropped at a " + roundTrip / 1_000 + " ms round trip";
                    var rig = new OwnershipRig();
                    var player = new ClaimPlayer(Bob, roundTrip);
                    rig.Add(player);
                    rig.DropRequests(player, dropped);
                    rig.Run(30_000_000);
                    Equal(dropped, rig.DroppedRequests, name + ": the host's inbox dropped them");
                    True(player.Claim.LostRequests == dropped && player.Claim.LastRecovery == OwnershipClaimRecovery.AnswerTimeout, name + ": each was taken as lost");
                    var regained = dropped * (Timeout + OwnershipRig.StepMicros) + roundTrip + OwnershipRig.HostFrameMicros;
                    True(rig.GrantTimes.Count > 0 && rig.GrantTimes[0] <= regained, name + ": the player holds the body by " + regained + " us");
                    True(rig.Grantees.Count >= 15, name + ": and keeps taking turns with it: " + rig.Grantees.Count + " grants");
                }
            }
        }
    }
}
