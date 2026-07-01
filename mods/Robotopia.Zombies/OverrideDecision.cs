using System;
using Robotopia.Mods;

namespace Robotopia.Zombies
{
    // The "robot psychology" that decides how an infected robot answers an OVERRIDE command. Pure, Unity-free, and
    // deterministic given a robot's seeded mind — it is the always-on authority that resolves at frame 0 with zero
    // network, so the cast feels instant and works offline. A live LLM answer (when available) only ENRICHES it via
    // ApplyBrainModulation, which can upgrade a failed cast toward the player's intent but never harden an outcome the
    // player has already been shown. Kept free of UnityEngine/ZombiesConfig so it unit-tests on net8.0.

    // The player's command. Each maps to a target outcome with its own persuasiveness/difficulty.
    internal enum OverrideCommand
    {
        JoinMe,    // hardest: convert to an ally; refusal ENRAGES
        Freeze,    // stop it in place
        GetOut,    // make it flee
        StandDown  // safe pacify: easiest, never enrages
    }

    // The resolved effect, ordered by how compliant it is (the rank is used by the soften-only brain modulation).
    internal enum HijackOutcome
    {
        Resist = 0,
        Flee = 1,
        Freeze = 2,
        Convert = 3
    }

    // The runtime state a robot is driven by after an override. Hostile is the default (chase the player); the rest
    // are time-limited and revert (or, for Allied, burn out) when their timer lapses.
    internal enum HijackState
    {
        Hostile,
        Frozen,
        Fleeing,
        Enraged,
        Allied
    }

    // A robot's seeded disposition. Stable for the robot's lifetime so the same robot reacts consistently (no
    // save-scumming a single cast), while different robots — and rising wave corruption — vary the outcome.
    internal readonly struct RobotMind
    {
        public readonly float Suggestibility; // 0..1, how open it is to a command
        public readonly float Loyalty;        // 0..1, attachment to the infection (resistance)
        public readonly float Corruption;     // 0..1, rises with wave; erratic, slightly easier to flip AND to enrage
        public readonly float Bias;           // signed personality jitter folded in once, so casts are deterministic

        public RobotMind(float suggestibility, float loyalty, float corruption, float bias)
        {
            Suggestibility = suggestibility;
            Loyalty = loyalty;
            Corruption = corruption;
            Bias = bias;
        }

        public static RobotMind Seed(Random random, int wave, in OverrideTuning tuning)
        {
            var suggestibility = Lerp(tuning.SuggestibilityMin, tuning.SuggestibilityMax, (float)random.NextDouble());
            var loyalty = Lerp(tuning.LoyaltyMin, tuning.LoyaltyMax, (float)random.NextDouble());
            var corruption = Clamp01(tuning.CorruptionBase + (Math.Max(0, wave - 1) * tuning.CorruptionPerWave));
            var bias = (float)((random.NextDouble() * 2.0) - 1.0) * tuning.BiasAmplitude;
            return new RobotMind(suggestibility, loyalty, corruption, bias);
        }

        private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }

    // Config-derived knobs for seeding + difficulty, kept as a plain struct so the resolver needs no Unity/config types.
    internal readonly struct OverrideTuning
    {
        public readonly float SuggestibilityMin;
        public readonly float SuggestibilityMax;
        public readonly float LoyaltyMin;
        public readonly float LoyaltyMax;
        public readonly float CorruptionBase;
        public readonly float CorruptionPerWave;
        public readonly float BiasAmplitude;
        public readonly float Difficulty; // scales every command threshold; >1 = harder to override

        public OverrideTuning(
            float suggestibilityMin,
            float suggestibilityMax,
            float loyaltyMin,
            float loyaltyMax,
            float corruptionBase,
            float corruptionPerWave,
            float biasAmplitude,
            float difficulty)
        {
            SuggestibilityMin = suggestibilityMin;
            SuggestibilityMax = suggestibilityMax;
            LoyaltyMin = loyaltyMin;
            LoyaltyMax = loyaltyMax;
            CorruptionBase = corruptionBase;
            CorruptionPerWave = corruptionPerWave;
            BiasAmplitude = biasAmplitude;
            Difficulty = difficulty;
        }
    }

    // The resolved decision plus whether a refusal enraged the robot.
    internal readonly struct OverrideResolution
    {
        public readonly HijackOutcome Outcome;
        public readonly bool Enraged;

        public OverrideResolution(HijackOutcome outcome, bool enraged)
        {
            Outcome = outcome;
            Enraged = enraged;
        }
    }

    internal static class OverrideDecision
    {
        // The raw compliance score a command earns against a robot's mind and archetype resistance (compared to the
        // command's threshold by Resolve). Exposed so the conversation verb can seed a persuasion disposition from the
        // same "robot psychology" the deterministic broadcast uses.
        public static float Compliance(OverrideCommand command, in RobotMind mind, float baseResistance)
        {
            return mind.Suggestibility
                + Persuasiveness(command)
                + (mind.Corruption * 0.25f)
                - baseResistance
                - (mind.Loyalty * 0.5f)
                + mind.Bias;
        }

        // Resolve the deterministic outcome of a command against a robot's mind and its archetype resistance.
        public static OverrideResolution Resolve(OverrideCommand command, in RobotMind mind, float baseResistance, float difficulty)
        {
            var compliance = Compliance(command, mind, baseResistance);

            var threshold = Threshold(command) * (difficulty <= 0f ? 1f : difficulty);
            var target = TargetOutcome(command);

            if (compliance >= threshold)
            {
                return new OverrideResolution(target, false);
            }

            // A near-miss on the hardest command reads as the robot hesitating (a brief freeze) rather than a hard
            // refusal — only JoinMe, so a barely-failed conversion still does something.
            if (command == OverrideCommand.JoinMe && compliance >= threshold - HesitationBand)
            {
                return new OverrideResolution(HijackOutcome.Freeze, false);
            }

            return new OverrideResolution(HijackOutcome.Resist, EnragesOnFail(command));
        }

        // Soften-only LLM modulation: a live brain answer may rescue a failed/partial cast up to the command's
        // intended outcome (clearing any enrage), but can never downgrade an outcome the player was already shown. A
        // brain that confirms or refuses leaves the deterministic result untouched (only its bark is surfaced).
        public static OverrideResolution ApplyBrainModulation(OverrideCommand command, OverrideResolution deterministic, RobotDecision brainAction)
        {
            var target = TargetOutcome(command);
            if (brainAction == RobotDecision.Comply && (int)deterministic.Outcome < (int)target)
            {
                return new OverrideResolution(target, false);
            }

            return deterministic;
        }

        // Map a brain `action` field value to the shared decision vocabulary. Unknown/empty → Unknown (treated as a
        // no-op by the modulation rule).
        public static RobotDecision ParseBrainAction(string? action)
        {
            if (string.IsNullOrEmpty(action))
            {
                return RobotDecision.Unknown;
            }

            switch (action!.Trim().ToLowerInvariant())
            {
                case "comply":
                case "obey":
                case "join":
                    return RobotDecision.Comply;
                case "freeze":
                case "halt":
                case "stop":
                    return RobotDecision.Freeze;
                case "flee":
                case "scatter":
                case "run":
                    return RobotDecision.Flee;
                case "resist":
                case "refuse":
                case "attack":
                    return RobotDecision.Resist;
                default:
                    return RobotDecision.Unknown;
            }
        }

        // The natural-language phrase the player "says" — used both as the HUD label and woven into the brain prompt.
        public static string Phrase(OverrideCommand command)
        {
            switch (command)
            {
                case OverrideCommand.JoinMe:
                    return "Join me — fight with me.";
                case OverrideCommand.Freeze:
                    return "Freeze. Power down.";
                case OverrideCommand.GetOut:
                    return "Get out of here. Run.";
                default:
                    return "Stand down. Stay calm.";
            }
        }

        public static HijackOutcome TargetOutcome(OverrideCommand command)
        {
            switch (command)
            {
                case OverrideCommand.JoinMe:
                    return HijackOutcome.Convert;
                case OverrideCommand.GetOut:
                    return HijackOutcome.Flee;
                default:
                    return HijackOutcome.Freeze; // Freeze and StandDown both pacify
            }
        }

        // How inherently persuasive the command is (added to compliance). JoinMe asks the most, StandDown the least.
        private static float Persuasiveness(OverrideCommand command)
        {
            switch (command)
            {
                case OverrideCommand.JoinMe:
                    return 0.18f;
                case OverrideCommand.Freeze:
                    return 0.45f;
                case OverrideCommand.GetOut:
                    return 0.48f;
                default:
                    return 0.60f;
            }
        }

        // The compliance needed to land the command's target outcome (before the global difficulty scale).
        private static float Threshold(OverrideCommand command)
        {
            switch (command)
            {
                case OverrideCommand.JoinMe:
                    return 0.55f;
                case OverrideCommand.Freeze:
                case OverrideCommand.GetOut:
                    return 0.32f;
                default:
                    return 0.15f;
            }
        }

        private static bool EnragesOnFail(OverrideCommand command) => command == OverrideCommand.JoinMe;

        private const float HesitationBand = 0.18f;
    }

    // Builds the structured brain query for one override cast: the robot's own LLM brain decides how it reacts and
    // barks a line. The action is constrained to the comply/freeze/flee/resist enum so the answer is machine-readable;
    // the bark is short free text. Pure (returns the Unity-free SDK request), so it unit-tests without a backend.
    internal static class OverridePrompt
    {
        private static readonly string[] ActionChoices = { "comply", "freeze", "flee", "resist" };

        public static BrainQueryRequest Build(string chassis, in RobotMind mind, OverrideCommand command, int wave, float temperature)
        {
            var prompt =
                "You are an infected combat robot in a robot-zombie outbreak. Chassis type: " + chassis +
                ". You are " + CorruptionWord(mind.Corruption) + " by the infection (wave " + wave +
                "). A lone human survivor points a weapon at you and says: \"" + OverrideDecision.Phrase(command) +
                "\". Decide how YOU, this specific robot, react this instant. 'comply' means you obey the human and turn on the other infected; otherwise freeze, flee, or resist. Then give a SHORT in-character line (max ~10 words).";

            var outputs = new BrainOutputField[]
            {
                new BrainOutputField("action", "How you react to the human's command.", BrainFieldType.String, ActionChoices),
                new BrainOutputField("bark", "A short in-character line you say out loud (<= 10 words).", BrainFieldType.String),
            };

            return new BrainQueryRequest(prompt, outputs)
            {
                Usage = "zombies-override",
                SuccessDescription = "Return a valid action and a short spoken bark.",
                Temperature = temperature,
                UseReasoning = false,
            };
        }

        // A crowd-level broadcast query: the whole swarm answers with one voice (a single line). Used purely as
        // flavour over the swarm — the per-robot outcomes are resolved deterministically.
        public static BrainQueryRequest BuildBroadcast(OverrideCommand command, int wave, int crowdSize, float temperature)
        {
            var prompt =
                "You are the collective voice of a swarm of " + crowdSize + " infected combat robots (wave " + wave +
                ") in a robot-zombie outbreak. A lone human survivor broadcasts to all of you: \"" +
                OverrideDecision.Phrase(command) + "\". Answer as the swarm with ONE short, menacing or glitchy line (max ~10 words).";

            var outputs = new BrainOutputField[]
            {
                new BrainOutputField("bark", "The swarm's single short spoken line (<= 10 words).", BrainFieldType.String),
            };

            return new BrainQueryRequest(prompt, outputs)
            {
                Usage = "zombies-broadcast",
                SuccessDescription = "Return one short spoken line for the swarm.",
                Temperature = temperature,
                UseReasoning = false,
            };
        }

        private static string CorruptionWord(float corruption)
        {
            if (corruption < 0.33f)
            {
                return "only lightly corrupted";
            }

            return corruption < 0.66f ? "badly corrupted" : "almost fully consumed";
        }
    }
}
