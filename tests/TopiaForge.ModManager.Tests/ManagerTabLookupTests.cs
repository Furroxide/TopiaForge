using System;
using System.IO;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The overlay finds its tabs by name: inserting MULTIPLAYER moved INSTALLED from the second place to the third, and every
    /// entry point still opens the tab it names.
    /// </summary>
    internal static class ManagerTabLookupTests
    {
        internal static void Run()
        {
            NamesFindTheirTabsWhereverTheyAre();
            NamesMustBeDistinct();
            TheOverlayOpensTabsByName();
            Console.WriteLine("ManagerTabLookupTests passed.");
        }

        private static void NamesFindTheirTabsWhereverTheyAre()
        {
            var rail = new[] { "GAMEMODES", "MULTIPLAYER", "INSTALLED", "PACKAGES", "SETTINGS", "LOGS" };
            That(ManagerTabLookup.IndexOf(rail, ManagerTabLookup.Gamemodes) == 0, "GAMEMODES is found");
            That(ManagerTabLookup.IndexOf(rail, ManagerTabLookup.Multiplayer) == 1, "MULTIPLAYER is found");
            That(ManagerTabLookup.IndexOf(rail, ManagerTabLookup.Installed) == 2, "INSTALLED is found in its new place");
            var reordered = new[] { "LOGS", "INSTALLED", "GAMEMODES" };
            That(ManagerTabLookup.IndexOf(reordered, ManagerTabLookup.Installed) == 1 && ManagerTabLookup.IndexOf(reordered, ManagerTabLookup.Gamemodes) == 2,
                "a reordered rail finds the same tabs");
            That(ManagerTabLookup.IndexOf(reordered, ManagerTabLookup.Multiplayer) == -1, "a missing tab is not found");
            That(ManagerTabLookup.IndexOf(rail, "installed") == -1 && ManagerTabLookup.IndexOf(rail, string.Empty) == -1, "names compare exactly");
            Throws<ArgumentNullException>(() => ManagerTabLookup.IndexOf(null!, "LOGS"), "a missing rail is refused");
        }

        private static void NamesMustBeDistinct()
        {
            ManagerTabLookup.RequireDistinct(new[] { "A", "B" });
            Throws<InvalidOperationException>(() => ManagerTabLookup.RequireDistinct(new[] { "A", "A" }), "two tabs with one name are refused");
            Throws<InvalidOperationException>(() => ManagerTabLookup.RequireDistinct(new[] { "A", string.Empty }), "a tab without a name is refused");
        }

        /// <summary>The overlay's entry points name their tabs; no hard-coded index is left.</summary>
        private static void TheOverlayOpensTabsByName()
        {
            var source = File.ReadAllText(Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.ModManager", "Overlay", "ManagerOverlay.cs"));
            That(source.Contains("OpenAt(ManagerTabLookup.Installed)", StringComparison.Ordinal) &&
                 source.Contains("OpenAt(ManagerTabLookup.Gamemodes)", StringComparison.Ordinal) &&
                 source.Contains("OpenAt(ManagerTabLookup.Multiplayer)", StringComparison.Ordinal), "Show, ShowGamemodes and ShowMultiplayer open tabs by name");
            That(!System.Text.RegularExpressions.Regex.IsMatch(source, @"OpenAt\(\s*\d"), "no entry point opens a tab by index");
            That(source.Contains("ManagerTabLookup.RequireDistinct(titles)", StringComparison.Ordinal), "the rail's names are checked to be distinct");
            var injector = File.ReadAllText(Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.ModManager", "MenuButtonInjector.cs"));
            That(injector.Contains("\"MULTIPLAYER\", () => overlay.ShowMultiplayer()", StringComparison.Ordinal), "the main menu's MULTIPLAYER button opens the Multiplayer tab");
            That(injector.Contains("overlay.HasMultiplayer == mountedWithMultiplayer", StringComparison.Ordinal),
                "and the menu mounts again when the multiplayer provider attaches after its buttons went up");
        }
    }
}
