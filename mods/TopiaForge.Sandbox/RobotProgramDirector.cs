using System;
using System.Collections.Generic;
using System.Text;
using TopiaForge.Mods;

namespace TopiaForge.Sandbox
{
    // The "program a robot by talking to it" brain of the sandbox PROGRAM verb: builds the multi-turn conversation
    // request (operator persona + the authoritative facts + the closed action/target sets), and deterministically
    // parses the robot's chosen action+target — plus, for REPROGRAM, the delivered task and its target — into a
    // RobotObjective. The exit-chat signal is structural: the CHAT
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
        public const string ProgramField = "program";
        public const string ProgramTargetField = "program_target";
        public const string NoTarget = "NONE";

        public static readonly string[] DecisionOptions =
            { "CHAT", "IDLE", "GO_TO", "FOLLOW", "PATROL", "WANDER", "FLEE", "REPROGRAM", "AUTONOMOUS" };

        // The closed set of tasks a REPROGRAM decision may deliver — every program except REPROGRAM itself
        // (couriers deliver programs, not chain letters; RobotObjective.Reprogram would throw on nesting anyway).
        public static readonly string[] ReprogramPrograms = { "IDLE", "GO_TO", "FOLLOW", "PATROL", "WANDER", "FLEE" };

        // Build the conversation request for one programmable sandbox robot: who it is, what is authoritatively
        // true (its current program and the only places/things that exist), and the closed action + target sets.
        // The optional describeTargets provider is invoked at the start of EVERY turn so the known-targets fact
        // carries fresh "kind + direction/distance" lines; without it the fact degrades to the bare name list.
        public static RobotConversationRequest BuildRequest(
            string robotName,
            string currentProgram,
            IReadOnlyList<string> targetNames,
            string? selfTargetName,
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
                "You can also carry a task to another robot: choose REPROGRAM and you will physically walk over " +
                "and reprogram it yourself. " +
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

            // The delivered task may target the messenger itself — "tell ROBOT 2 to follow you" programs ROBOT 2
            // to follow THIS robot — so the program_target set is the offered set plus the robot's own name (which
            // the caller keeps out of the plain target set: a robot never targets itself directly).
            var programTargetOptions = new List<string>(targetOptions);
            if (!string.IsNullOrWhiteSpace(selfTargetName))
            {
                var self = selfTargetName!.Trim();
                var present = false;
                foreach (var option in programTargetOptions)
                {
                    if (string.Equals(option, self, StringComparison.OrdinalIgnoreCase))
                    {
                        present = true;
                        break;
                    }
                }

                if (!present)
                {
                    programTargetOptions.Add(self);
                }
            }

            var programOptions = new List<string> { NoTarget };
            programOptions.AddRange(ReprogramPrograms);

            return new RobotConversationRequest(
                frame,
                DecisionOptions,
                groundTruthFacts: facts,
                liveFacts: describeTargets == null
                    ? null
                    : () => new Dictionary<string, string>
                    {
                        ["known-targets"] = KnownTargetsFact(null, describeTargets()),
                    },
                maxTurns: maxTurns,
                temperature: temperature,
                usage: "sandbox-program",
                replyGuidance: "Your short, in-character spoken line back to the operator (max ~16 words).",
                decisionGuidance:
                    "CHAT = you are still talking, need clarification, or were not given a task yet. Pick any " +
                    "other decision ONLY when the operator has given you that task and you accept it — choosing " +
                    "one ends the conversation and you go do it immediately. IDLE = stand down and wait; GO_TO = " +
                    "walk to the target once; FOLLOW = keep following the target; PATROL = walk back and forth " +
                    "between where you are now and the target; WANDER = roam around near the target, or around " +
                    "right here when target is NONE; FLEE = keep away from the target, running off whenever it " +
                    "comes close; REPROGRAM = walk over to the target robot and give IT a new task — put that " +
                    "task in program and that task's target in program_target; AUTONOMOUS = the operator told " +
                    "you to be free / think for yourself — you leave operator control and act on your own.",
                maxReplyChars: 160,
                // At most three extra outputs: the RoboAPI /agent/check3 backend caps a request at FIVE output
                // fields total, and ConversationPrompt always adds the built-in `reply` + `decision`. A fourth
                // extra (e.g. a per-turn emote field) tips it to six and the backend rejects the whole turn with
                // "Too many outputs: max 5" — which surfaces to the player as "brain unreachable". The robot's
                // facial expression is instead derived deterministically from its chosen decision (see
                // EmoteForDecision), costing no output field.
                extraOutputs: new[]
                {
                    new BrainOutputField(
                        TargetField,
                        "The known target your action applies to. Use a real known target for GO_TO, FOLLOW, " +
                        "PATROL, FLEE, and for the REPROGRAM recipient; never use NONE for those accepted " +
                        "actions. Use PLAYER when the operator means themselves (follow me, stay with me, " +
                        "follow the player). NONE only for CHAT, IDLE, AUTONOMOUS, or WANDER right here.",
                        BrainFieldType.String,
                        targetOptions),
                    new BrainOutputField(
                        ProgramField,
                        "REPROGRAM only: the task you give the other robot. NONE for every other decision.",
                        BrainFieldType.String,
                        programOptions),
                    new BrainOutputField(
                        ProgramTargetField,
                        "REPROGRAM only: what the delivered task applies to — it may be you yourself (\"tell it " +
                        "to follow you\" means it follows YOU). NONE otherwise.",
                        BrainFieldType.String,
                        programTargetOptions),
                });
        }

        // The robot's facial expression for a turn, derived from the decision it just made — a free stand-in for
        // an LLM-chosen emote field (which would push the request past the backend's 5-output cap). Returns null
        // when no face fits (the caller then leaves the current expression alone). Discord-style shortcodes;
        // SetEmote is best-effort, so an unrecognised code simply no-ops.
        public static string? EmoteForDecision(string? decision)
        {
            switch ((decision ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "CHAT":
                    return ":thinking_face:"; // still mulling it over
                case "GO_TO":
                case "FOLLOW":
                case "PATROL":
                case "WANDER":
                    return ":thumbsup:";      // on it
                case "REPROGRAM":
                case "AUTONOMOUS":
                    return ":wave:";          // heading off
                default:
                    return null;              // IDLE / FLEE / unknown — leave the face as-is
            }
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

        // Deterministically map a completed turn's decision+target(+program+program_target) to an objective.
        // Unknown/absent targets never program the robot — they degrade back to chat with a problem the UI can
        // surface, so a model that names a place that does not exist just gets nudged to try again. Robot-kind
        // knowledge arrives purely as the pre-computed robotTargets subset (REPROGRAM recipients must be robots);
        // selfTargetName is the robot's own registered name, resolvable ONLY for the delivered task's target.
        public static ProgramParseResult Parse(
            string? decision,
            string? target,
            string? program,
            string? programTarget,
            IReadOnlyList<string> knownTargets,
            IReadOnlyList<string> robotTargets,
            string? selfTargetName,
            string? operatorText = null)
        {
            var action = (decision ?? string.Empty).Trim().ToUpperInvariant();
            switch (action)
            {
                case "IDLE":
                    return ProgramParseResult.Program(RobotObjective.Idle());
                case "AUTONOMOUS":
                    return ProgramParseResult.Autonomous();
                case "WANDER":
                    {
                        // No/NONE target roams right here; a known target anchors the roam to it. A name the model
                        // invented degrades to chat rather than silently wandering somewhere unintended.
                        if (IsNone(target))
                        {
                            return ProgramParseResult.Program(RobotObjective.Wander());
                        }

                        var anchor = ResolveTarget(target, knownTargets);
                        return anchor == null
                            ? ProgramParseResult.Chat("It needs a target it knows to wander near — or just \"wander here\".")
                            : ProgramParseResult.Program(RobotObjective.Wander(anchor));
                    }

                case "FLEE":
                    {
                        var threat = ResolveTarget(target, knownTargets);
                        return threat == null
                            ? ProgramParseResult.Chat("It needs a real target to run from — name one it knows.")
                            : ProgramParseResult.Program(RobotObjective.Flee(threat));
                    }

                case "REPROGRAM":
                    return ParseReprogram(target, program, programTarget, knownTargets, robotTargets, selfTargetName);
                case "GO_TO":
                case "FOLLOW":
                case "PATROL":
                    break;
                default:
                    // CHAT, empty (failed turn), or anything unexpected — keep talking.
                    return ProgramParseResult.Chat();
            }

            var resolved = ResolveTarget(target, knownTargets);
            if (resolved == null && action == "FOLLOW" && IsNone(target))
            {
                resolved = InferFollowTarget(operatorText, knownTargets);
            }

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

        // The REPROGRAM decision: recipient must be a robot the messenger knows; the delivered task must be a real
        // program (never another REPROGRAM — no chain letters); the task's target resolves against the offered set
        // plus the messenger itself. Every misfire degrades to chat with a nudge, so the courier objective that
        // reaches RobotKit is always well-formed.
        private static ProgramParseResult ParseReprogram(
            string? target,
            string? program,
            string? programTarget,
            IReadOnlyList<string> knownTargets,
            IReadOnlyList<string> robotTargets,
            string? selfTargetName)
        {
            var recipient = ResolveTarget(target, robotTargets);
            if (recipient == null)
            {
                return ProgramParseResult.Chat("It can only reprogram another robot — name one it knows.");
            }

            var task = (program ?? string.Empty).Trim().ToUpperInvariant();
            if (Array.IndexOf(ReprogramPrograms, task) < 0)
            {
                return ProgramParseResult.Chat("Say what the other robot should do — like follow you or go to a marker.");
            }

            var taskTarget = ResolveTarget(programTarget, knownTargets);
            if (taskTarget == null && !IsNone(programTarget) && !string.IsNullOrWhiteSpace(selfTargetName)
                && string.Equals(programTarget!.Trim(), selfTargetName!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                taskTarget = selfTargetName!.Trim(); // "tell it to follow you" — the messenger is the task's target
            }

            if (taskTarget != null && string.Equals(taskTarget, recipient, StringComparison.OrdinalIgnoreCase))
            {
                // A robot cannot follow/flee/go to itself; "wander around yourself" just means wander in place.
                if (task == "WANDER")
                {
                    return ProgramParseResult.Program(RobotObjective.Reprogram(recipient, RobotObjective.Wander()));
                }

                return ProgramParseResult.Chat("The other robot can't target itself — give its task a different target.");
            }

            RobotObjective payload;
            switch (task)
            {
                case "IDLE":
                    payload = RobotObjective.Idle();
                    break;
                case "WANDER":
                    if (IsNone(programTarget))
                    {
                        payload = RobotObjective.Wander();
                    }
                    else if (taskTarget != null)
                    {
                        payload = RobotObjective.Wander(taskTarget);
                    }
                    else
                    {
                        return ProgramParseResult.Chat("It doesn't know that place — pick a target it knows for the wander.");
                    }

                    break;
                default:
                    if (taskTarget == null)
                    {
                        return ProgramParseResult.Chat("The delivered task needs a real target — name one it knows.");
                    }

                    payload = task == "GO_TO" ? RobotObjective.GoTo(taskTarget)
                        : task == "FOLLOW" ? RobotObjective.Follow(taskTarget)
                        : task == "PATROL" ? RobotObjective.PatrolTo(taskTarget)
                        : RobotObjective.Flee(taskTarget);
                    break;
            }

            return ProgramParseResult.Program(RobotObjective.Reprogram(recipient, payload));
        }

        // A compact "currently: FOLLOW PLAYER (moving)" line for per-turn awareness facts and roster badges: what
        // a robot is doing right now, phrased for both an LLM ground-truth fact and a HUD label. Pure.
        public static string DescribeActivity(RobotBrainMode brainMode, IRobotObjectiveHandle? handle)
        {
            if (brainMode == RobotBrainMode.Autonomous)
            {
                return "currently: thinking for itself";
            }

            if (handle == null)
            {
                return "currently: no program";
            }

            return "currently: " + handle.Objective.Describe() + StateSuffix(handle.State);
        }

        private static string StateSuffix(RobotObjectiveState state)
        {
            switch (state)
            {
                case RobotObjectiveState.Seeking:
                    return " (moving)";
                case RobotObjectiveState.Arrived:
                    return " (arrived)";
                case RobotObjectiveState.Dwelling:
                    return " (pausing)";
                case RobotObjectiveState.TargetMissing:
                    return " (waiting — target missing)";
                case RobotObjectiveState.Delivered:
                    return " (delivered)";
                default:
                    return string.Empty;
            }
        }

        private static bool IsNone(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                || string.Equals(value!.Trim(), NoTarget, StringComparison.OrdinalIgnoreCase);
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

        private static string? InferFollowTarget(string? operatorText, IReadOnlyList<string> knownTargets)
        {
            if (string.IsNullOrWhiteSpace(operatorText) || knownTargets == null || knownTargets.Count == 0)
            {
                return null;
            }

            var text = NormalizePhrase(operatorText);
            if (text.Length == 0 || !HasFollowIntent(text))
            {
                return null;
            }

            var matches = new List<TargetMatch>();
            var player = ResolveTarget("PLAYER", knownTargets);
            if (player != null && MentionsOperatorAsFollowTarget(text))
            {
                AddTargetMatch(matches, player, "ME");
            }

            foreach (var known in knownTargets)
            {
                var phrase = NormalizePhrase(known);
                if (phrase.Length > 0 && ContainsWholePhrase(text, phrase))
                {
                    AddTargetMatch(matches, known, phrase);
                }
            }

            return PickUnambiguousTarget(matches);
        }

        private static bool HasFollowIntent(string normalizedText)
        {
            // Recovery is deliberately narrower than general language understanding: it only repairs a
            // FOLLOW/NONE model response when the operator text reads like a direct movement instruction.
            // Descriptive questions such as "what is behind X?" must stay chat, never become a program.
            return StartsWithWholePhrase(normalizedText, "FOLLOW")
                || StartsWithWholePhrase(normalizedText, "PLEASE FOLLOW")
                || StartsWithWholePhrase(normalizedText, "GO FOLLOW")
                || StartsWithWholePhrase(normalizedText, "STAY WITH")
                || StartsWithWholePhrase(normalizedText, "STICK WITH")
                || StartsWithWholePhrase(normalizedText, "KEEP UP WITH")
                || StartsWithWholePhrase(normalizedText, "STAY CLOSE TO")
                || StartsWithWholePhrase(normalizedText, "KEEP CLOSE TO")
                || StartsWithWholePhrase(normalizedText, "RIGHT BEHIND")
                || StartsWithWholePhrase(normalizedText, "STAY RIGHT BEHIND")
                || StartsWithWholePhrase(normalizedText, "KEEP RIGHT BEHIND")
                || ContainsWholePhrase(normalizedText, "CAN YOU FOLLOW")
                || ContainsWholePhrase(normalizedText, "COULD YOU FOLLOW")
                || ContainsWholePhrase(normalizedText, "WOULD YOU FOLLOW")
                || ContainsWholePhrase(normalizedText, "I WANT YOU TO FOLLOW")
                || ContainsWholePhrase(normalizedText, "I NEED YOU TO FOLLOW");
        }

        private static bool MentionsOperatorAsFollowTarget(string normalizedText)
        {
            return ContainsWholePhrase(normalizedText, "ME")
                || ContainsWholePhrase(normalizedText, "MYSELF")
                || ContainsWholePhrase(normalizedText, "OPERATOR")
                || ContainsWholePhrase(normalizedText, "HUMAN");
        }

        private static void AddTargetMatch(List<TargetMatch> matches, string target, string phrase)
        {
            if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(phrase))
            {
                return;
            }

            foreach (var match in matches)
            {
                if (string.Equals(match.Target, target, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            matches.Add(new TargetMatch(target, phrase));
        }

        private static string? PickUnambiguousTarget(List<TargetMatch> matches)
        {
            if (matches.Count == 0)
            {
                return null;
            }

            if (matches.Count == 1)
            {
                return matches[0].Target;
            }

            matches.Sort((left, right) => right.Phrase.Length.CompareTo(left.Phrase.Length));
            var best = matches[0];
            for (var index = 1; index < matches.Count; index++)
            {
                var other = matches[index];
                if (best.Phrase.Length == other.Phrase.Length || !ContainsWholePhrase(best.Phrase, other.Phrase))
                {
                    return null;
                }
            }

            return best.Target;
        }

        private static bool ContainsWholePhrase(string text, string phrase)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(phrase))
            {
                return false;
            }

            return (" " + text + " ").IndexOf(" " + phrase + " ", StringComparison.Ordinal) >= 0;
        }

        private static bool StartsWithWholePhrase(string text, string phrase)
        {
            return string.Equals(text, phrase, StringComparison.Ordinal)
                || text.StartsWith(phrase + " ", StringComparison.Ordinal);
        }

        private static string NormalizePhrase(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = new StringBuilder(value!.Length);
            var pendingSpace = false;
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    if (pendingSpace && normalized.Length > 0)
                    {
                        normalized.Append(' ');
                    }

                    normalized.Append(char.ToUpperInvariant(ch));
                    pendingSpace = false;
                }
                else
                {
                    pendingSpace = normalized.Length > 0;
                }
            }

            return normalized.ToString();
        }

        private readonly struct TargetMatch
        {
            public TargetMatch(string target, string phrase)
            {
                Target = target;
                Phrase = phrase;
            }

            public string Target { get; }

            public string Phrase { get; }
        }
    }
}
