using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    internal sealed class RuntimeSessionSelection
    {
        private RuntimeSessionSelection(EffectiveProfile profile, IReadOnlyList<ModPackage> packages,
            IReadOnlyList<RejectedRuntimeSelection> rejected)
        { Profile = profile; Packages = packages; RejectedSelections = rejected; }
        internal EffectiveProfile Profile { get; }
        internal IReadOnlyList<ModPackage> Packages { get; }
        internal IReadOnlyList<RejectedRuntimeSelection> RejectedSelections { get; }
        internal static RuntimeSessionSelection Create(string profileId, int revision,
            IReadOnlyList<ModPackage> scanned, LoadOrderResult order, ManifestValidationContext validation)
        {
            var orderedPaths = new HashSet<string>(order.OrderedPackages.Select(package => package.PackagePath), StringComparer.Ordinal);
            var candidates = order.OrderedPackages.Concat(scanned
                .Where(package => !orderedPaths.Contains(package.PackagePath))
                .OrderBy(package => package.Manifest?.Id, StringComparer.Ordinal));
            var selected = new List<ModPackage>();
            var enabled = new List<ResolvedPackage>();
            var disabled = new List<ResolvedPackage>();
            var rejected = new List<RejectedRuntimeSelection>();
            foreach (var package in candidates)
            {
                var manifest = package.Manifest;
                if (manifest == null) continue;
                try { _ = new PackageIdentity(manifest.Id, manifest.Version); }
                catch (ArgumentException error)
                {
                    rejected.Add(new RejectedRuntimeSelection(package, error.Message));
                    continue;
                }
                var frozen = new ResolvedPackage(manifest.Id, manifest.Version, manifest);
                if (!package.IsEnabled) { disabled.Add(frozen); continue; }
                var errors = package.Errors.ToList();
                if (order.Errors.TryGetValue(manifest.Id, out var excluded)) errors.AddRange(excluded);
                if (!orderedPaths.Contains(package.PackagePath) && errors.Count == 0)
                    errors.Add("The enabled selection was excluded from dependency loading.");
                selected.Add(new ModPackage(package.PackagePath, frozen.Manifest,
                    package.State == null ? null : JsonUtil.Clone(package.State),
                    errors.Distinct(StringComparer.Ordinal).ToArray(), package.SelectionReason));
                enabled.Add(frozen);
            }
            var facts = new InstallFacts(validation.Platform, validation.Architecture,
                gameVersion: validation.GameVersion ?? string.Empty, contentTargets: validation.ContentTargets);
            return new RuntimeSessionSelection(new EffectiveProfile(profileId, revision, enabled, facts, disabled),
                selected.AsReadOnly(), rejected.AsReadOnly());
        }
    }

    internal sealed class RejectedRuntimeSelection
    {
        internal RejectedRuntimeSelection(ModPackage package, string reason)
        {
            PackagePath = package.PackagePath;
            Id = package.Manifest?.Id;
            Version = package.Manifest?.Version;
            IsEnabled = package.IsEnabled;
            Errors = Array.AsReadOnly(package.Errors.Concat(new[] { reason }).Distinct(StringComparer.Ordinal).ToArray());
        }
        internal string PackagePath { get; }
        internal string? Id { get; }
        internal string? Version { get; }
        internal bool IsEnabled { get; }
        internal IReadOnlyList<string> Errors { get; }
    }

    internal sealed class InvalidRuntimeSelectionException : InvalidOperationException
    {
        internal InvalidRuntimeSelectionException(IReadOnlyList<RejectedRuntimeSelection> rejected)
            : base("The enabled selection contains invalid package identities. Repair, disable, or remove these packages before launching a session: "
                + string.Join("; ", rejected.Select(item => "'" + item.PackagePath + "' (id='" + item.Id + "', version='" + item.Version + "'): "
                    + string.Join("; ", item.Errors))))
        { RejectedSelections = Array.AsReadOnly(rejected.ToArray()); }
        internal IReadOnlyList<RejectedRuntimeSelection> RejectedSelections { get; }
        internal static void ThrowIfAny(IReadOnlyList<RejectedRuntimeSelection> rejected)
        {
            var enabled = rejected.Where(item => item.IsEnabled).OrderBy(item => item.PackagePath, StringComparer.Ordinal).ToArray();
            if (enabled.Length != 0) throw new InvalidRuntimeSelectionException(enabled);
        }
    }

}
