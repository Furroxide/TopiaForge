using System;
using System.Collections.Generic;
using Robotopia.Mods;

namespace Robotopia.Sandbox
{
    // The "program a robot by talking to it" brain of the sandbox PROGRAM verb: builds the multi-turn conversation
    // request (operator persona + the authoritative facts + the closed action/target sets), and deterministically
    // parses the robot's chosen action+target into a RobotObjective. The exit-chat signal is structural: the CHAT
    // decision means "still talking"; any other decision means the robot accepted a program and the chat closes.
    // The parse gates misfires safely — an action without a usable target degrades back to chat with a nudge, so
    // the model can never program the robot against a place it invented. Targets are presented as per-turn
    // "who/what/where" facts (kind + live direction/distance) so the robot can find any registered entity instead
    // of guessing what a bare name means. Pure (Unity-free) so it unit-tests on net8.0 alongside
    // ConversationDirector.

    // What one completed turn means for the chat flow: keep talking (optionally with a problem to surface), exit
    // the chat and run the parsed objective, or exit the chat and hand the robot back to its own native brain.
    internal sealed class ProgramParseResult
    {
        private ProgramParseResult(bool isChat, RobotObjective? objective, string? problem, bool goAutonomous)
        {
            IsChat = isChat;
            Objective = objective;
            Problem = problem;
            GoAutonomous = goAutonomous;
        }

        public bool IsChat { get; }
        public RobotObjective? Objective { get; }
        public string? Problem { get; }

        /// <summary>The operator set the robot free: exit the chat and re-enable the native autonomous brain.</summary>
        public bool GoAutonomous { get; }

        public static ProgramParseResult Chat(string? problem = null)
        {
            return new ProgramParseResult(true, null, problem, false);
        }

        public static ProgramParseResult Program(RobotObjective objective)
        {
            return new ProgramParseResult(false, objective, null, false);
        }

        public static ProgramParseResult Autonomous()
        {
            return new ProgramParseResult(false, null, null, true);
        }
    }

    internal static class RobotProgramDirector
    {
        public const string TargetField = "target";
        public const string NoTarget = "NONE";

        public static readonly string[] DecisionOptions = { "CHAT", "IDLE", "GO_TO", "FOLLOW", "PATROL", "AUTONOMOUS" };

        // Build the conversation request for one programmable sandbox robot: who it is, what is authoritatively
        // true (its current program and the only places/things that exist), and the closed action + target sets.
        // The optional describeTargets provider is invoked at the start of EVERY turn so the known-targets fact
        // carries fresh "kind + direction/distance" lines; without it the fact degrades to the bare name list.
        public static RobotConversationRequest BuildRequest(
            string robotName,
            string currentProgram,
            IReadOnlyList<string> targetNames,
            Func<IReadOnlyList<string>>? describeTargets,
            int maxTurns,
            float temperature)
        {
            var name = string.IsNullOrWhiteSpace(robotName) ? "Robot" : robotName;
            var frame =
                "You are " + name + ", a friendly service robot in a creator sandbox. The human talking to you is " +
                "your OPERATOR — they can program and re-program what you do by talking to you, and you genuinely " +
                "want to help. Stay fully in character as this robot — never mention being an AI, a model, code, or " +
                "a game. Chat naturally, ask for clarification when a request is vague, and when the operator gives " +
                "you a task you understand and accept, take it: choosing any action other than CHAT immediately ends " +
                "the conversation and you go do it. Only act on tasks the operator actually asked for. " +
                "The target PLAYER always means your operator, the human talking to you — choose it only when they " +
                "mean themselves (\"follow me\", \"come to me\"). Every other known target — robots, props, marker " +
                "pads — is a real thing in the world and an equally valid target to GO_TO, FOLLOW, or PATROL to. " +
                "When the operator asks where something is, answer from your known-targets facts; never ask the " +
                "operator where a known target is.";

            var facts = new Dictionary<string, string>
            {
                ["your-name"] = name,
                ["current-program"] = string.IsNullOrWhiteSpace(currentProgram) ? "NONE (idle)" : currentProgram,
                ["known-targets"] = KnownTargetsFact(targetNames, null),
                ["operator"] = "the human you are talking to",
            };

            var targetOptions = new List<string> { NoTarget };
            if (targetNames != null)
            {
                foreach (var targetName in targetNames)
                {
                    if (!string.IsNullOrWhiteSpace(targetName))
                    {
                        targetOptions.Add(targetName);
                    }
                }
            }

            return new RobotConversationRequest(frame, DecisionOptions)
            {
                GroundTruthFacts = facts,
                LiveFacts = describeTargets == null
                    ? null
                    : () => new Dictionary<string, string>
                    {
                        ["known-targets"] = KnownTargetsFact(null, describeTargets()),
                    },
                MaxTurns = maxTurns,
                Temperature = temperature,
                Usage = "sandbox-program",
                ReplyGuidance = "Your short, in-character spoken line back to the operator (max ~16 words).",
                DecisionGuidance =
                    "CHAT = you are still talking, need clarification, or were not given a task yet. Pick " +
                    "IDLE/GO_TO/FOLLOW/PATROL/AUTONOMOUS ONLY when the operator has given you that task and you " +
                    "accept it — choosing one ends the conversation and you go do it immediately. IDLE = stand " +
                    "down and wait; GO_TO = walk to the target once; FOLLOW = keep following the target; PATROL = " +
                    "walk back and forth between where you are now and the target; AUTONOMOUS = the operator told " +
                    "you to be free / think for yourself — you leave operator control and act on your own.",
                MaxReplyChars = 160,
                ExtraOutputs = new[]
                {
                    new BrainOutputField(
                        TargetField,
                        "The known target your action applies to. NONE when you chose CHAT, IDLE, or AUTONOMOUS.",
                        BrainFieldType.String,
                        targetOptions),
                },
            };
        }

        // The known-targets fact body: described lines when available ("ROBOT 2: another robot, 8 m north-east of
        // you"), else the bare name list, else the empty-world text.
        private static string KnownTargetsFact(IReadOnlyList<string>? targetNames, IReadOnlyList<string>? described)
        {
            if (described != null && described.Count > 0)
            {
                return string.Join("; ", described);
            }

            if (targetNames != null && targetNames.Count > 0)
            {
                return string.Join(", ", targetNames);
            }

            return "none yet — nothing has been spawned for you to target";
        }

        // Deterministically map a completed turn's decision+target to an objective. Unknown/absent targets never
        // program the robot — they degrade back to chat with a problem the UI can surface, so a model that names a
        // place that does not exist just gets nudged to try again.
        public static ProgramParseResult Parse(string? decision, string? target, IReadOnlyList<string> knownTargets)
        {
            var action = (decision ?? string.Empty).Trim().ToUpperInvariant();
            switch (action)
            {
                case "IDLE":
                    return ProgramParseResult.Program(RobotObjective.Idle());
                case "AUTONOMOUS":
                    return ProgramParseResult.Autonomous();
                case "GO_TO":
                case "FOLLOW":
                case "PATROL":
                    break;
                default:
                    // CHAT, empty (failed turn), or anything unexpected — keep talking.
                    return ProgramParseResult.Chat();
            }

            var resolved = ResolveTarget(target, knownTargets);
            if (resolved == null)
            {
                return ProgramParseResult.Chat("It needs a real target — place a marker or name one it knows.");
            }

            switch (action)
            {
                case "GO_TO":
                    return ProgramParseResult.Program(RobotObjective.GoTo(resolved));
                case "FOLLOW":
                    return ProgramParseResult.Program(RobotObjective.Follow(resolved));
                default:
                    return ProgramParseResult.Program(RobotObjective.PatrolTo(resolved));
            }
        }

        private static string? ResolveTarget(string? target, IReadOnlyList<string> knownTargets)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            var wanted = target!.Trim();
            if (string.Equals(wanted, NoTarget, StringComparison.OrdinalIgnoreCase) || knownTargets == null)
            {
                return null;
            }

            foreach (var known in knownTargets)
            {
                if (string.Equals(known, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }

            return null;
        }
    }
}
