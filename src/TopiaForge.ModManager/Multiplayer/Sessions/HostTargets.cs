using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The games HOST can start at the main menu, from the launch targets the enabled packages declare: each choice that
    /// replaces the scene, named by its target's title and its world. A choice that adds an arena to a running game needs
    /// that game, which the menu has not got, so its launch is refused there; HOST leaves it out. A running game is opened
    /// in place instead, with no choice to make.
    /// </summary>
    internal static class HostTargets
    {
        internal static IReadOnlyList<HostTargetChoice> AtTheMenu(IEnumerable<LaunchTargetPreview> previews)
        {
            if (previews == null) throw new ArgumentNullException(nameof(previews));
            var choices = new List<HostTargetChoice>();
            foreach (var preview in previews)
            {
                foreach (var choice in preview.Choices)
                {
                    if (string.Equals(choice.Transition, ModTransitions.AdditiveArena, StringComparison.Ordinal)) continue;
                    choices.Add(new HostTargetChoice(preview.Title + " · " + choice.WorldId, choice.Request, preview.Title, choice.WorldId));
                }
            }

            return choices.AsReadOnly();
        }
    }
}
