using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Finds a manager tab by its name rather than its place in the rail, so opening the overlay on a tab survives tabs being
    /// added or reordered. Names compare exactly, as the rail shows them. Unity-free, so the ModManager harness tests it.
    /// </summary>
    internal static class ManagerTabLookup
    {
        internal const string Gamemodes = "GAMEMODES";
        internal const string Installed = "INSTALLED";
        internal const string Multiplayer = "MULTIPLAYER";

        /// <summary>Gets the index of the tab named <paramref name="name"/>, or -1 when no tab has that name.</summary>
        internal static int IndexOf(IReadOnlyList<string> titles, string name)
        {
            if (titles == null) throw new ArgumentNullException(nameof(titles));
            if (string.IsNullOrEmpty(name)) return -1;
            for (var index = 0; index < titles.Count; index++)
            {
                if (string.Equals(titles[index], name, StringComparison.Ordinal)) return index;
            }

            return -1;
        }

        /// <summary>Throws unless every tab has a distinct, non-empty name, which name-based lookup relies on.</summary>
        internal static void RequireDistinct(IReadOnlyList<string> titles)
        {
            if (titles == null) throw new ArgumentNullException(nameof(titles));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var title in titles)
            {
                if (string.IsNullOrEmpty(title)) throw new InvalidOperationException("Every manager tab needs a name.");
                if (!seen.Add(title)) throw new InvalidOperationException("Two manager tabs are named " + title + ".");
            }
        }
    }
}
