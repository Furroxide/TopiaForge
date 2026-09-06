using System;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    // Exercises which of the two parties that can request a gamemode wins (WorldLaunchArming is
    // compiled into this assembly via <Compile Include>; it is deliberately Unity-free).
    internal static class WorldLaunchArmingTests
    {
        private const string ZombiesGamemodeId = "io.github.furroxide.topiaforge.zombies.survival";
        private const string SandboxGamemodeId = "io.github.furroxide.topiaforge.worlds.sandbox";

        public static void Run()
        {
            TestNoLauncherKeepsTheRememberedSelection();
            TestExplicitMainMenuOverridesTheRememberedSelection();
            TestLaunchTargetWins();
            TestProfileWithoutACommandSuppressesRememberedSelection();
            TestSafeModeRejectsGamemodeAutoload();
            TestNothingRememberedBootsNormally();
            TestInvalidRememberedTransitionIsPreserved();
            Console.WriteLine("All world launch arming tests passed.");
        }

        /// <summary>Starting Robotopia directly is what the overlay's autoload setting is for.</summary>
        private static void TestNoLauncherKeepsTheRememberedSelection()
        {
            var armed = WorldLaunchArming.Resolve(null, Remembered(ZombiesGamemodeId, autoLoad: true));

            Assert(armed != null, "a direct start should honour the manager's own selection");
            Assert(armed!.GamemodeId == ZombiesGamemodeId, "it should arm the remembered gamemode");
        }

        /// <summary>
        /// The regression this exists for. Home's "None -- play normally" and `--gamemode none` both
        /// promise an ordinary boot. Reading that as "the launcher said nothing" let a remembered
        /// autoload start a gamemode anyway, contradicting the launcher, the CLI and the docs.
        /// </summary>
        private static void TestExplicitMainMenuOverridesTheRememberedSelection()
        {
            var profile = Profile(new WorldLaunchIntent
            {
                Command = WorldLaunchIntent.MainMenuCommand
            });

            var armed = WorldLaunchArming.Resolve(profile, Remembered(ZombiesGamemodeId, autoLoad: true));

            Assert(armed == null, "an explicit play-normally command must beat the remembered selection");
        }

        private static void TestLaunchTargetWins()
        {
            var profile = Profile(new WorldLaunchIntent { GamemodeId = SandboxGamemodeId });

            var armed = WorldLaunchArming.Resolve(profile, Remembered(ZombiesGamemodeId, autoLoad: true));

            Assert(armed != null && armed!.GamemodeId == SandboxGamemodeId,
                "the launcher's requested target must beat the remembered selection");
        }

        /// <summary>
        /// Only direct startup without any launcher profile may use remembered autoload.
        /// Missing launcher commands must not inherit an unrelated durable selection.
        /// </summary>
        private static void TestProfileWithoutACommandSuppressesRememberedSelection()
        {
            var armed = WorldLaunchArming.Resolve(
                Profile(null),
                Remembered(ZombiesGamemodeId, autoLoad: true));

            Assert(armed == null, "a launcher profile without a command must not reuse remembered autoload");
        }

        internal static void TestSafeModeRejectsGamemodeAutoload()
        {
            var profile = Profile(new WorldLaunchIntent { GamemodeId = ZombiesGamemodeId });
            profile.SafeMode = true;
            Assert(WorldLaunchArming.Resolve(profile, Remembered(ZombiesGamemodeId, true)) == null,
                "safe mode must suppress both explicit legacy mode commands and remembered autoload");
        }

        private static void TestNothingRememberedBootsNormally()
        {
            Assert(WorldLaunchArming.Resolve(null, Remembered(ZombiesGamemodeId, autoLoad: false)) == null,
                "autoload off means boot normally");
            Assert(WorldLaunchArming.Resolve(null, Remembered(string.Empty, autoLoad: true)) == null,
                "autoload on with nothing selected means boot normally");
            Assert(WorldLaunchArming.Resolve(null, null) == null,
                "no remembered state at all means boot normally");
        }

        internal static void TestInvalidRememberedTransitionIsPreserved()
        {
            var saved = Remembered(ZombiesGamemodeId, true);
            saved.LoadMode = "obsolete-transition";
            var armed = WorldLaunchArming.Resolve(null, saved);
            Assert(armed != null && armed.LoadMode == saved.LoadMode && armed.Validate().Count > 0,
                "unknown saved transitions must remain invalid rather than silently normalize to another launch");
        }

        private static ProfileLaunchConfiguration Profile(WorldLaunchIntent? intent)
        {
            return new ProfileLaunchConfiguration
            {
                SchemaVersion = ProfileLaunchConfiguration.CurrentSchemaVersion,
                ProfileId = "profile",
                WorldLaunch = intent
            };
        }

        private static WorldLaunchSettings Remembered(string gamemodeId, bool autoLoad)
        {
            return new WorldLaunchSettings
            {
                SelectedGamemodeId = gamemodeId,
                AutoLoadOnStart = autoLoad
            };
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
