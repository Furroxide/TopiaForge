using System;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Motion ownership limits: the hold range on grabs, steals, catches and throws; the first-sample deadline; and
    /// yielding after going quiet or after a hold too short to contest, so one player cannot keep a body from everyone
    /// else (WS3.5). MultiplayerOwnershipGriefingTests races the griefers against a real claim over delayed links.
    /// </summary>
    internal static class MultiplayerOwnershipLimitsTests
    {
        private const int Alice = 2;
        private const int Bob = 3;
        private const int Unit = NetQuantization.PositionUnitsPerMeter;
        private const int Reach = 100 * Unit + OwnershipPolicy.DefaultToleranceUnits;
        private static readonly NetQuantizedPosition Origin = new NetQuantizedPosition(0, 0, 0);
        private static readonly NetQuantizedPosition AtReach = new NetQuantizedPosition(Reach, 0, 0);
        private static readonly NetQuantizedPosition PastReach = new NetQuantizedPosition(Reach + 1, 0, 0);
        private static readonly MotionHandback Rest = new MotionHandback(new NetQuantizedPosition(0, 0, 0), NetVector3.Zero, NetVector3.Zero);

        public static void Run()
        {
            AGrabMustComeFromWithinTheHoldRange();
            StealsAndCatchesMustComeFromWithinTheHoldRange();
            ARefusedCatchKeepsTheThrowGoingOnBothSides();
            AThrowMustStartWithinTheHoldRange();
            AGrantWithoutAFirstSampleLapses();
            AQuietOwnerYieldsForTheCooldown();
            AHoldTooShortToContestStillYields();
            ADepartedSlotStartsClean();
            ValidatesThePolicy();
            Console.WriteLine("MultiplayerOwnershipLimitsTests passed.");
        }

        private static void AGrabMustComeFromWithinTheHoldRange()
        {
            Equal((long)Reach * Reach, OwnershipPolicy.Default.MaximumHoldRangeSquaredUnits, "100 m plus the tolerance");
            var host = new OwnershipStateMachine();
            var far = host.Request(Alice, 0, Origin, new NetQuantizedPosition(1_000 * Unit, 0, 0), 10_000);
            True(far.Result == OwnershipResult.OutOfRange && !far.EpochChanged, "a grab from a kilometre away is refused");
            True(host.State == OwnershipState.Server && host.Epoch == 0 && host.Owner.IsServer, "and changes nothing");
            Equal(OwnershipResult.OutOfRange, host.Request(Alice, 0, Origin, PastReach, 20_000).Result, "one unit past the hold range");

            // A 3-4-5 diagonal checks the exact squared distance: 51,450 units is inside, 51,460 outside.
            Equal(OwnershipResult.OutOfRange, host.Request(Alice, 0, Origin, new NetQuantizedPosition(30_876, -41_168, 0), 30_000).Result, "a diagonal just outside");
            var inside = new OwnershipStateMachine();
            Equal(OwnershipResult.Granted, inside.Request(Alice, 0, Origin, new NetQuantizedPosition(30_870, -41_160, 0), 30_000).Result, "a diagonal just inside");
            Equal(OwnershipResult.Granted, host.Request(Alice, 0, Origin, AtReach, 40_000).Result, "exactly at the hold range");

            // The body's position is the host's, not the requester's: a body far from its old baseline is measured where it is.
            var moved = new OwnershipStateMachine();
            var body = new NetQuantizedPosition(-300 * Unit, 0, 0);
            Equal(OwnershipResult.OutOfRange, moved.Request(Alice, 0, body, Origin, 0).Result, "a body 300 m from the requester");
            Equal(OwnershipResult.Granted, moved.Request(Alice, 0, body, new NetQuantizedPosition(-250 * Unit, 0, 0), 0).Result, "the requester walked over");
            Equal(body, moved.LastValidatedPosition, "the grant starts from the body's position");
        }

        private static void StealsAndCatchesMustComeFromWithinTheHoldRange()
        {
            var host = Thrown(Alice, Origin, 0);
            var steal = host.Request(Bob, 1, Origin, PastReach, 100_000);
            True(steal.Result == OwnershipResult.OutOfRange && host.State == OwnershipState.ReleaseWindow, "a steal from beyond the hold range");
            Equal(NetOwner.ForParticipant(Alice), host.Owner, "the thrower keeps its throw");
            var near = host.Request(Bob, 1, Origin, AtReach, 110_000);
            True(near.Result == OwnershipResult.Granted && near.EpochChanged && host.Owner.Equals(NetOwner.ForParticipant(Bob)), "a steal from within it");

            var own = Thrown(Alice, Origin, 0);
            var farCatch = own.Request(Alice, 1, Origin, PastReach, 100_000);
            True(farCatch.Result == OwnershipResult.OutOfRange && !farCatch.EpochChanged, "the thrower catching from too far");
            True(own.State == OwnershipState.ReleaseWindow && own.ReleaseDeadlineMicros == 1_010_000, "the throw goes on with its deadline");
            var catchNear = own.Request(Alice, 1, Origin, AtReach, 110_000);
            True(catchNear.Result == OwnershipResult.Granted && !catchNear.EpochChanged && own.State == OwnershipState.Owned, "catching from within range");
        }

        private static void ARefusedCatchKeepsTheThrowGoingOnBothSides()
        {
            var host = Granted(Alice, Origin, 0);
            var alice = new OwnershipClaim(Alice);
            alice.Grab(out _);
            alice.OnGranted(1);
            Equal(OwnershipResult.Accepted, host.Stream(Alice, 1, Origin, Origin, 10_000).Result, "first sample");
            True(alice.BeginRelease(100_000), "throw");
            Equal(OwnershipResult.Releasing, host.BeginRelease(Alice, 1, Origin, 100_000).Result, "the host opens the window");

            // The body flies one way at 30 m/s while its thrower runs 90 m the other way.
            var body = Origin;
            for (var step = 1; step <= 27; step++)
            {
                body = new NetQuantizedPosition(step * Unit, 0, 0);
                Equal(OwnershipResult.Accepted, host.Stream(Alice, 1, body, new NetQuantizedPosition(-90 * Unit, 0, 0), 100_000 + step * 33_333L).Result, "a thrown body has no range limit");
            }

            var thrower = new NetQuantizedPosition(-90 * Unit, 0, 0);
            True(alice.Grab(out var catchEpoch) && catchEpoch == 1 && alice.State == OwnershipState.Owned, "the thrower tries to catch it");
            var refused = host.Request(Alice, catchEpoch, body, thrower, 1_000_000);
            True(refused.Result == OwnershipResult.OutOfRange && refused.Owner.Equals(NetOwner.ForParticipant(Alice)), "117 m apart");
            Equal(OwnershipState.ReleaseWindow, host.State, "the host keeps the throw going");

            // A second throw before the answer opens nothing new on the host, so the client goes back to the first deadline.
            True(alice.BeginRelease(1_050_000), "a second throw before the answer");
            Equal(OwnershipResult.Releasing, host.BeginRelease(Alice, 1, thrower, 1_050_000).Result, "the host keeps the first window");
            alice.OnRefused(refused.Epoch, refused.Owner);
            True(alice.State == OwnershipState.ReleaseWindow && alice.ReleaseDeadlineMicros == 1_100_000, "the client resumes the window with its deadline");
            True(alice.IsSimulating && !alice.GrantPending, "and keeps simulating");
            True(alice.TryCompleteHandback(1_100_000, out var handbackEpoch) && handbackEpoch == 1, "then hands back on time");
            var outcome = host.Handback(Alice, handbackEpoch, new MotionHandback(body, NetVector3.Zero, NetVector3.Zero), thrower, 1_100_000, out _);
            Equal(OwnershipResult.HandedBack, outcome.Result, "and the host takes it");

            // Without a pending catch, a refusal that names the owner still changes nothing.
            var bob = new OwnershipClaim(Bob);
            bob.Grab(out _);
            bob.OnGranted(1);
            bob.OnRefused(1, NetOwner.ForParticipant(Bob));
            Equal(OwnershipState.Owned, bob.State, "an owner's refused duplicate request");
        }

        private static void AThrowMustStartWithinTheHoldRange()
        {
            // The holder's avatar is 200 m from where the body was last validated: throwing from there is refused,
            // where it used to open a release window that took samples far from the holder.
            var away = new NetQuantizedPosition(-200 * Unit, 0, 0);
            var host = Granted(Alice, Origin, 0);
            host.Stream(Alice, 1, Origin, Origin, 10_000);
            var far = host.BeginRelease(Alice, 1, away, 20_000);
            True(far.Result == OwnershipResult.Rejected && far.ReclaimReason == OwnershipReclaimReason.ValidationFailed, "a throw from out of range");
            True(far.EpochChanged && host.State == OwnershipState.Server && host.Owner.IsServer, "the host reclaims the body");
            Equal(Origin, host.LastValidatedPosition, "at its last valid position");
            Equal(OwnershipResult.StaleEpoch, host.Stream(Alice, 1, new NetQuantizedPosition(Unit, 0, 0), away, 30_000).Result, "later samples are stale");
            Equal(0, host.YieldingParticipant, "validation failures do not make the owner yield");

            var past = Granted(Alice, Origin, 0);
            Equal(OwnershipResult.Rejected, past.BeginRelease(Alice, 1, PastReach, 10_000).Result, "one unit past the hold range");
            var edge = Granted(Alice, Origin, 0);
            Equal(OwnershipResult.Releasing, edge.BeginRelease(Alice, 1, AtReach, 10_000).Result, "exactly at the hold range");
            Equal(OwnershipResult.Releasing, edge.BeginRelease(Alice, 1, away, 20_000).Result, "a repeated throw inside the window");
            Equal(1_010_000L, edge.ReleaseDeadlineMicros, "keeps the first deadline");
            Equal(OwnershipResult.Accepted, edge.Stream(Alice, 1, new NetQuantizedPosition(3 * Unit, 0, 0), away, 50_000).Result, "the window lifts the range check");
        }

        private static void AGrantWithoutAFirstSampleLapses()
        {
            var host = Granted(Alice, Origin, 0);
            True(host.AwaitingFirstSample && host.FirstSampleDeadlineMicros == 250_000, "the grant waits for a first sample");
            Equal(OwnershipResult.NoChange, host.Poll(250_000).Result, "exactly at the deadline");
            var lapse = host.Poll(250_001);
            True(lapse.Result == OwnershipResult.Reclaimed && lapse.ReclaimReason == OwnershipReclaimReason.NoFirstSample, "then the grant lapses");
            True(lapse.EpochChanged && lapse.Epoch == 2 && host.Owner.IsServer && !host.AwaitingFirstSample, "the server takes the body back");

            var kept = Granted(Alice, Origin, 0);
            Equal(OwnershipResult.Accepted, kept.Stream(Alice, 1, Origin, Origin, 250_000).Result, "a first sample at the deadline");
            True(!kept.AwaitingFirstSample, "is enough");
            Equal(OwnershipResult.NoChange, kept.Poll(250_001).Result, "the grant holds");
            Equal(OwnershipResult.NoChange, kept.Poll(1_250_000).Result, "until silence");
            Equal(OwnershipReclaimReason.Silence, kept.Poll(1_250_001).ReclaimReason, "silence then counts from that sample");

            // A sample that beats the request is stale, so it is not the first sample.
            var early = new OwnershipStateMachine();
            Equal(OwnershipResult.StaleEpoch, early.Stream(Alice, 1, Origin, Origin, 0).Result, "a sample before the grant");
            early.Request(Alice, 0, Origin, Origin, 10_000);
            Equal(OwnershipReclaimReason.NoFirstSample, early.Poll(260_001).ReclaimReason, "the grant still lapses");

            // Throwing straight away is not a sample either; a thrown body must still be streamed.
            var punt = Granted(Alice, Origin, 0);
            Equal(OwnershipResult.Releasing, punt.BeginRelease(Alice, 1, Origin, 0).Result, "a throw with no sample");
            Equal(OwnershipReclaimReason.NoFirstSample, punt.Poll(250_001).ReclaimReason, "lapses too");
            var streamedPunt = Granted(Alice, Origin, 0);
            streamedPunt.BeginRelease(Alice, 1, Origin, 0);
            Equal(OwnershipResult.Accepted, streamedPunt.Stream(Alice, 1, new NetQuantizedPosition(Unit, 0, 0), Origin, 33_333).Result, "a streamed throw");
            Equal(OwnershipResult.NoChange, streamedPunt.Poll(250_001).Result, "keeps its window");
        }

        private static void AQuietOwnerYieldsForTheCooldown()
        {
            var host = Granted(Alice, Origin, 0);
            host.Poll(250_001);
            True(host.YieldingParticipant == Alice && host.YieldUntilMicros == 1_250_001, "the lapsed owner yields for a second");
            True(host.IsYielding(Alice, 250_001) && !host.IsYielding(Bob, 250_001), "only the lapsed owner");
            var again = host.Request(Alice, host.Epoch, Origin, Origin, 300_000);
            True(again.Result == OwnershipResult.Yielding && !again.EpochChanged && host.State == OwnershipState.Server, "it cannot take the body straight back");
            var bob = host.Request(Bob, host.Epoch, Origin, Origin, 300_001);
            True(bob.Result == OwnershipResult.Granted && bob.Owner.Equals(NetOwner.ForParticipant(Bob)), "another player wins the next request");

            var cooled = Granted(Alice, Origin, 0);
            cooled.Poll(250_001);
            Equal(OwnershipResult.Yielding, cooled.Request(Alice, 2, Origin, Origin, 1_250_000).Result, "one microsecond before the cooldown ends");
            Equal(OwnershipResult.Granted, cooled.Request(Alice, 2, Origin, Origin, 1_250_001).Result, "and when it ends, nobody else having asked");
            Equal(0, cooled.YieldingParticipant, "the new grant clears the cooldown");

            // Yielding also follows silence and a missing hand-back, so one sample a grant is not a way round it.
            var silent = Granted(Alice, Origin, 0);
            silent.Stream(Alice, 1, Origin, Origin, 0);
            Equal(OwnershipReclaimReason.Silence, silent.Poll(1_000_001).ReclaimReason, "silence");
            Equal(OwnershipResult.Yielding, silent.Request(Alice, 2, Origin, Origin, 1_000_002).Result, "yields after silence");

            var stubborn = Granted(Alice, Origin, 0);
            stubborn.Stream(Alice, 1, Origin, Origin, 0);
            stubborn.BeginRelease(Alice, 1, Origin, 0);
            for (var now = 33_333L; now <= 2_000_000; now += 33_333) stubborn.Stream(Alice, 1, Origin, Origin, now);
            Equal(OwnershipReclaimReason.HandbackTimeout, stubborn.Poll(2_000_001).ReclaimReason, "no hand-back");
            Equal(OwnershipResult.Yielding, stubborn.Request(Alice, 2, Origin, Origin, 2_000_002).Result, "yields after a missing hand-back");

            // A host reclaim and a failed validation say nothing about the owner's manners.
            var reclaimed = Granted(Alice, Origin, 0);
            reclaimed.Reclaim();
            Equal(OwnershipResult.Granted, reclaimed.Request(Alice, 2, Origin, Origin, 10_000).Result, "after a host reclaim");

            var quick = new OwnershipStateMachine(new OwnershipPolicy(yieldCooldownMicros: 0));
            quick.Request(Alice, 0, Origin, Origin, 0);
            quick.Poll(250_001);
            Equal(OwnershipResult.Granted, quick.Request(Alice, 2, Origin, Origin, 250_002).Result, "a zero cooldown turns yielding off");
        }

        private static void AHoldTooShortToContestStillYields()
        {
            // A grab, a sample and a hand-back that arrive together end the hold before another request can reach it.
            var host = new OwnershipStateMachine();
            Equal(OwnershipResult.Granted, host.Request(Alice, 0, Origin, Origin, 0).Result, "grab");
            Equal(OwnershipResult.Accepted, host.Stream(Alice, 1, Origin, Origin, 0).Result, "sample");
            Equal(OwnershipResult.HandedBack, host.Handback(Alice, 1, Rest, Origin, 0, out _).Result, "hand-back");
            True(!host.Contested && host.YieldingParticipant == 0, "nothing contested the hold");
            True(host.LastReleaser == Alice && host.LastReleaseMicros == 0, "so the host remembers who let go, and when");

            // Neither Alice's own stale grab nor one from out of range counts.
            Equal(OwnershipResult.StaleEpoch, host.Request(Alice, 0, Origin, Origin, 10_000).Result, "Alice's own stale grab");
            Equal(OwnershipResult.StaleEpoch, host.Request(Bob, 1, Origin, PastReach, 20_000).Result, "Bob's from out of range");
            Equal(0, host.YieldingParticipant, "make nobody yield");

            // Bob asked under the epoch of the hold he never saw: Alice yields from now, once, and Bob's retry wins.
            var stale = host.Request(Bob, 1, Origin, Origin, 30_000);
            True(stale.Result == OwnershipResult.StaleEpoch && stale.Epoch == 2 && !stale.EpochChanged, "Bob's stale grab is refused");
            True(host.YieldingParticipant == Alice && host.YieldUntilMicros == 1_030_000, "and Alice yields for the cooldown");
            Equal(0, host.LastReleaser, "each release makes its owner yield at most once");
            host.Request(Bob, 1, Origin, Origin, 40_000);
            Equal(1_030_000L, host.YieldUntilMicros, "a second stale grab does not extend it");
            Equal(OwnershipResult.Yielding, host.Request(Alice, 2, Origin, Origin, 50_000).Result, "Alice cannot take it back");
            Equal(OwnershipResult.Granted, host.Request(Bob, 2, Origin, Origin, 60_000).Result, "Bob's retry wins");

            // The cooldown after the release bounds it: a stale grab one cooldown later is too late, one microsecond
            // earlier is not.
            foreach (var (late, yields) in new[] { (OwnershipPolicy.DefaultYieldCooldownMicros, false), (OwnershipPolicy.DefaultYieldCooldownMicros - 1, true) })
            {
                var edge = ShortHold(Alice, 100_000);
                edge.Request(Bob, 0, Origin, Origin, 100_000 + late);
                Equal(yields ? Alice : 0, edge.YieldingParticipant, "a stale grab " + late + " us after the release");
            }

            // A sample rejected on purpose ends a hold as abruptly, and counts the same.
            var rejected = new OwnershipStateMachine();
            rejected.Request(Alice, 0, Origin, Origin, 0);
            Equal(OwnershipResult.Rejected, rejected.Stream(Alice, 1, new NetQuantizedPosition(20 * Unit, 0, 0), Origin, 0).Result, "a rejected sample");
            Equal(Alice, rejected.LastReleaser, "is remembered");
            rejected.Request(Bob, 1, Origin, Origin, 500_000);
            Equal(Alice, rejected.YieldingParticipant, "and yields to a stale grab");

            // A grant, a host reclaim and a disconnect leave nobody to blame.
            var granted = ShortHold(Alice, 0);
            granted.Request(Bob, 2, Origin, Origin, 10_000);
            Equal(0, granted.LastReleaser, "a grant forgets the release");
            granted.Reclaim();
            granted.Request(Alice, 3, Origin, Origin, 20_000);
            True(granted.LastReleaser == 0 && granted.YieldingParticipant == 0, "and a host reclaim is nobody's release");
            var departed = ShortHold(Alice, 0);
            departed.Disconnect(Alice);
            departed.Request(Bob, 0, Origin, Origin, 10_000);
            True(departed.LastReleaser == 0 && departed.YieldingParticipant == 0, "a disconnect forgets the release");

            // Putting a body down that nobody asked for still costs nothing: picking it up again under the epoch the
            // hand-back made is granted at once.
            var putDown = new OwnershipStateMachine();
            putDown.Request(Alice, 0, Origin, Origin, 0);
            putDown.Stream(Alice, 1, Origin, Origin, 33_333);
            putDown.Handback(Alice, 1, Rest, Origin, 500_000, out _);
            Equal(OwnershipResult.Granted, putDown.Request(Alice, 2, Origin, Origin, 510_000).Result, "an uncontested set-down and pick-up");
            Equal(0, putDown.YieldingParticipant, "with no cooldown");
        }

        /// <summary>A grab, a sample and a hand-back by <paramref name="participant"/>, all at <paramref name="nowMicros"/>.</summary>
        private static OwnershipStateMachine ShortHold(int participant, long nowMicros)
        {
            var host = new OwnershipStateMachine();
            host.Request(participant, 0, Origin, Origin, nowMicros);
            host.Stream(participant, 1, Origin, Origin, nowMicros);
            Equal(OwnershipResult.HandedBack, host.Handback(participant, 1, Rest, Origin, nowMicros, out _).Result, "setup short hold");
            return host;
        }

        private static void ADepartedSlotStartsClean()
        {
            var host = Granted(Alice, Origin, 0);
            host.Poll(250_001);
            Equal(Alice, host.YieldingParticipant, "the leaver was yielding");
            Equal(OwnershipResult.NoChange, host.Disconnect(Bob).Result, "another player leaving");
            Equal(Alice, host.YieldingParticipant, "keeps the leaver's cooldown");
            Equal(OwnershipResult.NoChange, host.Disconnect(Alice).Result, "the leaver owned nothing");
            True(host.YieldingParticipant == 0 && !host.IsYielding(Alice, 300_000), "its cooldown is forgotten");
            Equal(OwnershipResult.Granted, host.Request(Alice, host.Epoch, Origin, Origin, 300_000).Result, "a newcomer in the same slot can grab at once");

            var owned = Granted(Alice, Origin, 0);
            var gone = owned.Disconnect(Alice);
            True(gone.Result == OwnershipResult.Reclaimed && gone.ReclaimReason == OwnershipReclaimReason.Disconnected, "an owner leaving");
            Equal(0, owned.YieldingParticipant, "leaves no cooldown behind");
        }

        private static void ValidatesThePolicy()
        {
            Equal(250_000L, OwnershipPolicy.Default.FirstSampleTimeoutMicros, "first-sample deadline");
            Equal(1_000_000L, OwnershipPolicy.Default.YieldCooldownMicros, "yield cooldown");
            Throws<ArgumentOutOfRangeException>(() => new OwnershipPolicy(firstSampleTimeoutMicros: 0), "first-sample deadline zero");
            Throws<ArgumentOutOfRangeException>(() => new OwnershipPolicy(yieldCooldownMicros: -1), "negative cooldown");
            Throws<ArgumentOutOfRangeException>(() => new OwnershipPolicy(yieldCooldownMicros: 60_000_001), "cooldown too long");

            // A first-sample deadline past the silence timeout is harmless: silence reclaims first.
            var slow = new OwnershipStateMachine(new OwnershipPolicy(firstSampleTimeoutMicros: 5_000_000));
            slow.Request(Alice, 0, Origin, Origin, 0);
            Equal(OwnershipReclaimReason.Silence, slow.Poll(1_000_001).ReclaimReason, "silence comes first");
        }

        private static OwnershipStateMachine Granted(int participant, NetQuantizedPosition position, long nowMicros)
        {
            var host = new OwnershipStateMachine();
            Equal(OwnershipResult.Granted, host.Request(participant, 0, position, position, nowMicros).Result, "setup grant");
            return host;
        }

        /// <summary>Granted at the position, first sample streamed, then thrown at <paramref name="nowMicros"/> + 10 ms.</summary>
        private static OwnershipStateMachine Thrown(int participant, NetQuantizedPosition position, long nowMicros)
        {
            var host = Granted(participant, position, nowMicros);
            Equal(OwnershipResult.Accepted, host.Stream(participant, 1, position, position, nowMicros).Result, "setup sample");
            Equal(OwnershipResult.Releasing, host.BeginRelease(participant, 1, position, nowMicros + 10_000).Result, "setup throw");
            return host;
        }
    }
}
