using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>The world a launch intent resolved to, or why it could not be resolved.</summary>
    internal readonly struct WorldLaunchRoute
    {
        private WorldLaunchRoute(bool resolved, string worldId, string warning)
        {
            Resolved = resolved;
            WorldId = worldId;
            Warning = warning;
        }

        public bool Resolved { get; }

        public string WorldId { get; }

        /// <summary>A condition worth reporting even on success — e.g. a stale configured world.</summary>
        public string Warning { get; }

        public static WorldLaunchRoute Success(string worldId, string warning = "")
        {
            return new WorldLaunchRoute(true, worldId, warning);
        }

        public static WorldLaunchRoute Failure(string warning)
        {
            return new WorldLaunchRoute(false, string.Empty, warning);
        }
    }

    /// <summary>
    /// Turns "start this gamemode" into "load this world with this gamemode".
    /// <para>
    /// A gamemode does not imply a world on its own, so the caller may name one. When it does not — or
    /// names one the provider does not have — the gamemode's own registered menu entry supplies the
    /// world its author intended, which is almost always the better answer than a stale saved id.
    /// </para>
    /// <para>
    /// Deliberately Unity-free and side-effect-free, so the routing decisions are unit-tested rather
    /// than only observable by launching a game.
    /// </para>
    /// </summary>
    internal static class WorldLaunchRouter
    {
        public static WorldLaunchRoute Resolve(
            IReadOnlyList<WorldDefinition>? worlds,
            IReadOnlyList<GamemodeMenuEntry>? menuEntries,
            string? requestedWorldId,
            string? gamemodeId)
        {
            if (string.IsNullOrWhiteSpace(gamemodeId))
            {
                return WorldLaunchRoute.Failure("No gamemode was requested.");
            }

            var requested = requestedWorldId ?? string.Empty;
            if (requested.Length > 0 && IsRegisteredWorld(worlds, requested))
            {
                return WorldLaunchRoute.Success(requested);
            }

            var warning = requested.Length == 0
                ? string.Empty
                : "Launch intent: world '" + requested
                    + "' is not registered (the level list may not have loaded); "
                    + "falling back to the gamemode's own world.";

            var entryWorldId = FindMenuEntryWorld(menuEntries, gamemodeId!);
            if (entryWorldId.Length > 0 && IsRegisteredWorld(worlds, entryWorldId))
            {
                return WorldLaunchRoute.Success(entryWorldId, warning);
            }

            return WorldLaunchRoute.Failure(
                warning.Length > 0
                    ? warning + " No registered world could be resolved for gamemode '" + gamemodeId + "'."
                    : "No registered world could be resolved for gamemode '" + gamemodeId + "'.");
        }

        private static string FindMenuEntryWorld(
            IReadOnlyList<GamemodeMenuEntry>? menuEntries,
            string gamemodeId)
        {
            if (menuEntries == null)
            {
                return string.Empty;
            }

            foreach (var entry in menuEntries)
            {
                if (entry != null
                    && string.Equals(entry.GamemodeId, gamemodeId, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(entry.WorldId))
                {
                    return entry.WorldId;
                }
            }

            return string.Empty;
        }

        private static bool IsRegisteredWorld(IReadOnlyList<WorldDefinition>? worlds, string worldId)
        {
            if (worlds == null)
            {
                return false;
            }

            foreach (var world in worlds)
            {
                if (world != null && string.Equals(world.Id, worldId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
