using System;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Cooldowns on the host's machine (WS3.5): every participant keeps its own, strikes double up to the cap, taking
    /// turns costs only the base, and a closed link ends a hold as going quiet or letting go would.
    /// </summary>
    internal static class MultiplayerOwnershipCooldownTests
    {
        private const int Alice = 2;
        private const int Bob = 3;
        private const int Carol = 4;
        private const long Base = OwnershipPolicy.DefaultYieldCooldownMicros;
        private const long Cap = OwnershipPolicy.DefaultMaximumYieldCooldownMicros;
        private static readonly NetQuantizedPosition Origin = new NetQuantizedPosition(0, 0, 0);
        private static readonly MotionHandback Rest = new MotionHandback(Origin, NetVector3.Zero, NetVector3.Zero);

        public static void Run()
        {
            EveryParticipantKeepsItsOwnCooldown();
            StrikesDoubleUpToTheCap();
            TakingTurnsCostsOnlyTheBase();
            AClosedLinkEndsAHoldAsGoingQuietOrLettingGoWould();
            TheTableHasOneEntryPerSlot();
            Console.WriteLine("MultiplayerOwnershipCooldownTests passed.");
        }

        private static void EveryParticipantKeepsItsOwnCooldown()
        {
            // Alice and Bob take turns going quiet. One cooldown slot, which each new yield moved, freed Alice the
            // moment Bob lapsed, so the two of them could keep the body from Carol forever.
            var host = new OwnershipStateMachine();
            Lapse(host, Alice, 0);
            Equal(OwnershipResult.Granted, host.Request(Bob, host.Epoch, Origin, Origin, 300_000).Result, "Bob takes the body Alice let lapse");
            host.Poll(550_001);
            True(host.IsYielding(Alice, 600_000) && host.IsYielding(Bob, 600_000), "both yield, each for its own cooldown");
            Equal(OwnershipResult.Yielding, host.Request(Alice, host.Epoch, Origin, Origin, 600_000).Result, "Bob's lapse does not free Alice");
            Equal(OwnershipResult.Granted, host.Request(Carol, host.Epoch, Origin, Origin, 610_000).Result, "Carol gets the body");
            True(host.CooldownOf(Alice).UntilMicros == 1_250_001 && host.CooldownOf(Bob).UntilMicros == 1_550_001, "each cooldown runs from its own lapse");
            Equal(0L, host.CooldownOf(Carol).UntilMicros, "Carol has none");
        }

        private static void StrikesDoubleUpToTheCap()
        {
            // Alice goes quiet as soon as each cooldown ends: 1, 2, 4 and 8 s, and 8 s after that.
            var host = new OwnershipStateMachine();
            var now = 0L;
            foreach (var expected in new[] { 1_000_000L, 2_000_000L, 4_000_000L, 8_000_000L, 8_000_000L })
            {
                now = Lapse(host, Alice, now);
                var cooldown = host.CooldownOf(Alice);
                True(cooldown.StrikeMicros == expected && cooldown.UntilMicros == now + expected, "a strike costs " + expected + " us, here " + cooldown);
                Equal(OwnershipResult.Yielding, host.Request(Alice, host.Epoch, Origin, Origin, cooldown.UntilMicros - 1).Result, "until one microsecond before it ends");
                now = cooldown.UntilMicros;
            }

            // A strike within the cap after the last cooldown ended doubles; one exactly that long after starts again.
            foreach (var (gap, doubled) in new[] { (Cap - 1, true), (Cap, false) })
            {
                var edge = new OwnershipStateMachine();
                var lapse = Lapse(edge, Bob, 0);
                var end = edge.CooldownOf(Bob).UntilMicros;
                Lapse(edge, Bob, end + gap - OwnershipPolicy.DefaultFirstSampleTimeoutMicros - 1);
                Equal(doubled ? 2 * Base : Base, edge.CooldownOf(Bob).StrikeMicros, "a strike " + gap + " us after the cooldown ended at " + end + " (lapse at " + lapse + ")");
            }
        }

        private static void TakingTurnsCostsOnlyTheBase()
        {
            // Alice has struck twice, so her next strike would cost 4 s. She holds the body properly for over a second,
            // Bob asks for it, and she hands it back: that is taking turns, so it costs the base, and her strike stays.
            var host = new OwnershipStateMachine();
            var now = Lapse(host, Alice, 0);
            now = Lapse(host, Alice, host.CooldownOf(Alice).UntilMicros);
            now = host.CooldownOf(Alice).UntilMicros;
            Equal(2 * Base, host.CooldownOf(Alice).StrikeMicros, "two strikes");
            HoldStreamed(host, Alice, now);
            Equal(OwnershipResult.Denied, host.Request(Bob, host.Epoch, Origin, Origin, now + 50_000).Result, "Bob asks while she holds it");
            True(host.Contested, "and contests her hold");
            host.BeginRelease(Alice, host.Epoch, Origin, now + 100_000);
            Equal(OwnershipResult.HandedBack, host.Handback(Alice, host.Epoch, Rest, Origin, now + 1_100_000, out _).Result, "she hands back after 1.1 s");
            True(host.CooldownOf(Alice).UntilMicros == now + 1_100_000 + Base && host.CooldownOf(Alice).StrikeMicros == 2 * Base, "taking turns costs the base and keeps the strike");

            // The same hand-back after a 50 ms hold is a strike: no client's claim lets go that early.
            var shortHold = new OwnershipStateMachine();
            shortHold.Request(Alice, 0, Origin, Origin, 0);
            shortHold.Stream(Alice, 1, Origin, Origin, 10_000);
            shortHold.Request(Bob, 1, Origin, Origin, 20_000);
            shortHold.Handback(Alice, 1, Rest, Origin, 50_000, out _);
            True(shortHold.IsYielding(Alice, 50_000) && shortHold.CooldownOf(Alice).StrikeMicros == Base, "a contested hand-back after 50 ms is a strike");

            // A claim holds a body the whole release window; a hold is short below half of it, which leaves room for a
            // grab that reached the host late, after a retransmission. The edge is exact.
            Equal(500_000L, OwnershipPolicy.Default.ShortHoldMicros, "half the release window");
            foreach (var (held, strikes) in new[] { (OwnershipPolicy.Default.ShortHoldMicros - 1, true), (OwnershipPolicy.Default.ShortHoldMicros, false), (700_000L, false) })
            {
                var edge = new OwnershipStateMachine();
                edge.Request(Alice, 0, Origin, Origin, 0);
                edge.Stream(Alice, 1, Origin, Origin, 10_000);
                edge.Handback(Alice, 1, Rest, Origin, held, out _);
                Equal(strikes, edge.LastReleaseStrikes, "letting go after " + held + " us");
            }

            // Bob sends a stale grab after every one of Alice's releases, which makes her yield each time. Her holds last
            // the release window, so each yield is taking turns: Bob cannot drive her cooldown up.
            var forged = new OwnershipStateMachine();
            var at = 0L;
            for (var round = 0; round < 6; round++)
            {
                HoldAndThrow(forged, Alice, at);
                var epoch = forged.Epoch;
                forged.Handback(Alice, epoch, Rest, Origin, at + 1_000_000, out _);
                forged.Request(Bob, epoch, Origin, Origin, at + 1_010_000);
                var cooldown = forged.CooldownOf(Alice);
                True(cooldown.UntilMicros == at + 1_010_000 + Base && cooldown.StrikeMicros == 0, "round " + round + ": a stale grab costs Alice the base, no strike");
                at = cooldown.UntilMicros;
            }

            // But a release that short is a strike when raced, and doubles like any other.
            var raced = new OwnershipStateMachine();
            for (var round = 0; round < 3; round++)
            {
                var start = raced.CooldownOf(Alice).UntilMicros;
                raced.Request(Alice, raced.Epoch, Origin, Origin, start);
                raced.Stream(Alice, raced.Epoch, Origin, Origin, start);
                var epoch = raced.Epoch;
                raced.Handback(Alice, epoch, Rest, Origin, start, out _);
                True(raced.LastReleaseStrikes, "round " + round + ": a hold of no time would strike");
                raced.Request(Bob, epoch, Origin, Origin, start + 10_000);
                Equal(Base << round, raced.CooldownOf(Alice).StrikeMicros, "round " + round + ": raced, it doubles");
            }
        }

        private static void AClosedLinkEndsAHoldAsGoingQuietOrLettingGoWould()
        {
            // Before the first sample: going quiet, a strike.
            var quiet = new OwnershipStateMachine();
            quiet.Request(Alice, 0, Origin, Origin, 0);
            var closed = quiet.LinkClosed(Alice, 200_000);
            True(closed.Result == OwnershipResult.Reclaimed && closed.ReclaimReason == OwnershipReclaimReason.Disconnected && closed.EpochChanged, "the host takes the body back");
            True(quiet.CooldownOf(Alice).UntilMicros == 1_200_000 && quiet.CooldownOf(Alice).StrikeMicros == Base, "Alice yields, a strike");
            Equal(OwnershipResult.NoChange, quiet.LinkClosed(Alice, 300_000).Result, "a second close holds nothing");
            Equal(1_200_000L, quiet.CooldownOf(Alice).UntilMicros, "and keeps the cooldown through the grace period");

            // Contested after a second of streaming: taking turns.
            var contested = new OwnershipStateMachine();
            HoldStreamed(contested, Alice, 0);
            Equal(OwnershipResult.Denied, contested.Request(Bob, contested.Epoch, Origin, Origin, 500_000).Result, "Bob asks while Alice holds it");
            contested.LinkClosed(Alice, 1_100_000);
            True(contested.IsYielding(Alice, 1_100_000) && contested.CooldownOf(Alice).StrikeMicros == 0, "a contested hold yields the base");

            // Uncontested and streamed: remembered like a hand-back, as a strike only if it was short.
            var streamed = new OwnershipStateMachine();
            HoldAndThrow(streamed, Alice, 0);
            streamed.LinkClosed(Alice, 1_100_000);
            True(!streamed.IsYielding(Alice, 1_100_000) && streamed.LastReleaser == Alice && !streamed.LastReleaseStrikes, "an uncontested hold is a release");
            streamed.Request(Bob, streamed.Epoch - 1, Origin, Origin, 1_200_000);
            True(streamed.IsYielding(Alice, 1_200_000) && streamed.CooldownOf(Alice).StrikeMicros == 0, "a request it made stale makes Alice take turns");
            var brief = new OwnershipStateMachine();
            brief.Request(Alice, 0, Origin, Origin, 0);
            brief.Stream(Alice, 1, Origin, Origin, 10_000);
            brief.LinkClosed(Alice, 100_000);
            True(brief.LastReleaser == Alice && brief.LastReleaseStrikes, "after 100 ms it would be a strike");

            // Rejoining keeps the cooldown; leaving for good forgets it, so the slot's next player starts clean.
            Equal(OwnershipResult.Yielding, quiet.Request(Alice, quiet.Epoch, Origin, Origin, 400_000).Result, "Alice rejoined within the grace period");
            quiet.Disconnect(Alice);
            True(quiet.CooldownOf(Alice).UntilMicros == 0 && quiet.CooldownOf(Alice).StrikeMicros == 0, "Alice left for good");
            Equal(OwnershipResult.Granted, quiet.Request(Alice, quiet.Epoch, Origin, Origin, 400_000).Result, "the slot's next player grabs at once");
        }

        private static void TheTableHasOneEntryPerSlot()
        {
            var policy = new OwnershipPolicy(maximumParticipants: 16);
            var host = new OwnershipStateMachine(policy);
            var now = 0L;
            for (var participant = 1; participant <= 16; participant++) now = Lapse(host, participant, now) + 1;
            for (var participant = 1; participant <= 16; participant++) Equal(Base, host.CooldownOf(participant).StrikeMicros, "slot " + participant + " has its own entry");
            Throws<ArgumentOutOfRangeException>(() => host.CooldownOf(17), "slot 17 is past the policy's participants");
            Throws<ArgumentOutOfRangeException>(() => host.IsYielding(0, now), "slot 0 is the server");
        }

        /// <summary>The participant takes the free body at <paramref name="nowMicros"/> and lets it lapse; returns the lapse time.</summary>
        private static long Lapse(OwnershipStateMachine host, int participant, long nowMicros)
        {
            Equal(OwnershipResult.Granted, host.Request(participant, host.Epoch, Origin, Origin, nowMicros).Result, "setup grant at " + nowMicros);
            var lapse = nowMicros + OwnershipPolicy.DefaultFirstSampleTimeoutMicros + 1;
            Equal(OwnershipReclaimReason.NoFirstSample, host.Poll(lapse).ReclaimReason, "setup lapse at " + lapse);
            return lapse;
        }

        /// <summary>The participant takes the free body and streams a sample of it 33 ms later.</summary>
        private static void HoldStreamed(OwnershipStateMachine host, int participant, long nowMicros)
        {
            Equal(OwnershipResult.Granted, host.Request(participant, host.Epoch, Origin, Origin, nowMicros).Result, "setup grant at " + nowMicros);
            Equal(OwnershipResult.Accepted, host.Stream(participant, host.Epoch, Origin, Origin, nowMicros + 33_333).Result, "setup sample");
        }

        /// <summary>The participant takes the free body, streams it and throws it 100 ms later.</summary>
        private static void HoldAndThrow(OwnershipStateMachine host, int participant, long nowMicros)
        {
            HoldStreamed(host, participant, nowMicros);
            Equal(OwnershipResult.Releasing, host.BeginRelease(participant, host.Epoch, Origin, nowMicros + 100_000).Result, "setup throw");
        }
    }
}
