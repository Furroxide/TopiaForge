using System;
using TopiaForge.ModManager.Core;
using TopiaForge.Sandbox;

namespace TopiaForge.ModManager.Tests
{
    internal static class SandboxConfigTests
    {
        internal static void Run()
        {
            AssertRemoteFeaturesOff(new SandboxConfig(), "new config");
            AssertRemoteFeaturesOff(JsonUtil.Deserialize<SandboxConfig>("{}"), "missing fields");

            var optedIn = JsonUtil.Deserialize<SandboxConfig>(
                "{\"conversationEnabled\":true,\"voiceInputEnabled\":true,\"ambientBanter\":true}");
            Assert(optedIn.ConversationEnabled && optedIn.VoiceInputEnabled && optedIn.AmbientBanter,
                "explicit remote-conversation, microphone/STT, and banter opt-ins must survive default seeding");
            Console.WriteLine("SandboxConfigTests passed.");
        }

        private static void AssertRemoteFeaturesOff(SandboxConfig config, string source)
        {
            Assert(!config.ConversationEnabled,
                source + " must not read a player token or start remote programming by default");
            Assert(!config.VoiceInputEnabled,
                source + " must not expose microphone/STT capture by default");
            Assert(!config.AmbientBanter,
                source + " must not initiate background remote AI calls by default");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Sandbox config: " + message);
            }
        }
    }
}
