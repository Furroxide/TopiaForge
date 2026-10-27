using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.Mods.Analyzers
{
    internal sealed class EnvironmentPresetCatalogue
    {
        // Engine metadata proves the lookup shape, not the shipped asset keys. Qualify only after native capture.
        internal static readonly EnvironmentPresetCatalogue Shipped = new EnvironmentPresetCatalogue("0.0.2545", false, Array.Empty<string>());
        internal EnvironmentPresetCatalogue(string build, bool verified, IEnumerable<string> keys)
        {
            Build = build;
            Keys = keys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Verified = verified && Keys.Length > 0 && Keys.Length <= 256 && Keys.All(ValidKey);
        }
        internal string Build { get; }
        internal bool Verified { get; }
        internal string[] Keys { get; }
        internal static bool ValidKey(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 64
            && !value.Any(c => char.IsControl(c) || c == '|' || c == '"' || c == '\\');
        internal string[] Nearest(string key) => Keys.OrderBy(k => Distance(key, k)).ThenBy(k => k, StringComparer.Ordinal).Take(3).ToArray();
        private static int Distance(string left, string right)
        {
            var row = Enumerable.Range(0, right.Length + 1).ToArray();
            for (var i = 1; i <= left.Length; i++)
            {
                var diagonal = row[0]; row[0] = i;
                for (var j = 1; j <= right.Length; j++)
                { var previous = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (left[i - 1] == right[j - 1] ? 0 : 1)); diagonal = previous; }
            }
            return row[right.Length];
        }
    }
}
