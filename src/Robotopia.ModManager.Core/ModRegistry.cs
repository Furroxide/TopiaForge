using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Robotopia.ModManager.Core
{
    public sealed class ModRegistry
    {
        public IReadOnlyList<ModPackage> Scan(ManagerPaths paths, ManagerState state)
        {
            paths.EnsureCreated();
            var packages = new List<ModPackage>();

            if (!Directory.Exists(paths.Packages))
            {
                return packages;
            }

            foreach (var idDirectory in Directory.GetDirectories(paths.Packages))
            {
                foreach (var versionDirectory in Directory.GetDirectories(idDirectory))
                {
                    var manifestPath = Path.Combine(versionDirectory, "robotopia.mod.json");
                    if (!File.Exists(manifestPath))
                    {
                        packages.Add(ModPackage.Invalid(versionDirectory, "Missing robotopia.mod.json."));
                        continue;
                    }

                    try
                    {
                        var manifest = JsonUtil.LoadFile(manifestPath, new ModManifest());
                        var errors = ManifestValidator.Validate(manifest);
                        var modState = state.Find(manifest.Id);
                        if (modState == null)
                        {
                            modState = state.Upsert(manifest, enabled: true, restartRequired: true);
                        }

                        packages.Add(new ModPackage(versionDirectory, manifest, modState, errors));
                    }
                    catch (Exception ex)
                    {
                        packages.Add(ModPackage.Invalid(versionDirectory, ex.Message));
                    }
                }
            }

            // Group by the id DIRECTORY name (ids are directory names by construction — see
            // ManagerPaths.GetPackagePath), not the manifest id: a stale version whose old-schema manifest
            // no longer parses must fold into its mod's group so the valid current version wins the pick,
            // instead of surfacing as its own broken "package" and warning on every launch.
            return packages
                .GroupBy(
                    p => Path.GetFileName(Path.GetDirectoryName(p.PackagePath)) ?? p.PackagePath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => PickCurrentVersion(g.ToList(), state))
                .OrderBy(p => p.Manifest?.Name ?? p.PackagePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Deletes version directories superseded by the state-selected version of each installed mod.
        /// Meant for startup (nothing loaded, so no file locks): install-time pruning can be blocked by a
        /// loaded assembly, and pre-prune installs left old versions behind forever — their aged-out
        /// manifests then warned on every launch. Ids without a state entry, or whose selected version
        /// directory is missing, are left untouched (never delete the only candidates a broken selection has).
        /// </summary>
        public void PruneSupersededVersions(ManagerPaths paths, ManagerState state, Action<string>? onPruned = null)
        {
            if (!Directory.Exists(paths.Packages))
            {
                return;
            }

            foreach (var idDirectory in Directory.GetDirectories(paths.Packages))
            {
                var id = Path.GetFileName(idDirectory);
                var keepVersion = state.Find(id)?.Version;
                if (string.IsNullOrWhiteSpace(keepVersion)
                    || !Directory.Exists(Path.Combine(idDirectory, keepVersion)))
                {
                    continue;
                }

                foreach (var versionDirectory in Directory.GetDirectories(idDirectory))
                {
                    if (string.Equals(Path.GetFileName(versionDirectory), keepVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        Directory.Delete(versionDirectory, true);
                        onPruned?.Invoke(id + " " + Path.GetFileName(versionDirectory));
                    }
                    catch
                    {
                        // Still locked or access-denied; retried on the next launch.
                    }
                }
            }
        }

        public void ApplyPendingUninstalls(ManagerPaths paths, ManagerState state)
        {
            var pending = state.Mods.Where(m => m.UninstallPending).ToList();
            foreach (var mod in pending)
            {
                var modRoot = Path.Combine(paths.Packages, mod.Id);
                if (Directory.Exists(modRoot))
                {
                    Directory.Delete(modRoot, true);
                }

                state.Remove(mod.Id);
            }
        }

        private static ModPackage PickCurrentVersion(List<ModPackage> packages, ManagerState state)
        {
            if (packages.Count == 1)
            {
                return packages[0];
            }

            var firstManifest = packages.FirstOrDefault(p => p.Manifest != null)?.Manifest;
            if (firstManifest != null)
            {
                var selectedState = state.Find(firstManifest.Id);
                if (selectedState != null)
                {
                    var match = packages.FirstOrDefault(p =>
                        p.Manifest != null &&
                        string.Equals(p.Manifest.Version, selectedState.Version, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        return match;
                    }
                }
            }

            return packages
                .Where(p => p.Manifest != null && VersionUtil.TryParse(p.Manifest.Version, out _))
                .OrderByDescending(p =>
                {
                    VersionUtil.TryParse(p.Manifest!.Version, out var version);
                    return version;
                })
                .FirstOrDefault() ?? packages[0];
        }
    }

    public sealed class ModPackage
    {
        public ModPackage(string packagePath, ModManifest manifest, InstalledModState state, IReadOnlyList<string> errors)
        {
            PackagePath = packagePath;
            Manifest = manifest;
            State = state;
            Errors = errors;
        }

        private ModPackage(string packagePath, IReadOnlyList<string> errors)
        {
            PackagePath = packagePath;
            Errors = errors;
        }

        public string PackagePath { get; }
        public ModManifest? Manifest { get; }
        public InstalledModState? State { get; }
        public IReadOnlyList<string> Errors { get; }

        public bool IsValid => Manifest != null && State != null && Errors.Count == 0;
        public bool IsEnabled => State != null && State.Enabled && !State.UninstallPending;

        public static ModPackage Invalid(string packagePath, string error)
        {
            return new ModPackage(packagePath, new[] { error });
        }
    }
}
