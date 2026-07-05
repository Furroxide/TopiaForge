using System;
using Robotopia.Mods;

namespace Robotopia.ModManager.Tests
{
    // Unit tests for the pure who/what/where describe helper that grounds robot conversations: kind phrases,
    // whole-metre distances, the 8-way compass (north = +Z, east = +X), the next-to-you case, and the
    // currently-missing case.
    internal static class RobotTargetFactsTests
    {
        public static void Run()
        {
            TestKindPhrases();
            TestMissingTarget();
            TestNextToYou();
            TestDistanceRounding();
            TestCompassDirections();
            TestActivitySuffix();
            Console.WriteLine("All robot target facts tests passed.");
        }

        private static void TestKindPhrases()
        {
            var origin = Vec3.Zero;
            var north10 = new RobotTargetSnapshot(new Vec3(0f, 0f, 10f));

            Assert(RobotTargetFacts.Describe(new RobotTargetInfo("PLAYER", RobotTargetKind.Player), north10, origin)
                    == "the human operator, 10 m north of you", "player phrase");
            Assert(RobotTargetFacts.Describe(new RobotTargetInfo("ROBOT 2", RobotTargetKind.Robot), north10, origin)
                    .StartsWith("another robot,"), "robot phrase");
            Assert(RobotTargetFacts.Describe(new RobotTargetInfo("CRATE", RobotTargetKind.Prop), north10, origin)
                    .StartsWith("a prop,"), "prop phrase");
            Assert(RobotTargetFacts.Describe(new RobotTargetInfo("RED MARKER", RobotTargetKind.Marker), north10, origin)
                    .StartsWith("a marker pad,"), "marker phrase");
            Assert(RobotTargetFacts.Describe(new RobotTargetInfo("THING", RobotTargetKind.Custom), north10, origin)
                    .StartsWith("a target,"), "custom without description falls back to 'a target'");
            Assert(RobotTargetFacts.Describe(
                        new RobotTargetInfo("THING", RobotTargetKind.Custom, "a glowing obelisk"), north10, origin)
                    .StartsWith("a glowing obelisk,"), "custom description is used verbatim");
        }

        private static void TestMissingTarget()
        {
            var described = RobotTargetFacts.Describe(
                new RobotTargetInfo("CRATE", RobotTargetKind.Prop), null, Vec3.Zero);
            Assert(described == "a prop, currently missing", "unresolvable targets read as currently missing");
        }

        private static void TestNextToYou()
        {
            Assert(RobotTargetFacts.DirectionAndDistance(Vec3.Zero, new Vec3(0.5f, 0f, 0.5f)) == "right next to you",
                "under a metre is right next to you");
        }

        private static void TestDistanceRounding()
        {
            Assert(RobotTargetFacts.DirectionAndDistance(Vec3.Zero, new Vec3(0f, 0f, 8.4f)) == "8 m north of you",
                "distances round to whole metres");
            // Height differences are ignored — direction/distance are ground-plane facts.
            Assert(RobotTargetFacts.DirectionAndDistance(Vec3.Zero, new Vec3(0f, 30f, 8.4f)) == "8 m north of you",
                "the vertical component does not distort the distance");
        }

        private static void TestCompassDirections()
        {
            AssertDirection(0f, 10f, "north");
            AssertDirection(10f, 10f, "north-east");
            AssertDirection(10f, 0f, "east");
            AssertDirection(10f, -10f, "south-east");
            AssertDirection(0f, -10f, "south");
            AssertDirection(-10f, -10f, "south-west");
            AssertDirection(-10f, 0f, "west");
            AssertDirection(-10f, 10f, "north-west");
        }

        // The 4-arg overload appends what the target is doing right now, so a robot can reason about the
        // fleet ("another robot, 8 m north of you; currently: FOLLOW PLAYER (moving)").
        private static void TestActivitySuffix()
        {
            var info = new RobotTargetInfo("ROBOT 2", RobotTargetKind.Robot);
            var north8 = new RobotTargetSnapshot(new Vec3(0f, 0f, 8f));

            Assert(RobotTargetFacts.Describe(info, north8, Vec3.Zero, "currently: FOLLOW PLAYER (moving)")
                    == "another robot, 8 m north of you; currently: FOLLOW PLAYER (moving)",
                "a non-blank activity is appended after a semicolon");

            var plain = RobotTargetFacts.Describe(info, north8, Vec3.Zero);
            Assert(RobotTargetFacts.Describe(info, north8, Vec3.Zero, null) == plain,
                "a null activity yields exactly the 3-arg line");
            Assert(RobotTargetFacts.Describe(info, north8, Vec3.Zero, "  ") == plain,
                "a whitespace activity yields exactly the 3-arg line");

            Assert(RobotTargetFacts.Describe(info, null, Vec3.Zero, "currently: no program")
                    == "another robot, currently missing; currently: no program",
                "a missing target still carries the activity suffix");
        }

        private static void AssertDirection(float dx, float dz, string expected)
        {
            var described = RobotTargetFacts.DirectionAndDistance(Vec3.Zero, new Vec3(dx, 0f, dz));
            Assert(described.Contains(" " + expected + " of you"), "(" + dx + "," + dz + ") points " + expected + " — got: " + described);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
