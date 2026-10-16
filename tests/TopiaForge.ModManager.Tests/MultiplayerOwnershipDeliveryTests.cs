using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The claim's delivery contract (WS3.5): only answers settle requests, whatever order the host's answers and
    /// announcements arrive in, and whichever request the host's inbox loses; epochs never move backwards; and a reset
    /// recovers a claim whose stream of replies broke, after a lost link, a reconnect or a replaced session.
    /// </summary>
    internal static class MultiplayerOwnershipDeliveryTests
    {
        private const int Alice = 2;
        private const int Bob = 3;
        private const int Reach = 100 * NetQuantization.PositionUnitsPerMeter + OwnershipPolicy.DefaultToleranceUnits;
        private static readonly NetQuantizedPosition Origin = new NetQuantizedPosition(0, 0, 0);
        private static readonly NetQuantizedPosition PastReach = new NetQuantizedPosition(Reach + 1, 0, 0);
        private static readonly MotionHandback Rest = new MotionHandback(Origin, NetVector3.Zero, NetVector3.Zero);

        public static void Run()
        {
            AnAnnouncementBetweenAnswersSettlesNothing();
            var coverage = EveryOrderOfAnswersAndAnnouncementsSettlesWhereTheHostIs();
            var lost = EveryOrderStillSettlesWhenTheHostLosesARequest();
            EpochsNeverMoveBackwards();
            ALostLinkMidRequestIsReset();
            AReconnectIsReset();
            AReplacedSessionIsReset();
            Console.WriteLine(
                "MultiplayerOwnershipDeliveryTests passed (" + coverage.Orders + " orders of " + coverage.Sequences + " event sequences; with a request lost, " +
                lost.Orders + " orders of " + lost.Sequences + " sequences, " + lost.Skipped + " recovered by a later answer and " + lost.TimedOut +
                " by the answer timeout).");
        }

        private static void AnAnnouncementBetweenAnswersSettlesNothing()
        {
            // Within one round trip Alice grabs, throws, catches from too far, throws and catches from too far again. The
            // host answers each request at once and announces her grant at the end of its frame, which falls after the
            // first catch, so she hears Granted, Refused, OwnerChanged(Alice), Refused. A refusal used to forget the
            // grant had been answered, so the announcement settled the second catch: the claim ended Owned while the
            // host waited in its window, and the hand-back timeout reclaimed the body at 2.1 s.
            var host = new OwnershipStateMachine();
            var alice = new OwnershipClaim(Alice);
            var replies = new List<Reply>();
            True(alice.Grab(10_000, out var grab), "grab");
            var grant = host.Request(Alice, grab.Epoch, Origin, Origin, 10_000);
            replies.Add(Reply.Answer(grab, grant));
            True(alice.BeginRelease(20_000), "throw");
            Equal(OwnershipResult.Releasing, host.BeginRelease(Alice, alice.StreamEpoch, Origin, 20_000).Result, "the window opens");
            True(alice.Grab(30_000, out var firstCatch), "a catch from too far");
            replies.Add(Reply.Answer(firstCatch, host.Request(Alice, firstCatch.Epoch, Origin, PastReach, 30_000)));
            replies.Add(Reply.Changed(grant));
            True(alice.BeginRelease(40_000), "a second throw");
            Equal(OwnershipResult.Releasing, host.BeginRelease(Alice, alice.StreamEpoch, Origin, 40_000).Result, "inside the window");
            True(alice.Grab(50_000, out var secondCatch), "another catch from too far");
            replies.Add(Reply.Answer(secondCatch, host.Request(Alice, secondCatch.Epoch, Origin, PastReach, 50_000)));
            foreach (var reply in replies) reply.DeliverTo(alice);

            True(alice.State == OwnershipState.ReleaseWindow && host.State == OwnershipState.ReleaseWindow, "both are in the window");
            Equal(host.ReleaseDeadlineMicros, alice.ReleaseDeadlineMicros, "with the first throw's deadline");
            True(alice.TryCompleteHandback(host.ReleaseDeadlineMicros, out var handbackEpoch), "so Alice hands back on time");
            Equal(OwnershipResult.HandedBack, host.Handback(Alice, handbackEpoch, Rest, Origin, host.ReleaseDeadlineMicros, out _).Result, "and the host takes it");
            Equal(OwnershipResult.NoChange, host.Poll(2_100_000).Result, "nothing is left to time out");
        }

        private static Coverage EveryOrderOfAnswersAndAnnouncementsSettlesWhereTheHostIs()
        {
            // Every sequence of up to five events inside one round trip: Alice grabs or catches from near or far, throws
            // from near or far, or hands back when her window closes; Bob, beside the host, grabs, lets go or throws; or
            // time passes and the host polls. Then the replies arrive in every order that keeps answers in order and announcements
            // in order: each announcement before, between or after the answers, and Alice's own grants unannounced as
            // well. Every order must leave the claim where the host is, under the host's epoch.
            var coverage = new Coverage(-1);
            var sequence = new int[5];
            for (var length = 1; length <= sequence.Length; length++) Enumerate(sequence, 0, length, coverage);
            True(coverage.Orders > 100_000, "every order was played: " + coverage.Orders + " orders of " + coverage.Sequences + " sequences");
            True(coverage.Before > 0 && coverage.Between > 0 && coverage.After > 0 && coverage.Absent > 0,
                "announcements came before, between and after answers, and not at all: " + coverage.Before + ", " + coverage.Between + ", " +
                coverage.After + ", " + coverage.Absent);
            True(coverage.Windows > 1_000 && coverage.Steals > 100, "release windows and steals were reached: " + coverage.Windows + ", " + coverage.Steals);
            return coverage;
        }

        private static Coverage EveryOrderStillSettlesWhenTheHostLosesARequest()
        {
            // The same sequences, with the host's inbox losing one of Alice's requests in turn, as a system channel past
            // its bounds does without telling her: the host never handles it, so no answer comes. Every order of the
            // remaining replies must still leave the claim where the host is once the answer timeout has passed, whether
            // a later answer showed the request lost or the timeout did. The claim used to wait for that answer forever.
            var coverage = new Coverage(0);
            var sequence = new int[5];
            for (var dropped = 0; dropped < 3; dropped++)
            {
                coverage.Dropped = dropped;
                for (var length = 1; length <= sequence.Length; length++) Enumerate(sequence, 0, length, coverage);
            }

            True(coverage.Orders > 10_000, "every order was played: " + coverage.Orders + " orders of " + coverage.Sequences + " sequences");
            True(coverage.Skipped > 1_000 && coverage.TimedOut > 1_000,
                "lost requests were recovered by a later answer and by the timeout: " + coverage.Skipped + ", " + coverage.TimedOut);
            return coverage;
        }

        private static void EpochsNeverMoveBackwards()
        {
            // A runtime that fed the claim from replicated object state would hear the newest owner first, because object
            // changes are coalesced and flushed ahead of the results queued before them. Alice grabs and hands back
            // inside one round trip; the object state says the server holds the body under epoch 2 before the answer to
            // her grab, granted under epoch 1, arrives. The claim used to take epoch 1 back, so its next grab was stale.
            var host = new OwnershipStateMachine();
            var alice = new OwnershipClaim(Alice);
            alice.Grab(0, out var grab);
            var grant = host.Request(Alice, grab.Epoch, Origin, Origin, 0);
            host.Stream(Alice, alice.StreamEpoch, Origin, Origin, 10_000);
            alice.BeginRelease(20_000);
            host.BeginRelease(Alice, alice.StreamEpoch, Origin, 20_000);
            True(alice.TryCompleteHandback(1_020_000, out var handbackEpoch), "hand back");
            var handback = host.Handback(Alice, handbackEpoch, Rest, Origin, 1_020_000, out _);
            alice.OnOwnerChanged(handback.Owner, handback.Epoch);
            alice.OnGranted(grab.Number, grant.Epoch);
            True(alice.Epoch == 2 && alice.KnownOwner.IsServer && alice.State == OwnershipState.Server, "the late answer takes nothing back");
            True(alice.Grab(1_100_000, out var next) && next.Epoch == 2, "so the next grab names the current epoch");
            Equal(OwnershipResult.Granted, host.Request(Alice, next.Epoch, Origin, Origin, 1_100_000).Result, "and is granted");

            // A refusal or announcement older than what the claim heard is ignored, across the wrap as well.
            var wrapped = new OwnershipClaim(Bob, knownEpoch: uint.MaxValue);
            wrapped.OnOwnerChanged(NetOwner.Server, uint.MaxValue);
            wrapped.OnOwnerChanged(NetOwner.ForParticipant(Alice), 0);
            Equal(0u, wrapped.Epoch, "zero comes after the largest epoch");
            wrapped.OnOwnerChanged(NetOwner.Server, uint.MaxValue);
            wrapped.OnRefused(0, uint.MaxValue - 5, NetOwner.Server);
            True(wrapped.Epoch == 0 && wrapped.KnownOwner.Equals(NetOwner.ForParticipant(Alice)), "and older reports never take it back");
        }

        private static void ALostLinkMidRequestIsReset()
        {
            // Alice grabs and the link closes with the grant in flight: it is gone, and so is Alice, so the host reclaims
            // the body at once. The claim used to wait for that answer forever, simulating the body and unable to grab.
            var host = new OwnershipStateMachine();
            var alice = new OwnershipClaim(Alice);
            alice.Grab(0, out var grab);
            Equal(OwnershipResult.Granted, host.Request(Alice, grab.Epoch, Origin, Origin, 0).Result, "granted, but the answer is lost");
            var closed = host.LinkClosed(Alice, 100_000);
            True(closed.Result == OwnershipResult.Reclaimed && closed.ReclaimReason == OwnershipReclaimReason.Disconnected, "the host reclaims what the closed link held");
            True(host.IsYielding(Alice, 100_000), "and Alice, who had sent no first sample, yields");
            True(alice.State == OwnershipState.Requested && alice.IsSimulating && !alice.Grab(100_000, out _), "without a reset the claim is stuck");
            alice.Reset(OwnershipClaimRecovery.LinkClosed);
            True(alice.State == OwnershipState.Server && !alice.IsSimulating && alice.OutstandingRequests == 0 && !alice.GrantPending, "the reset stops it");
            Equal(0u, alice.Epoch, "and it keeps its last epoch as a guess");
            Equal(OwnershipClaimRecovery.LinkClosed, alice.LastRecovery, "and records why");

            // Back on a new link, the runtime tells the claim who holds the body now, and Alice's grab goes through once
            // her cooldown is over.
            alice.Reset(OwnershipClaimRecovery.Reconnected);
            alice.OnOwnerChanged(host.Owner, host.Epoch);
            True(alice.Grab(1_100_000, out var retry) && retry.Epoch == host.Epoch, "the grab names the epoch from the baseline");
            var regrant = host.Request(Alice, retry.Epoch, Origin, Origin, 1_100_000);
            Equal(OwnershipResult.Granted, regrant.Result, "and is granted");
            alice.OnGranted(retry.Number, regrant.Epoch);
            Equal(OwnershipState.Owned, alice.State, "Alice holds the body again");
        }

        private static void AReconnectIsReset()
        {
            // Alice holds the body, has thrown it and caught it again, and the link closes with the catch's answer in
            // flight. Without a reset the claim would wait for that answer and keep simulating a body the host took back.
            var host = new OwnershipStateMachine();
            var alice = new OwnershipClaim(Alice);
            alice.Grab(0, out var grab);
            alice.OnGranted(grab.Number, host.Request(Alice, grab.Epoch, Origin, Origin, 0).Epoch);
            host.Stream(Alice, alice.StreamEpoch, Origin, Origin, 10_000);
            alice.BeginRelease(20_000);
            host.BeginRelease(Alice, alice.StreamEpoch, Origin, 20_000);
            alice.Grab(30_000, out var catchGrab);
            host.Request(Alice, catchGrab.Epoch, Origin, Origin, 30_000);
            var closed = host.LinkClosed(Alice, 35_000);
            True(closed.Result == OwnershipResult.Reclaimed && !host.IsYielding(Alice, 35_000), "an uncontested, streamed hold yields nothing");
            True(host.LastReleaser == Alice && host.LastReleaseMicros == 35_000, "it is remembered as a release");
            True(alice.IsSimulating && alice.OutstandingRequests == 1, "the catch waits for an answer that will never come");

            // Alice reconnects as the same participant; the runtime resets the claim and gives it the baseline, which
            // carries the epoch the reclaim made, and Bob, who grabbed meanwhile.
            Equal(OwnershipResult.Granted, host.Request(Bob, host.Epoch, Origin, Origin, 40_000).Result, "Bob takes the free body");
            alice.Reset(OwnershipClaimRecovery.Reconnected);
            alice.OnOwnerChanged(host.Owner, host.Epoch);
            True(alice.State == OwnershipState.Server && alice.KnownOwner.Equals(NetOwner.ForParticipant(Bob)) && alice.Epoch == host.Epoch, "the claim follows the host");
            True(alice.Grab(50_000, out var attempt) && attempt.Epoch == host.Epoch, "Alice may grab again");
            var refusal = host.Request(Alice, attempt.Epoch, Origin, Origin, 50_000);
            Equal(OwnershipResult.Denied, refusal.Result, "Bob holds it, so the first grab wins");
            alice.OnRefused(attempt.Number, refusal.Epoch, refusal.Owner);
            True(alice.State == OwnershipState.Server && alice.OutstandingRequests == 0, "and the claim settles");
        }

        private static void AReplacedSessionIsReset()
        {
            // The host replaces the session: the system channels drop what they hold, and a body in the new world counts
            // its epochs from zero. A claim the runtime keeps for the same scene object must take the new count.
            var old = new OwnershipStateMachine(initialEpoch: 6);
            var alice = new OwnershipClaim(Alice, knownEpoch: 6);
            alice.Grab(0, out var grab);
            alice.OnGranted(grab.Number, old.Request(Alice, grab.Epoch, Origin, Origin, 0).Epoch);
            True(alice.State == OwnershipState.Owned && alice.Epoch == 7, "Alice holds the old session's body");

            var stuck = new OwnershipClaim(Alice, knownEpoch: 7);
            stuck.OnOwnerChanged(NetOwner.Server, 7);
            stuck.OnOwnerChanged(NetOwner.Server, 0);
            Equal(7u, stuck.Epoch, "without a reset the new count looks old and is ignored");

            var fresh = new OwnershipStateMachine();
            alice.Reset(OwnershipClaimRecovery.SessionReplaced);
            alice.OnOwnerChanged(fresh.Owner, fresh.Epoch);
            True(alice.Epoch == 0 && alice.State == OwnershipState.Server, "after one the claim takes the new session's epoch");
            True(alice.Grab(0, out var retry) && retry.Epoch == 0, "names it");
            var grant = fresh.Request(Alice, retry.Epoch, Origin, Origin, 0);
            Equal(OwnershipResult.Granted, grant.Result, "and is granted");
            alice.OnGranted(retry.Number, grant.Epoch);
            True(alice.State == OwnershipState.Owned && alice.Epoch == 1, "and counts on from there");
            alice.OnOwnerChanged(NetOwner.Server, 6);
            Equal(OwnershipState.Server, alice.State, "a newer epoch in the new session ends the hold as usual");

            // A drop on the client's own channel is reset within the session, so older reports stay refused.
            var dropped = new OwnershipClaim(Alice, knownEpoch: 7);
            dropped.OnOwnerChanged(NetOwner.Server, 7);
            dropped.Reset(OwnershipClaimRecovery.ChannelDropped);
            dropped.OnOwnerChanged(NetOwner.ForParticipant(Bob), 5);
            True(dropped.Epoch == 7 && dropped.KnownOwner.IsServer && dropped.LastRecovery == OwnershipClaimRecovery.ChannelDropped, "a drop keeps the epoch guard");
            foreach (var recovery in new[] { OwnershipClaimRecovery.None, OwnershipClaimRecovery.AnswerTimeout, OwnershipClaimRecovery.AnswerSkipped })
            {
                Throws<ArgumentOutOfRangeException>(() => dropped.Reset(recovery), "the runtime resets only for its own reasons, not " + recovery);
            }
        }

        private static void Enumerate(int[] sequence, int position, int length, Coverage coverage)
        {
            if (position == length)
            {
                PlayAllOrders(sequence, length, coverage);
                return;
            }

            for (var action = 0; action < ActionCount; action++)
            {
                sequence[position] = action;
                Enumerate(sequence, position + 1, length, coverage);
            }
        }

        private const int ActionCount = 9;

        /// <summary>
        /// Plays the events with no replies delivered and returns the host, the claim, the replies in host order and the
        /// time of the last event. When <paramref name="dropped"/> is not negative, the host's inbox loses Alice's request
        /// with that index: she sent it, but the host never handles or answers it.
        /// </summary>
        private static Sent Send(int[] sequence, int length, int dropped)
        {
            var host = new OwnershipStateMachine();
            var alice = new OwnershipClaim(Alice);
            var sent = new Sent(host, alice);
            var requests = 0;
            var now = 0L;
            for (var index = 0; index < length; index++)
            {
                now += 10_000;
                var owner = host.Owner;
                OwnershipOutcome outcome;
                switch (sequence[index])
                {
                    case 0:
                    case 1:
                        var catching = alice.State == OwnershipState.ReleaseWindow;
                        if (!alice.Grab(now, out var request)) continue;
                        if (requests++ == dropped)
                        {
                            sent.Lost = true;
                            continue;
                        }

                        outcome = host.Request(Alice, request.Epoch, Origin, sequence[index] == 0 ? Origin : PastReach, now);
                        sent.Answers.Add(Reply.Answer(request, outcome));
                        if (outcome.Result == OwnershipResult.Granted && outcome.EpochChanged)
                        {
                            sent.Notices.Add(Reply.OwnGrant(outcome, sent.Answers.Count - 1));
                            sent.Steal |= catching || owner.Participant == Bob;
                        }

                        continue;
                    case 2:
                    case 3:
                        if (!alice.BeginRelease(now)) continue;
                        outcome = host.BeginRelease(Alice, alice.StreamEpoch, sequence[index] == 2 ? Origin : PastReach, now);
                        sent.Window |= outcome.Result == OwnershipResult.Releasing;
                        break;
                    case 4:
                        now = Math.Max(now, alice.ReleaseDeadlineMicros);
                        if (!alice.TryCompleteHandback(now, out var handbackEpoch)) continue;
                        outcome = host.Handback(Alice, handbackEpoch, Rest, Origin, now, out _);
                        break;
                    case 5:
                        outcome = host.Request(Bob, host.Epoch, Origin, Origin, now);
                        sent.Steal |= outcome.Result == OwnershipResult.Granted && owner.Participant == Alice;
                        break;
                    case 6:
                        if (host.Owner.Participant != Bob) continue;
                        outcome = host.Handback(Bob, host.Epoch, Rest, Origin, now, out _);
                        break;
                    case 7:
                        if (host.Owner.Participant != Bob) continue;
                        host.Stream(Bob, host.Epoch, Origin, Origin, now);
                        outcome = host.BeginRelease(Bob, host.Epoch, Origin, now);
                        break;
                    default:
                        now += 300_000;
                        outcome = host.Poll(now);
                        break;
                }

                if (outcome.EpochChanged) sent.Notices.Add(Reply.Changed(outcome));
            }

            sent.Now = now;
            return sent;
        }

        private static void PlayAllOrders(int[] sequence, int length, Coverage coverage)
        {
            var sent = Send(sequence, length, coverage.Dropped);

            // A variant that loses a request Alice never sent is the same as losing none, which the first check plays.
            if (coverage.Dropped >= 0 && !sent.Lost) return;
            coverage.Sequences++;
            if (sent.Window) coverage.Windows++;
            if (sent.Steal) coverage.Steals++;
            var order = new List<Reply>();
            Interleave(sequence, length, sent.Answers, sent.Notices, 0, 0, order, coverage);
        }

        /// <summary>Delivers every interleaving of answers and announcements, each own-grant announcement also left out.</summary>
        private static void Interleave(int[] sequence, int length, List<Reply> answers, List<Reply> notices, int answer, int notice, List<Reply> order, Coverage coverage)
        {
            if (answer == answers.Count && notice == notices.Count)
            {
                Check(sequence, length, order, coverage);
                return;
            }

            if (answer < answers.Count)
            {
                order.Add(answers[answer]);
                Interleave(sequence, length, answers, notices, answer + 1, notice, order, coverage);
                order.RemoveAt(order.Count - 1);
            }

            if (notice < notices.Count)
            {
                order.Add(notices[notice]);
                Interleave(sequence, length, answers, notices, answer, notice + 1, order, coverage);
                order.RemoveAt(order.Count - 1);
                if (notices[notice].AnswerIndex >= 0) Interleave(sequence, length, answers, notices, answer, notice + 1, order, coverage);
            }
        }

        private static void Check(int[] sequence, int length, List<Reply> order, Coverage coverage)
        {
            // The events are replayed for each order, so every order starts from the same claim.
            var sent = Send(sequence, length, coverage.Dropped);
            var answersSeen = 0;
            var announced = 0;
            foreach (var reply in order)
            {
                reply.DeliverTo(sent.Alice);
                if (reply.IsAnswer)
                {
                    answersSeen++;
                    continue;
                }

                if (reply.AnswerIndex < 0) continue;
                announced++;
                if (answersSeen <= reply.AnswerIndex) coverage.Before++;
                else if (answersSeen < sent.Answers.Count) coverage.Between++;
                else coverage.After++;
            }

            foreach (var notice in sent.Notices)
            {
                if (notice.AnswerIndex >= 0) announced--;
            }

            if (announced < 0) coverage.Absent++;
            coverage.Orders++;
            var host = sent.Host;
            var claim = sent.Alice;

            // Once every reply is in and the answer timeout has passed, nothing may be left waiting.
            var skipped = claim.LastRecovery == OwnershipClaimRecovery.AnswerSkipped;
            var timedOut = claim.Poll(sent.Now + OwnershipPolicy.DefaultAnswerTimeoutMicros + 1);
            if (sent.Lost)
            {
                if (skipped) coverage.Skipped++;
                if (timedOut) coverage.TimedOut++;
                if (claim.LostRequests != 1 || skipped == timedOut) throw new InvalidOperationException("the lost request was not recovered once: " + Describe(sequence, length, order, host, claim));
            }

            var expected = host.Owner.Participant == Alice ? host.State : OwnershipState.Server;
            var agrees = claim.State == expected && claim.OutstandingRequests == 0 && claim.Epoch == host.Epoch &&
                         (expected != OwnershipState.ReleaseWindow || claim.ReleaseDeadlineMicros == host.ReleaseDeadlineMicros);
            if (!agrees) throw new InvalidOperationException(Describe(sequence, length, order, host, claim));
        }

        /// <summary>The events, the replies and both sides' states, built only when they disagree.</summary>
        private static string Describe(int[] sequence, int length, List<Reply> order, OwnershipStateMachine host, OwnershipClaim claim) =>
            "events " + string.Join(",", new ArraySegment<int>(sequence, 0, length)) + ", replies " + string.Join(" ", order) + ": the host is " + host.State +
            " for " + host.Owner + " under " + host.Epoch + " (deadline " + host.ReleaseDeadlineMicros + "), the claim " + claim.State +
            " under " + claim.Epoch + " with " + claim.OutstandingRequests + " answers due (deadline " + claim.ReleaseDeadlineMicros + "), " +
            claim.LostRequests + " lost (" + claim.LastRecovery + ").";

        /// <summary>What one play of the events sent and left behind.</summary>
        private sealed class Sent
        {
            public Sent(OwnershipStateMachine host, OwnershipClaim alice)
            {
                Host = host;
                Alice = alice;
            }

            public OwnershipStateMachine Host { get; }

            public OwnershipClaim Alice { get; }

            public List<Reply> Answers { get; } = new List<Reply>();

            public List<Reply> Notices { get; } = new List<Reply>();

            public bool Window { get; set; }

            public bool Steal { get; set; }

            /// <summary>Whether the host's inbox lost one of Alice's requests.</summary>
            public bool Lost { get; set; }

            public long Now { get; set; }
        }

        private sealed class Coverage
        {
            public Coverage(int dropped)
            {
                Dropped = dropped;
            }

            /// <summary>The index of Alice's request the host's inbox loses, or -1 for none.</summary>
            public int Dropped { get; set; }

            public int Sequences { get; set; }

            public int Orders { get; set; }

            public int Before { get; set; }

            public int Between { get; set; }

            public int After { get; set; }

            public int Absent { get; set; }

            public int Windows { get; set; }

            public int Steals { get; set; }

            /// <summary>Orders where a later answer showed the lost request lost.</summary>
            public int Skipped { get; set; }

            /// <summary>Orders where the answer timeout took the lost request as lost.</summary>
            public int TimedOut { get; set; }
        }

        /// <summary>An answer, or an announcement; an announcement of Alice's own grant names the answer it goes with.</summary>
        private readonly struct Reply
        {
            private Reply(bool isAnswer, uint number, OwnershipOutcome outcome, int answerIndex)
            {
                IsAnswer = isAnswer;
                Number = number;
                Outcome = outcome;
                AnswerIndex = answerIndex;
            }

            public bool IsAnswer { get; }

            /// <summary>The number of the request an answer settles.</summary>
            public uint Number { get; }

            public OwnershipOutcome Outcome { get; }

            /// <summary>For an announcement of Alice's own grant, the index of its answer; otherwise -1.</summary>
            public int AnswerIndex { get; }

            public static Reply Answer(OwnershipRequest request, OwnershipOutcome outcome) => new Reply(true, request.Number, outcome, -1);

            public static Reply Changed(OwnershipOutcome outcome) => new Reply(false, 0, outcome, -1);

            public static Reply OwnGrant(OwnershipOutcome outcome, int answerIndex) => new Reply(false, 0, outcome, answerIndex);

            public void DeliverTo(OwnershipClaim claim)
            {
                if (!IsAnswer) claim.OnOwnerChanged(Outcome.Owner, Outcome.Epoch);
                else if (Outcome.Result == OwnershipResult.Granted || Outcome.Result == OwnershipResult.AlreadyOwned) claim.OnGranted(Number, Outcome.Epoch);
                else claim.OnRefused(Number, Outcome.Epoch, Outcome.Owner);
            }

            public override string ToString() => (IsAnswer ? "A" + Number + ":" + Outcome.Result : "N:" + Outcome.Owner.Participant) + "@" + Outcome.Epoch;
        }
    }
}
