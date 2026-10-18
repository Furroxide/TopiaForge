using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The games HOST can start at the main menu, from the launch targets the enabled packages declare: each choice that
    /// replaces the scene, named by its target's title and its world's name. A choice that adds an arena to a running game
    /// needs that game, which the menu has not got, so its launch is refused there; HOST leaves it out. A running game is
    /// opened in place instead, with no choice to make.
    /// </summary>
    internal static class HostTargets
    {
        private static readonly IReadOnlyDictionary<string, string> NoNames = new Dictionary<string, string>();

        /// <param name="previews">The launch targets the enabled packages declare.</param>
        /// <param name="worldNames">The names players know the worlds by, by world id (<see cref="WorldNames"/>).</param>
        internal static IReadOnlyList<HostTargetChoice> AtTheMenu(IEnumerable<LaunchTargetPreview> previews, IReadOnlyDictionary<string, string>? worldNames = null)
        {
            if (previews == null) throw new ArgumentNullException(nameof(previews));
            var choices = new List<HostTargetChoice>();
            foreach (var preview in previews)
            {
                foreach (var choice in preview.Choices)
                {
                    if (string.Equals(choice.Transition, ModTransitions.AdditiveArena, StringComparison.Ordinal)) continue;
                    var world = WorldName(choice.WorldId, worldNames);
                    choices.Add(new HostTargetChoice(preview.Title + " · " + world, choice.Request, preview.Title, world));
                }
            }

            return choices.AsReadOnly();
        }

        /// <summary>
        /// Gets the name players know a world by: the one its package declares, or the one the game's discovery gave it, as
        /// for a native level. A world with neither keeps its id. HOST lists games by it, and a hosted session announces it
        /// to the players browsing for one.
        /// </summary>
        internal static string WorldName(string? worldId, IReadOnlyDictionary<string, string>? worldNames)
        {
            if (string.IsNullOrEmpty(worldId)) return string.Empty;
            return worldNames != null && worldNames.TryGetValue(worldId!, out var name) && !string.IsNullOrWhiteSpace(name) ? name : worldId!;
        }

        /// <summary>
        /// Gets the worlds' names by id, ignoring case as world ids are compared: each world a package declares, by its
        /// declared name, and each world the game discovered for this profile, such as a native level, by the name its
        /// discovery gave it.
        /// </summary>
        internal static IReadOnlyDictionary<string, string> WorldNames(EffectiveProfile? profile, RuntimeObservation? observation)
        {
            if (profile == null) return NoNames;
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in profile.Packages)
            {
                var worlds = package.Snapshot.Contributions?.Worlds;
                if (worlds == null) continue;
                foreach (var world in worlds) Remember(names, world.Id, world.Name);
            }

            foreach (var world in (observation ?? RuntimeObservation.None).ForProfile(profile).DiscoveredWorlds) Remember(names, world.Id, world.Name);
            return names;
        }

        private static void Remember(Dictionary<string, string> names, string? id, string? name)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(name) || names.ContainsKey(id!)) return;
            names[id!] = name!.Trim();
        }
    }
}
