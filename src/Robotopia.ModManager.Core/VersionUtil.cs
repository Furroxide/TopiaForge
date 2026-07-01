using System;
using System.Text.RegularExpressions;

namespace Robotopia.ModManager.Core
{
    public static class VersionUtil
    {
        private static readonly Regex RangePartRegex = new Regex(
            "(>=|>|<=|<|=)\\s*([0-9]+(?:\\.[0-9]+){0,2}(?:[-+][0-9A-Za-z_.-]+)?)",
            RegexOptions.Compiled);
        private static readonly Regex WildcardRangeRegex = new Regex(
            "^([0-9]+)(?:\\.([0-9]+|x|\\*))?(?:\\.([0-9]+|x|\\*))?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool TryParse(string text, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var normalized = text.Trim();
            var suffix = normalized.IndexOfAny(new[] { '-', '+' });
            if (suffix >= 0)
            {
                normalized = normalized.Substring(0, suffix);
            }

            var parts = normalized.Split('.');
            if (parts.Length == 1)
            {
                normalized += ".0.0";
            }
            else if (parts.Length == 2)
            {
                normalized += ".0";
            }

            return Version.TryParse(normalized, out version);
        }

        public static bool IsAtLeast(string actual, string required)
        {
            if (string.IsNullOrWhiteSpace(required))
            {
                return true;
            }

            if (!TryParse(actual, out var actualVersion) || !TryParse(required, out var requiredVersion))
            {
                return false;
            }

            return actualVersion >= requiredVersion;
        }

        public static bool AllowsRange(string actual, string range)
        {
            if (string.IsNullOrWhiteSpace(range) || range.Trim() == "*")
            {
                return true;
            }

            if (!TryParse(actual, out var actualVersion))
            {
                return false;
            }

            var text = range.Trim();
            if (AllowsWildcardRange(actualVersion, text, out var wildcardResult))
            {
                return wildcardResult;
            }

            if (!StartsWithRangeOperator(text))
            {
                return TryParse(text, out var exact) && actualVersion == exact;
            }

            var matches = RangePartRegex.Matches(text);
            if (matches.Count == 0)
            {
                return false;
            }

            foreach (Match match in matches)
            {
                if (!TryParse(match.Groups[2].Value, out var expected))
                {
                    return false;
                }

                var comparison = actualVersion.CompareTo(expected);
                switch (match.Groups[1].Value)
                {
                    case ">=":
                        if (comparison < 0)
                        {
                            return false;
                        }

                        break;
                    case ">":
                        if (comparison <= 0)
                        {
                            return false;
                        }

                        break;
                    case "<=":
                        if (comparison > 0)
                        {
                            return false;
                        }

                        break;
                    case "<":
                        if (comparison >= 0)
                        {
                            return false;
                        }

                        break;
                    case "=":
                        if (comparison != 0)
                        {
                            return false;
                        }

                        break;
                }
            }

            return true;
        }

        public static bool TryParseRange(string range)
        {
            if (string.IsNullOrWhiteSpace(range) || range.Trim() == "*")
            {
                return true;
            }

            var text = range.Trim();
            if (WildcardRangeRegex.IsMatch(text) && text.IndexOfAny(new[] { 'x', 'X', '*' }) >= 0)
            {
                return true;
            }

            if (!StartsWithRangeOperator(text))
            {
                return TryParse(text, out _);
            }

            var matches = RangePartRegex.Matches(text);
            if (matches.Count == 0)
            {
                return false;
            }

            foreach (Match match in matches)
            {
                if (!TryParse(match.Groups[2].Value, out _))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool StartsWithRangeOperator(string text)
        {
            return text.StartsWith(">=", StringComparison.Ordinal) ||
                   text.StartsWith(">", StringComparison.Ordinal) ||
                   text.StartsWith("<=", StringComparison.Ordinal) ||
                   text.StartsWith("<", StringComparison.Ordinal) ||
                   text.StartsWith("=", StringComparison.Ordinal);
        }

        private static bool AllowsWildcardRange(Version actualVersion, string text, out bool result)
        {
            result = false;
            var match = WildcardRangeRegex.Match(text);
            if (!match.Success || text.IndexOfAny(new[] { 'x', 'X', '*' }) < 0)
            {
                return false;
            }

            var major = int.Parse(match.Groups[1].Value);
            var minorText = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
            var patchText = match.Groups[3].Success ? match.Groups[3].Value : string.Empty;
            Version min;
            Version max;
            if (string.IsNullOrEmpty(minorText) || IsWildcard(minorText))
            {
                min = new Version(major, 0, 0);
                max = new Version(major + 1, 0, 0);
            }
            else if (string.IsNullOrEmpty(patchText) || IsWildcard(patchText))
            {
                var minor = int.Parse(minorText);
                min = new Version(major, minor, 0);
                max = new Version(major, minor + 1, 0);
            }
            else
            {
                var exact = new Version(major, int.Parse(minorText), int.Parse(patchText));
                result = actualVersion == exact;
                return true;
            }

            result = actualVersion >= min && actualVersion < max;
            return true;
        }

        private static bool IsWildcard(string value)
        {
            return value == "*" || value.Equals("x", StringComparison.OrdinalIgnoreCase);
        }
    }
}
