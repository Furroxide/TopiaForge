using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.CreatorContent
{
    internal sealed partial class CreatorBuiltInCatalog
    {
        private static string Append(string first, string second) =>
            string.IsNullOrWhiteSpace(first) ? second : first + " " + second;

        private static string DisplayName(string? preferred, string fallback)
        {
            var value = string.IsNullOrWhiteSpace(preferred) ? fallback : preferred!;
            return value.Length <= 128 ? value : value.Substring(0, 128);
        }

        private static string MakeLocalId(string prefix, string sourceKey)
        {
            var builder = new StringBuilder(80);
            var separated = true;
            foreach (var character in sourceKey)
            {
                var normalized = character >= 'A' && character <= 'Z'
                    ? (char)(character + ('a' - 'A'))
                    : character;
                if ((normalized >= 'a' && normalized <= 'z') || (normalized >= '0' && normalized <= '9'))
                {
                    if (builder.Length >= 80) break;
                    builder.Append(normalized);
                    separated = false;
                }
                else if (!separated && builder.Length < 80)
                {
                    builder.Append('-');
                    separated = true;
                }
            }

            var slug = builder.ToString().Trim('-');
            if (slug.Length == 0) slug = "content";
            return prefix + "." + slug + "." + StableHash(sourceKey).ToString("x16");
        }

        private static ulong StableHash(string value)
        {
            unchecked
            {
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                var hash = offset;
                foreach (var character in value)
                {
                    hash ^= character;
                    hash *= prime;
                }
                return hash;
            }
        }

        private sealed class Discovery
        {
            public List<Candidate> Candidates { get; } = new List<Candidate>();
            public Dictionary<string, CreatorCatalogSourceStatus> Statuses { get; } =
                new Dictionary<string, CreatorCatalogSourceStatus>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> Failures { get; } =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public void Add(Candidate candidate)
            {
                if (Candidates.Any(existing => string.Equals(existing.Key, candidate.Key, StringComparison.OrdinalIgnoreCase))) return;
                Candidates.Add(candidate);
            }

            public int Count(string sourceId) => Candidates.Count(candidate =>
                string.Equals(candidate.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

            public void Remove(string sourceId) => Candidates.RemoveAll(candidate =>
                string.Equals(candidate.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

            public int FailureCount(string sourceId) =>
                Failures.TryGetValue(sourceId, out var count) ? count : 0;
        }

        private sealed class Candidate
        {
            public Candidate(
                string sourceId,
                string localId,
                string displayName,
                string description,
                CreatorContentKind kind,
                GameObject prefab)
            {
                SourceId = sourceId;
                LocalId = localId;
                DisplayName = displayName;
                Description = description;
                Kind = kind;
                Prefab = prefab;
            }

            public string SourceId { get; }
            public string LocalId { get; }
            public string DisplayName { get; }
            public string Description { get; }
            public CreatorContentKind Kind { get; }
            public GameObject Prefab { get; }
            public string Key => SourceId + ":" + LocalId;
        }

        private sealed class RegistrationRecord
        {
            public RegistrationRecord(Candidate candidate, ICreatorContentRegistration registration)
            {
                Candidate = candidate;
                Registration = registration;
            }

            public Candidate Candidate { get; }
            public ICreatorContentRegistration Registration { get; }

            public bool Matches(Candidate next) =>
                ReferenceEquals(Candidate.Prefab, next.Prefab)
                && Candidate.DisplayName == next.DisplayName
                && Candidate.Description == next.Description
                && Candidate.Kind == next.Kind;
        }
    }
}
