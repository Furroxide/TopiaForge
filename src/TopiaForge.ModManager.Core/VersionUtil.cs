using System;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Core
{
    public static class VersionUtil
    {
        private static readonly Regex SemanticVersionRegex = new Regex(
            "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)" +
            "(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?" +
            "(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
            RegexOptions.Compiled);
        private static readonly Regex NumericIdentifierRegex = new Regex(
            "^[0-9]+$",
            RegexOptions.Compiled);
        private static readonly Regex RangePartRegex = new Regex(
            "(>=|>|<=|<|=)\\s*([^\\s]+)",
            RegexOptions.Compiled);
        private static readonly Regex WildcardRangeRegex = new Regex(
            "^(0|[1-9][0-9]*)(?:\\.(0|[1-9][0-9]*|x|\\*))?" +
            "(?:\\.(0|[1-9][0-9]*|x|\\*))?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Strictly validates SemVer 2.0 and returns the three-component compatibility value used by the
        /// established SDK surface. Prerelease/build data is retained by the internal parser used for ordering.
        /// </summary>
        public static bool TryParse(string? text, out Version version)
        {
            version = new Version(0, 0, 0);
            if (!TryParseSemantic(text, out var semantic))
            {
                return false;
            }

            version = semantic.CoreVersion;
            return true;
        }

        public static bool IsAtLeast(string actual, string required)
        {
            if (string.IsNullOrWhiteSpace(required))
            {
                return true;
            }

            return TryParseSemantic(actual, out var actualVersion) &&
                   TryParseSemantic(required, out var requiredVersion) &&
                   actualVersion.CompareTo(requiredVersion) >= 0;
        }

        public static bool AllowsRange(string actual, string range)
        {
            return TryParseSemantic(actual, out var actualVersion) &&
                   TryParseRange(range, out var parsedRange) &&
                   parsedRange.Allows(actualVersion);
        }

        public static bool TryParseRange(string? range)
        {
            return TryParseRange(range, out _);
        }

        internal static bool TryParseSemantic(string? text, out ParsedSemanticVersion version)
        {
            version = default;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var match = SemanticVersionRegex.Match(text);
            if (!match.Success)
            {
                return false;
            }

            var prerelease = match.Groups[4].Success ? match.Groups[4].Value : string.Empty;
            if (!HasValidPrereleaseIdentifiers(prerelease))
            {
                return false;
            }

            version = new ParsedSemanticVersion(
                match.Groups[1].Value,
                match.Groups[2].Value,
                match.Groups[3].Value,
                prerelease,
                match.Groups[5].Success ? match.Groups[5].Value : string.Empty);
            return true;
        }

        private static bool TryParseRange(string? range, out ParsedRange parsedRange)
        {
            parsedRange = ParsedRange.Any;
            var text = range?.Trim() ?? string.Empty;
            if (text.Length == 0 || text == "*")
            {
                return true;
            }

            var wildcardMatch = WildcardRangeRegex.Match(text);
            if (wildcardMatch.Success &&
                ((wildcardMatch.Groups[2].Success && IsWildcard(wildcardMatch.Groups[2].Value)) ||
                 (wildcardMatch.Groups[3].Success && IsWildcard(wildcardMatch.Groups[3].Value))))
            {
                if (!TryGetWildcardBounds(text, out var wildcardMin, out var wildcardMax))
                {
                    return false;
                }

                parsedRange = new ParsedRange(
                    wildcardMin,
                    wildcardMax,
                    includeMin: true,
                    includeMax: false);
                return true;
            }

            if (!StartsWithRangeOperator(text))
            {
                if (!TryParseRangeVersion(text, out var exact))
                {
                    return false;
                }

                parsedRange = new ParsedRange(exact, exact, includeMin: true, includeMax: true);
                return true;
            }

            var matches = RangePartRegex.Matches(text);
            if (matches.Count == 0)
            {
                return false;
            }

            ParsedSemanticVersion? min = null;
            ParsedSemanticVersion? max = null;
            var includeMin = true;
            var includeMax = false;
            var cursor = 0;
            foreach (Match match in matches)
            {
                var separator = text.Substring(cursor, match.Index - cursor);
                var isFirst = cursor == 0;
                if (!string.IsNullOrWhiteSpace(separator) || (!isFirst && separator.Length == 0) ||
                    !TryParseRangeVersion(match.Groups[2].Value, out var candidate))
                {
                    return false;
                }

                switch (match.Groups[1].Value)
                {
                    case ">=":
                        ApplyMinimum(ref min, ref includeMin, candidate, inclusive: true);
                        break;
                    case ">":
                        ApplyMinimum(ref min, ref includeMin, candidate, inclusive: false);
                        break;
                    case "<=":
                        ApplyMaximum(ref max, ref includeMax, candidate, inclusive: true);
                        break;
                    case "<":
                        ApplyMaximum(ref max, ref includeMax, candidate, inclusive: false);
                        break;
                    case "=":
                        ApplyMinimum(ref min, ref includeMin, candidate, inclusive: true);
                        ApplyMaximum(ref max, ref includeMax, candidate, inclusive: true);
                        break;
                }

                cursor = match.Index + match.Length;
            }

            if (!string.IsNullOrWhiteSpace(text.Substring(cursor)) || !BoundsAreSatisfiable(min, max, includeMin, includeMax))
            {
                return false;
            }

            parsedRange = new ParsedRange(min, max, includeMin, includeMax);
            return true;
        }

        private static bool TryParseRangeVersion(string value, out ParsedSemanticVersion version)
        {
            return TryParseSemantic(value, out version);
        }

        private static bool TryGetWildcardBounds(
            string text,
            out ParsedSemanticVersion minimum,
            out ParsedSemanticVersion maximum)
        {
            minimum = default;
            maximum = default;
            var match = WildcardRangeRegex.Match(text);
            if (!match.Success || text.IndexOfAny(new[] { 'x', 'X', '*' }) < 0)
            {
                return false;
            }

            var major = match.Groups[1].Value;
            var minorText = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
            var patchText = match.Groups[3].Success ? match.Groups[3].Value : string.Empty;
            if (string.IsNullOrEmpty(minorText) || IsWildcard(minorText))
            {
                if (!string.IsNullOrEmpty(patchText) && !IsWildcard(patchText))
                {
                    return false;
                }

                minimum = new ParsedSemanticVersion(major, "0", "0");
                maximum = new ParsedSemanticVersion(IncrementNumericIdentifier(major), "0", "0");
                return true;
            }

            if (string.IsNullOrEmpty(patchText) || IsWildcard(patchText))
            {
                minimum = new ParsedSemanticVersion(major, minorText, "0");
                maximum = new ParsedSemanticVersion(major, IncrementNumericIdentifier(minorText), "0");
                return true;
            }

            return false;
        }

        private static bool HasValidPrereleaseIdentifiers(string prerelease)
        {
            if (prerelease.Length == 0)
            {
                return true;
            }

            foreach (var identifier in prerelease.Split('.'))
            {
                if (NumericIdentifierRegex.IsMatch(identifier) &&
                    identifier.Length > 1 &&
                    identifier[0] == '0')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool BoundsAreSatisfiable(
            ParsedSemanticVersion? min,
            ParsedSemanticVersion? max,
            bool includeMin,
            bool includeMax)
        {
            if (min == null || max == null)
            {
                return true;
            }

            var comparison = min.Value.CompareTo(max.Value);
            return comparison < 0 || (comparison == 0 && includeMin && includeMax);
        }

        private static void ApplyMinimum(
            ref ParsedSemanticVersion? current,
            ref bool includeCurrent,
            ParsedSemanticVersion candidate,
            bool inclusive)
        {
            var comparison = current == null ? 1 : candidate.CompareTo(current.Value);
            if (comparison > 0)
            {
                current = candidate;
                includeCurrent = inclusive;
            }
            else if (comparison == 0)
            {
                includeCurrent = includeCurrent && inclusive;
            }
        }

        private static void ApplyMaximum(
            ref ParsedSemanticVersion? current,
            ref bool includeCurrent,
            ParsedSemanticVersion candidate,
            bool inclusive)
        {
            var comparison = current == null ? -1 : candidate.CompareTo(current.Value);
            if (comparison < 0)
            {
                current = candidate;
                includeCurrent = inclusive;
            }
            else if (comparison == 0)
            {
                includeCurrent = includeCurrent && inclusive;
            }
        }

        private static bool StartsWithRangeOperator(string text)
        {
            return text.StartsWith(">=", StringComparison.Ordinal) ||
                   text.StartsWith(">", StringComparison.Ordinal) ||
                   text.StartsWith("<=", StringComparison.Ordinal) ||
                   text.StartsWith("<", StringComparison.Ordinal) ||
                   text.StartsWith("=", StringComparison.Ordinal);
        }

        private static bool IsWildcard(string value)
        {
            return value == "*" || value.Equals("x", StringComparison.OrdinalIgnoreCase);
        }

        private static string IncrementNumericIdentifier(string value)
        {
            var characters = value.ToCharArray();
            for (var index = characters.Length - 1; index >= 0; index--)
            {
                if (characters[index] != '9')
                {
                    characters[index]++;
                    return new string(characters);
                }

                characters[index] = '0';
            }

            return "1" + new string(characters);
        }

        internal readonly struct ParsedSemanticVersion : IComparable<ParsedSemanticVersion>
        {
            public ParsedSemanticVersion(
                int major,
                int minor,
                int patch,
                string prerelease = "",
                string buildMetadata = "")
                : this(
                    major.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    minor.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    patch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    prerelease,
                    buildMetadata)
            {
            }

            public ParsedSemanticVersion(
                string major,
                string minor,
                string patch,
                string prerelease = "",
                string buildMetadata = "")
            {
                Major = major;
                Minor = minor;
                Patch = patch;
                Prerelease = prerelease;
                BuildMetadata = buildMetadata;
            }

            public string Major { get; }
            public string Minor { get; }
            public string Patch { get; }
            public string Prerelease { get; }
            public string BuildMetadata { get; }
            // System.Version cannot represent SemVer core identifiers larger than Int32. Preserve the original
            // compatibility surface with a saturated projection while all validation and ordering uses the
            // unbounded canonical digit strings above.
            public Version CoreVersion => new Version(
                ToLegacyComponent(Major),
                ToLegacyComponent(Minor),
                ToLegacyComponent(Patch));

            public int CompareTo(ParsedSemanticVersion other)
            {
                var comparison = CompareNumericIdentifier(Major, other.Major);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = CompareNumericIdentifier(Minor, other.Minor);
                if (comparison != 0)
                {
                    return comparison;
                }

                comparison = CompareNumericIdentifier(Patch, other.Patch);
                if (comparison != 0)
                {
                    return comparison;
                }

                if (Prerelease.Length == 0)
                {
                    return other.Prerelease.Length == 0 ? 0 : 1;
                }

                if (other.Prerelease.Length == 0)
                {
                    return -1;
                }

                var identifiers = Prerelease.Split('.');
                var otherIdentifiers = other.Prerelease.Split('.');
                var sharedLength = Math.Min(identifiers.Length, otherIdentifiers.Length);
                for (var index = 0; index < sharedLength; index++)
                {
                    comparison = ComparePrereleaseIdentifier(identifiers[index], otherIdentifiers[index]);
                    if (comparison != 0)
                    {
                        return comparison;
                    }
                }

                return identifiers.Length.CompareTo(otherIdentifiers.Length);
            }

            private static int ComparePrereleaseIdentifier(string left, string right)
            {
                var leftIsNumeric = NumericIdentifierRegex.IsMatch(left);
                var rightIsNumeric = NumericIdentifierRegex.IsMatch(right);
                if (leftIsNumeric && rightIsNumeric)
                {
                    var lengthComparison = left.Length.CompareTo(right.Length);
                    return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
                }

                if (leftIsNumeric)
                {
                    return -1;
                }

                if (rightIsNumeric)
                {
                    return 1;
                }

                return string.CompareOrdinal(left, right);
            }

            private static int CompareNumericIdentifier(string left, string right)
            {
                var lengthComparison = left.Length.CompareTo(right.Length);
                return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
            }

            private static int ToLegacyComponent(string value)
            {
                return int.TryParse(
                    value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var component)
                    ? component
                    : int.MaxValue;
            }
        }

        private readonly struct ParsedRange
        {
            public static ParsedRange Any { get; } = new ParsedRange(null, null, true, true);

            public ParsedRange(
                ParsedSemanticVersion? min,
                ParsedSemanticVersion? max,
                bool includeMin,
                bool includeMax)
            {
                Min = min;
                Max = max;
                IncludeMin = includeMin;
                IncludeMax = includeMax;
            }

            private ParsedSemanticVersion? Min { get; }
            private ParsedSemanticVersion? Max { get; }
            private bool IncludeMin { get; }
            private bool IncludeMax { get; }

            public bool Allows(ParsedSemanticVersion version)
            {
                if (Min != null)
                {
                    var comparison = version.CompareTo(Min.Value);
                    if (comparison < 0 || (comparison == 0 && !IncludeMin))
                    {
                        return false;
                    }
                }

                if (Max != null)
                {
                    var comparison = version.CompareTo(Max.Value);
                    if (comparison > 0 || (comparison == 0 && !IncludeMax))
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
