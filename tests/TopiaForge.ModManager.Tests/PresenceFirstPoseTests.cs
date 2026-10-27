using System;
using TopiaForge.ModManager.Core.Multiplayer;
using static TopiaForge.ModManager.Tests.MultiplayerModelAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static class PresenceFirstPoseTests
    {
        private const long Second = 1_000_000;
        private static NetQuantizedPosition At(float x, float y = 0f) => NetQuantization.QuantizePosition(new NetVector3(x, y, 0f));
        private static PresenceCheck Pose(PresencePlausibility rules, NetQuantizedPosition point, long now, PresenceFlags flags = PresenceFlags.None) =>
            rules.Check(point, NetQuantization.IdentityRotationBits, flags, rules.Correction, now, PresenceBounds.Everywhere);

        internal static void Run()
        {
            MissingTrustWaits();
            FirstPoseMustStayNearTheTrustedStart();
            FirstPoseMovementIsBounded();
            PendingPlacementsStaySeparate();
            PlacementDeadlinesHold();
            RestartKeepsCreditAndStrikes();
            Console.WriteLine("PresenceFirstPoseTests passed: 6 model groups.");
        }

        private static void MissingTrustWaits()
        {
            var rules = new PresencePlausibility(PresencePolicy.Default, 0);
            foreach (var flags in new[] { PresenceFlags.None, PresenceFlags.Teleported })
            {
                foreach (var point in new[] { At(0), At(900) })
                {
                    var result = Pose(rules, point, Second, flags);
                    True(result.Verdict == PresenceVerdict.Ignored && !result.HasCorrection && !rules.HasAnchor && !rules.IsFresh(Second),
                        "without trust even origin or a flagged first pose validates nothing");
                }
            }

            Equal(0, rules.Strikes, "waiting for readiness costs no strike");
            rules.SetInitialPosition(At(100), Second);
            Equal(PresenceVerdict.Accepted, Pose(rules, At(100), Second).Verdict, "readiness becoming available recovers automatically");
        }

        private static void FirstPoseMustStayNearTheTrustedStart()
        {
            foreach (var flags in new[] { PresenceFlags.None, PresenceFlags.Teleported })
            {
                var rules = new PresencePlausibility(PresencePolicy.Default, 0);
                rules.SetInitialPosition(At(100), 0);
                var refused = Pose(rules, At(900), 50_000, flags);
                True(refused.Verdict == PresenceVerdict.Corrected && refused.HasCorrection && refused.CorrectTo.Equals(At(100)) && !rules.HasAnchor,
                    "a first pose far from the trusted spawn is corrected there without validating the target or packet");
                rules.SetInitialPosition(At(900), 50_000);
                Equal(PresenceVerdict.Corrected, Pose(rules, At(900), 100_000, flags).Verdict, "repeated setup cannot move the first-pose region");
                Equal(PresenceVerdict.Accepted, Pose(rules, At(100), 150_000).Verdict, "the corrected player can establish its first pose");
            }
        }

        private static void FirstPoseMovementIsBounded()
        {
            CheckAt(At(5), 100_000, true, "running during delivery at the limit plus placement slack");
            CheckAt(At(5.01f), 100_000, false, "past the running allowance");
            CheckAt(At(0, 5), 100_000, true, "climbing uses running speed");
            CheckAt(At(0, 5.01f), 100_000, false, "climbing cannot use fall speed");
            CheckAt(At(0, -8.39f), 100_000, true, "falling during delivery");
            CheckAt(At(0, -8.42f), 100_000, false, "past the falling allowance");
            CheckAt(At(62), 100 * Second, true, "two-second running cap inclusive");
            CheckAt(At(62.01f), 100 * Second, false, "waiting cannot widen the region indefinitely");
            CheckAt(At(0, -130.01f), 100 * Second, false, "falling also has a bounded first-pose region");

            static void CheckAt(NetQuantizedPosition point, long now, bool accepted, string message)
            {
                var rules = new PresencePlausibility(PresencePolicy.Default, 0);
                rules.SetInitialPosition(At(0), 0);
                Equal(accepted ? PresenceVerdict.Accepted : PresenceVerdict.Corrected, Pose(rules, point, now).Verdict, message);
            }
        }

        private static void PendingPlacementsStaySeparate()
        {
            foreach (var flags in new[] { PresenceFlags.None, PresenceFlags.Teleported })
            {
                foreach (var arrivalFirst in new[] { false, true })
                {
                    var rules = new PresencePlausibility(PresencePolicy.Default, 0);
                    rules.SetInitialPosition(At(100), 0);
                    if (arrivalFirst) rules.ExpectPlacement(At(900), 0);
                    Equal(PresenceVerdict.Accepted, Pose(rules, At(101), 100_000).Verdict, "an honest first pose moving from readiness stands");
                    if (!arrivalFirst) rules.ExpectPlacement(At(900), 100_000);
                    Equal(PresenceVerdict.Accepted, Pose(rules, At(900), 200_000, flags).Verdict, "only the landing consumes the pending placement");
                    Equal(PresenceVerdict.Accepted, Pose(rules, At(1100), 300_000, PresenceFlags.Teleported).Verdict, "first own teleport stands");
                    Equal(PresenceVerdict.Accepted, Pose(rules, At(1300), 400_000, PresenceFlags.Teleported).Verdict, "second own teleport stands");
                    Equal(PresenceViolation.Teleport, Pose(rules, At(1500), 500_000, PresenceFlags.Teleported).Violation, "third own teleport is refused");
                }
            }
        }

        private static void PlacementDeadlinesHold()
        {
            foreach (var flags in new[] { PresenceFlags.None, PresenceFlags.Teleported })
            {
                foreach (var hasSpawn in new[] { false, true })
                {
                    var onTime = new PresencePlausibility(PresencePolicy.Default, 0);
                    if (hasSpawn) onTime.SetInitialPosition(At(100), 0);
                    onTime.ExpectPlacement(At(900), 0);
                    Equal(PresenceVerdict.Accepted, Pose(onTime, At(900), 10 * Second, flags).Verdict, "a host landing at its deadline establishes a first pose");
                    var late = new PresencePlausibility(PresencePolicy.Default, 0);
                    if (hasSpawn) late.SetInitialPosition(At(100), 0);
                    late.ExpectPlacement(At(900), 0);
                    var result = Pose(late, At(900), 10 * Second + 1, flags);
                    Equal(hasSpawn ? PresenceVerdict.Corrected : PresenceVerdict.Ignored, result.Verdict, "an expired placement never establishes a first pose");
                    True(!late.HasAnchor && (!result.HasCorrection || result.CorrectTo.Equals(At(100))), "the expired target cannot become a correction or validated anchor");
                }
            }
        }

        private static void RestartKeepsCreditAndStrikes()
        {
            var rules = new PresencePlausibility(PresencePolicy.Default, 0);
            rules.SetInitialPosition(At(0), 0);
            Pose(rules, At(0), 0);
            Pose(rules, At(200), 0, PresenceFlags.Teleported);
            Pose(rules, At(400), 0, PresenceFlags.Teleported);
            Pose(rules, At(460), 0);
            Equal(0L, rules.CreditUnits, "running credit spent");
            Equal(1, Pose(rules, At(900), 0).Strikes, "one strike before restart");
            rules.ExpectPlacement(At(900), 0);
            rules.Restart(0);
            Equal(PresenceVerdict.Ignored, Pose(rules, At(900), 0).Verdict, "restart clears an old placement and spawn");
            rules.SetInitialPosition(At(100), 0);
            Equal(PresenceVerdict.Accepted, Pose(rules, At(100), 0).Verdict, "the new trusted spawn establishes a new pose");
            Equal(0L, rules.CreditUnits, "neither restart nor first pose refills movement credit");
            Equal(2, Pose(rules, At(110), 0).Strikes, "restart keeps strikes and refuses spent running credit");
            Equal(PresenceViolation.Teleport, Pose(rules, At(900), 0, PresenceFlags.Teleported).Violation, "restart cannot replenish own teleports");
        }
    }
}
