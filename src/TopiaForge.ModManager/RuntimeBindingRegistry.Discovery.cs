using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    internal sealed partial class RuntimeBindingRegistry
    {
        internal RuntimeDiscoveryAttempt? BeginDiscovery(PackageIdentity identity, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                if (stopping || cancellationToken.IsCancellationRequested || !packages.TryGetValue(identity.Id, out var package)
                    || !package.Selection.Identity.Equals(identity) || package.Context == null || package.Context.Lifetime.IsStopping
                    || package.Batch == null || package.Batch.DiscoverySources.Count == 0) return null;
                var families = package.Batch.DiscoverySources.Where(value => ownership.Owns(package.Selection, value.DeclarationId)).ToArray();
                if (families.Length == 0) return null;
                return new RuntimeDiscoveryAttempt(this, profile, identity, package.Generation, ++package.DiscoverySequence,
                    package.Context, families, cancellationToken);
            }
        }

        internal bool PublishDiscovery(RuntimeDiscoveryAttempt attempt, IReadOnlyList<DiscoveredWorldObservation> worlds,
            IReadOnlyList<DeclarationAvailability> availability)
        {
            lock (gate)
            {
                if (!CurrentDiscovery(attempt, out var package) || attempt.CancellationToken.IsCancellationRequested) return false;
                if (worlds == null || availability == null) throw new ArgumentNullException(worlds == null ? nameof(worlds) : nameof(availability));
                if (worlds.Count > 4096 || availability.Count > 8192) throw new ArgumentException("A package discovery exceeds the observation bounds.");
                var families = attempt.Families.Select(value => value.DeclarationId).ToArray();
                if (worlds.Any(world => !families.Contains(world.FamilyId, StringComparer.Ordinal)
                    || families.Where(family => world.Id.StartsWith(family + ".", StringComparison.OrdinalIgnoreCase)).OrderByDescending(family => family.Length).FirstOrDefault() != world.FamilyId)
                    || availability.Any(item => item.Kind != "world" || item.Blocks.Any(block => block.Code != LaunchBlockCode.WorldUnavailable || block.Subject != item.Id || block.SubjectVersion != attempt.Package.Version)
                        || (!families.Contains(item.Id, StringComparer.Ordinal) && !worlds.Any(world => world.Id == item.Id))))
                    throw new ArgumentException("Discovery results must describe the captured package families and their instances.");
                var envelope = new RuntimeObservationEnvelope(profile.ProfileId, profile.Revision, attempt.Package,
                    attempt.PackageSetDigest, checked(package!.ObservationRevision + 1), worlds, availability);
                var accepted = RuntimeObservation.FromEnvelopes(profile, new[] { envelope });
                if (accepted.DiscoveredWorlds.Count != worlds.Count || accepted.Availability.Count != availability.Count)
                    throw new ArgumentException("Discovery results conflict with the configured declaration ownership.");
                package.ObservationRevision = envelope.ObservationRevision;
                package.Observation = envelope;
                package.DiscoverySequence++;
                Publish();
                return true;
            }
        }

        internal bool AbandonDiscovery(RuntimeDiscoveryAttempt attempt)
        {
            lock (gate)
            {
                if (!CurrentDiscovery(attempt, out var package)) return false;
                package!.DiscoverySequence++;
                return true;
            }
        }

        private bool CurrentDiscovery(RuntimeDiscoveryAttempt attempt, out PackageState? package)
        {
            package = null;
            return !stopping && ReferenceEquals(attempt.Registry, this) && packages.TryGetValue(attempt.Package.Id, out package)
                && package.Selection.Identity.Equals(attempt.Package) && package.Generation == attempt.PackageGeneration
                && package.DiscoverySequence == attempt.Sequence && ReferenceEquals(package.Context, attempt.OwnerContext)
                && package.Context != null && !package.Context.Lifetime.IsStopping && package.Batch != null;
        }
    }
}
