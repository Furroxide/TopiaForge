using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    public sealed class DiscoveredWorldObservation
    {
        public DiscoveredWorldObservation(string id, string familyId, string name, string? description = null)
        {
            Id = LaunchContractValues.Identifier(id, nameof(id));
            FamilyId = LaunchContractValues.Identifier(familyId, nameof(familyId));
            Name = LaunchContractValues.Text(name, nameof(name), 1, 128);
            Description = description == null ? null : LaunchContractValues.Text(description, nameof(description), 0, 1024);
            if (!Id.StartsWith(FamilyId + ".", StringComparison.OrdinalIgnoreCase) || Id.Length <= FamilyId.Length + 1)
                throw new ArgumentException("A discovered instance must belong beneath its family.");
        }

        public string Id { get; }
        public string FamilyId { get; }
        public string Name { get; }
        public string? Description { get; }
    }

    /// <summary>A recorded failure for one declared or discovered item, never a successful binding claim.</summary>
    public sealed class DeclarationAvailability
    {
        public DeclarationAvailability(string kind, string id, IEnumerable<LaunchBlock> blocks)
        {
            Kind = LaunchContractValues.Choice(kind, nameof(kind), new[] { "world", "gamemode" });
            Id = LaunchContractValues.Identifier(id, nameof(id));
            Blocks = LaunchBlockCollection.Copy(blocks);
            if (Blocks.Count == 0) throw new ArgumentException("An availability record must contain a failure.", nameof(blocks));
        }

        public string Kind { get; }
        public string Id { get; }
        public IReadOnlyList<LaunchBlock> Blocks { get; }
    }

    /// <summary>Inactive, versioned observations attributed to the exact producing package and profile.</summary>
    public sealed class RuntimeObservationEnvelope
    {
        public const int SchemaVersion = 1;

        public RuntimeObservationEnvelope(string profileId, int profileRevision, PackageIdentity producer,
            string packageSetDigest, int observationRevision, IEnumerable<DiscoveredWorldObservation> discoveredWorlds,
            IEnumerable<DeclarationAvailability> availability)
        {
            ProfileId = LaunchContractValues.Token(profileId, nameof(profileId));
            ProfileRevision = LaunchContractValues.Revision(profileRevision, nameof(profileRevision));
            Producer = new PackageIdentity(producer.Id, producer.Version);
            PackageSetDigest = LaunchContractValues.Digest(packageSetDigest);
            ObservationRevision = LaunchContractValues.Revision(observationRevision, nameof(observationRevision));
            DiscoveredWorlds = LaunchContractValues.Copy(discoveredWorlds.OrderBy(item => item.Id, StringComparer.Ordinal));
            Availability = LaunchContractValues.Copy(availability.OrderBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal));
            if (DiscoveredWorlds.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != DiscoveredWorlds.Count
                || Availability.Select(item => item.Kind + ":" + item.Id.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count() != Availability.Count)
                throw new ArgumentException("Observation identities must be unique within each collection.");
        }

        public string ProfileId { get; }
        public int ProfileRevision { get; }
        public PackageIdentity Producer { get; }
        public string PackageSetDigest { get; }
        public int ObservationRevision { get; }
        public IReadOnlyList<DiscoveredWorldObservation> DiscoveredWorlds { get; }
        public IReadOnlyList<DeclarationAvailability> Availability { get; }
    }

    /// <summary>Current physical binding evidence, supplied by the live registry rather than cached observations.</summary>
    public sealed class RuntimeBindingSnapshot
    {
        public RuntimeBindingSnapshot(string profileId, int profileRevision, string packageSetDigest,
            IEnumerable<string> boundWorldIds, IEnumerable<string> boundGamemodeIds,
            IEnumerable<DeclarationAvailability>? availability = null)
        {
            ProfileId = LaunchContractValues.Token(profileId, nameof(profileId));
            ProfileRevision = LaunchContractValues.Revision(profileRevision, nameof(profileRevision));
            PackageSetDigest = LaunchContractValues.Digest(packageSetDigest);
            BoundWorldIds = LaunchContractValues.Identifiers(boundWorldIds, nameof(boundWorldIds));
            BoundGamemodeIds = LaunchContractValues.Identifiers(boundGamemodeIds, nameof(boundGamemodeIds));
            Availability = LaunchContractValues.Copy(availability);
            if (Availability.Select(item => item.Kind + ":" + item.Id.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count() != Availability.Count
                || Availability.Any(item => (item.Kind == "world" ? BoundWorldIds : BoundGamemodeIds)
                .Any(id => string.Equals(id, item.Id, StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException("A live binding cannot be both successful and failed.");
        }

        public string ProfileId { get; }
        public int ProfileRevision { get; }
        public string PackageSetDigest { get; }
        public IReadOnlyList<string> BoundWorldIds { get; }
        public IReadOnlyList<string> BoundGamemodeIds { get; }
        public IReadOnlyList<DeclarationAvailability> Availability { get; }
    }

    internal static class LaunchBlockCollection
    {
        internal static IReadOnlyList<LaunchBlock> Copy(IEnumerable<LaunchBlock> blocks) =>
            Array.AsReadOnly(LaunchContractValues.Copy(blocks).Select(block => new LaunchBlock(block.Code, block.Subject, block.SubjectVersion))
                .GroupBy(block => new { block.Code, block.Subject, block.SubjectVersion }).Select(group => group.First())
                .OrderBy(block => block.Code.ToString(), StringComparer.Ordinal)
                .ThenBy(block => block.Subject, StringComparer.Ordinal)
                .ThenBy(block => block.SubjectVersion, StringComparer.Ordinal).ToArray());
    }
}
