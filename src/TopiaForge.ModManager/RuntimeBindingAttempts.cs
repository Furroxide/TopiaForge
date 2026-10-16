using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed class RuntimePackageBindingAttempt
    {
        internal RuntimePackageBindingAttempt(object registry, PackageIdentity package, long generation)
        { Registry = registry; Package = package; Generation = generation; }
        internal object Registry { get; }
        internal PackageIdentity Package { get; }
        internal long Generation { get; }
    }

    internal sealed class RuntimeDiscoveryAttempt
    {
        internal RuntimeDiscoveryAttempt(object registry, EffectiveProfile profile, PackageIdentity package,
            long packageGeneration, long sequence, ModContext ownerContext,
            IEnumerable<ISessionImplementation<IWorldDiscoverySource>> families, CancellationToken cancellationToken)
        {
            Registry = registry; ProfileId = profile.ProfileId; ProfileRevision = profile.Revision;
            PackageSetDigest = TopiaForge.ModManager.Core.PackageSetDigest.Of(profile.Packages);
            Package = new PackageIdentity(package.Id, package.Version); PackageGeneration = packageGeneration;
            Sequence = sequence; OwnerContext = ownerContext; Families = Array.AsReadOnly(families.ToArray());
            CancellationToken = cancellationToken;
        }
        internal object Registry { get; }
        internal long PackageGeneration { get; }
        internal long Sequence { get; }
        internal CancellationToken CancellationToken { get; }
        internal string ProfileId { get; }
        internal int ProfileRevision { get; }
        internal string PackageSetDigest { get; }
        internal PackageIdentity Package { get; }
        internal ModContext OwnerContext { get; }
        internal IReadOnlyList<ISessionImplementation<IWorldDiscoverySource>> Families { get; }
    }
}
