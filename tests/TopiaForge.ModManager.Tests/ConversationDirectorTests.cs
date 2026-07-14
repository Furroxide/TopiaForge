using System;
using TopiaForge.Mods;
using TopiaForge.Zombies;

namespace TopiaForge.ModManager.Tests
{
    // Unit tests for the Unity-free Zombies JACK-IN director: decision parsing, the engine-owned persuasion meter
    // (seed/nudge/threshold), and the request framing (hostile vs ally renegotiation). Compiled into the net8.0 test
    // assembly via the csproj Compile include, like OverrideDecision.
    internal static class ConversationDirectorTests
    {
        public static void Run()
        {
            TestDecisionParsing();
            TestSeedFavoursSuggestible();
            TestNudgeMovesAndClamps();
            TestConvertThresholdScalesWithResistance();
            TestRequestFramingHostileVsAlly();
            TestTurnRefillAddsTimeAndCapsAtWindow();
            Console.WriteLine("All conversation-director tests passed.");
        }

        private static void TestDecisionParsing()
        {
            Assert(ConversationDirector.Parse("CONVERT") == ConversationDecision.Convert, "CONVERT parses");
            Assert(ConversationDirector.Parse(" stand_down ") == ConversationDecision.StandDown, "stand_down parses, trims/cases");
            Assert(ConversationDirector.Parse("flee") == ConversationDecision.Flee, "flee parses");
            Assert(ConversationDirector.Parse("REFUSE") == ConversationDecision.Refuse, "refuse parses");
            Assert(ConversationDirector.Parse("banana") == ConversationDecision.Unknown, "junk → Unknown");
            Assert(ConversationDirector.Parse(null) == ConversationDecision.Unknown, "null → Unknown");
        }

        private static void TestSeedFavoursSuggestible()
        {
            var tuning = Tuning();
            // Suggestible, disloyal, low resistance → higher starting disposition.
            var open = new RobotMind(0.9f, 0.05f, 0.2f, 0f);
            // Loyal, low suggestibility → near zero.
            var stubborn = new RobotMind(0.1f, 0.9f, 0.2f, 0f);
            var openSeed = ConversationDirector.SeedDisposition(open, tuning);
            var stubbornSeed = ConversationDirector.SeedDisposition(stubborn, tuning);
            Assert(openSeed > stubbornSeed, "a suggestible, disloyal robot should seed more persuadable");
            Assert(openSeed >= 0f && openSeed <= 1f && stubbornSeed >= 0f && stubbornSeed <= 1f, "seed stays in 0..1");
        }

        private static void TestNudgeMovesAndClamps()
        {
            var tuning = Tuning();
            Assert(ConversationDirector.Nudge(0.5f, ConversationDecision.Convert, tuning) > 0.5f, "CONVERT raises disposition");
            Assert(ConversationDirector.Nudge(0.5f, ConversationDecision.Refuse, tuning) < 0.5f, "REFUSE lowers disposition");
            Assert(ConversationDirector.Nudge(0.99f, ConversationDecision.Convert, tuning) <= 1f, "nudge clamps at 1");
            Assert(ConversationDirector.Nudge(0.01f, ConversationDecision.Refuse, tuning) >= 0f, "nudge clamps at 0");
            Assert(Math.Abs(ConversationDirector.Nudge(0.5f, ConversationDecision.Unknown, tuning) - 0.5f) < 1e-6f, "Unknown leaves disposition unchanged");
        }

        private static void TestConvertThresholdScalesWithResistance()
        {
            var tuning = Tuning();
            var easy = ConversationDirector.ConvertThreshold(0.15f, tuning); // Runt
            var hard = ConversationDirector.ConvertThreshold(0.70f, tuning); // Brute
            Assert(hard > easy, "a more resistant archetype needs a higher persuasion to convert");
            Assert(hard <= 0.97f, "the threshold is capped below 1 so conversion is never impossible");
        }

        private static void TestRequestFramingHostileVsAlly()
        {
            var mind = new RobotMind(0.4f, 0.4f, 0.3f, 0f);
            var hostile = ConversationDirector.BuildRequest("Brute", mind, 3, 0.8f, recentlyShot: true, isAlly: false, loyalty: 0f, temperature: 0.7f, maxTurns: 3);
            Assert(hostile.DecisionOptions.Count == 4, "four decision options");
            Assert(hostile.SystemFrame.Contains("infected"), "hostile frame casts the robot as infected");
            Assert(hostile.GroundTruthFacts != null && hostile.GroundTruthFacts.ContainsKey("the-human-just-shot-you"), "ground truth carries the just-shot fact");
            Assert(hostile.GroundTruthFacts!["the-human-just-shot-you"] == "yes", "recentlyShot is injected as ground truth");

            var ally = ConversationDirector.BuildRequest("Grunt", mind, 5, 1f, recentlyShot: false, isAlly: true, loyalty: 0.2f, temperature: 0.7f, maxTurns: 3);
            Assert(ally.SystemFrame.Contains("switched sides") || ally.SystemFrame.Contains("FOR a lone human"), "ally frame is a loyalty check-in");
            Assert(ally.GroundTruthFacts != null && ally.GroundTruthFacts.ContainsKey("your-loyalty"), "ally ground truth carries loyalty");
            Assert(ally.Usage == "zombies-renegotiate", "ally usage label distinguishes a renegotiation");
        }

        private static void TestTurnRefillAddsTimeAndCapsAtWindow()
        {
            var extended = ConversationDirector.RefillDeadline(now: 10f, deadline: 14f, windowSeconds: 22f, refillSeconds: 4f);
            Assert(Math.Abs(extended - 18f) < 1e-6f, "turn refill adds to remaining time");

            var capped = ConversationDirector.RefillDeadline(now: 10f, deadline: 31f, windowSeconds: 22f, refillSeconds: 4f);
            Assert(Math.Abs(capped - 32f) < 1e-6f, "turn refill cannot exceed a full window from now");

            var recovered = ConversationDirector.RefillDeadline(now: 10f, deadline: 8f, windowSeconds: 22f, refillSeconds: 4f);
            Assert(Math.Abs(recovered - 14f) < 1e-6f, "a late robot reply still grants the next exchange time");

            var disabled = ConversationDirector.RefillDeadline(now: 10f, deadline: 12f, windowSeconds: 22f, refillSeconds: 0f);
            Assert(Math.Abs(disabled - 12f) < 1e-6f, "zero refill leaves deadline unchanged");
        }

        private static ConversationTuning Tuning()
        {
            return new ConversationTuning(
                seedBias: 0.35f,
                convertThreshold: 0.72f,
                resistanceWeight: 0.3f,
                convertNudge: 0.3f,
                standDownNudge: 0.16f,
                fleeNudge: 0.06f,
                refuseNudge: -0.14f,
                enrageFloor: 0.12f);
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
