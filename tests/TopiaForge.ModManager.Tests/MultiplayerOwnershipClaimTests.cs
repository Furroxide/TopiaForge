using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The client's claim against the host's machine when answers are in flight: each answer settles the request it
    /// belongs to, and the release window settles only on the last one (WS3.5).
    /// </summary>
    internal static class MultiplayerOwnershipClaimTests
    {
        private const int Alice = 2;
        private const int Bob = 3;
        private const int Reach = 100 * NetQuantization.PositionUnitsPerMeter + OwnershipPolicy.DefaultToleranceUnits;
        private static readonly NetQuantizedPosition Origin = new NetQuantizedPosition(0, 0, 0);
        private static readonly NetQuantizedPosition PastReach = new NetQuantizedPosition(Reach + 1, 0, 0);
        private static readonly NetQuantizedPosition TwentyMeters = new NetQuantizedPosition(20 * NetQuantization.PositionUnitsPerMeter, 0, 0);
        private static readonly MotionHandback Rest = new MotionHandback(Origin, NetVector3.Zero, NetVector3.Zero);
        private static readonly MotionHandback FarHandback = new MotionHandback(TwentyMeters, NetVector3.Zero, NetVector3.Zero);

        public static void Run()
        {
            ACatchRefusedBehindAPendingGrabResumesTheWindow();
            TwoCatchesInOneRoundTripSettleOnTheLastAnswer();
            AGrabWaitsForOutstandingAnswers();
            TheClaimAgreesWithTheHostWhateverTheTiming();
            Console.WriteLine("MultiplayerOwnershipClaimTests passed.");
        }

        private static void ACatchRefusedBehindAPendingGrabResumesTheWindow()
        {
            // Grab, throw, then catch before the grant comes back. The host grants, opens the window and refuses the
            // catch from one unit past reach. The grant used to clear the catch, so the refusal changed nothing: the
            // client stayed Owned while the host waited in its window, never handed back, and was reclaimed at 2.03 s.
            var host = new OwnershipStateMachine();
            var alice = new OwnershipClaim(Alice);
            True(alice.Grab(out var grabEpoch), "grab");
            var grant = host.Request(Alice, grabEpoch, Origin, Origin, 10_000);
            Equal(OwnershipResult.Granted, grant.Result, "the host grants");
            True(alice.BeginRelease(20_000), "throw before the grant arrives");
            Equal(OwnershipResult.Releasing, host.BeginRelease(Alice, alice.StreamEpoch, Origin, 20_000).Result, "the host opens the window");
            True(alice.Grab(out var catchEpoch), "catch before the grant arrives");
            Equal(2, alice.OutstandingRequests, "two answers outstanding");
            var refusal = host.Request(Alice, catchEpoch, Origin, PastReach, 30_000);
            Equal(OwnershipResult.OutOfRange, refusal.Result, "the host refuses the catch");

            alice.OnGranted(grant.Epoch);
            True(alice.State == OwnershipState.Owned && alice.OutstandingRequests == 1, "the grant settles the grab alone");
            alice.OnRefused(refusal.Epoch, refusal.Owner);
            Equal(OwnershipState.ReleaseWindow, alice.State, "the refused catch resumes the window");
            Equal(host.ReleaseDeadlineMicros, alice.ReleaseDeadlineMicros, "with the host's deadline");
            True(alice.TryCompleteHandback(1_020_000, out var handbackEpoch), "so the client hands back on time");
            Equal(OwnershipResult.HandedBack, host.Handback(Alice, handbackEpoch, Rest, Origin, 1_020_000, out _).Result, "and the host takes it");
        }

        private static void TwoCatchesInOneRoundTripSettleOnTheLastAnswer()
        {
            // Throw, catch, throw, catch, before any answer: the host's window keeps the first throw's deadline while
            // the catches are refused, and closes when one is granted.
            foreach (var (firstInRange, secondInRange) in new[] { (false, true), (false, false), (true, false), (true, true) })
            {
                var host = Granted(out var alice);
                alice.BeginRelease(100_000);
                host.BeginRelease(Alice, alice.StreamEpoch, Origin, 100_000);
                alice.Grab(out var firstEpoch);
                var first = host.Request(Alice, firstEpoch, Origin, firstInRange ? Origin : PastReach, 110_000);
                alice.BeginRelease(120_000);
                host.BeginRelease(Alice, alice.StreamEpoch, Origin, 120_000);
                alice.Grab(out var secondEpoch);
                var second = host.Request(Alice, secondEpoch, Origin, secondInRange ? Origin : PastReach, 130_000);

                var name = (firstInRange ? "granted" : "refused") + " then " + (secondInRange ? "granted" : "refused");
                Deliver(alice, first);
                Equal(OwnershipState.Owned, alice.State, name + ": nothing settles before the last answer");
                Deliver(alice, second);
                Equal(host.State, alice.State, name + ": the claim settles where the host is");
                if (host.State == OwnershipState.ReleaseWindow)
                {
                    Equal(host.ReleaseDeadlineMicros, alice.ReleaseDeadlineMicros, name + ": with the host's deadline");
                }
            }
        }

        private static void AGrabWaitsForOutstandingAnswers()
        {
            // A refusal naming someone else rolls back at once, but a fresh grab waits for the answers still due, so
            // the host's next answer is always the one for the oldest request.
            var host = Granted(out var alice);
            alice.BeginRelease(100_000);
            host.BeginRelease(Alice, alice.StreamEpoch, Origin, 100_000);
            alice.Grab(out var catchEpoch);
            var steal = host.Request(Bob, host.Epoch, Origin, Origin, 105_000);
            Equal(OwnershipResult.Granted, steal.Result, "Bob steals the throw first");
            var late = host.Request(Alice, catchEpoch, Origin, Origin, 110_000);
            alice.OnOwnerChanged(steal.Owner, steal.Epoch);
            True(alice.State == OwnershipState.Server && alice.OutstandingRequests == 1, "the steal rolls back the catch");
            True(!alice.Grab(out _), "no new grab while the catch's answer is due");
            alice.OnRefused(late.Epoch, late.Owner);
            True(alice.OutstandingRequests == 0 && alice.State == OwnershipState.Server, "the refusal arrives");
            True(alice.Grab(out _), "then the grab goes out");

            var greedy = new OwnershipClaim(Alice);
            greedy.Grab(out _);
            greedy.OnGranted(1);
            for (var index = 0; index < OwnershipClaim.MaximumOutstandingRequests; index++)
            {
                greedy.BeginRelease(index);
                True(greedy.Grab(out _), "catch " + index);
            }

            greedy.BeginRelease(100);
            True(!greedy.Grab(out _) && greedy.State == OwnershipState.ReleaseWindow, "no more than the outstanding limit");
        }

        private static void TheClaimAgreesWithTheHostWhateverTheTiming()
        {
            // Alice grabs, throws, catches, streams and hands back at random; Bob grabs, throws and lets go, so each can
            // steal the other's throw; and the host polls. The host's replies reach Alice after random delays, in order.
            // Whenever every reply has arrived, Alice's claim must show exactly what the host holds for her. Bob's
            // throws matter: a steal can grant Alice an epoch her optimistic samples did not guess, and the host then
            // ignores what she sent under the guess.
            var sweep = new Sweep();
            for (ulong seed = 1; seed <= 400; seed++) Play(seed, sweep);
            True(sweep.Comparisons > 15_000 && sweep.InWindow > 1_000 && sweep.RefusedCatches > 200 && sweep.Steals > 200 && sweep.Rejections > 200,
                "the sweep covered the cases: " + sweep.Comparisons + " comparisons, " + sweep.InWindow + " in a window, " +
                sweep.RefusedCatches + " refused catches, " + sweep.Steals + " steals, " + sweep.Rejections + " rejections");
        }

        /// <summary>
        /// One seed of the sweep: 300 steps, each a random event. One in six of Alice's samples, throws and hand-backs
        /// fails validation, and two steps in ten deliver nothing, so replies wait longer.
        /// </summary>
        private static void Play(ulong seed, Sweep sweep)
        {
            var random = new SeededRandom(seed);
            var host = new OwnershipStateMachine();
            var alice = new OwnershipClaim(Alice);
            var inbox = new Queue<Reply>();
            var now = 0L;
            for (var step = 0; step < 300; step++)
            {
                now += random.NextInt(1_000, 60_000);
                var faulty = random.NextInt(0, 6) == 0;
                switch (random.NextInt(0, 10))
                {
                    case 0:
                    case 1:
                        var wasWindow = alice.State == OwnershipState.ReleaseWindow;
                        var bobsThrow = host.State == OwnershipState.ReleaseWindow && host.Owner.Equals(NetOwner.ForParticipant(Bob));
                        if (!alice.Grab(out var requestEpoch)) break;
                        var answer = host.Request(Alice, requestEpoch, Origin, random.NextInt(0, 3) == 0 ? PastReach : Origin, now);
                        if (wasWindow && answer.Result == OwnershipResult.OutOfRange) sweep.RefusedCatches++;
                        if (bobsThrow && answer.Result == OwnershipResult.Granted) sweep.Steals++;
                        var granted = answer.Result == OwnershipResult.Granted || answer.Result == OwnershipResult.AlreadyOwned;
                        var notice = granted && answer.EpochChanged ? random.NextInt(0, 3) : 0;
                        if (notice == 1) inbox.Enqueue(Reply.Changed(answer));
                        inbox.Enqueue(Reply.Answer(answer));
                        if (notice == 2) inbox.Enqueue(Reply.Changed(answer));
                        break;
                    case 2:
                        if (alice.BeginRelease(now)) Note(inbox, host.BeginRelease(Alice, alice.StreamEpoch, faulty ? PastReach : Origin, now), sweep);
                        break;
                    case 3:
                        if (alice.IsSimulating) Note(inbox, host.Stream(Alice, alice.StreamEpoch, faulty ? TwentyMeters : Origin, Origin, now), sweep);
                        break;
                    case 4:
                        if (!alice.TryCompleteHandback(now, out var handbackEpoch)) break;
                        Note(inbox, host.Handback(Alice, handbackEpoch, faulty ? FarHandback : Rest, Origin, now, out _), sweep);
                        break;
                    case 5:
                        Note(inbox, Bobs(host, now, random.NextInt(0, 2) == 0), sweep);
                        break;
                    case 6:
                        Note(inbox, host.Poll(now), sweep);
                        break;
                    case 7:
                        for (var deliveries = random.NextInt(1, 4); deliveries > 0 && inbox.Count > 0; deliveries--) Deliver(alice, inbox.Dequeue());
                        break;
                }

                if (inbox.Count > 0) continue;
                sweep.Comparisons++;
                var expected = host.Owner.Equals(NetOwner.ForParticipant(Alice)) ? host.State : OwnershipState.Server;
                Equal(expected, alice.State, "seed " + seed + ", step " + step + ": the claim agrees with the host");
                Equal(0, alice.OutstandingRequests, "seed " + seed + ", step " + step + ": every answer is in");
                if (expected != OwnershipState.ReleaseWindow) continue;
                Equal(host.ReleaseDeadlineMicros, alice.ReleaseDeadlineMicros, "seed " + seed + ", step " + step + ": the same deadline");
                sweep.InWindow++;
            }
        }

        /// <summary>
        /// Bob's move: he grabs whenever he does not hold the body. Holding it, he throws or hands back; in his release
        /// window he streams until it closes, then hands back.
        /// </summary>
        private static OwnershipOutcome Bobs(OwnershipStateMachine host, long now, bool throws)
        {
            if (!host.Owner.Equals(NetOwner.ForParticipant(Bob))) return host.Request(Bob, host.Epoch, Origin, Origin, now);
            if (host.State == OwnershipState.Owned && throws) return host.BeginRelease(Bob, host.Epoch, Origin, now);
            if (host.State == OwnershipState.ReleaseWindow && now < host.ReleaseDeadlineMicros) return host.Stream(Bob, host.Epoch, Origin, Origin, now);
            return host.Handback(Bob, host.Epoch, Rest, Origin, now, out _);
        }

        private static OwnershipStateMachine Granted(out OwnershipClaim alice)
        {
            var host = new OwnershipStateMachine();
            alice = new OwnershipClaim(Alice);
            alice.Grab(out var epoch);
            var grant = host.Request(Alice, epoch, Origin, Origin, 0);
            alice.OnGranted(grant.Epoch);
            host.Stream(Alice, alice.StreamEpoch, Origin, Origin, 10_000);
            return host;
        }

        private static void Deliver(OwnershipClaim claim, OwnershipOutcome answer) => Deliver(claim, Reply.Answer(answer));

        private static void Deliver(OwnershipClaim claim, Reply reply)
        {
            var outcome = reply.Outcome;
            if (!reply.IsAnswer)
            {
                claim.OnOwnerChanged(outcome.Owner, outcome.Epoch);
            }
            else if (outcome.Result == OwnershipResult.Granted || outcome.Result == OwnershipResult.AlreadyOwned)
            {
                claim.OnGranted(outcome.Epoch);
            }
            else
            {
                claim.OnRefused(outcome.Epoch, outcome.Owner);
            }
        }

        /// <summary>Every change of owner is announced to the client.</summary>
        private static void Note(Queue<Reply> inbox, OwnershipOutcome outcome, Sweep sweep)
        {
            if (outcome.Result == OwnershipResult.Rejected) sweep.Rejections++;
            if (outcome.EpochChanged) inbox.Enqueue(Reply.Changed(outcome));
        }

        /// <summary>What the sweep compared and which cases it reached.</summary>
        private sealed class Sweep
        {
            public int Comparisons { get; set; }

            public int InWindow { get; set; }

            public int RefusedCatches { get; set; }

            public int Steals { get; set; }

            public int Rejections { get; set; }
        }

        private readonly struct Reply
        {
            private Reply(bool isAnswer, OwnershipOutcome outcome)
            {
                IsAnswer = isAnswer;
                Outcome = outcome;
            }

            public bool IsAnswer { get; }

            public OwnershipOutcome Outcome { get; }

            public static Reply Answer(OwnershipOutcome outcome) => new Reply(true, outcome);

            public static Reply Changed(OwnershipOutcome outcome) => new Reply(false, outcome);
        }
    }
}
