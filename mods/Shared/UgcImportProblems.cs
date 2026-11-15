using System;
using System.Collections;
using System.Reflection;

namespace TopiaForge.Mods.GameBridge
{
    /// <summary>
    /// Shared clean-room reader for the problem report that Robotopia build 2640 added to its UGC importer, scene
    /// builder and environment applier. The game shows that report to the player after its own imports; TopiaForge
    /// logs a fixed-text summary instead. Problem titles and details are native text that can name assets, files or
    /// URLs, so only the count leaves this helper. Source-linked into Worlds and RobotKit so the rule lives in one
    /// place. Unity-free and GameCode-free on purpose: callers pass the report instance.
    /// </summary>
    internal static class UgcImportProblems
    {
        /// <summary>Returned when the report or its <c>Problems</c> list cannot be read.</summary>
        internal const int Unreadable = -1;

        // A bounded walk keeps a hostile or runaway enumerable from stalling the caller.
        private const int MaxCounted = 10000;

        /// <summary>Counts the entries of the report's public <c>Problems</c> list, or <see cref="Unreadable"/>.</summary>
        internal static int Count(object? report)
        {
            try
            {
                var property = report?.GetType().GetProperty("Problems", BindingFlags.Public | BindingFlags.Instance);
                if (property == null || property.GetIndexParameters().Length != 0 || property.GetMethod == null) return Unreadable;
                switch (property.GetValue(report))
                {
                    case ICollection collection:
                        return Math.Min(collection.Count, MaxCounted);
                    case IEnumerable entries:
                        var count = 0;
                        foreach (var _ in entries)
                            if (++count >= MaxCounted) break;
                        return count;
                    default:
                        return Unreadable;
                }
            }
            catch (Exception)
            {
                return Unreadable;
            }
        }

        /// <summary>
        /// A fixed-text summary of the report for a log line, or null when the game reported nothing.
        /// <paramref name="operation"/> is a constant the caller supplies, such as "importing a map".
        /// </summary>
        internal static string? Summary(string operation, object? report)
        {
            var count = Count(report);
            if (count == 0) return null;
            return count == Unreadable
                ? "The game's problem report for " + operation + " could not be read."
                : "The game reported " + count + (count == 1 ? " problem" : " problems") + " while " + operation
                    + ". Details are not logged because they can name files or assets.";
        }

        /// <summary>Writes <see cref="Summary"/> as a warning prefixed by <paramref name="source"/>. Never throws.</summary>
        internal static void Warn(TopiaForge.Mods.IModLogger? logger, string source, string operation, object? report)
        {
            var summary = Summary(operation, report);
            if (logger == null || summary == null) return;
            try { logger.Warn(source + ": " + summary); }
            catch (Exception) { /* A diagnostic cannot fail the native call it describes. */ }
        }
    }
}
