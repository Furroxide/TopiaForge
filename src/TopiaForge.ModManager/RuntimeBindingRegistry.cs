using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>One immutable publication holds selected ownership, live bindings, failures and observations.</summary>
    internal sealed partial class RuntimeBindingRegistry
    {
        private readonly object gate = new object();
        private readonly EffectiveProfile profile;
        private readonly LaunchProfileIndex ownership;
        private readonly Dictionary<string, PackageState> packages;
        private readonly HashSet<string> ambiguousOwners;
        private readonly IReadOnlyList<RuntimeBindingFailure> ambiguousFailures;
        private bool selectionVerified;
        private RuntimeSessionSnapshot snapshot = null!;
        private bool stopping;
        private bool packageCleanupPending;
        private long generation;

        internal RuntimeBindingRegistry(EffectiveProfile profile)
        {
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
            ownership = new LaunchProfileIndex(profile);
            var groups = profile.Packages.GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            ambiguousOwners = new HashSet<string>(groups.Where(group => group.Count() > 1).Select(group => group.Key), StringComparer.OrdinalIgnoreCase);
            ambiguousFailures = Array.AsReadOnly(groups.Where(group => group.Count() > 1).SelectMany(group => group)
                .OrderBy(package => package.Id, StringComparer.Ordinal).ThenBy(package => package.Version, StringComparer.Ordinal)
                .SelectMany(package => Failures(package, RuntimeBindingFailureCode.AmbiguousPackage,
                    "The selected package id is ambiguous; no duplicate owner may load: " + package.Id + ".")).ToArray());
            packages = groups.Where(group => group.Count() == 1).Select(group => group.Single())
                .ToDictionary(package => package.Id, package => new PackageState(package), StringComparer.OrdinalIgnoreCase);
            foreach (var package in packages.Values) package.Failures = Failures(package.Selection, RuntimeBindingFailureCode.PackageNotLoaded, "The selected package has not loaded.");
            Publish();
        }

        internal RuntimeSessionSnapshot Capture() { lock (gate) return snapshot; }

        internal void SetPackageCleanupPending(bool pending)
        {
            lock (gate)
            {
                if (packageCleanupPending == pending) return;
                packageCleanupPending = pending;
                Publish();
            }
        }

        internal bool IsAmbiguousOwner(string id) => ambiguousOwners.Contains(id);
        internal IEnumerable<ModPackage> UnambiguousSelections(IEnumerable<ModPackage> selections) =>
            selections.Where(package => package.Manifest != null && !IsAmbiguousOwner(package.Manifest.Id));

        internal IReadOnlyList<ModPackage> VerifyAndFreezeSelection(IReadOnlyList<ModPackage> incoming)
        {
            lock (gate)
            {
                if (stopping || selectionVerified || packages.Values.Any(package => package.Generation != 0))
                    throw new InvalidOperationException("The configured package selection can load only once.");
                if (incoming.Count != profile.Packages.Count)
                    throw new InvalidOperationException("The packages supplied to the runtime do not match its exact configured selection.");
                // Match the complete manifest multiset, including every ambiguous selection. A duplicate
                // ID never picks a winner, and one repeated manifest cannot replace another selected manifest.
                var remaining = profile.Packages.GroupBy(package => JsonUtil.Serialize(package.Manifest), StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => new Queue<ResolvedPackage>(group), StringComparer.Ordinal);
                var frozen = new List<ModPackage>();
                foreach (var package in incoming)
                {
                    var manifest = package.Manifest;
                    if (manifest == null || !package.IsEnabled)
                        throw new InvalidOperationException("A package manifest changed after the exact runtime selection was configured.");
                    var key = JsonUtil.Serialize(new ResolvedPackage(manifest.Id, manifest.Version, manifest).Manifest);
                    if (!remaining.TryGetValue(key, out var matches) || matches.Count == 0)
                        throw new InvalidOperationException("A package manifest changed after the exact runtime selection was configured.");
                    var selected = matches.Dequeue();
                    var errors = package.Errors.ToList();
                    if (IsAmbiguousOwner(manifest.Id)) errors.Add("The selected package id is ambiguous: " + manifest.Id + ".");
                    frozen.Add(new ModPackage(package.PackagePath, selected.Manifest,
                        package.State == null ? null : JsonUtil.Clone(package.State),
                        errors.Distinct(StringComparer.Ordinal).ToArray(), package.SelectionReason));
                }
                selectionVerified = true;
                return frozen.AsReadOnly();
            }
        }
        internal RuntimePackageBindingAttempt BeginPackageLoad(PackageIdentity identity)
        {
            lock (gate)
            {
                if (stopping) throw new ObjectDisposedException(nameof(RuntimeBindingRegistry));
                var package = Find(identity);
                package.Generation = ++generation;
                package.Context = null; package.Batch = null; package.Observation = null; package.DiscoverySequence++;
                package.Pending = true;
                package.Failures = Failures(package.Selection, RuntimeBindingFailureCode.PackageNotLoaded, "The selected package is loading.");
                Publish();
                return new RuntimePackageBindingAttempt(this, package.Selection.Identity, package.Generation);
            }
        }

        internal bool CommitLoaded(RuntimePackageBindingAttempt attempt, ModContext context, PackageBindingBatch batch)
        {
            lock (gate)
            {
                if (!Current(attempt, out var package) || context.Lifetime.IsStopping) return false;
                if (!batch.Package.Equals(attempt.Package) || !string.Equals(context.Identity.Id, attempt.Package.Id, StringComparison.Ordinal))
                    throw new ArgumentException("The context and binding batch must belong to the selected package.");
                ValidateBatch(package!, batch);
                package!.Pending = false; package.Context = context; package.Batch = batch; package.Failures = batch.Failures;
                Publish();
                return true;
            }
        }

        internal bool CommitFailed(RuntimePackageBindingAttempt attempt, string message)
        {
            lock (gate)
            {
                if (!Current(attempt, out var package)) return false;
                package!.Pending = false; package.Context = null; package.Batch = null; package.Observation = null; package.DiscoverySequence++;
                package.Failures = Failures(package.Selection, RuntimeBindingFailureCode.PackageLoadFailed, message);
                Publish();
                return true;
            }
        }

        internal void BeginOwnerStop(PackageIdentity identity)
        {
            lock (gate) { Remove(Find(identity), RuntimeBindingFailureCode.OwnerStopping); Publish(); }
        }
        internal void CompleteOwnerRemoval(PackageIdentity identity)
        {
            lock (gate) { Remove(Find(identity), RuntimeBindingFailureCode.OwnerRemoved); Publish(); }
        }
        internal void BeginShutdown()
        {
            lock (gate)
            {
                stopping = true;
                foreach (var package in packages.Values) Remove(package, RuntimeBindingFailureCode.OwnerStopping);
                Publish();
            }
        }
        internal void CompleteShutdown()
        {
            lock (gate)
            {
                stopping = true;
                foreach (var package in packages.Values) Remove(package, RuntimeBindingFailureCode.OwnerRemoved);
                Publish();
            }
        }

        private void Remove(PackageState package, RuntimeBindingFailureCode reason)
        {
            package.Generation = ++generation; package.Pending = false; package.Context = null; package.Batch = null;
            package.Observation = null; package.DiscoverySequence++;
            package.Failures = Failures(package.Selection, reason, "The owning package is stopping or has been removed.");
        }
        private PackageState Find(PackageIdentity identity) => packages.TryGetValue(identity.Id, out var package)
            && package.Selection.Identity.Equals(identity) ? package : throw new ArgumentException("The package is outside the configured selection.");
        private bool Current(RuntimePackageBindingAttempt attempt, out PackageState? package)
        {
            package = null;
            return !stopping && ReferenceEquals(attempt.Registry, this) && packages.TryGetValue(attempt.Package.Id, out package)
                && package.Selection.Identity.Equals(attempt.Package) && package.Generation == attempt.Generation && package.Pending;
        }
        private static IReadOnlyList<RuntimeBindingFailure> Failures(ResolvedPackage package, RuntimeBindingFailureCode code, string message)
        {
            var contributions = package.Manifest.Contributions;
            var values = new List<RuntimeBindingFailure>();
            if (contributions != null)
            {
                values.AddRange(contributions.Gamemodes.Select(mode => new RuntimeBindingFailure(package.Identity, "gamemode", mode.Id, code, message, mode.Implementation?.Assembly, mode.Implementation?.Type)));
                values.AddRange(contributions.Worlds.Select(world => new RuntimeBindingFailure(package.Identity, "world", world.Id, code, message, world.Content?.Implementation?.Assembly, world.Content?.Implementation?.Type)));
            }
            if (values.Count == 0) values.Add(new RuntimeBindingFailure(package.Identity, "package", package.Id, code, message));
            return values.AsReadOnly();
        }
        private static void ValidateBatch(PackageState package, PackageBindingBatch batch)
        {
            var contributions = package.Selection.Manifest.Contributions ?? new ModContributions();
            var modeIds = batch.Gamemodes.Select(value => value.DeclarationId).Concat(batch.Failures.Where(value => value.Kind == "gamemode").Select(value => value.DeclarationId)).ToArray();
            var worldIds = batch.Worlds.Select(value => value.DeclarationId).Concat(batch.Failures.Where(value => value.Kind == "world").Select(value => value.DeclarationId)).ToArray();
            if (!modeIds.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(contributions.Gamemodes.Select(value => value.Id).OrderBy(value => value, StringComparer.Ordinal))
                || !worldIds.OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(contributions.Worlds.Select(value => value.Id).OrderBy(value => value, StringComparer.Ordinal))
                || batch.Gamemodes.Any(value => !value.Package.Equals(batch.Package)) || batch.Worlds.Any(value => !value.Package.Equals(batch.Package))
                || batch.DiscoverySources.Any(value => !value.Package.Equals(batch.Package) || !batch.Worlds.Any(world => ReferenceEquals(world, value))))
                throw new ArgumentException("The binding batch must cover exactly the owning package's declarations.");
        }
        private void Publish()
        {
            publicationRevision = checked(publicationRevision + 1);
            var states = packages.Values.OrderBy(package => package.Selection.Id, StringComparer.Ordinal).ToArray();
            var failures = states.SelectMany(package => package.Failures).Concat(ambiguousFailures).ToArray();
            var modes = states.Where(package => package.Batch != null).SelectMany(package => package.Batch!.Gamemodes.Where(value => ownership.Owns(package.Selection, value.DeclarationId))).ToArray();
            var worlds = states.Where(package => package.Batch != null).SelectMany(package => package.Batch!.Worlds.Where(value => ownership.Owns(package.Selection, value.DeclarationId))).ToArray();
            var discoveries = states.Where(package => package.Batch != null).SelectMany(package => package.Batch!.DiscoverySources.Where(value => ownership.Owns(package.Selection, value.DeclarationId))).ToArray();
            var bindings = new RuntimeBindingSnapshot(profile.ProfileId, profile.Revision, PackageSetDigest.Of(profile.Packages),
                worlds.Select(value => value.DeclarationId), modes.Select(value => value.DeclarationId), failures.Where(value => value.Kind != "package" && packages.TryGetValue(value.Package.Id, out var state) && ownership.Owns(state.Selection, value.DeclarationId)).Select(value => value.ToAvailability()));
            snapshot = new RuntimeSessionSnapshot(profile, bindings,
                states.Where(package => package.Context != null).ToDictionary(package => package.Selection.Id, package => package.Context!, StringComparer.OrdinalIgnoreCase),
                modes, worlds, RuntimeObservation.FromEnvelopes(profile, states.Where(package => package.Observation != null).Select(package => package.Observation!)), discoveries, failures, packageCleanupPending);
        }
        private sealed class PackageState
        {
            internal PackageState(ResolvedPackage selection) { Selection = selection; }
            internal readonly ResolvedPackage Selection;
            internal long Generation;
            internal bool Pending;
            internal ModContext? Context;
            internal PackageBindingBatch? Batch;
            internal IReadOnlyList<RuntimeBindingFailure> Failures = Array.Empty<RuntimeBindingFailure>();
            internal long DiscoverySequence;
            internal int ObservationRevision;
            internal RuntimeObservationEnvelope? Observation;
        }
    }
}
