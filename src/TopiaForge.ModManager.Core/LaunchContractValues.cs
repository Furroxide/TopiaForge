using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>An immutable exact package identity, without a mutable manifest attached.</summary>
    public sealed class PackageIdentity : IEquatable<PackageIdentity>
    {
        public PackageIdentity(string id, string version)
        {
            if (!ManifestValidator.IsValidId(id)) throw new ArgumentException("Invalid package id.", nameof(id));
            LaunchContractValues.Version(version, nameof(version));
            Id = id;
            Version = version;
        }

        public string Id { get; }
        public string Version { get; }
        public bool Equals(PackageIdentity? other) => other != null
            && string.Equals(Id, other.Id, StringComparison.Ordinal)
            && string.Equals(Version, other.Version, StringComparison.Ordinal);
        public override bool Equals(object? other) => Equals(other as PackageIdentity);
        public override int GetHashCode() => (StringComparer.Ordinal.GetHashCode(Id) * 397)
            ^ StringComparer.Ordinal.GetHashCode(Version);
        public override string ToString() => Id + "@" + Version;
    }

    internal static class LaunchContractValues
    {
        internal static readonly string[] Phases =
            { "idle", "preparing", "loading-world", "starting-mode", "running", "stopping" };
        internal static readonly string[] Commands = { "main-menu", "launch-target" };
        internal static readonly string[] Statuses = { "succeeded", "failed", "cancelled" };
        internal static readonly string[] RuntimeErrors =
        {
            "invalidArgument", "notFound", "unavailable", "conflict", "invalidState", "cancelled",
            "timedOut", "io", "external", "unknown", "notAuthoritative", "rateLimited"
        };

        internal static string Version(string value, string name)
        {
            if (string.IsNullOrEmpty(value) || value.Any(c => !AsciiAlphaNumeric(c) && c != '.' && c != '-' && c != '+')
                || !VersionUtil.TryParse(value, out _))
                throw new ArgumentException("Invalid package version.", name);
            return value;
        }

        internal static string Identifier(string value, string name)
        {
            if (!ManifestContributionValidator.IsValidDeclarationId(value))
                throw new ArgumentException("Invalid declaration identifier.", name);
            return value;
        }

        internal static string? OptionalIdentifier(string? value, string name) =>
            value == null ? null : Identifier(value, name);

        internal static string Token(string value, string name, int maximum = 128)
        {
            if (string.IsNullOrEmpty(value) || value.Length > maximum
                || !AsciiAlphaNumeric(value[0])
                || value.Any(c => !AsciiAlphaNumeric(c) && c != '.' && c != '-' && c != '_'))
                throw new ArgumentException("Invalid safe identifier.", name);
            return value;
        }

        private static bool AsciiAlphaNumeric(char value) =>
            (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z') || (value >= '0' && value <= '9');

        internal static string Choice(string value, string name, IEnumerable<string> choices)
        {
            if (!choices.Contains(value, StringComparer.Ordinal))
                throw new ArgumentException("Unsupported " + name + ".", name);
            return value;
        }

        internal static string Text(string value, string name, int minimum, int maximum)
        {
            var length = ManifestContributionValidator.UnicodeScalarLength(value);
            if (value == null || length < minimum || length > maximum)
                throw new ArgumentException("Invalid text length.", name);
            return value;
        }

        internal static string Digest(string value)
        {
            if (value == null || value.Length != 16 || value.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
                throw new ArgumentException("A package digest must be 16 lower-case hexadecimal characters.", nameof(value));
            return value;
        }

        internal static int Revision(int value, string name)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(name);
            return value;
        }

        internal const int MaxCollectionCount = 4096;

        internal static IReadOnlyList<T> Copy<T>(IEnumerable<T>? values)
        {
            var copy = (values ?? Enumerable.Empty<T>()).Take(MaxCollectionCount + 1).ToArray();
            if (copy.Length > MaxCollectionCount) throw new ArgumentException("Transport collection exceeds its item limit.", nameof(values));
            return Array.AsReadOnly(copy);
        }

        internal static IReadOnlyList<string> Identifiers(IEnumerable<string> values, string name)
        {
            var copy = Copy(values).Select(id => Identifier(id, name)).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            if (copy.Distinct(StringComparer.OrdinalIgnoreCase).Count() != copy.Length)
                throw new ArgumentException("Declaration identifiers must be unique.", name);
            return Array.AsReadOnly(copy);
        }

        internal static IReadOnlyList<PackageIdentity> Packages(IEnumerable<PackageIdentity> packages)
        {
            var values = Copy(packages).Select(value => new PackageIdentity(value.Id, value.Version))
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .ThenBy(value => value.Version, StringComparer.Ordinal).ToArray();
            if (values.GroupBy(value => value.Id, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new ArgumentException("A package set must select one version per package id.", nameof(packages));
            return Array.AsReadOnly(values);
        }

        internal static bool SamePackages(IEnumerable<PackageIdentity> a, IEnumerable<PackageIdentity> b) =>
            a.OrderBy(value => value.Id, StringComparer.Ordinal).ThenBy(value => value.Version, StringComparer.Ordinal)
                .SequenceEqual(b.OrderBy(value => value.Id, StringComparer.Ordinal).ThenBy(value => value.Version, StringComparer.Ordinal));

        internal static IReadOnlyDictionary<string, string> Dictionary(IReadOnlyDictionary<string, string> values)
        {
            if (values.Count > MaxCollectionCount) throw new ArgumentException("Transport map exceeds its item limit.", nameof(values));
            return new ReadOnlyDictionary<string, string>(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }
    }
}
