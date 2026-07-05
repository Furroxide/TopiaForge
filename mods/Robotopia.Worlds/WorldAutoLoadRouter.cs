using System;
using System.Collections.Generic;
using Robotopia.Mods;

namespace Robotopia.Worlds
{
    internal enum WorldAutoLoadRouteKind
    {
        LoadSelection,
        LaunchMenuEntry
    }

    internal sealed class WorldAutoLoadRoute
    {
        private WorldAutoLoadRoute(
            WorldAutoLoadRouteKind kind,
            WorldLoadRequest? request,
            string menuEntryId,
            bool preferSceneReplacement,
            bool allowAdditiveFallback,
            string warning)
        {
            Kind = kind;
            Request = request;
            MenuEntryId = menuEntryId;
            PreferSceneReplacement = preferSceneReplacement;
            AllowAdditiveFallback = allowAdditiveFallback;
            Warning = warning;
        }

        public WorldAutoLoadRouteKind Kind { get; }
        public WorldLoadRequest? Request { get; }
        public string MenuEntryId { get; }
        public bool PreferSceneReplacement { get; }
        public bool AllowAdditiveFallback { get; }
        public string Warning { get; }

        public static WorldAutoLoadRoute LoadSelection(WorldLoadRequest request, string warning = "")
        {
            return new WorldAutoLoadRoute(
                WorldAutoLoadRouteKind.LoadSelection,
                request,
                string.Empty,
                request.PreferSceneReplacement,
                request.AllowAdditiveFallback,
                warning);
        }

        public static WorldAutoLoadRoute LaunchMenuEntry(
            string menuEntryId,
            bool preferSceneReplacement,
            bool allowAdditiveFallback,
            string warning = "")
        {
            return new WorldAutoLoadRoute(
                WorldAutoLoadRouteKind.LaunchMenuEntry,
                null,
                menuEntryId,
                preferSceneReplacement,
                allowAdditiveFallback,
                warning);
        }
    }

    internal static class WorldAutoLoadRouter
    {
        public static WorldAutoLoadRoute Resolve(
            IReadOnlyList<WorldDefinition> worlds,
            IReadOnlyList<GamemodeMenuEntry> menuEntries,
            string selectedWorldId,
            string selectedGamemodeId,
            bool preferSceneReplacement,
            bool allowAdditiveFallback)
        {
            var hasSelectedWorld = !string.IsNullOrWhiteSpace(selectedWorldId);
            var worldId = selectedWorldId ?? string.Empty;
            var gamemodeId = selectedGamemodeId ?? string.Empty;

            if (hasSelectedWorld && IsRegisteredWorld(worlds, worldId))
            {
                return WorldAutoLoadRoute.LoadSelection(new WorldLoadRequest(
                    worldId,
                    gamemodeId,
                    preferSceneReplacement,
                    allowAdditiveFallback));
            }

            var warning = string.Empty;
            if (hasSelectedWorld)
            {
                warning = "Auto-launch: configured world '" + worldId
                    + "' is not registered (the level list may not have loaded); falling back to the gamemode's default world.";
            }

            foreach (var entry in menuEntries)
            {
                if (string.Equals(entry.GamemodeId, gamemodeId, StringComparison.OrdinalIgnoreCase))
                {
                    return WorldAutoLoadRoute.LaunchMenuEntry(
                        entry.Id,
                        preferSceneReplacement,
                        allowAdditiveFallback,
                        warning);
                }
            }

            return WorldAutoLoadRoute.LoadSelection(new WorldLoadRequest(
                worldId,
                gamemodeId,
                preferSceneReplacement,
                allowAdditiveFallback), warning);
        }

        private static bool IsRegisteredWorld(IReadOnlyList<WorldDefinition> worlds, string worldId)
        {
            foreach (var world in worlds)
            {
                if (string.Equals(world.Id, worldId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
