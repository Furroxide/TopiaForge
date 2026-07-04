using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Robotopia.ModManager.Core
{
    public sealed class PackageInstaller
    {
        public PackageInstallResult Install(string packagePath, ManagerPaths paths, ManagerState state, bool restartRequired)
        {
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            {
                return PackageInstallResult.Fail("Package file does not exist: " + packagePath);
            }

            paths.EnsureCreated();
            var stagingPath = Path.Combine(paths.Staging, "install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingPath);

            try
            {
                ExtractToSafeDirectory(packagePath, stagingPath);
                var manifestPath = Path.Combine(stagingPath, "robotopia.mod.json");
                if (!File.Exists(manifestPath))
                {
                    return PackageInstallResult.Fail("Package is missing robotopia.mod.json.");
                }

                var manifest = JsonUtil.LoadFile(manifestPath, new ModManifest());
                var errors = ManifestValidator.Validate(manifest);
                if (errors.Count > 0)
                {
                    return PackageInstallResult.Fail(errors);
                }

                var entryAssemblyPath = Path.Combine(stagingPath, manifest.EntryAssembly);
                if (!File.Exists(entryAssemblyPath))
                {
                    return PackageInstallResult.Fail("entryAssembly was not found in package: " + manifest.EntryAssembly);
                }

                var targetPath = paths.GetPackagePath(manifest.Id, manifest.Version);
                if (Directory.Exists(targetPath))
                {
                    Directory.Delete(targetPath, true);
                }

                CopyDirectory(stagingPath, targetPath);
                var existing = state.Find(manifest.Id);
                state.Upsert(manifest, enabled: existing?.Enabled ?? true, restartRequired: restartRequired);
                PruneOtherVersions(paths, manifest.Id, manifest.Version);

                return PackageInstallResult.Success(manifest, targetPath);
            }
            catch (Exception ex)
            {
                return PackageInstallResult.Fail(ex.Message);
            }
            finally
            {
                TryDelete(stagingPath);
            }
        }

        /// <summary>
        /// Installs every .robotopiamod file waiting in the package-inbox. When the inbox holds several
        /// versions of the same mod, only the highest version is installed and the rest are marked
        /// superseded. Successfully processed files are consumed (deleted, or renamed to *.installed when
        /// the delete is blocked); failed installs leave their file in place so the user can inspect it.
        /// </summary>
        public IReadOnlyList<InboxInstallResult> InstallInbox(ManagerPaths paths, ManagerState state, bool restartRequired)
        {
            var results = new List<InboxInstallResult>();
            if (!Directory.Exists(paths.PackageInbox))
            {
                return results;
            }

            var files = Directory.GetFiles(paths.PackageInbox, "*.robotopiamod", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
            {
                return results;
            }

            // Pick one winner per mod id up front (highest parseable version); everything else for that id
            // is superseded. Files whose manifest cannot be pre-read stay winners of their own group so the
            // normal install path can produce the real, actionable error.
            var winners = new Dictionary<string, (string File, Version Version)>(StringComparer.OrdinalIgnoreCase);
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var fileToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                var manifest = TryReadPackedManifest(file);
                var id = manifest != null && !string.IsNullOrWhiteSpace(manifest.Id) ? manifest.Id : file;
                fileToId[file] = id;
                if (!groups.TryGetValue(id, out var group))
                {
                    group = new List<string>();
                    groups[id] = group;
                }

                group.Add(file);
                VersionUtil.TryParse(manifest?.Version ?? string.Empty, out var version);
                if (!winners.TryGetValue(id, out var best) || version > best.Version)
                {
                    winners[id] = (file, version);
                }
            }

            foreach (var file in files)
            {
                var groupId = fileToId[file];
                if (!string.Equals(winners[groupId].File, file, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // superseded — handled after its winner installs
                }

                var install = Install(file, paths, state, restartRequired);
                var result = new InboxInstallResult(file, install, superseded: false);
                if (install.Ok)
                {
                    Consume(result);
                }

                results.Add(result);

                foreach (var loser in groups[groupId])
                {
                    if (string.Equals(loser, file, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Only consume superseded files once the winner actually installed; otherwise leave
                    // the whole group on disk for inspection.
                    var supersededResult = new InboxInstallResult(loser, null, superseded: true);
                    if (install.Ok)
                    {
                        Consume(supersededResult);
                    }

                    results.Add(supersededResult);
                }
            }

            return results;
        }

        // Reads just the manifest out of a packed .robotopiamod zip; null when the file or manifest is
        // unreadable (the caller then routes the file through the normal install path for a real error).
        private static ModManifest? TryReadPackedManifest(string packagePath)
        {
            try
            {
                using (var file = File.OpenRead(packagePath))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    var entry = archive.GetEntry("robotopia.mod.json");
                    if (entry == null)
                    {
                        return null;
                    }

                    using (var stream = entry.Open())
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        buffer.Position = 0;
                        return JsonUtil.Deserialize<ModManifest>(buffer);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static void Consume(InboxInstallResult result)
        {
            try
            {
                File.Delete(result.FilePath);
                result.Consumed = true;
            }
            catch (Exception)
            {
                // A locked file (AV scan, Explorer preview) cannot be deleted; renaming it out of the
                // *.robotopiamod pattern keeps it from being reprocessed while preserving the bytes.
                try
                {
                    var renamed = result.FilePath + ".installed";
                    if (File.Exists(renamed))
                    {
                        File.Delete(renamed);
                    }

                    File.Move(result.FilePath, renamed);
                    result.Consumed = true;
                }
                catch (Exception ex)
                {
                    result.ConsumeError = ex.Message;
                }
            }
        }

        // Superseded sibling versions would otherwise accumulate forever and, once their manifest schema
        // ages out, produce a startup warning per launch. Deletes are best-effort: a mid-session upgrade
        // has the old version's DLL loaded/locked, and the startup prune sweeps it next boot.
        private static void PruneOtherVersions(ManagerPaths paths, string id, string keepVersion)
        {
            var idRoot = Path.Combine(paths.Packages, id);
            if (!Directory.Exists(idRoot))
            {
                return;
            }

            foreach (var versionDirectory in Directory.GetDirectories(idRoot))
            {
                if (string.Equals(Path.GetFileName(versionDirectory), keepVersion, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(versionDirectory, true);
                }
                catch
                {
                    // Locked by a loaded assembly; the startup prune retries when nothing is loaded.
                }
            }
        }

        private static void ExtractToSafeDirectory(string zipPath, string destination)
        {
            var destinationFullPath = Path.GetFullPath(destination);
            if (!destinationFullPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                destinationFullPath += Path.DirectorySeparatorChar;
            }

            using (var file = File.OpenRead(zipPath))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    var targetPath = Path.GetFullPath(Path.Combine(destinationFullPath, entry.FullName));
                    if (!targetPath.StartsWith(destinationFullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Package contains a path outside the install directory: " + entry.FullName);
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(targetPath);
                        continue;
                    }

                    var targetDirectory = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDirectory))
                    {
                        Directory.CreateDirectory(targetDirectory);
                    }

                    entry.ExtractToFile(targetPath, overwrite: true);
                }
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(directory.Replace(source, destination));
            }

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = file.Replace(source, destination);
                var targetDirectory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                File.Copy(file, target, overwrite: true);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
                // Staging cleanup failure should not hide the install result.
            }
        }
    }

    /// <summary>One inbox file's outcome from <see cref="PackageInstaller.InstallInbox"/>.</summary>
    public sealed class InboxInstallResult
    {
        public InboxInstallResult(string filePath, PackageInstallResult? install, bool superseded)
        {
            FilePath = filePath;
            Install = install;
            Superseded = superseded;
        }

        public string FilePath { get; }

        /// <summary>Null when the file was skipped as superseded by a newer version in the same inbox.</summary>
        public PackageInstallResult? Install { get; }

        public bool Superseded { get; }

        public bool Consumed { get; internal set; }

        public string? ConsumeError { get; internal set; }
    }

    public sealed class PackageInstallResult
    {
        private PackageInstallResult(bool ok, ModManifest? manifest, string? installPath, IReadOnlyList<string> errors)
        {
            Ok = ok;
            Manifest = manifest;
            InstallPath = installPath;
            Errors = errors;
        }

        public bool Ok { get; }
        public ModManifest? Manifest { get; }
        public string? InstallPath { get; }
        public IReadOnlyList<string> Errors { get; }

        public static PackageInstallResult Success(ModManifest manifest, string installPath)
        {
            return new PackageInstallResult(true, manifest, installPath, Array.Empty<string>());
        }

        public static PackageInstallResult Fail(string error)
        {
            return new PackageInstallResult(false, null, null, new[] { error });
        }

        public static PackageInstallResult Fail(IReadOnlyList<string> errors)
        {
            return new PackageInstallResult(false, null, null, errors);
        }
    }
}
