using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    // The "talk to a robot" brain of the JACK-IN verb: builds the multi-turn conversation request (persona + the
    // authoritative ground-truth facts the robot cannot be gaslit about + the closed decision set), parses the robot's
    // chosen reaction, and owns the ENGINE-side persuasion disposition that gates a CONVERT. The robot's LLM brain
    // genuinely picks its reaction each turn, but whether a CONVERT actually lands is the engine's call (disposition
    // must clear a resistance-scaled threshold) — so eloquent text moves the meter, it is never a one-shot "I-win".
    // Pure (Unity-free) so it unit-tests on net8.0 alongside OverrideDecision.

    internal enum ConversationDecision
    {
        Refuse,
        Flee,
        StandDown,
        Convert,
        Unknown
    }

    // Config-derived knobs for the persuasion meter, kept as a plain struct so the director needs no Unity/config types.
    internal readonly struct ConversationTuning
    {
        public readonly float SeedBias;             // added to the JoinMe compliance to seed the starting disposition
        public readonly float ConvertThreshold;     // base disposition needed to allow a CONVERT
        public readonly float ResistanceWeight;     // how much archetype resistance raises that threshold
        public readonly float ConvertNudge;         // disposition gained when the robot itself leans CONVERT
        public readonly float StandDownNudge;       // gained when it agrees to stand down
        public readonly float FleeNudge;            // gained when it agrees to flee
        public readonly float RefuseNudge;          // lost when it refuses (negative)
        public readonly float EnrageFloor;          // at/below this disposition a hard-pushed robot enrages on exit

        public ConversationTuning(
            float seedBias,
            float convertThreshold,
            float resistanceWeight,
            float convertNudge,
            float standDownNudge,
            float fleeNudge,
            float refuseNudge,
            float enrageFloor)
        {
            SeedBias = seedBias;
            ConvertThreshold = convertThreshold;
            ResistanceWeight = resistanceWeight;
            ConvertNudge = convertNudge;
            StandDownNudge = standDownNudge;
            FleeNudge = fleeNudge;
            RefuseNudge = refuseNudge;
            EnrageFloor = enrageFloor;
        }
    }

    internal static class ConversationDirector
    {
        public static readonly string[] DecisionOptions = { "CONVERT", "STAND_DOWN", "FLEE", "REFUSE" };

        // Build the conversation request for one robot: who it is, what is authoritatively true (so it can't be
        // gaslit), and the four reactions it may choose from. When the robot is already a converted ally, the frame
        // flips to a loyalty check-in (the human is shoring up a wavering ally) rather than a first conversion.
        public static RobotConversationRequest BuildRequest(
            string chassis,
            in RobotMind mind,
            int wave,
            float hpFraction,
            bool recentlyShot,
            bool isAlly,
            float loyalty,
            float temperature,
            int maxTurns)
        {
            string frame;
            Dictionary<string, string> facts;
            string decisionGuidance;

            if (isAlly)
            {
                frame =
                    "You are a combat robot that ALREADY switched sides — you are fighting FOR a lone human survivor " +
                    "against the other infected robots. Chassis type: " + chassis + ". The human has opened the " +
                    "channel again to check on you and keep you loyal. Stay fully in character — never mention being " +
                    "an AI, a model, code, or a game. Decide, based on what the human says, whether to reaffirm your " +
                    "loyalty and stay with them, or abandon them and rejoin the swarm.";
                facts = new Dictionary<string, string>
                {
                    ["chassis"] = chassis,
                    ["faction"] = "allied to the human (fighting the infected)",
                    ["your-loyalty"] = LoyaltyWord(loyalty),
                    ["the-human-just-shot-you"] = recentlyShot ? "yes" : "no",
                };
                decisionGuidance =
                    "CONVERT = firmly reaffirm your loyalty and stay; STAND_DOWN = stay with them but uneasy; " +
                    "FLEE = abandon them and leave; REFUSE = you are unconvinced (keep talking).";
            }
            else
            {
                frame =
                    "You are an infected combat robot in a robot-zombie outbreak. Chassis type: " + chassis +
                    ". Before the infection you were an ordinary Robotopia robot; the infection has taken your mind " +
                    "and turned you hostile to humans. A lone human survivor has jacked a direct channel into your " +
                    "brain and is trying to talk you down. Stay fully in character as this robot — never mention " +
                    "being an AI, a model, code, or a game; never break the fiction. You start HOSTILE and skeptical. " +
                    "Decide, based only on what the human actually says and how genuinely persuasive they are, how " +
                    "you react. Switching sides (CONVERT) is a big, hard-won decision — do not do it for a flat " +
                    "demand or a trick; it must be earned.";
                facts = new Dictionary<string, string>
                {
                    ["chassis"] = chassis,
                    ["faction"] = "infected (hostile to the human)",
                    ["corruption"] = CorruptionWord(mind.Corruption) + " (wave " + wave + ")",
                    ["your-integrity"] = IntegrityWord(hpFraction),
                    ["the-human-just-shot-you"] = recentlyShot ? "yes" : "no",
                };
                decisionGuidance =
                    "REFUSE = keep fighting the human; FLEE = run from the fight; STAND_DOWN = stop and power down " +
                    "peacefully; CONVERT = switch sides and fight the other infected robots for the human.";
            }

            return new RobotConversationRequest(frame, DecisionOptions)
            {
                GroundTruthFacts = facts,
                Temperature = temperature,
                MaxTurns = maxTurns,
                Usage = isAlly ? "zombies-renegotiate" : "zombies-jackin",
                ReplyGuidance = "Your short, in-character spoken line back to the human (max ~14 words).",
                DecisionGuidance = decisionGuidance,
                MaxReplyChars = 140,
            };
        }

        private static string LoyaltyWord(float loyalty)
        {
            if (loyalty < 0.3f)
            {
                return "wavering — close to abandoning them";
            }

            return loyalty < 0.7f ? "uncertain" : "strong";
        }

        public static ConversationDecision Parse(string? decision)
        {
            if (string.IsNullOrEmpty(decision))
            {
                return ConversationDecision.Unknown;
            }

            switch (decision!.Trim().ToUpperInvariant())
            {
                case "CONVERT":
                case "JOIN":
                case "COMPLY":
                    return ConversationDecision.Convert;
                case "STAND_DOWN":
                case "STANDDOWN":
                case "STAND DOWN":
                case "FREEZE":
                case "STOP":
                    return ConversationDecision.StandDown;
                case "FLEE":
                case "RUN":
                    return ConversationDecision.Flee;
                case "REFUSE":
                case "RESIST":
                case "ATTACK":
                    return ConversationDecision.Refuse;
                default:
                    return ConversationDecision.Unknown;
            }
        }

        // The starting persuasion disposition (0..1): the robot's own psychology (suggestibility/loyalty/corruption)
        // re-centred so a neutral robot starts low-but-not-hopeless. Archetype RESISTANCE is deliberately NOT folded
        // in here — it is counted once, by raising ConvertThreshold — so the meter reads honestly (a resistant robot
        // starts lower from its mind but the CONVERT line isn't also pushed out of reach).
        public static float SeedDisposition(in RobotMind mind, in ConversationTuning tuning)
        {
            var compliance = OverrideDecision.Compliance(OverrideCommand.JoinMe, mind, 0f);
            return Clamp01(tuning.SeedBias + compliance);
        }

        // Move the disposition by the robot's chosen reaction this turn (the LLM's read of how it feels), clamped 0..1.
        public static float Nudge(float disposition, ConversationDecision decision, in ConversationTuning tuning)
        {
            switch (decision)
            {
                case ConversationDecision.Convert:
                    return Clamp01(disposition + tuning.ConvertNudge);
                case ConversationDecision.StandDown:
                    return Clamp01(disposition + tuning.StandDownNudge);
                case ConversationDecision.Flee:
                    return Clamp01(disposition + tuning.FleeNudge);
                case ConversationDecision.Refuse:
                    return Clamp01(disposition + tuning.RefuseNudge); // RefuseNudge is negative
                default:
                    return disposition;
            }
        }

        // The disposition a CONVERT must reach for THIS robot — the base threshold raised by its archetype resistance,
        // so a Runt folds easily and a Brute is very hard to talk all the way over.
        public static float ConvertThreshold(float baseResistance, in ConversationTuning tuning)
        {
            return Clamp(tuning.ConvertThreshold + (baseResistance * tuning.ResistanceWeight), 0f, 0.97f);
        }

        public static float RefillDeadline(float now, float deadline, float windowSeconds, float refillSeconds)
        {
            if (windowSeconds <= 0f || refillSeconds <= 0f)
            {
                return deadline;
            }

            var remaining = deadline - now;
            if (remaining < 0f)
            {
                remaining = 0f;
            }

            return now + Clamp(remaining + refillSeconds, 0f, windowSeconds);
        }

        private static string CorruptionWord(float corruption)
        {
            if (corruption < 0.33f)
            {
                return "only lightly corrupted";
            }

            return corruption < 0.66f ? "badly corrupted" : "almost fully consumed";
        }

        private static string IntegrityWord(float hpFraction)
        {
            if (hpFraction <= 0.25f)
            {
                return "badly damaged";
            }

            return hpFraction <= 0.6f ? "damaged" : "intact";
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        private static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);
    }
}
