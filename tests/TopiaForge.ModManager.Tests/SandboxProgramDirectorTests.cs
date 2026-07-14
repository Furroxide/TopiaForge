using System;
using TopiaForge.Mods;
using TopiaForge.Sandbox;

namespace TopiaForge.ModManager.Tests
{
    // Unit tests for the sandbox PROGRAM verb's pure brain: the conversation request shape (persona + facts + the
    // closed action/target/program sets) and the deterministic decision+target(+program+program_target) -> objective
    // parse, including the structural exit-chat rule (CHAT keeps talking; any accepted action exits), the safety
    // gate (an action without a real target degrades back to chat), and the REPROGRAM courier rules (robot-kind
    // recipients only, real payload programs only, no nesting, self-targeting guards). Mirrors ConversationDirectorTests.
    internal static class SandboxProgramDirectorTests
    {
        private static readonly string[] KnownTargets = { "PLAYER", "RED MARKER", "CRATE", "ROBOT 2" };
        private static readonly string[] RobotTargets = { "ROBOT 2" };
        private const string Self = "ROBOT 1";

        public static void Run()
        {
            TestRequestCarriesPersonaFactsAndClosedSets();
            TestRequestOffersSelfOnlyForProgramTarget();
            TestRequestWithNoTargetsStillOffersNone();
            TestRequestGuidesTargetSemantics();
            TestDescribedTargetsFlowThroughLiveFacts();
            TestChatNeverExits();
            TestIdleNeedsNoTarget();
            TestAutonomousExitsWithoutObjective();
            TestActionsMapToObjectives();
            TestFollowMissingTargetInfersKnownTargetsFromOperatorText();
            TestFollowMissingTargetInferenceStaysConservative();
            TestActionWithoutTargetDegradesToChat();
            TestUnknownTargetDegradesToChat();
            TestTargetMatchingIsCaseInsensitive();
            TestWanderWithoutTargetWandersHere();
            TestWanderWithTargetAnchorsToIt();
            TestWanderUnknownTargetDegradesToChat();
            TestFleeRequiresTarget();
            TestFleeUnknownTargetDegradesToChat();
            TestReprogramBuildsPayload();
            TestReprogramPayloadMayTargetSelfMessenger();
            TestReprogramRequiresRobotKindTarget();
            TestReprogramNeedsAProgram();
            TestReprogramPayloadNeedingTargetDegrades();
            TestReprogramNestedReprogramDegrades();
            TestReprogramPayloadCannotTargetRecipient();
            TestDescribeActivityStrings();
            TestEmoteForDecision();
            Console.WriteLine("All sandbox program director tests passed.");
        }

        private static void TestRequestCarriesPersonaFactsAndClosedSets()
        {
            var request = RobotProgramDirector.BuildRequest("Bolt", "FOLLOW PLAYER", KnownTargets, Self, null, 12, 0.6f);

            Assert(request.SystemFrame.Contains("Bolt"), "the persona carries the robot's name");
            Assert(request.SystemFrame.Contains("OPERATOR"), "the persona frames the human as the operator");
            Assert(request.MaxTurns == 12 && Math.Abs(request.Temperature - 0.6f) < 1e-6f, "turn/temperature knobs carry");
            Assert(request.Usage == "sandbox-program", "the backend usage label is set");

            Assert(request.GroundTruthFacts != null, "ground-truth facts exist");
            Assert(request.GroundTruthFacts!["current-program"] == "FOLLOW PLAYER", "the current program is authoritative");
            Assert(request.GroundTruthFacts["known-targets"].Contains("RED MARKER"), "the target vocabulary is authoritative");
            Assert(request.LiveFacts == null, "no describe provider -> no live facts");

            Assert(request.DecisionOptions.Count == 9 && request.DecisionOptions[0] == "CHAT",
                "the decision set is the nine actions with CHAT first");
            Assert(request.DecisionOptions[8] == "AUTONOMOUS", "AUTONOMOUS is the last offered decision");

            // At most three extra outputs (target, program, program_target): with the built-in reply + decision
            // that is the backend's 5-output ceiling. A fourth would 400 the whole turn ("Too many outputs").
            Assert(request.ExtraOutputs != null && request.ExtraOutputs.Count == 3,
                "three extra outputs: target, program, program_target");
            var target = request.ExtraOutputs![0];
            Assert(target.Name == RobotProgramDirector.TargetField, "the first extra field is the target");
            Assert(target.AllowedStrings != null && target.AllowedStrings.Count == KnownTargets.Length + 1
                && target.AllowedStrings[0] == RobotProgramDirector.NoTarget,
                "the target enum is NONE plus every known target");

            var program = request.ExtraOutputs[1];
            Assert(program.Name == RobotProgramDirector.ProgramField
                && program.AllowedStrings!.Count == RobotProgramDirector.ReprogramPrograms.Length + 1
                && program.AllowedStrings[0] == RobotProgramDirector.NoTarget,
                "the program enum is NONE plus the deliverable tasks");
        }

        // The plain target set never offers the robot itself (no "follow yourself"), but the delivered task's
        // target set does ("tell ROBOT 2 to follow you" — the messenger is the payload's target). And the program
        // set never offers REPROGRAM (no chain letters).
        private static void TestRequestOffersSelfOnlyForProgramTarget()
        {
            var request = RobotProgramDirector.BuildRequest("Bolt", string.Empty, KnownTargets, Self, null, 12, 0.6f);

            var targetOptions = request.ExtraOutputs![0].AllowedStrings!;
            var programOptions = request.ExtraOutputs[1].AllowedStrings!;
            var programTargetOptions = request.ExtraOutputs[2].AllowedStrings!;

            Assert(!Contains(targetOptions, Self), "the plain target set excludes the robot itself");
            Assert(Contains(programTargetOptions, Self), "the program_target set offers the robot itself");
            Assert(programTargetOptions.Count == targetOptions.Count + 1, "program_target is the target set plus self");
            Assert(!Contains(programOptions, "REPROGRAM"), "a delivered task can never be another REPROGRAM");
            Assert(Contains(programOptions, "WANDER") && Contains(programOptions, "FLEE"),
                "the new programs are deliverable tasks too");
        }

        private static void TestRequestWithNoTargetsStillOffersNone()
        {
            var request = RobotProgramDirector.BuildRequest("Bolt", string.Empty, Array.Empty<string>(), null, null, 12, 0.6f);
            Assert(request.GroundTruthFacts!["current-program"].Contains("NONE"), "no program reads as NONE");
            Assert(request.ExtraOutputs![0].AllowedStrings!.Count == 1, "with no targets the enum is just NONE");
            Assert(request.ExtraOutputs[2].AllowedStrings!.Count == 1, "with no self the program_target enum is just NONE");
        }

        // The follow-the-player bug fix: the persona must explain that PLAYER means the operator (and only for
        // "follow me"), that robots/props/markers are all real valid targets, and that "where is X?" is answered
        // from facts instead of asked back. The guidance must explain every offered decision.
        private static void TestRequestGuidesTargetSemantics()
        {
            var request = RobotProgramDirector.BuildRequest("Bolt", string.Empty, KnownTargets, Self, null, 12, 0.6f);
            Assert(request.SystemFrame.Contains("PLAYER always means your operator"),
                "the persona pins PLAYER to the operator");
            Assert(request.SystemFrame.Contains("robots, props, marker"),
                "the persona names robots/props/markers as valid targets");
            Assert(request.SystemFrame.Contains("never ask the operator where a known target is"),
                "the persona forbids asking where a known target is");
            Assert(request.SystemFrame.Contains("REPROGRAM"), "the persona mentions carrying tasks to other robots");
            Assert(request.DecisionGuidance != null && request.DecisionGuidance.Contains("AUTONOMOUS"),
                "the decision guidance explains AUTONOMOUS");
            Assert(request.DecisionGuidance!.Contains("WANDER") && request.DecisionGuidance.Contains("FLEE")
                && request.DecisionGuidance.Contains("REPROGRAM"),
                "the decision guidance explains the new actions");
            Assert(request.ExtraOutputs![0].Description.Contains("never use NONE"),
                "the target field tells accepted movement actions to use a real target");
        }

        // The describe provider is wired into LiveFacts and re-invoked per call, so every turn sees fresh
        // positions; the static known-targets fact stays as the bare-name fallback.
        private static void TestDescribedTargetsFlowThroughLiveFacts()
        {
            var calls = 0;
            var request = RobotProgramDirector.BuildRequest("Bolt", string.Empty, KnownTargets, Self, () =>
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
            Assert(Parse("CHAT", "PLAYER").IsChat, "CHAT keeps talking even with a target");
            Assert(Parse("", null).IsChat, "an empty decision (failed turn) keeps talking");
            Assert(Parse("SING", "PLAYER").IsChat, "an unexpected decision keeps talking");
        }

        private static void TestIdleNeedsNoTarget()
        {
            var result = Parse("IDLE", RobotProgramDirector.NoTarget);
            Assert(!result.IsChat && result.Objective != null, "IDLE exits the chat");
            Assert(result.Objective!.Kind == RobotObjectiveKind.Idle, "IDLE programs an idle objective");
        }

        private static void TestAutonomousExitsWithoutObjective()
        {
            var result = Parse("AUTONOMOUS", RobotProgramDirector.NoTarget);
            Assert(!result.IsChat && result.GoAutonomous, "AUTONOMOUS exits the chat as a set-free");
            Assert(result.Objective == null, "set-free carries no objective (the native brain takes over)");
        }

        private static void TestActionsMapToObjectives()
        {
            var goTo = Parse("GO_TO", "RED MARKER");
            Assert(!goTo.IsChat && goTo.Objective!.Kind == RobotObjectiveKind.GoTo && goTo.Objective.TargetName == "RED MARKER",
                "GO_TO maps to a named go-to");

            var follow = Parse("FOLLOW", "PLAYER");
            Assert(!follow.IsChat && follow.Objective!.Kind == RobotObjectiveKind.Follow && follow.Objective.TargetName == "PLAYER",
                "FOLLOW maps to a named follow");

            var patrol = Parse("PATROL", "CRATE");
            Assert(!patrol.IsChat && patrol.Objective!.Kind == RobotObjectiveKind.Patrol && patrol.Objective.TargetName == "CRATE",
                "PATROL maps to a here<->target patrol");
        }

        private static void TestFollowMissingTargetInfersKnownTargetsFromOperatorText()
        {
            AssertFollowFallback("follow me", "PLAYER");
            AssertFollowFallback("follow the player", "PLAYER");
            AssertFollowFallback("stay with me", "PLAYER");
            AssertFollowFallback("right behind me", "PLAYER");
            AssertFollowFallback("follow robot 2", "ROBOT 2");
            AssertFollowFallback("follow the red marker", "RED MARKER");
            AssertFollowFallback("follow crate", "CRATE");

            var numbered = RobotProgramDirector.Parse("FOLLOW", RobotProgramDirector.NoTarget, null, null,
                new[] { "CRATE", "CRATE 2" }, Array.Empty<string>(), Self, "follow crate 2");
            Assert(!numbered.IsChat && numbered.Objective!.TargetName == "CRATE 2",
                "a numbered target beats its shorter base name when the operator says the full name");
        }

        private static void TestFollowMissingTargetInferenceStaysConservative()
        {
            var ambiguous = Parse("FOLLOW", RobotProgramDirector.NoTarget, "follow me to crate");
            Assert(ambiguous.IsChat && ambiguous.Objective == null,
                "fallback does not guess when the operator text names multiple unrelated targets");

            var unknown = Parse("FOLLOW", RobotProgramDirector.NoTarget, "follow the moon");
            Assert(unknown.IsChat && unknown.Objective == null,
                "fallback does not invent a target outside the closed known-target set");

            var noFollowIntent = Parse("FOLLOW", RobotProgramDirector.NoTarget, "go to crate");
            Assert(noFollowIntent.IsChat && noFollowIntent.Objective == null,
                "fallback only repairs follow-like operator text");

            Assert(Parse("FOLLOW", RobotProgramDirector.NoTarget, "what is behind the red marker?").IsChat,
                "an incidental spatial question must not become a follow command");
            Assert(Parse("FOLLOW", RobotProgramDirector.NoTarget, "inspect robot 2's tail").IsChat,
                "an incidental body-part mention must not become a follow command");
            Assert(Parse("FOLLOW", RobotProgramDirector.NoTarget, "the crate's shadow looks odd").IsChat,
                "an incidental shadow mention must not become a follow command");
            Assert(Parse("FOLLOW", RobotProgramDirector.NoTarget, "what is following robot 2?").IsChat,
                "a descriptive use of following must not become an imperative command");
            Assert(Parse("FOLLOW", RobotProgramDirector.NoTarget, "what does follow red marker mean?").IsChat,
                "mentioning the follow verb in a question must not become an imperative command");
            Assert(Parse("FOLLOW", RobotProgramDirector.NoTarget, "why did you stay with robot 2?").IsChat,
                "describing past motion must not become an imperative command");

            var noPlayer = RobotProgramDirector.Parse("FOLLOW", RobotProgramDirector.NoTarget, null, null,
                new[] { "CRATE" }, Array.Empty<string>(), Self, "follow me");
            Assert(noPlayer.IsChat && noPlayer.Objective == null,
                "fallback cannot resolve PLAYER when PLAYER is not a known target");

            var absentEntity = RobotProgramDirector.Parse("FOLLOW", RobotProgramDirector.NoTarget, null, null,
                new[] { "PLAYER", "CRATE" }, Array.Empty<string>(), Self, "follow red marker");
            Assert(absentEntity.IsChat && absentEntity.Objective == null,
                "fallback cannot resolve a named entity that is absent from known targets");

            var structuredWins = Parse("FOLLOW", "CRATE", "follow robot 2");
            Assert(!structuredWins.IsChat && structuredWins.Objective!.TargetName == "CRATE",
                "a valid structured target is never overridden by text fallback");
        }

        private static void TestActionWithoutTargetDegradesToChat()
        {
            var result = Parse("GO_TO", RobotProgramDirector.NoTarget);
            Assert(result.IsChat && result.Objective == null, "an action with NONE stays in chat");
            Assert(!string.IsNullOrEmpty(result.Problem), "the degraded turn surfaces a problem to show the player");
        }

        private static void TestUnknownTargetDegradesToChat()
        {
            var result = Parse("FOLLOW", "THE MOON");
            Assert(result.IsChat && result.Objective == null, "a hallucinated target never programs the robot");
            Assert(!string.IsNullOrEmpty(result.Problem), "the hallucinated target surfaces a problem");
        }

        private static void TestTargetMatchingIsCaseInsensitive()
        {
            var result = Parse("go_to", "red marker");
            Assert(!result.IsChat && result.Objective!.TargetName == "RED MARKER",
                "decision and target matching are case-insensitive and use the canonical name");
        }

        private static void TestWanderWithoutTargetWandersHere()
        {
            var none = Parse("WANDER", RobotProgramDirector.NoTarget);
            Assert(!none.IsChat && none.Objective!.Kind == RobotObjectiveKind.Wander && none.Objective.TargetName == null,
                "WANDER with NONE roams right where the robot stands");

            var blank = Parse("WANDER", null);
            Assert(!blank.IsChat && blank.Objective!.Kind == RobotObjectiveKind.Wander,
                "WANDER with a blank target (failed field) still roams here");
        }

        private static void TestWanderWithTargetAnchorsToIt()
        {
            var result = Parse("WANDER", "red marker");
            Assert(!result.IsChat && result.Objective!.Kind == RobotObjectiveKind.Wander
                && result.Objective.TargetName == "RED MARKER",
                "WANDER with a known target anchors the roam to it (canonical name)");
        }

        private static void TestWanderUnknownTargetDegradesToChat()
        {
            var result = Parse("WANDER", "THE MOON");
            Assert(result.IsChat && !string.IsNullOrEmpty(result.Problem),
                "a hallucinated wander anchor degrades to chat instead of silently wandering here");
        }

        private static void TestFleeRequiresTarget()
        {
            var none = Parse("FLEE", RobotProgramDirector.NoTarget);
            Assert(none.IsChat && !string.IsNullOrEmpty(none.Problem), "FLEE with NONE stays in chat with a nudge");

            var result = Parse("FLEE", "PLAYER");
            Assert(!result.IsChat && result.Objective!.Kind == RobotObjectiveKind.Flee
                && result.Objective.TargetName == "PLAYER",
                "FLEE with a known target maps to a flee objective");
        }

        private static void TestFleeUnknownTargetDegradesToChat()
        {
            var result = Parse("FLEE", "THE VOID");
            Assert(result.IsChat && !string.IsNullOrEmpty(result.Problem), "a hallucinated threat degrades to chat");
        }

        private static void TestReprogramBuildsPayload()
        {
            var result = ParseReprogram("robot 2", "FOLLOW", "PLAYER");
            Assert(!result.IsChat && result.Objective != null, "a well-formed REPROGRAM exits the chat");
            Assert(result.Objective!.Kind == RobotObjectiveKind.Reprogram && result.Objective.TargetName == "ROBOT 2",
                "the courier objective targets the canonical recipient");
            Assert(result.Objective.Payload != null && result.Objective.Payload!.Kind == RobotObjectiveKind.Follow
                && result.Objective.Payload.TargetName == "PLAYER",
                "the payload carries the delivered task and its target");

            var idle = ParseReprogram("ROBOT 2", "idle", RobotProgramDirector.NoTarget);
            Assert(!idle.IsChat && idle.Objective!.Payload!.Kind == RobotObjectiveKind.Idle,
                "an IDLE payload needs no target");

            var wanderHere = ParseReprogram("ROBOT 2", "WANDER", RobotProgramDirector.NoTarget);
            Assert(!wanderHere.IsChat && wanderHere.Objective!.Payload!.Kind == RobotObjectiveKind.Wander
                && wanderHere.Objective.Payload.TargetName == null,
                "a WANDER payload with NONE roams wherever the recipient stands");
        }

        private static void TestReprogramPayloadMayTargetSelfMessenger()
        {
            var result = ParseReprogram("ROBOT 2", "FOLLOW", "robot 1");
            Assert(!result.IsChat && result.Objective != null, "the messenger is a legitimate payload target");
            Assert(result.Objective!.Payload!.Kind == RobotObjectiveKind.Follow
                && result.Objective.Payload.TargetName == Self,
                "\"tell it to follow you\" programs the recipient to follow the messenger");
        }

        private static void TestReprogramRequiresRobotKindTarget()
        {
            Assert(ParseReprogram("CRATE", "FOLLOW", "PLAYER").IsChat, "a prop cannot be reprogrammed");
            Assert(ParseReprogram("PLAYER", "FOLLOW", "RED MARKER").IsChat, "the operator cannot be reprogrammed");
            Assert(ParseReprogram(RobotProgramDirector.NoTarget, "FOLLOW", "PLAYER").IsChat,
                "REPROGRAM without a recipient stays in chat");
            Assert(!string.IsNullOrEmpty(ParseReprogram("CRATE", "FOLLOW", "PLAYER").Problem),
                "the non-robot recipient surfaces a problem");
        }

        private static void TestReprogramNeedsAProgram()
        {
            Assert(ParseReprogram("ROBOT 2", RobotProgramDirector.NoTarget, "PLAYER").IsChat,
                "REPROGRAM with no delivered task stays in chat");
            Assert(ParseReprogram("ROBOT 2", "", "PLAYER").IsChat, "an empty program stays in chat");
            Assert(ParseReprogram("ROBOT 2", "SING", "PLAYER").IsChat, "an invented program stays in chat");
            Assert(!string.IsNullOrEmpty(ParseReprogram("ROBOT 2", "SING", "PLAYER").Problem),
                "the missing program surfaces a problem");
        }

        private static void TestReprogramPayloadNeedingTargetDegrades()
        {
            Assert(ParseReprogram("ROBOT 2", "FOLLOW", RobotProgramDirector.NoTarget).IsChat,
                "a FOLLOW payload without a target stays in chat");
            Assert(ParseReprogram("ROBOT 2", "GO_TO", "THE MOON").IsChat,
                "a payload with a hallucinated target stays in chat");
        }

        private static void TestReprogramNestedReprogramDegrades()
        {
            var result = ParseReprogram("ROBOT 2", "REPROGRAM", "PLAYER");
            Assert(result.IsChat && !string.IsNullOrEmpty(result.Problem),
                "a chain-letter REPROGRAM payload degrades to chat (and never reaches the throwing factory)");
        }

        private static void TestReprogramPayloadCannotTargetRecipient()
        {
            var follow = ParseReprogram("ROBOT 2", "FOLLOW", "ROBOT 2");
            Assert(follow.IsChat && !string.IsNullOrEmpty(follow.Problem),
                "the recipient cannot follow itself — degrade with a nudge");

            var wander = ParseReprogram("ROBOT 2", "WANDER", "ROBOT 2");
            Assert(!wander.IsChat && wander.Objective!.Payload!.Kind == RobotObjectiveKind.Wander
                && wander.Objective.Payload.TargetName == null,
                "\"wander around yourself\" normalises to wandering in place");
        }

        private static void TestDescribeActivityStrings()
        {
            Assert(RobotProgramDirector.DescribeActivity(RobotBrainMode.Autonomous, null) == "currently: thinking for itself",
                "an autonomous robot thinks for itself regardless of handles");
            Assert(RobotProgramDirector.DescribeActivity(RobotBrainMode.Dormant, null) == "currently: no program",
                "a dormant robot with no handle has no program");

            Assert(Describe(RobotObjective.Follow("PLAYER"), RobotObjectiveState.Seeking) == "currently: FOLLOW PLAYER (moving)",
                "a seeking objective reads as moving");
            Assert(Describe(RobotObjective.GoTo("CRATE"), RobotObjectiveState.Arrived) == "currently: GO TO CRATE (arrived)",
                "an arrived objective reads as arrived");
            Assert(Describe(RobotObjective.Wander(), RobotObjectiveState.Dwelling) == "currently: WANDER (pausing)",
                "a dwelling objective reads as pausing");
            Assert(Describe(RobotObjective.Flee("PLAYER"), RobotObjectiveState.TargetMissing)
                == "currently: FLEE FROM PLAYER (waiting — target missing)",
                "a parked objective reads as waiting");
            Assert(Describe(RobotObjective.Reprogram("ROBOT 2", RobotObjective.Follow("PLAYER")), RobotObjectiveState.Delivered)
                == "currently: REPROGRAM ROBOT 2: FOLLOW PLAYER (delivered)",
                "a delivered courier reads as delivered");
            Assert(Describe(RobotObjective.Idle(), RobotObjectiveState.Idle) == "currently: IDLE",
                "an idle objective carries no suffix");
        }

        // The chat emote is derived from the decision (no output field spent), so it must map the accepted
        // actions to a face and leave neutral/unknown decisions expressionless.
        private static void TestEmoteForDecision()
        {
            Assert(RobotProgramDirector.EmoteForDecision("CHAT") == ":thinking_face:", "CHAT mulls it over");
            Assert(RobotProgramDirector.EmoteForDecision("follow") == ":thumbsup:", "an accepted move is a thumbs-up (case-insensitive)");
            Assert(RobotProgramDirector.EmoteForDecision("WANDER") == ":thumbsup:", "WANDER is a thumbs-up too");
            Assert(RobotProgramDirector.EmoteForDecision("REPROGRAM") == ":wave:", "REPROGRAM waves as it heads off");
            Assert(RobotProgramDirector.EmoteForDecision("AUTONOMOUS") == ":wave:", "AUTONOMOUS waves goodbye");
            Assert(RobotProgramDirector.EmoteForDecision("IDLE") == null, "IDLE keeps a neutral face");
            Assert(RobotProgramDirector.EmoteForDecision("SING") == null && RobotProgramDirector.EmoteForDecision(null) == null,
                "unknown/empty decisions leave the face unchanged");
        }

        private static string Describe(RobotObjective objective, RobotObjectiveState state)
        {
            return RobotProgramDirector.DescribeActivity(RobotBrainMode.Dormant, new FakeHandle(objective, state));
        }

        // The common-case parse: no REPROGRAM sub-fields in play.
        private static ProgramParseResult Parse(string? decision, string? target, string? operatorText = null)
        {
            return RobotProgramDirector.Parse(decision, target, null, null, KnownTargets, RobotTargets, Self, operatorText);
        }

        private static void AssertFollowFallback(string operatorText, string expectedTarget)
        {
            var result = Parse("FOLLOW", RobotProgramDirector.NoTarget, operatorText);
            Assert(!result.IsChat && result.Objective != null, operatorText + " should program a follow objective");
            Assert(result.Objective!.Kind == RobotObjectiveKind.Follow && result.Objective.TargetName == expectedTarget,
                operatorText + " should resolve to " + expectedTarget);
        }

        private static ProgramParseResult ParseReprogram(string? target, string? program, string? programTarget)
        {
            return RobotProgramDirector.Parse("REPROGRAM", target, program, programTarget, KnownTargets, RobotTargets, Self);
        }

        private static bool Contains(System.Collections.Generic.IReadOnlyList<string> options, string wanted)
        {
            foreach (var option in options)
            {
                if (string.Equals(option, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private sealed class FakeHandle : IRobotObjectiveHandle
        {
            public FakeHandle(RobotObjective objective, RobotObjectiveState state)
            {
                Objective = objective;
                State = state;
            }

            public RobotObjective Objective { get; }

            public RobotObjectiveState State { get; }

            public int WaypointIndex => 0;

            public void Cancel()
            {
            }
        }
    }
}
