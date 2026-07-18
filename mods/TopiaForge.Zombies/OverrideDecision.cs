using System;

namespace TopiaForge.Zombies
{
    // The deterministic "robot psychology" that decides how an infected robot answers an OVERRIDE command. It is
    // engine-independent and remains the offline authority whenever the opt-in JACK IN conversation path is closed.

    // The player's command. Each maps to a target outcome with its own persuasiveness/difficulty.
    internal enum OverrideCommand
    {
        JoinMe,    // hardest: convert to an ally; refusal ENRAGES
        Freeze,    // stop it in place
        GetOut,    // make it flee
        StandDown  // safe pacify: easiest, never enrages
    }

    // The resolved effect, ordered from least to most compliant.
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
}
