using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    public sealed class ModRegistry
    {
        private const int MaxVersionSelectionNodes = 100000;

        public IReadOnlyList<ModPackage> Scan(ManagerPaths paths, ManagerState state)
        {
            return Scan(paths, state, ManifestValidationContext.Current);
        }

        public IReadOnlyList<ModPackage> Scan(
            ManagerPaths paths,
            ManagerState state,
            ManifestValidationContext validationContext)
        {
            if (validationContext == null)
            {
                throw new ArgumentNullException(nameof(validationContext));
            }

            paths.EnsureCreated();
            state.Normalize();
            state.Mods.RemoveAll(mod => !paths.TryGetPackageIdPath(mod.Id, out _));
            var candidates = new List<ScannedCandidate>();

            if (!Directory.Exists(paths.Packages))
            {
                return Array.Empty<ModPackage>();
            }

            foreach (var idDirectory in Directory.GetDirectories(paths.Packages)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var directoryId = Path.GetFileName(idDirectory);
                foreach (var versionDirectory in Directory.GetDirectories(idDirectory)
                             .OrderBy(path => path, StringComparer.Ordinal))
                {
                    var directoryVersion = Path.GetFileName(versionDirectory);
                    var manifestPath = Path.Combine(versionDirectory, "topiaforge.mod.json");
                    if (!File.Exists(manifestPath))
                    {
                        candidates.Add(ScannedCandidate.Invalid(
                            directoryId,
                            versionDirectory,
                            "Missing topiaforge.mod.json."));
                        continue;
                    }

                    try
                    {
                        var manifest = ModManifestJson.LoadFile(manifestPath);
                        var errors = ManifestValidator.Validate(manifest, validationContext).ToList();
                        if (!paths.TryGetPackageIdPath(manifest.Id, out _))
                        {
                            errors.Add("Manifest contains an unsafe mod id.");
                        }

                        if (!string.Equals(directoryId, manifest.Id, StringComparison.Ordinal))
                        {
                            errors.Add(
                                "Installed directory id '" + directoryId +
                                "' does not exactly match manifest name '" + manifest.Id + "'.");
                        }

                        if (!string.Equals(directoryVersion, manifest.Version, StringComparison.Ordinal))
                        {
                            errors.Add(
                                "Installed directory version '" + directoryVersion +
                                "' does not exactly match manifest version '" + manifest.Version + "'.");
                        }

                        if (errors.Count == 0)
                        {
                            errors.AddRange(ManifestContentValidator.Validate(versionDirectory, manifest));
                        }

                        if (errors.Count == 0)
                        {
                            errors.AddRange(PackageInstallReceipt.Verify(versionDirectory, manifest));
                        }

                        if (errors.Count == 0)
                        {
                            errors.AddRange(ManagedModAssemblyValidator.Validate(versionDirectory, manifest));
                        }

                        candidates.Add(new ScannedCandidate(
                            directoryId,
                            versionDirectory,
                            manifest,
                            errors));
                    }
                    catch (Exception ex)
                    {
                        candidates.Add(ScannedCandidate.Invalid(directoryId, versionDirectory, ex.Message));
                    }
                }
            }

            // Selection happens only after the complete, sorted scan. Resolve exact pins first, then search
            // unpinned versions as one dependency-compatible assignment so a newer provider cannot disable a
            // consumer when an installed lower version satisfies its declared range.
            var groups = candidates
                .GroupBy(candidate => candidate.DirectoryId, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => CreateSelectionGroup(group.Key, group.ToList(), state))
                .ToList();
            var selections = SelectCompatibleCandidates(groups);
            return selections
                .Select(selection => MaterializeSelection(selection, state))
                .OrderBy(p => p.Manifest?.Name ?? p.PackagePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.PackagePath, StringComparer.Ordinal)
                .ToList();
        }

        public void ApplyPendingUninstalls(ManagerPaths paths, ManagerState state)
        {
            var pending = state.Mods.Where(m => m.UninstallPending).ToList();
            foreach (var mod in pending)
            {
                RemoveInstalledPackage(paths, state, mod.Id);
            }
        }

        /// <summary>
        /// Removes one state-selected package. Invalid/tampered ids are removed from state without touching disk.
        /// </summary>
        public bool RemoveInstalledPackage(ManagerPaths paths, ManagerState state, string id)
        {
            var mod = state.Find(id);
            if (mod == null)
            {
                return false;
            }

            if (paths.TryGetPackageIdPath(mod.Id, out var modRoot) && Directory.Exists(modRoot))
            {
                Directory.Delete(modRoot, true);
            }

            state.Remove(id);
            return true;
        }

        private static SelectionGroup CreateSelectionGroup(
            string directoryId,
            List<ScannedCandidate> candidates,
            ManagerState state)
        {
            var selectedState = state.Find(directoryId);
            if (selectedState != null && selectedState.VersionPinned)
            {
                var selected = candidates.FirstOrDefault(candidate =>
                    candidate.Manifest != null &&
                    string.Equals(candidate.Manifest.Version, selectedState.Version, StringComparison.Ordinal))
                    ?? ScannedCandidate.Invalid(
                        directoryId,
                        Path.Combine(Path.GetDirectoryName(candidates[0].PackagePath) ?? candidates[0].PackagePath,
                            selectedState.Version),
                        "Pinned version '" + selectedState.Version + "' is not installed; refusing to fall back.");
                return new SelectionGroup(
                    directoryId,
                    selectedState,
                    new[] { selected },
                    pinned: true);
            }

            var options = candidates
                .Where(candidate => candidate.Manifest != null && candidate.Errors.Count == 0)
                .Where(candidate => VersionUtil.TryParseSemantic(candidate.Manifest!.Version, out _))
                .OrderByDescending(candidate =>
                {
                    VersionUtil.TryParseSemantic(candidate.Manifest!.Version, out var version);
                    return version;
                })
                .ThenBy(candidate => candidate.PackagePath, StringComparer.Ordinal)
                .ToList();
            if (options.Count == 0)
            {
                options.Add(candidates
                    .OrderBy(candidate => candidate.PackagePath, StringComparer.Ordinal)
                    .First());
            }

            return new SelectionGroup(directoryId, selectedState, options, pinned: false);
        }

        private static IReadOnlyList<CandidateSelection> SelectCompatibleCandidates(
            IReadOnlyList<SelectionGroup> groups)
        {
            var byId = groups.ToDictionary(
                group => group.DirectoryId,
                group => group,
                StringComparer.OrdinalIgnoreCase);
            var selected = new Dictionary<string, ScannedCandidate>(StringComparer.OrdinalIgnoreCase);
            var searchable = groups
                .Where(group => group.Options.Any(IsSelectableCandidate))
                .ToList();
            var visited = 0;
            var limitReached = false;

            bool Visit(int index)
            {
                if (index == searchable.Count)
                {
                    return IsVersionAssignmentCompatible(selected, byId);
                }

                var group = searchable[index];
                foreach (var candidate in group.Options.Where(IsSelectableCandidate))
                {
                    visited++;
                    if (visited > MaxVersionSelectionNodes)
                    {
                        limitReached = true;
                        return false;
                    }

                    selected[group.DirectoryId] = candidate;
                    if (IsPartialVersionAssignmentCompatible(selected, byId) && Visit(index + 1))
                    {
                        return true;
                    }

                    selected.Remove(group.DirectoryId);
                    if (limitReached)
                    {
                        return false;
                    }
                }

                return false;
            }

            var found = Visit(0);
            var suffix = found
                ? string.Empty
                : limitReached
                    ? "; dependency-compatible version search exceeded its bounded node limit"
                    : "; no complete dependency-compatible installed-version assignment exists";
            return groups.Select(group =>
            {
                var candidate = found && selected.TryGetValue(group.DirectoryId, out var compatible)
                    ? compatible
                    : group.Options[0];
                return new CandidateSelection(candidate, SelectionReason(group, candidate, suffix));
            }).ToList();
        }

        private static bool IsSelectableCandidate(ScannedCandidate candidate)
        {
            return candidate.Manifest != null && candidate.Errors.Count == 0;
        }

        private static bool IsPartialVersionAssignmentCompatible(
            IReadOnlyDictionary<string, ScannedCandidate> selected,
            IReadOnlyDictionary<string, SelectionGroup> groups)
        {
            foreach (var entry in selected)
            {
                if (!groups.TryGetValue(entry.Key, out var group) || !group.IsEnabled || entry.Value.Manifest == null)
                {
                    continue;
                }

                var manifest = entry.Value.Manifest;
                foreach (var dependency in manifest.Dependencies ?? new Dictionary<string, string>())
                {
                    if (!groups.TryGetValue(dependency.Key, out var dependencyGroup) || !dependencyGroup.IsEnabled)
                    {
                        continue;
                    }

                    if (selected.TryGetValue(dependency.Key, out var dependencyCandidate))
                    {
                        if (dependencyCandidate.Manifest != null &&
                            !VersionUtil.AllowsRange(dependencyCandidate.Manifest.Version, dependency.Value))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        var hasCompatibleOption = dependencyGroup.Options.Any(candidate =>
                            IsSelectableCandidate(candidate) &&
                            VersionUtil.AllowsRange(candidate.Manifest!.Version, dependency.Value));
                        if (dependencyGroup.Options.Any(IsSelectableCandidate) && !hasCompatibleOption)
                        {
                            return false;
                        }
                    }
                }

                foreach (var conflict in manifest.Conflicts ?? new List<ModConflict>())
                {
                    if (!groups.TryGetValue(conflict.Id, out var conflictGroup) || !conflictGroup.IsEnabled)
                    {
                        continue;
                    }

                    if (selected.TryGetValue(conflict.Id, out var conflictCandidate))
                    {
                        if (conflictCandidate.Manifest != null &&
                            ConflictMatches(conflictCandidate.Manifest.Version, conflict))
                        {
                            return false;
                        }
                    }
                    else if (conflictGroup.Options.Any(IsSelectableCandidate) &&
                             conflictGroup.Options.Where(IsSelectableCandidate).All(candidate =>
                                 ConflictMatches(candidate.Manifest!.Version, conflict)))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsVersionAssignmentCompatible(
            IReadOnlyDictionary<string, ScannedCandidate> selected,
            IReadOnlyDictionary<string, SelectionGroup> groups)
        {
            return IsPartialVersionAssignmentCompatible(selected, groups);
        }

        private static bool ConflictMatches(string actualVersion, ModConflict conflict)
        {
            return string.IsNullOrWhiteSpace(conflict.VersionRange) ||
                VersionUtil.AllowsRange(actualVersion, conflict.VersionRange);
        }

        private static string SelectionReason(
            SelectionGroup group,
            ScannedCandidate selected,
            string suffix)
        {
            if (group.Pinned)
            {
                return "exact profile pin '" + (group.State?.Version ?? string.Empty) + "'";
            }

            var selectedVersion = selected.Manifest?.Version ?? string.Empty;
            var previouslySelectedVersion = group.State?.Version ?? string.Empty;
            var reason = previouslySelectedVersion.Length == 0
                ? "highest compatible version '" + selectedVersion + "' selected for an unpinned profile"
                : string.Equals(previouslySelectedVersion, selectedVersion, StringComparison.Ordinal)
                    ? "highest compatible unpinned version '" + selectedVersion + "' retained"
                    : "recovered unpinned selection from '" + previouslySelectedVersion
                        + "' to highest compatible version '" + selectedVersion + "'";
            return reason + suffix;
        }

        private static ModPackage MaterializeSelection(CandidateSelection selection, ManagerState state)
        {
            var selected = selection.Candidate;
            if (selected.Manifest == null)
            {
                return ModPackage.Invalid(selected.PackagePath, selected.Errors);
            }

            var modState = state.Find(selected.Manifest.Id);
            if (selected.Errors.Count == 0)
            {
                if (modState == null)
                {
                    modState = state.Upsert(
                        selected.Manifest,
                        enabled: ModActivationPolicy.IsEnabledByDefault(selected.Manifest),
                        restartRequired: true);
                }
                else if (!modState.VersionPinned)
                {
                    modState.Name = selected.Manifest.Name;
                    modState.Version = selected.Manifest.Version;
                }
            }

            return new ModPackage(
                selected.PackagePath,
                selected.Manifest,
                modState,
                selected.Errors,
                selection.Reason);
        }

        private sealed class SelectionGroup
        {
            public SelectionGroup(
                string directoryId,
                InstalledModState? state,
                IReadOnlyList<ScannedCandidate> options,
                bool pinned)
            {
                DirectoryId = directoryId;
                State = state;
                Options = options;
                Pinned = pinned;
            }

            public string DirectoryId { get; }
            public InstalledModState? State { get; }
            public IReadOnlyList<ScannedCandidate> Options { get; }
            public bool Pinned { get; }
            public bool IsEnabled => State == null || (State.Enabled && !State.UninstallPending);
        }

        private sealed class CandidateSelection
        {
            public CandidateSelection(ScannedCandidate candidate, string reason)
            {
                Candidate = candidate;
                Reason = reason;
            }

            public ScannedCandidate Candidate { get; }
            public string Reason { get; }
        }

        private sealed class ScannedCandidate
        {
            public ScannedCandidate(
                string directoryId,
                string packagePath,
                ModManifest? manifest,
                IReadOnlyList<string> errors)
            {
                DirectoryId = directoryId;
                PackagePath = packagePath;
                Manifest = manifest;
                Errors = errors;
            }

            public string DirectoryId { get; }
            public string PackagePath { get; }
            public ModManifest? Manifest { get; }
            public IReadOnlyList<string> Errors { get; }

            public static ScannedCandidate Invalid(string directoryId, string packagePath, string error)
            {
                return new ScannedCandidate(directoryId, packagePath, null, new[] { error });
            }
        }
    }

    public sealed class ModPackage
    {
        public ModPackage(
            string packagePath,
            ModManifest manifest,
            InstalledModState? state,
            IReadOnlyList<string> errors,
            string selectionReason = "")
        {
            PackagePath = packagePath;
            Manifest = manifest;
            State = state;
            Errors = errors;
            SelectionReason = selectionReason ?? string.Empty;
        }

        private ModPackage(string packagePath, IReadOnlyList<string> errors)
        {
            PackagePath = packagePath;
            Errors = errors;
            SelectionReason = string.Empty;
        }

        public string PackagePath { get; }
        public ModManifest? Manifest { get; }
        public InstalledModState? State { get; }
        public IReadOnlyList<string> Errors { get; }
        public string SelectionReason { get; }

        public bool IsValid => Manifest != null && State != null && Errors.Count == 0;
        public bool IsEnabled => State != null && State.Enabled && !State.UninstallPending;

        public static ModPackage Invalid(string packagePath, string error)
        {
            return new ModPackage(packagePath, new[] { error });
        }

        public static ModPackage Invalid(string packagePath, IReadOnlyList<string> errors)
        {
            return new ModPackage(packagePath, errors);
        }
    }
}
