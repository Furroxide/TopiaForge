using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

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
