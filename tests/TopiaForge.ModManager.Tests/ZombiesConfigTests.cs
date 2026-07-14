using System;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Zombies;

namespace TopiaForge.ModManager.Tests
{
    internal static class ZombiesConfigTests
    {
        public static void Run()
        {
            TestDefaultTargetWorldIsOpenSandbox();
            TestBlankTargetWorldMigratesToOpenSandbox();
            TestMissingTargetWorldDeserializesToOpenSandbox();
            TestRemoteFeaturesDefaultOff();
            TestExplicitRemoteOptInIsPreserved();
        }

        private static void TestDefaultTargetWorldIsOpenSandbox()
        {
            var config = new ZombiesConfig();

            Assert(config.TargetWorldId == WellKnownIds.OpenSandboxWorldId,
                "Zombies default target world should be Open Sandbox");
        }

        private static void TestBlankTargetWorldMigratesToOpenSandbox()
        {
            var config = new ZombiesConfig { TargetWorldId = "  " };

            config.Normalize();

            Assert(config.TargetWorldId == WellKnownIds.OpenSandboxWorldId,
                "blank Zombies targetWorldId should migrate to Open Sandbox");
        }

        private static void TestMissingTargetWorldDeserializesToOpenSandbox()
        {
            var config = JsonUtil.Deserialize<ZombiesConfig>("{}");

            Assert(config.TargetWorldId == WellKnownIds.OpenSandboxWorldId,
                "missing Zombies targetWorldId should deserialize to Open Sandbox");
        }

        private static void TestRemoteFeaturesDefaultOff()
        {
            var created = new ZombiesConfig();
            var migrated = JsonUtil.Deserialize<ZombiesConfig>("{}");

            foreach (var config in new[] { created, migrated })
            {
                Assert(!config.UseLiveBrain, "live brain should default off so installing the mod cannot use the player token");
                Assert(!config.ConversationEnabled, "remote conversation should default off until the player opts in");
                Assert(!config.UseVoiceInput, "microphone/STT should default off until the player opts in");
            }
        }

        private static void TestExplicitRemoteOptInIsPreserved()
        {
            var config = JsonUtil.Deserialize<ZombiesConfig>(
                "{\"useLiveBrain\":true,\"conversationEnabled\":true,\"useVoiceInput\":true}");

            Assert(config.UseLiveBrain && config.ConversationEnabled && config.UseVoiceInput,
                "explicit persisted remote-AI and voice opt-ins must survive default seeding");
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
