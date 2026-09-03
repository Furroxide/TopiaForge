using System;
using System.Collections.Generic;
using TopiaForge.ModManager;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    // Exercises the manager's launch-intent routing (WorldLaunchRouter is compiled into this assembly via
    // <Compile Include>; it is deliberately Unity-free).
    internal static class WorldLaunchRouterTests
    {
        private const string OpenSandboxWorldId = "io.github.furroxide.topiaforge.worlds.open_sandbox";
        private const string CityWorldId = "io.github.furroxide.topiaforge.worlds.level.city";
        private const string ZombiesGamemodeId = "io.github.furroxide.topiaforge.zombies.survival";
        private const string ZombiesMenuEntryId = "io.github.furroxide.topiaforge.zombies.menu";

        public static void Run()
        {
            TestRegisteredWorldIsHonoured();
            TestMissingWorldFallsBackToTheGamemodesOwnEntry();
            TestNoWorldRequestedUsesTheGamemodesOwnEntry();
            TestUnresolvableGamemodeFails();
            TestMissingGamemodeFails();
            Console.WriteLine("All world launch router tests passed.");
        }

        private static void TestRegisteredWorldIsHonoured()
        {
            var route = WorldLaunchRouter.Resolve(Worlds(), MenuEntries(), CityWorldId, ZombiesGamemodeId);

            Assert(route.Resolved, "a registered world should resolve");
            Assert(route.WorldId == CityWorldId, "an explicitly requested registered world must be used as-is");
            Assert(route.Warning.Length == 0, "a registered world is not worth warning about");
        }

        /// <summary>
        /// A saved world id can go stale — the level list may not have loaded, or the world may have been
        /// removed with its mod. Falling back to the gamemode's own menu entry is better than failing,
        /// because that entry names the world the gamemode's author intended.
        /// </summary>
        private static void TestMissingWorldFallsBackToTheGamemodesOwnEntry()
        {
            var route = WorldLaunchRouter.Resolve(
                Worlds(),
                MenuEntries(),
                "io.github.furroxide.topiaforge.worlds.level.missing",
                ZombiesGamemodeId);

            Assert(route.Resolved, "a stale world should fall back rather than fail");
            Assert(route.WorldId == OpenSandboxWorldId, "the fallback must use the gamemode's own world");
            Assert(route.Warning.Contains("missing"), "the stale world id must be surfaced, not swallowed");
        }

        private static void TestNoWorldRequestedUsesTheGamemodesOwnEntry()
        {
            var route = WorldLaunchRouter.Resolve(Worlds(), MenuEntries(), string.Empty, ZombiesGamemodeId);

            Assert(route.Resolved, "a gamemode with a registered menu entry resolves without an explicit world");
            Assert(route.WorldId == OpenSandboxWorldId, "the entry's world must be used");
            Assert(route.Warning.Length == 0, "not naming a world is normal, not a problem");
        }

        private static void TestUnresolvableGamemodeFails()
        {
            var route = WorldLaunchRouter.Resolve(
                Worlds(),
                new List<GamemodeMenuEntry>(),
                string.Empty,
                ZombiesGamemodeId);

            Assert(!route.Resolved, "a gamemode with no world and no menu entry cannot be routed");
            Assert(route.Warning.Contains(ZombiesGamemodeId), "the failure must name the gamemode");
        }

        private static void TestMissingGamemodeFails()
        {
            var route = WorldLaunchRouter.Resolve(Worlds(), MenuEntries(), CityWorldId, string.Empty);
            Assert(!route.Resolved, "an intent without a gamemode is not a launch");
        }

        private static IReadOnlyList<WorldDefinition> Worlds()
        {
            return new List<WorldDefinition>
            {
                new WorldDefinition(OpenSandboxWorldId, "Open Sandbox", "Generated arena."),
                new WorldDefinition(CityWorldId, "Welcome to Robotopia", "City streets.", "City Streets",
                    firstParty: true, supportsSceneReplacement: true, supportsAdditiveArena: false),
            };
        }

        private static IReadOnlyList<GamemodeMenuEntry> MenuEntries()
        {
            return new List<GamemodeMenuEntry>
            {
                new GamemodeMenuEntry(ZombiesMenuEntryId, "Zombies", "Wave survival.",
                    ZombiesGamemodeId, OpenSandboxWorldId),
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
