using System;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Motion ownership: grants, races, epochs, validation and the release window (WS3.5).</summary>
    internal static class MultiplayerOwnershipTests
    {
        private const int Alice = 2;
        private const int Bob = 3;
        private const int Unit = NetQuantization.PositionUnitsPerMeter;
        private static readonly NetQuantizedPosition Origin = new NetQuantizedPosition(0, 0, 0);

        public static void Run()
        {
            AGrabIsOptimistic();
            TheFirstGrabWins();
            StaleEpochsAreRefused();
            TheHostValidatesSpeed();
            TheHostValidatesRangeWhileHeld();
            AThrowKeepsOwnershipThroughTheReleaseWindow();
            AMissingHandbackIsReclaimedAfterTheWindow();
            ASilentOrDisconnectedOwnerLosesTheBody();
            AThrownBodyCanBeStolenOrCaught();
            AClaimFollowsTheHost();
            EpochsWrapWithoutBreaking();
            RandomEventsKeepTheInvariants();
            Console.WriteLine("MultiplayerOwnershipTests passed.");
        }

        private static void AGrabIsOptimistic()
        {
            var host = new OwnershipStateMachine();
            var alice = new OwnershipClaim(Alice);
            True(alice.Grab(0, out var grab) && grab.Epoch == 0, "a grab sends the epoch it observed");
            Equal(0u, grab.Number, "and the claim's first request number");
            Equal(OwnershipState.Requested, alice.State, "requested");
            True(alice.IsSimulating && alice.GrantPending, "the client simulates before the host answers");
            Equal(1u, alice.StreamEpoch, "samples carry the epoch a grant will assign");
            Equal(OwnershipResult.StaleEpoch, host.Stream(Alice, alice.StreamEpoch, Origin, Origin, 10_000).Result, "a sample that beats the request");
            Equal(OwnershipState.Server, host.State, "and changes nothing");

            var grant = host.Request(Alice, grab.Epoch, Origin, Origin, 20_000);
            Equal(OwnershipResult.Granted, grant.Result, "granted");
            True(grant.EpochChanged && grant.Epoch == 1 && grant.Owner.Equals(NetOwner.ForParticipant(Alice)), "new owner and epoch");
            Equal(OwnershipResult.Accepted, host.Stream(Alice, alice.StreamEpoch, P(0.5), Origin, 40_000).Result, "the optimistic stream is valid at once");
            alice.OnGranted(grab.Number, grant.Epoch);
            Equal(OwnershipState.Owned, alice.State, "owned");
            True(!alice.GrantPending && alice.StreamEpoch == 1, "confirmed");
            True(!alice.Grab(50_000, out _), "grabbing again sends nothing");
            Equal(OwnershipResult.AlreadyOwned, host.Request(Alice, 1, Origin, Origin, 50_000).Result, "a repeated request");
        }

        private static void TheFirstGrabWins()
        {
            var host = new OwnershipStateMachine();
            var alice = new OwnershipClaim(Alice);
            var bob = new OwnershipClaim(Bob);
            alice.Grab(0, out var aliceGrab);
            bob.Grab(0, out var bobGrab);
            var first = host.Request(Bob, bobGrab.Epoch, Origin, Origin, 1_000);
            var second = host.Request(Alice, aliceGrab.Epoch, Origin, Origin, 1_001);
            Equal(OwnershipResult.Granted, first.Result, "the first request wins");
            Equal(OwnershipResult.Denied, second.Result, "the second is denied");
            True(!second.EpochChanged && second.Owner.Equals(NetOwner.ForParticipant(Bob)) && second.Epoch == 1, "denial names the winner");
            alice.OnRefused(aliceGrab.Number, second.Epoch, second.Owner);
            bob.OnGranted(bobGrab.Number, first.Epoch);
            True(!alice.IsSimulating && alice.State == OwnershipState.Server, "the loser rolls back");
            Equal(NetOwner.ForParticipant(Bob), alice.KnownOwner, "and knows who won");
            Equal(OwnershipResult.NotOwner, host.Stream(Alice, 1, Origin, Origin, 2_000).Result, "the loser's samples are refused");
            Equal(OwnershipResult.NotOwner, host.BeginRelease(Alice, 1, Origin, 2_000).Result, "and its throw");
            Equal(OwnershipResult.NotOwner, host.Handback(Alice, 1, Handback(Origin), Origin, 2_000, out _).Result, "and its hand-back");
            Equal(NetOwner.ForParticipant(Bob), host.Owner, "the winner keeps the body");
        }

        private static void StaleEpochsAreRefused()
        {
            var host = Granted(Alice, Origin, 0);
            var reclaim = host.Reclaim();
            True(reclaim.Result == OwnershipResult.Reclaimed && reclaim.ReclaimReason == OwnershipReclaimReason.Host && reclaim.Epoch == 2, "host reclaim");
            Equal(OwnershipResult.NoChange, host.Reclaim().Result, "reclaiming a server body");
            Equal(OwnershipResult.StaleEpoch, host.Stream(Alice, 1, Origin, Origin, 10_000).Result, "a sample from the old epoch");
            Equal(OwnershipResult.StaleEpoch, host.BeginRelease(Alice, 1, Origin, 10_000).Result, "a throw from the old epoch");
            Equal(OwnershipResult.StaleEpoch, host.Handback(Alice, 1, Handback(Origin), Origin, 10_000, out _).Result, "a hand-back from the old epoch");
            var stale = host.Request(Alice, 1, Origin, Origin, 10_000);
            True(stale.Result == OwnershipResult.StaleEpoch && stale.Epoch == 2, "a grab based on an old epoch, with the current one");
            Equal(OwnershipResult.Granted, host.Request(Alice, stale.Epoch, Origin, Origin, 20_000).Result, "retrying with the current epoch");
            Equal(OwnershipResult.StaleEpoch, host.Request(Alice, 2, Origin, Origin, 30_000).Result, "the owner naming an old epoch");
        }

        private static void TheHostValidatesSpeed()
        {
            // 64 m/s with a quarter-second burst and 0.5 m of tolerance: 8,448 grid units of credit.
            Equal(8_448L, OwnershipPolicy.Default.BurstCreditUnits, "burst credit");
            // The holder runs with the body, so only the speed budget is in play. 2 m every 33.3 ms is 60 m/s.
            var host = Granted(Alice, Origin, 0);
            for (var step = 1; step <= 300; step++)
            {
                var body = new NetQuantizedPosition(step * 1_024, 0, 0);
                Equal(OwnershipResult.Accepted, host.Stream(Alice, 1, body, body, step * 33_333L).Result, "60 m/s is sustainable (step " + step + ")");
            }

            // 2.334 m every 33.3 ms is 70 m/s: each sample overspends by 103 units until the burst is gone.
            var fast = Granted(Alice, Origin, 0);
            var rejectedAt = 0;
            for (var step = 1; step <= 200 && rejectedAt == 0; step++)
            {
                var body = new NetQuantizedPosition(step * 1_195, 0, 0);
                if (fast.Stream(Alice, 1, body, body, step * 33_333L).Result == OwnershipResult.Rejected) rejectedAt = step;
            }

            Equal(72, rejectedAt, "70 m/s drains the burst and is rejected after about 2.4 s");
            Equal(OwnershipState.Server, fast.State, "rejection reclaims");

            var teleport = Granted(Alice, Origin, 0);
            var jump = teleport.Stream(Alice, 1, new NetQuantizedPosition(20 * Unit, 0, 0), Origin, 33_333);
            True(jump.Result == OwnershipResult.Rejected && jump.ReclaimReason == OwnershipReclaimReason.ValidationFailed, "a 20 m jump");
            True(jump.EpochChanged && jump.Epoch == 2 && teleport.Owner.IsServer, "the host takes the body back");
            Equal(Origin, teleport.LastValidatedPosition, "at its last valid position");

            // Holding a body still for five seconds banks no more than the burst, so it cannot fund a teleport later.
            var still = Granted(Alice, Origin, 0);
            for (var now = 100_000L; now <= 5_000_000; now += 100_000)
            {
                Equal(OwnershipResult.Accepted, still.Stream(Alice, 1, Origin, Origin, now).Result, "holding still");
            }

            Equal(OwnershipPolicy.Default.BurstCreditUnits, still.DistanceCreditUnits, "credit is capped at the burst");
            Equal(OwnershipResult.Rejected, still.Stream(Alice, 1, new NetQuantizedPosition(20 * Unit, 0, 0), Origin, 5_100_000).Result, "the idle time funds no teleport");

            var edge = Granted(Alice, Origin, 0);
            Equal(OwnershipResult.Accepted, edge.Stream(Alice, 1, new NetQuantizedPosition(8_448, 0, 0), Origin, 0).Result, "a jump of exactly the burst");
            Equal(0L, edge.DistanceCreditUnits, "spends all of it");
            Equal(OwnershipResult.Rejected, edge.Stream(Alice, 1, new NetQuantizedPosition(8_449, 0, 0), Origin, 0).Result, "then one more unit with no time passed");
            var past = Granted(Alice, Origin, 0);
            Equal(OwnershipResult.Rejected, past.Stream(Alice, 1, new NetQuantizedPosition(8_449, 0, 0), Origin, 0).Result, "a jump one unit past the burst");
        }

        private static void TheHostValidatesRangeWhileHeld()
        {
            var reach = 100 * Unit + OwnershipPolicy.DefaultToleranceUnits;
            var body = new NetQuantizedPosition(reach, 0, 0);
            var host = Granted(Alice, body, 0);
            Equal(OwnershipResult.Accepted, host.Stream(Alice, 1, body, Origin, 33_333).Result, "100.5 m from the holder is in range");
            var outOfRange = host.Stream(Alice, 1, body, new NetQuantizedPosition(-1, 0, 0), 66_666);
            True(outOfRange.Result == OwnershipResult.Rejected && outOfRange.ReclaimReason == OwnershipReclaimReason.ValidationFailed, "one unit further");

            var thrown = Granted(Alice, body, 0);
            thrown.BeginRelease(Alice, 1, Origin, 10_000);
            Equal(OwnershipResult.Accepted, thrown.Stream(Alice, 1, body, new NetQuantizedPosition(-50 * Unit, 0, 0), 33_333).Result, "a thrown body has no range limit");
        }

        private static void AThrowKeepsOwnershipThroughTheReleaseWindow()
        {
            var host = Granted(Alice, Origin, 0);
            var alice = new OwnershipClaim(Alice);
            alice.Grab(0, out var grab);
            alice.OnGranted(grab.Number, 1);
            True(alice.BeginRelease(100_000), "throw");
            Equal(OwnershipResult.Releasing, host.BeginRelease(Alice, alice.StreamEpoch, Origin, 100_000).Result, "the host opens the window");
            Equal(OwnershipState.ReleaseWindow, host.State, "release window");
            Equal(1_100_000L, host.ReleaseDeadlineMicros, "one second");
            Equal(OwnershipResult.Releasing, host.BeginRelease(Alice, 1, Origin, 500_000).Result, "a second throw is idempotent");
            Equal(1_100_000L, host.ReleaseDeadlineMicros, "and keeps the first deadline");
            Equal(NetOwner.ForParticipant(Alice), host.Owner, "the thrower still owns the body");
            Equal(OwnershipResult.Accepted, host.Stream(Alice, 1, P(10), Origin, 400_000).Result, "and still streams it");

            True(!alice.IsHandbackDue(1_099_999), "not before the window ends");
            True(alice.IsHandbackDue(1_100_000), "exactly when it ends");
            True(alice.TryCompleteHandback(1_100_000, out var epoch) && epoch == 1, "hand back under the owned epoch");
            Equal(OwnershipState.Server, alice.State, "the client stops simulating");

            var handback = new MotionHandback(P(20), new NetVector3(100f, 0f, 0f), new NetVector3(0f, 80f, 60f));
            var outcome = host.Handback(Alice, epoch, handback, Origin, 1_100_000, out var accepted);
            True(outcome.Result == OwnershipResult.HandedBack && outcome.EpochChanged && outcome.Epoch == 2, "handed back");
            True(host.State == OwnershipState.Server && host.Owner.IsServer, "the server simulates it now");
            Equal(P(20), accepted.Position, "from the handed-back position");
            Equal(new NetVector3(64f, 0f, 0f), accepted.LinearVelocity, "linear velocity clamped to 64 m/s");
            Equal(new NetVector3(0f, 51.2f, 38.4f), accepted.AngularVelocity, "angular velocity scaled to 64 rad/s");
            Equal(OwnershipResult.StaleEpoch, host.Stream(Alice, 1, P(20), Origin, 1_200_000).Result, "late samples are stale");

            var broken = Granted(Alice, Origin, 0);
            broken.Handback(Alice, 1, new MotionHandback(Origin, new NetVector3(float.NaN, 0f, 0f), new NetVector3(float.PositiveInfinity, 0f, 0f)), Origin, 10_000, out var cleaned);
            True(cleaned.LinearVelocity.Equals(NetVector3.Zero) && cleaned.AngularVelocity.Equals(NetVector3.Zero), "non-finite velocities become zero");

            var cheat = Granted(Alice, Origin, 0);
            var teleported = cheat.Handback(Alice, 1, Handback(new NetQuantizedPosition(40 * Unit, 0, 0)), Origin, 10_000, out var refused);
            True(teleported.Result == OwnershipResult.Rejected && cheat.Owner.IsServer, "a hand-back that teleports is rejected");
            Equal(default(MotionHandback).Position, refused.Position, "and nothing is accepted");
        }

        private static void AMissingHandbackIsReclaimedAfterTheWindow()
        {
            var host = Granted(Alice, Origin, 0);
            host.Stream(Alice, 1, Origin, Origin, 0);
            host.BeginRelease(Alice, 1, Origin, 0);
            host.Stream(Alice, 1, Origin, Origin, 1_000_000);
            Equal(OwnershipResult.NoChange, host.Poll(2_000_000).Result, "exactly one timeout after the deadline");
            var late = host.Poll(2_000_001);
            True(late.Result == OwnershipResult.Reclaimed && late.ReclaimReason == OwnershipReclaimReason.HandbackTimeout, "then reclaimed");

            var stubborn = Granted(Alice, Origin, 0);
            stubborn.BeginRelease(Alice, 1, Origin, 0);
            for (var now = 33_333L; now <= 2_000_000; now += 33_333) stubborn.Stream(Alice, 1, Origin, Origin, now);
            Equal(OwnershipResult.Reclaimed, stubborn.Poll(2_000_001).Result, "streaming past the window never keeps the body");
        }

        private static void ASilentOrDisconnectedOwnerLosesTheBody()
        {
            var host = Granted(Alice, Origin, 0);
            host.Stream(Alice, 1, Origin, Origin, 0);
            Equal(OwnershipResult.NoChange, host.Poll(1_000_000).Result, "silent for exactly the timeout");
            var silent = host.Poll(1_000_001);
            True(silent.Result == OwnershipResult.Reclaimed && silent.ReclaimReason == OwnershipReclaimReason.Silence, "then reclaimed");
            Equal(OwnershipResult.NoChange, host.Poll(5_000_000).Result, "a server body needs nothing");

            var streaming = Granted(Alice, Origin, 0);
            streaming.Stream(Alice, 1, Origin, Origin, 0);
            streaming.Stream(Alice, 1, Origin, Origin, 500_000);
            Equal(OwnershipResult.NoChange, streaming.Poll(1_500_000).Result, "silence counts from the last sample");
            Equal(OwnershipResult.Reclaimed, streaming.Poll(1_500_001).Result, "and runs out");

            var midWindow = Granted(Alice, Origin, 0);
            midWindow.Stream(Alice, 1, Origin, Origin, 0);
            midWindow.BeginRelease(Alice, 1, Origin, 0);
            Equal(OwnershipReclaimReason.Silence, midWindow.Poll(1_000_001).ReclaimReason, "silence inside the window");

            var dropped = Granted(Alice, Origin, 0);
            Equal(OwnershipResult.NoChange, dropped.Disconnect(Bob).Result, "another player leaving");
            var gone = dropped.Disconnect(Alice);
            True(gone.Result == OwnershipResult.Reclaimed && gone.ReclaimReason == OwnershipReclaimReason.Disconnected && gone.Epoch == 2, "the owner leaving");
        }

        private static void AThrownBodyCanBeStolenOrCaught()
        {
            var host = Granted(Alice, Origin, 0);
            Equal(OwnershipResult.Denied, host.Request(Bob, 1, Origin, Origin, 10_000).Result, "a held body cannot be taken");
            host.BeginRelease(Alice, 1, Origin, 20_000);
            Equal(OwnershipResult.StaleEpoch, host.Request(Bob, 0, Origin, Origin, 30_000).Result, "a steal must name the current epoch");
            var steal = host.Request(Bob, 1, P(1), Origin, 30_000);
            True(steal.Result == OwnershipResult.Granted && steal.EpochChanged && steal.Epoch == 2, "a thrown body can be caught by another player");
            Equal(OwnershipState.Owned, host.State, "the catcher holds it");
            Equal(OwnershipResult.StaleEpoch, host.Stream(Alice, 1, P(1), Origin, 40_000).Result, "the thrower's samples are now stale");
            Equal(OwnershipResult.StaleEpoch, host.Handback(Alice, 1, Handback(P(1)), Origin, 40_000, out _).Result, "and its hand-back");
            Equal(OwnershipResult.Accepted, host.Stream(Bob, 2, P(1.5), Origin, 40_000).Result, "the catcher streams");

            var own = Granted(Alice, Origin, 0);
            var alice = new OwnershipClaim(Alice);
            alice.Grab(0, out var grab);
            alice.OnGranted(grab.Number, 1);
            alice.BeginRelease(10_000);
            own.BeginRelease(Alice, 1, Origin, 10_000);
            True(alice.Grab(15_000, out var catchGrab) && catchGrab.Epoch == 1 && alice.State == OwnershipState.Owned, "the thrower catches its own throw");
            Equal(grab.Number + 1, catchGrab.Number, "under the next request number");
            var caught = own.Request(Alice, catchGrab.Epoch, Origin, Origin, 20_000);
            True(caught.Result == OwnershipResult.Granted && !caught.EpochChanged && own.State == OwnershipState.Owned, "without an epoch change");
            Equal(OwnershipResult.Accepted, own.Stream(Alice, 1, P(0.25), Origin, 30_000).Result, "and keeps streaming");
        }

        private static void AClaimFollowsTheHost()
        {
            var alice = new OwnershipClaim(Alice);
            alice.Grab(0, out var grab);
            True(alice.BeginRelease(5_000), "a throw before the grant is confirmed");
            True(alice.GrantPending && alice.State == OwnershipState.ReleaseWindow && alice.StreamEpoch == 1, "still pending");
            var host = new OwnershipStateMachine();
            host.Request(Alice, 0, Origin, Origin, 6_000);
            Equal(OwnershipResult.Releasing, host.BeginRelease(Alice, alice.StreamEpoch, Origin, 6_000).Result, "the host applies both in order");
            alice.OnGranted(grab.Number, 1);
            True(!alice.GrantPending && alice.State == OwnershipState.ReleaseWindow, "the window stays open");

            alice.OnRefused(grab.Number, 1, NetOwner.ForParticipant(Alice));
            Equal(OwnershipState.ReleaseWindow, alice.State, "a refusal of a request already settled, naming us, changes nothing");
            alice.OnOwnerChanged(NetOwner.Server, 2);
            True(alice.State == OwnershipState.Server && alice.Epoch == 2 && alice.KnownOwner.IsServer, "a reclaim stops simulation");

            // Only an answer settles a request: an announcement of our own grant records it and nothing more.
            var bob = new OwnershipClaim(Bob);
            bob.Grab(0, out var bobGrab);
            bob.OnOwnerChanged(NetOwner.ForParticipant(Bob), 1);
            True(bob.State == OwnershipState.Requested && bob.GrantPending && bob.OutstandingRequests == 1, "hearing our own grant first settles nothing");
            True(bob.Epoch == 1 && bob.KnownOwner.Equals(NetOwner.ForParticipant(Bob)) && bob.StreamEpoch == 1, "but records it");
            bob.OnGranted(bobGrab.Number, 1);
            True(bob.State == OwnershipState.Owned && !bob.GrantPending && bob.OutstandingRequests == 0, "its answer does");
            bob.OnOwnerChanged(NetOwner.ForParticipant(Bob), 1);
            Equal(OwnershipState.Owned, bob.State, "and hearing it again changes nothing");
            bob.OnOwnerChanged(NetOwner.ForParticipant(Alice), 2);
            True(bob.State == OwnershipState.Server && !bob.IsSimulating, "losing the body to someone else");
            bob.OnOwnerChanged(NetOwner.ForParticipant(Bob), 3);
            Equal(OwnershipState.Server, bob.State, "a grant notice with no request behind it never restarts simulation");
            True(!bob.BeginRelease(10_000) && !bob.TryCompleteHandback(10_000, out _), "nothing to release");
            Throws<ArgumentOutOfRangeException>(() => new OwnershipClaim(0), "participant zero");
        }

        private static void EpochsWrapWithoutBreaking()
        {
            var host = new OwnershipStateMachine(initialEpoch: uint.MaxValue);
            var alice = new OwnershipClaim(Alice, knownEpoch: uint.MaxValue);
            alice.Grab(0, out var grab);
            Equal(0u, alice.StreamEpoch, "the stream epoch wraps");
            var grant = host.Request(Alice, grab.Epoch, Origin, Origin, 0);
            True(grant.Result == OwnershipResult.Granted && grant.Epoch == 0, "the grant wraps to zero");
            Equal(OwnershipResult.Accepted, host.Stream(Alice, alice.StreamEpoch, P(0.1), Origin, 10_000).Result, "and still matches");
        }

        private static void RandomEventsKeepTheInvariants()
        {
            var random = new SeededRandom(97);
            var host = new OwnershipStateMachine();
            var range = OwnershipPolicy.Default.MaximumHoldRangeSquaredUnits;
            var now = 0L;
            var counts = new int[13];
            var linkCloses = 0;
            for (var step = 0; step < 50_000; step++)
            {
                now += random.NextInt(0, 60_000);
                var participant = random.NextInt(1, 4);
                var epoch = random.NextInt(0, 4) == 0 ? host.Epoch - 1 : host.Epoch;
                var position = new NetQuantizedPosition(random.NextInt(-3 * Unit, 3 * Unit), 0, 0);

                // Avatars stand anywhere from beside the body to well past the 100.5 m hold range.
                var avatar = new NetQuantizedPosition(random.NextInt(-110 * Unit, 110 * Unit), 0, 0);
                var before = (host.State, host.Owner, host.Epoch);
                var wasYielding = host.IsYielding(participant, now);
                var quietOwner = host.State != OwnershipState.Server && host.Owner.Participant == participant &&
                                 (host.AwaitingFirstSample || host.Contested);
                var kind = random.NextInt(0, 8);
                OwnershipOutcome outcome;
                switch (kind)
                {
                    case 0: outcome = host.Request(participant, epoch, position, avatar, now); break;
                    case 1:
                    case 2: outcome = host.Stream(participant, epoch, position, avatar, now); break;
                    case 3: outcome = host.BeginRelease(participant, epoch, avatar, now); break;
                    case 4: outcome = host.Handback(participant, epoch, Handback(position), avatar, now, out _); break;
                    case 5: outcome = host.Disconnect(participant); break;
                    case 6: outcome = host.LinkClosed(participant, now); break;
                    default: outcome = host.Poll(now); break;
                }

                counts[(int)outcome.Result]++;
                for (var slot = 1; slot <= 3; slot++)
                {
                    var cooldown = host.CooldownOf(slot);
                    True(cooldown.UntilMicros - now <= OwnershipPolicy.DefaultMaximumYieldCooldownMicros, "no cooldown is longer than the cap");
                    True(cooldown.StrikeMicros == 0 || (cooldown.StrikeMicros % OwnershipPolicy.DefaultYieldCooldownMicros == 0 &&
                                                        cooldown.StrikeMicros <= OwnershipPolicy.DefaultMaximumYieldCooldownMicros), "a strike is a doubling of the base");
                }

                if (kind == 6 && outcome.Result == OwnershipResult.Reclaimed)
                {
                    linkCloses++;
                    True(outcome.ReclaimReason == OwnershipReclaimReason.Disconnected, "a closed link reclaims as disconnected");
                    Equal(quietOwner, host.IsYielding(participant, now), "a closed link yields exactly when the hold was quiet or contested");
                }
                True((host.State == OwnershipState.Server) == host.Owner.IsServer, "server state and server owner agree");
                True(host.State != OwnershipState.Requested, "the host is never in the requested state");
                True(!host.AwaitingFirstSample || host.State != OwnershipState.Server, "only an owner owes a first sample");
                Equal(host.Epoch, outcome.Epoch, "the outcome reports the current epoch");
                Equal(outcome.EpochChanged, host.Epoch != before.Epoch, "epoch changes are always reported");
                if (outcome.EpochChanged) Equal(unchecked(before.Epoch + 1), host.Epoch, "an epoch moves by one");
                if (!outcome.EpochChanged) Equal(before.Owner, host.Owner, "the owner changes only with the epoch");
                var refused = outcome.Result == OwnershipResult.StaleEpoch || outcome.Result == OwnershipResult.NotOwner ||
                              outcome.Result == OwnershipResult.Denied || outcome.Result == OwnershipResult.NoChange ||
                              outcome.Result == OwnershipResult.AlreadyOwned || outcome.Result == OwnershipResult.OutOfRange ||
                              outcome.Result == OwnershipResult.Yielding;
                if (refused)
                {
                    True(before.State == host.State && before.Owner.Equals(host.Owner) && before.Epoch == host.Epoch, outcome.Result + " changes nothing");
                }

                if (outcome.Result == OwnershipResult.Rejected || outcome.Result == OwnershipResult.Reclaimed ||
                    outcome.Result == OwnershipResult.HandedBack)
                {
                    True(host.State == OwnershipState.Server, outcome.Result + " leaves the server in charge");
                }

                if (kind == 0 && outcome.Result == OwnershipResult.Granted)
                {
                    True(position.DistanceSquaredTo(avatar) <= range, "a grant is always within the hold range");
                    True(!wasYielding, "a yielding participant is never granted the body");
                }

                if (outcome.Result == OwnershipResult.OutOfRange) True(position.DistanceSquaredTo(avatar) > range, "out of range only beyond it");
                if (outcome.Result == OwnershipResult.Yielding) True(wasYielding, "yielding only during a cooldown");
            }

            foreach (var result in new[] { OwnershipResult.Granted, OwnershipResult.OutOfRange, OwnershipResult.Yielding, OwnershipResult.Rejected, OwnershipResult.Reclaimed })
            {
                True(counts[(int)result] > 0, "the sweep reached " + result);
            }

            True(linkCloses > 100, "the sweep closed holders' links " + linkCloses + " times");
        }

        private static OwnershipStateMachine Granted(int participant, NetQuantizedPosition position, long nowMicros)
        {
            var host = new OwnershipStateMachine();
            Equal(OwnershipResult.Granted, host.Request(participant, 0, position, position, nowMicros).Result, "setup grant");
            return host;
        }

        private static MotionHandback Handback(NetQuantizedPosition position) =>
            new MotionHandback(position, NetVector3.Zero, NetVector3.Zero);

        private static NetQuantizedPosition P(double meters) =>
            new NetQuantizedPosition((int)Math.Round(meters * Unit), 0, 0);
    }
}
