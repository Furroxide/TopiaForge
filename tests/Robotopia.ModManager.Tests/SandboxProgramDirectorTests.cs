using System;
using Robotopia.Mods;
using Robotopia.Sandbox;

namespace Robotopia.ModManager.Tests
{
    // Unit tests for the sandbox PROGRAM verb's pure brain: the conversation request shape (persona + facts + the
    // closed action/target sets) and the deterministic decision+target -> objective parse, including the structural
    // exit-chat rule (CHAT keeps talking; any accepted action exits) and the safety gate (an action without a real
    // target degrades back to chat). Mirrors ConversationDirectorTests.
    internal static class SandboxProgramDirectorTests
    {
        private static readonly string[] KnownTargets = { "PLAYER", "RED MARKER", "CRATE" };

        public static void Run()
        {
            TestRequestCarriesPersonaFactsAndClosedSets();
            TestRequestWithNoTargetsStillOffersNone();
            TestRequestGuidesTargetSemantics();
            TestDescribedTargetsFlowThroughLiveFacts();
            TestChatNeverExits();
            TestIdleNeedsNoTarget();
            TestAutonomousExitsWithoutObjective();
            TestActionsMapToObjectives();
            TestActionWithoutTargetDegradesToChat();
            TestUnknownTargetDegradesToChat();
            TestTargetMatchingIsCaseInsensitive();
            Console.WriteLine("All sandbox program director tests passed.");
        }

        private static void TestRequestCarriesPersonaFactsAndClosedSets()
        {
            var request = RobotProgramDirector.BuildRequest("Bolt", "FOLLOW PLAYER", KnownTargets, null, 12, 0.6f);

            Assert(request.SystemFrame.Contains("Bolt"), "the persona carries the robot's name");
            Assert(request.SystemFrame.Contains("OPERATOR"), "the persona frames the human as the operator");
            Assert(request.MaxTurns == 12 && Math.Abs(request.Temperature - 0.6f) < 1e-6f, "turn/temperature knobs carry");
            Assert(request.Usage == "sandbox-program", "the backend usage label is set");

            Assert(request.GroundTruthFacts != null, "ground-truth facts exist");
            Assert(request.GroundTruthFacts!["current-program"] == "FOLLOW PLAYER", "the current program is authoritative");
            Assert(request.GroundTruthFacts["known-targets"].Contains("RED MARKER"), "the target vocabulary is authoritative");
            Assert(request.LiveFacts == null, "no describe provider -> no live facts");

            Assert(request.DecisionOptions.Count == 6 && request.DecisionOptions[0] == "CHAT",
                "the decision set is the six actions with CHAT first");
            Assert(request.DecisionOptions[5] == "AUTONOMOUS", "AUTONOMOUS is an offered decision");

            Assert(request.ExtraOutputs != null && request.ExtraOutputs.Count == 1, "one extra output: the target");
            var target = request.ExtraOutputs![0];
            Assert(target.Name == RobotProgramDirector.TargetField, "the extra field is the target");
            Assert(target.AllowedStrings != null && target.AllowedStrings.Count == KnownTargets.Length + 1
                && target.AllowedStrings[0] == RobotProgramDirector.NoTarget,
                "the target enum is NONE plus every known target");
        }

        private static void TestRequestWithNoTargetsStillOffersNone()
        {
            var request = RobotProgramDirector.BuildRequest("Bolt", string.Empty, Array.Empty<string>(), null, 12, 0.6f);
            Assert(request.GroundTruthFacts!["current-program"].Contains("NONE"), "no program reads as NONE");
            Assert(request.ExtraOutputs![0].AllowedStrings!.Count == 1, "with no targets the enum is just NONE");
        }

        // The follow-the-player bug fix: the persona must explain that PLAYER means the operator (and only for
        // "follow me"), that robots/props/markers are all real valid targets, and that "where is X?" is answered
        // from facts instead of asked back.
        private static void TestRequestGuidesTargetSemantics()
        {
            var request = RobotProgramDirector.BuildRequest("Bolt", string.Empty, KnownTargets, null, 12, 0.6f);
            Assert(request.SystemFrame.Contains("PLAYER always means your operator"),
                "the persona pins PLAYER to the operator");
            Assert(request.SystemFrame.Contains("robots, props, marker"),
                "the persona names robots/props/markers as valid targets");
            Assert(request.SystemFrame.Contains("never ask the operator where a known target is"),
                "the persona forbids asking where a known target is");
            Assert(request.DecisionGuidance != null && request.DecisionGuidance.Contains("AUTONOMOUS"),
                "the decision guidance explains AUTONOMOUS");
        }

        // The describe provider is wired into LiveFacts and re-invoked per call, so every turn sees fresh
        // positions; the static known-targets fact stays as the bare-name fallback.
        private static void TestDescribedTargetsFlowThroughLiveFacts()
        {
            var calls = 0;
            var request = RobotProgramDirector.BuildRequest("Bolt", string.Empty, KnownTargets, () =>
            {
                calls++;
                return new[] { "RED MARKER: a marker pad, " + calls + " m north of you" };
            }, 12, 0.6f);

            Assert(request.LiveFacts != null, "a describe provider wires LiveFacts");
            var first = request.LiveFacts!();
            var second = request.LiveFacts();
            Assert(calls == 2, "the provider is invoked once per LiveFacts call (fresh every turn)");
            Assert(first!["known-targets"].Contains("1 m north"), "the first turn carries the first snapshot");
            Assert(second!["known-targets"].Contains("2 m north"), "the next turn carries fresh positions");
            Assert(request.GroundTruthFacts!["known-targets"].Contains("RED MARKER"),
                "the static fact keeps the bare names as a fallback");
        }

        private static void TestChatNeverExits()
        {
            Assert(RobotProgramDirector.Parse("CHAT", "PLAYER", KnownTargets).IsChat, "CHAT keeps talking even with a target");
            Assert(RobotProgramDirector.Parse("", null, KnownTargets).IsChat, "an empty decision (failed turn) keeps talking");
            Assert(RobotProgramDirector.Parse("SING", "PLAYER", KnownTargets).IsChat, "an unexpected decision keeps talking");
        }

        private static void TestIdleNeedsNoTarget()
        {
            var result = RobotProgramDirector.Parse("IDLE", RobotProgramDirector.NoTarget, KnownTargets);
            Assert(!result.IsChat && result.Objective != null, "IDLE exits the chat");
            Assert(result.Objective!.Kind == RobotObjectiveKind.Idle, "IDLE programs an idle objective");
        }

        private static void TestAutonomousExitsWithoutObjective()
        {
            var result = RobotProgramDirector.Parse("AUTONOMOUS", RobotProgramDirector.NoTarget, KnownTargets);
            Assert(!result.IsChat && result.GoAutonomous, "AUTONOMOUS exits the chat as a set-free");
            Assert(result.Objective == null, "set-free carries no objective (the native brain takes over)");
        }

        private static void TestActionsMapToObjectives()
        {
            var goTo = RobotProgramDirector.Parse("GO_TO", "RED MARKER", KnownTargets);
            Assert(!goTo.IsChat && goTo.Objective!.Kind == RobotObjectiveKind.GoTo && goTo.Objective.TargetName == "RED MARKER",
                "GO_TO maps to a named go-to");

            var follow = RobotProgramDirector.Parse("FOLLOW", "PLAYER", KnownTargets);
            Assert(!follow.IsChat && follow.Objective!.Kind == RobotObjectiveKind.Follow && follow.Objective.TargetName == "PLAYER",
                "FOLLOW maps to a named follow");

            var patrol = RobotProgramDirector.Parse("PATROL", "CRATE", KnownTargets);
            Assert(!patrol.IsChat && patrol.Objective!.Kind == RobotObjectiveKind.Patrol && patrol.Objective.TargetName == "CRATE",
                "PATROL maps to a here<->target patrol");
        }

        private static void TestActionWithoutTargetDegradesToChat()
        {
            var result = RobotProgramDirector.Parse("GO_TO", RobotProgramDirector.NoTarget, KnownTargets);
            Assert(result.IsChat && result.Objective == null, "an action with NONE stays in chat");
            Assert(!string.IsNullOrEmpty(result.Problem), "the degraded turn surfaces a problem to show the player");
        }

        private static void TestUnknownTargetDegradesToChat()
        {
            var result = RobotProgramDirector.Parse("FOLLOW", "THE MOON", KnownTargets);
            Assert(result.IsChat && result.Objective == null, "a hallucinated target never programs the robot");
            Assert(!string.IsNullOrEmpty(result.Problem), "the hallucinated target surfaces a problem");
        }

        private static void TestTargetMatchingIsCaseInsensitive()
        {
            var result = RobotProgramDirector.Parse("go_to", "red marker", KnownTargets);
            Assert(!result.IsChat && result.Objective!.TargetName == "RED MARKER",
                "decision and target matching are case-insensitive and use the canonical name");
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
