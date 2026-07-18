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
            TestNonFiniteNumbersRestoreDocumentedDefaults();
            TestKeyNamesNormalizeToSdkSetOrFallback();
            TestLegacyOverrideKeyMigratesToJackIn();
            TestRemoteFeaturesDefaultOff();
            TestExplicitRemoteOptInIsPreserved();
        }

        private static void TestDefaultTargetWorldIsOpenSandbox()
        {
            var config = new ZombiesConfig();

            Assert(config.TargetWorldId == WellKnownWorldIds.OpenSandboxWorld,
                "Zombies default target world should be Open Sandbox");
        }

        private static void TestBlankTargetWorldMigratesToOpenSandbox()
        {
            var config = new ZombiesConfig { TargetWorldId = "  " };

            config.Normalize();

            Assert(config.TargetWorldId == WellKnownWorldIds.OpenSandboxWorld,
                "blank Zombies targetWorldId should migrate to Open Sandbox");
        }

        private static void TestMissingTargetWorldDeserializesToOpenSandbox()
        {
            var config = JsonUtil.Deserialize<ZombiesConfig>("{}");

            Assert(config.TargetWorldId == WellKnownWorldIds.OpenSandboxWorld,
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
            config.Normalize();

            Assert(config.UseLiveBrain && config.ConversationEnabled && config.UseVoiceInput,
                "explicit persisted remote-AI and voice opt-ins must survive default seeding");
        }

        private static void TestNonFiniteNumbersRestoreDocumentedDefaults()
        {
            var defaults = new ZombiesConfig();
            var floatProperties = typeof(ZombiesConfig).GetProperties();
            var floatIndex = 0;

            foreach (var property in floatProperties)
            {
                if (property.PropertyType != typeof(float))
                {
                    continue;
                }

                float invalid;
                switch (floatIndex % 3)
                {
                    case 0:
                        invalid = float.NaN;
                        break;
                    case 1:
                        invalid = float.PositiveInfinity;
                        break;
                    default:
                        invalid = float.NegativeInfinity;
                        break;
                }

                var config = new ZombiesConfig();
                property.SetValue(config, invalid);
                config.Normalize();
                var actual = (float)property.GetValue(config)!;
                var expected = (float)property.GetValue(defaults)!;
                Assert(actual.Equals(expected),
                    property.Name + " should restore its documented default when configured with NaN or infinity"
                    + " (expected " + expected + ", actual " + actual + ")");
                floatIndex++;
            }

            Assert(floatIndex > 0, "the Zombies config test should discover floating-point tuning properties");
        }

        private static void TestKeyNamesNormalizeToSdkSetOrFallback()
        {
            var config = new ZombiesConfig
            {
                BroadcastKey = " f12 ",
                JackInKey = "alpha7",
                VoiceKey = "downArrow",
                ShopKey = "!"
            };

            config.Normalize();

            Assert(config.ShopKey == "B",
                "unknown key names should use each action's documented fallback");
            Assert(config.BroadcastKey == "F12" && config.JackInKey == "Alpha7",
                "function and top-row digit keys should normalize to SDK names");
            Assert(config.VoiceKey == "DownArrow",
                "named SDK keys should normalize casing and surrounding whitespace");
        }

        private static void TestLegacyOverrideKeyMigratesToJackIn()
        {
            var config = JsonUtil.Deserialize<ZombiesConfig>("{\"overrideKey\":\" f12 \"}");

            config.MigrateFrom(1);
            config.Normalize();

            Assert(config.JackInKey == "F12",
                "schema-1 overrideKey should migrate to the effective JACK IN binding");
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
