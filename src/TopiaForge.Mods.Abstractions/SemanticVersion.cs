using System;
using System.Text.RegularExpressions;

namespace TopiaForge.Mods
{
    /// <summary>
    /// Represents a Semantic Versioning 2.0.0 value without discarding prerelease or build metadata.
    /// Core numeric identifiers are retained as strings so valid values are not limited by integer size.
    /// </summary>
    public readonly struct SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
    {
        private readonly string? major;
        private readonly string? minor;
        private readonly string? patch;
        private readonly string? prerelease;
        private readonly string? buildMetadata;

        private static readonly Regex Pattern = new Regex(
            "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)" +
            "(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?" +
            "(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
            RegexOptions.Compiled);

        private SemanticVersion(
            string major,
            string minor,
            string patch,
            string prerelease,
            string buildMetadata)
        {
            this.major = major;
            this.minor = minor;
            this.patch = patch;
            this.prerelease = prerelease;
            this.buildMetadata = buildMetadata;
        }

        /// <summary>Gets the canonical, unbounded major numeric identifier.</summary>
        public string Major => major ?? "0";

        /// <summary>Gets the canonical, unbounded minor numeric identifier.</summary>
        public string Minor => minor ?? "0";

        /// <summary>Gets the canonical, unbounded patch numeric identifier.</summary>
        public string Patch => patch ?? "0";

        /// <summary>Gets the dot-separated prerelease identifiers, or an empty string for a stable release.</summary>
        public string Prerelease => prerelease ?? string.Empty;

        /// <summary>Gets the dot-separated build metadata, or an empty string when none was supplied.</summary>
        public string BuildMetadata => buildMetadata ?? string.Empty;

        /// <summary>Gets whether this version has prerelease identifiers.</summary>
        public bool IsPrerelease => Prerelease.Length != 0;

        /// <summary>Parses a complete Semantic Versioning 2.0.0 value.</summary>
        /// <param name="value">The version text to parse.</param>
        /// <returns>The parsed semantic version.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
        /// <exception cref="FormatException"><paramref name="value"/> is not a canonical semantic version.</exception>
        public static SemanticVersion Parse(string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (!TryParse(value, out var version))
            {
                throw new FormatException("Value is not a valid Semantic Versioning 2.0.0 version: " + value);
            }

            return version;
        }

        /// <summary>Tries to parse a complete Semantic Versioning 2.0.0 value.</summary>
        /// <param name="value">The version text to parse.</param>
        /// <param name="version">Receives the parsed semantic version when this method succeeds.</param>
        /// <returns><see langword="true"/> when <paramref name="value"/> is valid; otherwise, <see langword="false"/>.</returns>
        public static bool TryParse(string? value, out SemanticVersion version)
        {
            version = default;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            var match = Pattern.Match(value);
            if (!match.Success)
            {
                return false;
            }

            var prerelease = match.Groups[4].Success ? match.Groups[4].Value : string.Empty;
            if (!HasValidPrereleaseIdentifiers(prerelease))
            {
                return false;
            }

            version = new SemanticVersion(
                match.Groups[1].Value,
                match.Groups[2].Value,
                match.Groups[3].Value,
                prerelease,
                match.Groups[5].Success ? match.Groups[5].Value : string.Empty);
            return true;
        }

        /// <summary>Compares this version with another version using SemVer precedence rules.</summary>
        /// <param name="other">The version to compare with.</param>
        /// <returns>A value below zero, zero, or above zero according to precedence. Build metadata is ignored.</returns>
        public int CompareTo(SemanticVersion other)
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

        /// <summary>Determines whether this value exactly matches another value, including build metadata.</summary>
        /// <param name="other">The value to compare with.</param>
        /// <returns><see langword="true"/> when every identifier matches.</returns>
        public bool Equals(SemanticVersion other)
        {
            return string.Equals(Major, other.Major, StringComparison.Ordinal)
                && string.Equals(Minor, other.Minor, StringComparison.Ordinal)
                && string.Equals(Patch, other.Patch, StringComparison.Ordinal)
                && string.Equals(Prerelease, other.Prerelease, StringComparison.Ordinal)
                && string.Equals(BuildMetadata, other.BuildMetadata, StringComparison.Ordinal);
        }

        /// <summary>Determines whether this value exactly matches another object.</summary>
        /// <param name="obj">The object to compare with.</param>
        /// <returns><see langword="true"/> when the object is an identical semantic version.</returns>
        public override bool Equals(object? obj)
        {
            return obj is SemanticVersion other && Equals(other);
        }

        /// <summary>Returns a hash code for the complete version, including build metadata.</summary>
        /// <returns>A hash code for this value.</returns>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(Major ?? string.Empty);
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(Minor ?? string.Empty);
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(Patch ?? string.Empty);
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(Prerelease ?? string.Empty);
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(BuildMetadata ?? string.Empty);
                return hash;
            }
        }

        /// <summary>Formats the complete canonical version.</summary>
        /// <returns>The Semantic Versioning 2.0.0 text.</returns>
        public override string ToString()
        {
            var value = Major + "." + Minor + "." + Patch;
            if (Prerelease.Length != 0)
            {
                value += "-" + Prerelease;
            }

            if (BuildMetadata.Length != 0)
            {
                value += "+" + BuildMetadata;
            }

            return value;
        }

        /// <summary>Tests two values for exact equality, including build metadata.</summary>
        public static bool operator ==(SemanticVersion left, SemanticVersion right) => left.Equals(right);

        /// <summary>Tests two values for exact inequality, including build metadata.</summary>
        public static bool operator !=(SemanticVersion left, SemanticVersion right) => !left.Equals(right);

        /// <summary>Tests whether the left value has lower SemVer precedence.</summary>
        public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

        /// <summary>Tests whether the left value has greater SemVer precedence.</summary>
        public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

        /// <summary>Tests whether the left value has lower or equal SemVer precedence.</summary>
        public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

        /// <summary>Tests whether the left value has greater or equal SemVer precedence.</summary>
        public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

        private static bool HasValidPrereleaseIdentifiers(string prerelease)
        {
            if (prerelease.Length == 0)
            {
                return true;
            }

            foreach (var identifier in prerelease.Split('.'))
            {
                if (IsNumeric(identifier) && identifier.Length > 1 && identifier[0] == '0')
                {
                    return false;
                }
            }

            return true;
        }

        private static int CompareNumericIdentifier(string left, string right)
        {
            var lengthComparison = left.Length.CompareTo(right.Length);
            return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
        }

        private static int ComparePrereleaseIdentifier(string left, string right)
        {
            var leftIsNumeric = IsNumeric(left);
            var rightIsNumeric = IsNumeric(right);
            if (leftIsNumeric && rightIsNumeric)
            {
                return CompareNumericIdentifier(left, right);
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

        private static bool IsNumeric(string value)
        {
            if (value.Length == 0)
            {
                return false;
            }

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
