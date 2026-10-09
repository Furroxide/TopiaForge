using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    internal sealed partial class RuntimeBindingRegistry
    {
        private int publicationRevision;
        internal IReadOnlyList<RuntimeObservationEnvelope> CaptureObservations()
        {
            lock (gate)
            {
                var digest = PackageSetDigest.Of(profile.Packages);
                return Array.AsReadOnly(profile.Packages.Select(selected =>
                {
                    packages.TryGetValue(selected.Id, out var state);
                    var discovery = state != null && state.Selection.Identity.Equals(selected.Identity) ? state.Observation : null;
                    var failures = (state?.Failures ?? ambiguousFailures.Where(item => item.Package.Equals(selected.Identity)))
                        .Where(item => item.Kind == "world" || item.Kind == "gamemode").Select(item => item.ToAvailability());
                    var availability = failures.Concat(discovery?.Availability ?? Array.Empty<DeclarationAvailability>())
                        .GroupBy(item => item.Kind + ":" + item.Id, StringComparer.OrdinalIgnoreCase)
                        .Select(group => new DeclarationAvailability(group.First().Kind, group.First().Id, group.SelectMany(item => item.Blocks)));
                    return new RuntimeObservationEnvelope(profile.ProfileId, profile.Revision, selected.Identity, digest,
                        publicationRevision, discovery?.DiscoveredWorlds ?? Array.Empty<DiscoveredWorldObservation>(), availability);
                }).ToArray());
            }
        }
    }
}
