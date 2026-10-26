using System;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The host's rules for players' poses (WS3.10), Unity-free: the 30 m/s budget and its two-second bank, falling at up to
    /// 64 m/s, the world's bounds, the teleport allowance, corrections and the grace for poses already on their way, the
    /// kick at the fifth strike and strikes forgiven over time, placements, the away claim, freshness, and health.
    /// </summary>
    internal static class PresencePlausibilityTests
    {
        private const long Second = 1_000_000;
        private const long Tick = 50_000;

        internal static void Run()
        {
            RunningWithinTheBudgetStands();
            TooFastIsCorrectedToTheLastValidPose();
            FallingHasABudgetOfItsOwn();
            TheBoundsHoldPlayersIn();
            TeleportsNeedTheFlagAndTheAllowance();
            PosesOnTheirWayAreIgnoredForAMoment();
            TheFifthStrikeKicks();
            StrikesAreForgivenOverTime();
            APlacementIsTakenAsItLands();
            AwayWhileMovingIsCleared();
            FreshnessAndRestart();
            HealthKeepsToTheGamesRules();
            PolicyAndBoundsValidateTheirInputs();
            Console.WriteLine("PresencePlausibilityTests passed.");
        }

        private static NetQuantizedPosition At(float x, float y = 0f, float z = 0f) => NetQuantization.QuantizePosition(new NetVector3(x, y, z));

        private static PresenceCheck Check(PresencePlausibility rules, NetQuantizedPosition position, long now, PresenceFlags flags = PresenceFlags.None, byte? correction = null) =>
            rules.Check(position, NetQuantization.IdentityRotationBits, flags, correction ?? rules.Correction, now, PresenceBounds.Everywhere);

        private static void RunningWithinTheBudgetStands()
        {
            var rules = new PresencePlausibility(PresencePolicy.Default, 0);
            Equal(PresenceVerdict.Accepted, Check(rules, At(0), 0).Verdict, "the first pose anchors the player");
            var now = 0L;
            var x = 0f;
            for (var tick = 0; tick < 200; tick++)
            {
                now += Tick;
                x += 1.45f;
                Equal(PresenceVerdict.Accepted, Check(rules, At(x), now).Verdict, "running at 29 m/s for ten seconds stands (tick " + tick + ")");
            }

            // Standing still banks credit: two seconds of it carry one burst of 60 m, and no more.
            now += 2 * Second;
            Equal(PresenceVerdict.Accepted, Check(rules, At(x + 60f), now).Verdict, "a burst of the two-second bank stands");
            now += 3 * Second;
            Equal(PresenceVerdict.Corrected, Check(rules, At(x + 60f + 61f), now).Verdict, "a bank never holds more than two seconds, whatever the wait");

            // The tolerance is slack once, not on every pose: a steady 40 m/s, a third past the limit, stands only while the bank
            // lasts. The first tick spends 2 m of a full bank, each after it half a metre more than it earns, so the 60 m bank
            // runs dry on the 119th.
            var steady = new PresencePlausibility(PresencePolicy.Default, 0);
            Check(steady, At(0f), 0);
            var caught = -1;
            var at = 0f;
            for (var tick = 1; tick <= 400 && caught < 0; tick++)
            {
                at += 2f;
                if (Check(steady, At(at), tick * Tick).Verdict == PresenceVerdict.Corrected) caught = tick;
            }

            Equal(119, caught, "a steady run past the limit is corrected once its bank runs dry");
        }

        private static void TooFastIsCorrectedToTheLastValidPose()
        {
            var rules = new PresencePlausibility(PresencePolicy.Default, 0);
            Check(rules, At(5f, 1f, 5f), 0);
            Check(rules, At(5f, 1f, 6f), Tick);
            var check = Check(rules, At(5f, 1f, 200f), 2 * Tick);
            Equal(PresenceVerdict.Corrected, check.Verdict, "200 m in a tick is a speed hack");
            Equal(PresenceViolation.Speed, check.Violation, "and named so");
            True(check.HasCorrection && check.CorrectTo.Equals(At(5f, 1f, 6f)), "the player goes back to its last valid pose");
            Equal((byte)1, check.Correction, "under a new correction number");
            Equal(1, check.Strikes, "one strike");
            Equal(At(5f, 1f, 6f), rules.Anchor, "the refused pose never moves the anchor");

            // Climbing counts against the same budget as running.
            var climber = new PresencePlausibility(PresencePolicy.Default, 0);
            Check(climber, At(0f), 0);
            Equal(PresenceVerdict.Accepted, Check(climber, At(0f, 50f), Second).Verdict, "50 m up in a second stands while the bank allows it");
            Equal(PresenceVerdict.Corrected, Check(climber, At(0f, 90f), Second + Tick).Verdict, "40 m more up a tick later does not");
        }

        private static void FallingHasABudgetOfItsOwn()
        {
            var rules = new PresencePlausibility(PresencePolicy.Default, 0);
            Check(rules, At(0f, 500f), 0);
            var now = 0L;
            var y = 500f;
            for (var tick = 0; tick < 40; tick++)
            {
                now += Tick;
                y -= 3.1f;
                Equal(PresenceVerdict.Accepted, Check(rules, At(0f, y), now).Verdict, "a fall at 62 m/s stands, past the running limit (tick " + tick + ")");
            }

            var shoved = new PresencePlausibility(PresencePolicy.Default, 0);
            Check(shoved, At(0f, 500f), 0);
            Equal(PresenceVerdict.Corrected, Check(shoved, At(0f, 300f), Tick).Verdict, "200 m down in a tick is not a fall");
        }

        private static void TheBoundsHoldPlayersIn()
        {
            var world = PresenceBounds.FromMeters(new NetVector3(-10f, -2f, -10f), new NetVector3(10f, 5f, 10f), 64f, 256f);
            True(world.IsLimited, "a measured world is limited");
            var rules = new PresencePlausibility(PresencePolicy.Default, 0);
            Equal(PresenceVerdict.Accepted, rules.Check(At(0f), NetQuantization.IdentityRotationBits, PresenceFlags.None, 0, 0, world).Verdict, "inside");
            Equal(PresenceVerdict.Accepted, rules.Check(At(73.9f), NetQuantization.IdentityRotationBits, PresenceFlags.Teleported, 0, Second, world).Verdict,
                "the margin lets a player out to 64 m past the colliders");
            var outside = rules.Check(At(74.5f), NetQuantization.IdentityRotationBits, PresenceFlags.None, 0, Second + Tick, world);
            Equal(PresenceViolation.Bounds, outside.Violation, "past the margin is outside the world");
            Equal(PresenceVerdict.Corrected, outside.Verdict, "and is corrected");
            True(world.Contains(At(0f, 260f)) && !world.Contains(At(0f, 262f)), "the headroom reaches 256 m above the colliders");
            True(!world.Contains(At(0f, -67f)), "and the margin 64 m below");

            var lost = new PresencePlausibility(PresencePolicy.Default, 0);
            var first = lost.Check(At(500f), NetQuantization.IdentityRotationBits, PresenceFlags.None, 0, 0, world);
            True(first.Verdict == PresenceVerdict.Corrected && !first.HasCorrection, "a first pose outside the world has nothing valid to go back to");
            True(PresenceBounds.Everywhere.Contains(At(4095f, -4096f, 4095f)), "unmeasured bounds take every pose the wire carries");
        }

        private static void TeleportsNeedTheFlagAndTheAllowance()
        {
            var rules = new PresencePlausibility(PresencePolicy.Default, 0);
            Check(rules, At(0f), 0);
            Equal(PresenceVerdict.Accepted, Check(rules, At(500f), Tick, PresenceFlags.Teleported).Verdict, "a flagged teleport stands");
            Equal(PresenceVerdict.Accepted, Check(rules, At(-500f), 2 * Tick, PresenceFlags.Teleported).Verdict, "and a second at once");
            var third = Check(rules, At(500f), 3 * Tick, PresenceFlags.Teleported);
            Equal(PresenceViolation.Teleport, third.Violation, "a third within ten seconds is past the allowance");
            True(third.HasCorrection && third.CorrectTo.Equals(At(-500f)), "and goes back to the last teleport's landing");
            Equal(PresenceVerdict.Accepted, Check(rules, At(-499f), 3 * Tick + 1, PresenceFlags.None, third.Correction).Verdict, "the player recovers there");
            Equal(PresenceVerdict.Accepted, Check(rules, At(500f), 10 * Second + 3 * Tick, PresenceFlags.Teleported, third.Correction).Verdict,
                "ten seconds bring one teleport back");
            Equal(PresenceVerdict.Accepted, Check(rules, At(501f), 10 * Second + 4 * Tick, PresenceFlags.Teleported, third.Correction).Verdict,
                "a flag on a pose that needed no teleport costs nothing");
            var flags = Check(rules, At(502f), 10 * Second + 5 * Tick, PresenceFlags.Teleported | PresenceFlags.Crouching, third.Correction).Flags;
            True((flags & PresenceFlags.Teleported) == 0 && (flags & PresenceFlags.Crouching) != 0, "the teleport flag is the host's to read, never relayed");
        }

        private static void PosesOnTheirWayAreIgnoredForAMoment()
        {
            var rules = new PresencePlausibility(PresencePolicy.Default, 0);
            Check(rules, At(0f), 0);
            var corrected = Check(rules, At(300f), Tick);
            Equal(PresenceVerdict.Corrected, corrected.Verdict, "a cheat is corrected");
            var late = Check(rules, At(310f), 2 * Tick, PresenceFlags.None, 0);
            Equal(PresenceVerdict.Ignored, late.Verdict, "a pose sent before the correction arrived is ignored");
            Equal(1, late.Strikes, "and costs no strike");
            Equal(PresenceVerdict.Accepted, Check(rules, At(0.5f), 3 * Tick, PresenceFlags.None, corrected.Correction).Verdict,
                "a pose with the new number is measured from where the host sent the player");
            var stubborn = Check(rules, At(320f), Second + 2 * Tick, PresenceFlags.None, 0);
            Equal(PresenceViolation.IgnoredCorrection, stubborn.Violation, "a player still on the old number a second later never took the correction");
            Equal(PresenceVerdict.Corrected, stubborn.Verdict, "and is struck and corrected again");
        }

        private static void TheFifthStrikeKicks()
        {
            var rules = new PresencePlausibility(PresencePolicy.Default, 0);
            Check(rules, At(0f), 0);
            var now = 0L;
            for (var strike = 1; strike < PresencePolicy.DefaultStrikesBeforeKick; strike++)
            {
                now += Tick;
                var check = Check(rules, At(1000f + strike), now);
                Equal(PresenceVerdict.Corrected, check.Verdict, "strike " + strike + " is corrected");
                Equal(strike, check.Strikes, "and counted");
            }

            now += Tick;
            var last = Check(rules, At(2000f), now);
            Equal(PresenceVerdict.Kick, last.Verdict, "the fifth strike kicks");
            Equal(PresencePolicy.DefaultStrikesBeforeKick, last.Strikes, "at five strikes");
            Equal(5, PresencePolicy.DefaultStrikesBeforeKick, "the bound is five");
        }

        private static void StrikesAreForgivenOverTime()
        {
            var rules = new PresencePlausibility(PresencePolicy.Default, 0);
            Check(rules, At(0f), 0);
            Check(rules, At(900f), Tick);
            Check(rules, At(900f), 2 * Tick, PresenceFlags.None, 0);
            var second = Check(rules, At(900f), Second + 3 * Tick, PresenceFlags.None, rules.Correction);
            Equal(2, second.Strikes, "two strikes");
            Equal(2, rules.StrikesAt(Second + 3 * Tick + 9 * Second), "nine clean seconds forgive nothing");
            Equal(1, rules.StrikesAt(Second + 3 * Tick + 10 * Second), "ten forgive one");
            Equal(0, rules.StrikesAt(Second + 3 * Tick + 20 * Second), "twenty forgive both");
            var later = Check(rules, At(5000f), Second + 3 * Tick + 25 * Second, PresenceFlags.None, rules.Correction);
            Equal(1, later.Strikes, "a strike after a clean spell starts from nothing");

            // An honest player with a lag spike now and then is never removed.
            var honest = new PresencePlausibility(PresencePolicy.Default, 0);
            Check(honest, At(0f), 0);
            var now = 0L;
            for (var spike = 0; spike < 50; spike++)
            {
                now += 11 * Second;
                var check = Check(honest, At(5000f + spike), now, PresenceFlags.None, honest.Correction);
                True(check.Verdict == PresenceVerdict.Corrected && check.Strikes == 1, "a spike every eleven seconds is one strike at a time (" + spike + ")");
            }
        }

        private static void APlacementIsTakenAsItLands()
        {
            var rules = new PresencePlausibility(PresencePolicy.Default, 0);
            Check(rules, At(0f), 0);
            rules.ExpectPlacement(At(900f), Tick);
            Equal(PresenceVerdict.Accepted, Check(rules, At(901.5f), 2 * Tick).Verdict, "the host's own placement is taken wherever it is, within two metres");
            Equal(PresenceVerdict.Accepted, Check(rules, At(902f), 3 * Tick).Verdict, "and measured from there on");
            rules.ExpectPlacement(At(-900f), 4 * Tick);
            Equal(PresenceVerdict.Corrected, Check(rules, At(-900f), 4 * Tick + 11 * Second).Verdict, "a placement waits ten seconds at most");
            var near = new PresencePlausibility(PresencePolicy.Default, 0);
            Check(near, At(0f), 0);
            near.ExpectPlacement(At(900f), Tick);
            Equal(PresenceVerdict.Corrected, Check(near, At(904f), 2 * Tick).Verdict, "four metres off is not the placement");

            foreach (var flags in new[] { PresenceFlags.None, PresenceFlags.Teleported })
            {
                var arriving = new PresencePlausibility(PresencePolicy.Default, 0);
                arriving.ExpectPlacement(At(900f), 0);
                Check(arriving, At(0f), Tick);
                Equal(PresenceVerdict.Accepted, Check(arriving, At(900f), 2 * Tick, flags).Verdict,
                    "a first pose before the landing does not consume the host's expected placement");
                Equal(PresenceVerdict.Accepted, Check(arriving, At(1100f), 3 * Tick, PresenceFlags.Teleported).Verdict,
                    "the first own teleport still has its allowance after the landing");
                Equal(PresenceVerdict.Accepted, Check(arriving, At(1300f), 4 * Tick, PresenceFlags.Teleported).Verdict,
                    "the second own teleport still has its allowance after the landing");
                Equal(PresenceVerdict.Corrected, Check(arriving, At(1500f), 5 * Tick, PresenceFlags.Teleported).Verdict,
                    "the third own teleport remains over the allowance");
            }
        }

        private static void AwayWhileMovingIsCleared()
        {
            var rules = new PresencePlausibility(PresencePolicy.Default, 0);
            Check(rules, At(0f), 0);
            True((Check(rules, At(0.2f), Second, PresenceFlags.Away).Flags & PresenceFlags.Away) != 0, "away while standing still stands");
            True((Check(rules, At(3f), 2 * Second, PresenceFlags.Away).Flags & PresenceFlags.Away) == 0, "away while running is cleared");
            var turned = NetQuantization.QuantizeRotation(new NetQuaternion(0f, 0.3826834f, 0f, 0.9238795f));
            var check = rules.Check(At(3f), turned, PresenceFlags.Away, rules.Correction, 3 * Second, PresenceBounds.Everywhere);
            True((check.Flags & PresenceFlags.Away) == 0, "away while turning 45 degrees is cleared");
            var nudged = NetQuantization.QuantizeRotation(new NetQuaternion(0f, 0.0261769f, 0f, 0.9996573f));
            check = rules.Check(At(3f), nudged, PresenceFlags.Away, rules.Correction, 4 * Second, PresenceBounds.Everywhere);
            True((check.Flags & PresenceFlags.Away) == 0, "a three-degree turn from 45 degrees is still a turn of 42");
            check = rules.Check(At(3f), nudged, PresenceFlags.Away, rules.Correction, 5 * Second, PresenceBounds.Everywhere);
            True((check.Flags & PresenceFlags.Away) != 0, "while keeping still, away stands");
        }

        private static void FreshnessAndRestart()
        {
            var rules = new PresencePlausibility(PresencePolicy.Default, 0);
            True(!rules.IsFresh(0), "nothing is fresh before a pose");
            Check(rules, At(1f), Second);
            True(rules.IsFresh(Second + PresencePolicy.DefaultStaleAfterMicros), "a pose stays fresh for 2.5 seconds");
            True(!rules.IsFresh(Second + PresencePolicy.DefaultStaleAfterMicros + 1), "and no longer");
            rules.Restart(2 * Second);
            True(!rules.HasAnchor && !rules.IsFresh(2 * Second), "a restart forgets the anchor");
            Equal(PresenceVerdict.Accepted, Check(rules, At(3000f), 2 * Second + 1).Verdict, "and takes the next pose wherever it is, as a new world's");
        }

        private static void HealthKeepsToTheGamesRules()
        {
            var health = new PresenceHealth(PresencePolicy.Default);
            True(health.Take(80f, 100f, 0) && health.Health == 80f && health.Maximum == 100f, "a first report stands");
            True(health.Take(20f, 100f, Second) && health.Health == 20f, "damage always stands");
            True(health.Take(45f, 100f, 2 * Second) && health.Health == 45f, "regaining a quarter of the maximum in a second stands");
            True(!health.Take(100f, 100f, 2 * Second + Tick) && health.Health < 47f, "full health a tick later is refused and held to what could regenerate");
            True(!health.Take(120f, 100f, 3 * Second) && health.Health < 47f && health.Refused == 2, "health past the maximum is refused whole");
            True(!health.Take(50f, 1000f, 4 * Second) && health.Maximum == 100f, "a maximum that changes is refused; the first stands");
            True(!health.Take(float.NaN, 100f, 5 * Second) && !health.Take(10f, float.PositiveInfinity, 5 * Second) && !health.Take(-1f, 100f, 5 * Second),
                "values no game reports are refused");
            True(!new PresenceHealth(PresencePolicy.Default).Take(10f, 2_000_000f, 0), "a maximum past the policy's is refused");

            // At 50 of 100 since four seconds: a tenth of a second regenerates 2.5, and a heal of 30 allows that much more.
            Equal(50f, health.Health, "the refused maximum left health at 50");
            health.AllowGain(30f);
            True(health.Take(80f, 100f, 4 * Second + 100_000) && health.Health == 80f, "a heal the host made itself stands");
            True(!health.Take(95f, 100f, 4 * Second + 200_000) && health.Health == 85f, "and is spent by the rise it allowed: what is left of it and the regeneration cap the next");

            // Health held back rises toward the report as regeneration allows, with no report more, and no further.
            True(health.Advance(4 * Second + 400_000) && health.Health == 90f, "held-back health rises at the regeneration rate");
            True(health.Advance(4 * Second + 800_000) && health.Health == 95f, "up to the report");
            True(!health.Advance(6 * Second) && health.Health == 95f, "and no further");
            True(health.Take(60f, 100f, 6 * Second) && !health.Advance(7 * Second) && health.Health == 60f, "damage reported since is not undone");
            True(!new PresenceHealth(PresencePolicy.Default).Advance(Second), "with no report there is nothing to raise");
        }

        private static void PolicyAndBoundsValidateTheirInputs()
        {
            Throws<ArgumentOutOfRangeException>(() => new PresencePolicy(maximumSpeedMetersPerSecond: 0), "speed");
            Throws<ArgumentOutOfRangeException>(() => new PresencePolicy(strikesBeforeKick: 0), "strikes");
            Throws<ArgumentOutOfRangeException>(() => new PresencePolicy(maximumHealth: float.NaN), "health");
            Throws<ArgumentNullException>(() => new PresencePlausibility(null!, 0), "policy");
            Equal(PresenceBounds.Everywhere, PresenceBounds.FromMeters(new NetVector3(1f, 0f, 0f), new NetVector3(0f, 0f, 0f), 1f, 1f), "an inverted box is no box");
            Equal(PresenceBounds.Everywhere, PresenceBounds.FromMeters(new NetVector3(float.NaN, 0f, 0f), new NetVector3(0f, 0f, 0f), 1f, 1f), "a box that is not finite is no box");
            Equal(30, PresencePolicy.Default.MaximumSpeedMetersPerSecond, "at most 30 m/s");
        }
    }
}
