using System;
using Robotopia.Mods;
using Robotopia.Worlds;

namespace Robotopia.ModManager.Tests
{
    internal static class WorldAutoLoadRouterTests
    {
        private const string ZombiesGamemodeId = "robotopia.zombies.survival";

        public static void Run()
        {
            TestOpenSandboxSelectionLoadsDirectly();
            TestFirstPartySelectionLoadsDirectly();
            TestUnregisteredSelectionFallsBackToMenuEntry();
        }

        private static void TestOpenSandboxSelectionLoadsDirectly()
        {
            var route = WorldAutoLoadRouter.Resolve(
                Worlds(),
                MenuEntries(),
                WellKnownIds.OpenSandboxWorldId,
                ZombiesGamemodeId,
                preferSceneReplacement: false,
                allowAdditiveFallback: true);

            Assert(route.Kind == WorldAutoLoadRouteKind.LoadSelection, "Open Sandbox auto-load should use the selected world directly");
            Assert(route.Request != null, "direct Open Sandbox auto-load should carry a load request");
            Assert(route.Request!.WorldId == WellKnownIds.OpenSandboxWorldId, "Open Sandbox auto-load should not fall through to the gamemode menu");
            Assert(route.Request.GamemodeId == ZombiesGamemodeId, "Open Sandbox auto-load should keep the selected gamemode");
            Assert(string.IsNullOrWhiteSpace(route.Warning), "registered Open Sandbox should not warn");
        }

        private static void TestFirstPartySelectionLoadsDirectly()
        {
            var route = WorldAutoLoadRouter.Resolve(
                Worlds(),
                MenuEntries(),
                "robotopia.level.city",
                ZombiesGamemodeId,
                preferSceneReplacement: true,
                allowAdditiveFallback: false);

            Assert(route.Kind == WorldAutoLoadRouteKind.LoadSelection, "registered first-party world should load directly");
            Assert(route.Request != null, "registered first-party world should carry a load request");
            Assert(route.Request!.WorldId == "robotopia.level.city", "registered first-party world id should be preserved");
            Assert(route.Request.PreferSceneReplacement, "scene-replacement preference should be preserved");
            Assert(!route.Request.AllowAdditiveFallback, "additive fallback preference should be preserved");
        }

        private static void TestUnregisteredSelectionFallsBackToMenuEntry()
        {
            var route = WorldAutoLoadRouter.Resolve(
                Worlds(),
                MenuEntries(),
                "robotopia.level.missing",
                ZombiesGamemodeId,
                preferSceneReplacement: true,
                allowAdditiveFallback: true);

            Assert(route.Kind == WorldAutoLoadRouteKind.LaunchMenuEntry, "missing world should fall back to the gamemode menu entry");
            Assert(route.MenuEntryId == "robotopia.zombies.menu", "missing world fallback should launch the matching gamemode menu entry");
            Assert(route.Warning.Contains("robotopia.level.missing"), "missing world fallback should surface the stale world id");
        }

        private static WorldDefinition[] Worlds()
        {
            return new[]
            {
                new WorldDefinition(
                    WellKnownIds.OpenSandboxWorldId,
                    "Open Sandbox",
                    "Generated open-world sandbox arena."),
                new WorldDefinition(
                    "robotopia.level.city",
                    "City",
                    "First-party Robotopia level.",
                    "City",
                    firstParty: true,
                    supportsSceneReplacement: true,
                    supportsAdditiveArena: false)
            };
        }

        private static GamemodeMenuEntry[] MenuEntries()
        {
            return new[]
            {
                new GamemodeMenuEntry(
                    "robotopia.zombies.menu",
                    "Zombies",
                    "Survive escalating waves.",
                    ZombiesGamemodeId)
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
