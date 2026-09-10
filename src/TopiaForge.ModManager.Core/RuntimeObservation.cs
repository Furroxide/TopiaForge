using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Only provenance-matching observations admitted against installed declarations.</summary>
    public sealed class RuntimeObservation
    {
        private RuntimeObservation(string? profileId, int revision, string? digest,
            IEnumerable<DiscoveredWorldObservation> worlds, IEnumerable<DeclarationAvailability> availability)
        {
            ProfileId = profileId;
            Revision = revision;
            Digest = digest;
            DiscoveredWorlds = LaunchContractValues.Copy(worlds.OrderBy(world => world.Id, StringComparer.Ordinal));
            Availability = LaunchContractValues.Copy(availability.OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal));
        }

        public static readonly RuntimeObservation None = new RuntimeObservation(null, 0, null,
            Array.Empty<DiscoveredWorldObservation>(), Array.Empty<DeclarationAvailability>());
        public IReadOnlyList<DiscoveredWorldObservation> DiscoveredWorlds { get; }
        public IReadOnlyList<DeclarationAvailability> Availability { get; }
        private string? ProfileId { get; }
        private int Revision { get; }
        private string? Digest { get; }

        internal RuntimeObservation ForProfile(EffectiveProfile profile) =>
            ProfileId == profile.ProfileId && Revision == profile.Revision && Digest == PackageSetDigest.Of(profile.Packages) ? this : None;

        public static RuntimeObservation FromEnvelopes(EffectiveProfile profile, IEnumerable<RuntimeObservationEnvelope> envelopes)
        {
            var digest = PackageSetDigest.Of(profile.Packages);
            var index = new LaunchProfileIndex(profile);
            if (index.Duplicates.Count > 0) return None;
            var worlds = new List<DiscoveredWorldObservation>();
            var availability = new List<DeclarationAvailability>();
            var matching = envelopes.Where(envelope => envelope.ProfileId == profile.ProfileId
                && envelope.ProfileRevision == profile.Revision && envelope.PackageSetDigest == digest
                && profile.Packages.Any(package => package.Identity.Equals(envelope.Producer)))
                .GroupBy(envelope => envelope.Producer.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var producer in matching)
            {
                var revision = producer.Max(envelope => envelope.ObservationRevision);
                var latest = producer.Where(envelope => envelope.ObservationRevision == revision).ToArray();
                // Conflicting same-revision observations have no deterministic authority.
                if (latest.Select(LaunchTransportJson.WriteObservation).Distinct(StringComparer.Ordinal).Count() != 1) continue;
                var envelope = latest[0];
                var package = profile.Packages.Single(candidate => candidate.Identity.Equals(envelope.Producer));
                var declared = package.Snapshot.Contributions;
                if (declared == null) continue;
                var accepted = new List<DiscoveredWorldObservation>();
                foreach (var world in envelope.DiscoveredWorlds)
                {
                    if (!index.Owns(package, world.Id) || !index.Owns(package, world.FamilyId)) continue;
                    if (declared.Worlds.Any(item => LaunchProfileIndex.Same(item.Id, world.Id))) continue;
                    var families = declared.Worlds.Where(item => item.Content?.Kind == ModWorldContent.DiscoveredKind
                        && world.Id.StartsWith(item.Id + ".", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(item => item.Id.Length).ToArray();
                    if (families.Length == 0 || !LaunchProfileIndex.Same(families[0].Id, world.FamilyId)
                        || (families.Length > 1 && families[0].Id.Length == families[1].Id.Length)) continue;
                    accepted.Add(world);
                }
                worlds.AddRange(accepted);
                foreach (var item in envelope.Availability)
                {
                    if (!index.Owns(package, item.Id)) continue;
                    var count = item.Kind == "gamemode"
                        ? declared.Gamemodes.Count(mode => LaunchProfileIndex.Same(mode.Id, item.Id))
                        : declared.Worlds.Count(world => LaunchProfileIndex.Same(world.Id, item.Id))
                            + accepted.Count(world => LaunchProfileIndex.Same(world.Id, item.Id));
                    if (count == 1) availability.Add(item);
                }
            }
            return new RuntimeObservation(profile.ProfileId, profile.Revision, digest, worlds, availability);
        }

        internal bool ExplicitlyUnavailable(string id)
        {
            var familyId = DiscoveredWorlds.FirstOrDefault(world => LaunchProfileIndex.Same(world.Id, id))?.FamilyId;
            return Failures("world", id, familyId).Any(block => block.Code == LaunchBlockCode.WorldUnavailable);
        }

        internal IEnumerable<LaunchBlock> Failures(string kind, string id, string? familyId = null) => Availability
            .Where(item => item.Kind == kind && (LaunchProfileIndex.Same(item.Id, id)
                || (familyId != null && LaunchProfileIndex.Same(item.Id, familyId))))
            .SelectMany(item => item.Blocks);
    }
}
