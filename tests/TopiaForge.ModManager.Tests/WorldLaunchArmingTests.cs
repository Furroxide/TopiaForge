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
            TestProfileWithoutACommandKeepsTheRememberedSelection();
            TestNothingRememberedBootsNormally();
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
        /// A profile from a launcher that predates the command asked for neither a gamemode nor an
        /// ordinary boot, so it must not be read as having suppressed the remembered one.
        /// </summary>
        private static void TestProfileWithoutACommandKeepsTheRememberedSelection()
        {
            var armed = WorldLaunchArming.Resolve(
                Profile(null),
                Remembered(ZombiesGamemodeId, autoLoad: true));

            Assert(armed != null && armed!.GamemodeId == ZombiesGamemodeId,
                "a profile carrying no command must not silently cancel the remembered selection");
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
