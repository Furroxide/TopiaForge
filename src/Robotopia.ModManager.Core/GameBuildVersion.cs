using System;
using System.Globalization;

namespace Robotopia.ModManager.Core
{
    /// <summary>
    /// Converts Robotopia's monotonically increasing launcher build id into the SemVer value used by
    /// manifest compatibility ranges. Build 2227 is represented as 0.0.2227. This type is deliberately
    /// filesystem- and Unity-free so every runtime and launcher implementation can share the same rule.
    /// </summary>
    public static class GameBuildVersion
    {
        private const string BuildPrefix = "build ";

        public static bool TryFromBuildId(string? buildId, out string semanticVersion)
        {
            semanticVersion = string.Empty;
            if (string.IsNullOrEmpty(buildId))
            {
                return false;
            }

            if ((buildId.Length > 1 && buildId[0] == '0') || !ContainsOnlyAsciiDigits(buildId))
            {
                return false;
            }

            if (!long.TryParse(buildId, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value <= 0 ||
                value > int.MaxValue)
            {
                return false;
            }

            semanticVersion = "0.0." + value.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        public static bool TryFromBuildLabel(string? label, out string semanticVersion)
        {
            semanticVersion = string.Empty;
            if (string.IsNullOrEmpty(label))
            {
                return false;
            }

            var value = label.StartsWith(BuildPrefix, StringComparison.OrdinalIgnoreCase)
                ? label.Substring(BuildPrefix.Length)
                : label;
            return TryFromBuildId(value, out semanticVersion);
        }

        /// <summary>
        /// Accepts a canonical SemVer value or a launcher build id/label. The returned SemVer is suitable
        /// for <see cref="VersionUtil.AllowsRange"/>. Whitespace is rejected rather than silently normalized.
        /// </summary>
        public static bool TryNormalize(string? value, out string semanticVersion)
        {
            semanticVersion = string.Empty;
            if (VersionUtil.TryParseSemantic(value, out _))
            {
                semanticVersion = value!;
                return true;
            }

            return TryFromBuildLabel(value, out semanticVersion);
        }

        private static bool ContainsOnlyAsciiDigits(string value)
        {
            foreach (var character in value)
            {
                if (character < '0' || character > '9')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
