using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Pure package-ownership catalog for the AppDomain resolver. A mod may see its own private assemblies,
    /// framework assemblies beside the loader, and assemblies owned by dependencies it explicitly declared.
    /// </summary>
    internal sealed class ModAssemblyResolutionCatalog
    {
        private readonly Dictionary<string, ModPackage> packages;
        private readonly string pluginDirectory;

        internal ModAssemblyResolutionCatalog(IEnumerable<ModPackage> orderedPackages, string pluginDirectory)
        {
            packages = (orderedPackages ?? throw new ArgumentNullException(nameof(orderedPackages)))
                .Where(package => package.Manifest != null)
                .GroupBy(package => package.Manifest!.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            this.pluginDirectory = string.IsNullOrWhiteSpace(pluginDirectory)
                ? string.Empty
                : Path.GetFullPath(pluginDirectory);
        }

        internal IReadOnlyDictionary<string, IReadOnlyList<string>> ValidateScopes()
        {
            var errors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in packages.Values.OrderBy(
                candidate => candidate.Manifest!.Id,
                StringComparer.OrdinalIgnoreCase))
            {
                var ownerErrors = ValidateScope(package.Manifest!.Id);
                if (ownerErrors.Count > 0)
                {
                    errors[package.Manifest.Id] = ownerErrors;
                }
            }

            return errors;
        }

        internal bool IsOwnerVisible(string requesterOwner, string candidateOwner)
        {
            if (string.Equals(requesterOwner, candidateOwner, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return VisibleOwners(requesterOwner).Contains(candidateOwner, StringComparer.OrdinalIgnoreCase);
        }

        internal bool TryGetOwner(string assemblyPath, out string owner)
        {
            owner = string.Empty;
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(assemblyPath);
            }
            catch
            {
                return false;
            }

            foreach (var package in packages.Values)
            {
                if (PathSafety.IsSameOrChild(package.PackagePath, fullPath))
                {
                    owner = package.Manifest!.Id;
                    return true;
                }
            }

            return false;
        }

        internal string? FindCandidate(string? requesterOwner, AssemblyName requested)
        {
            if (requested == null || string.IsNullOrWhiteSpace(requested.Name) ||
                requested.Name!.IndexOfAny(new[] { '/', '\\', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
            {
                return null;
            }

            var candidates = CandidateFiles(requesterOwner, requested.Name)
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            var matches = new List<string>();
            var mismatches = new List<string>();
            foreach (var candidate in candidates)
            {
                EnsureRegularFile(candidate);
                var definition = AssemblyName.GetAssemblyName(candidate);
                if (IdentityMatches(requested, definition))
                {
                    matches.Add(candidate);
                }
                else
                {
                    mismatches.Add(candidate + " [" + definition.FullName + "]");
                }
            }

            if (matches.Count == 0)
            {
                throw new FileLoadException(
                    "Assembly '" + requested.FullName + "' was found in the requester scope, but its identity did not match: "
                    + string.Join(", ", mismatches));
            }

            if (matches.Count > 1 && !HaveIdenticalBytes(matches))
            {
                throw new FileLoadException(
                    "Assembly '" + requested.FullName + "' is ambiguous in the requester scope: "
                    + string.Join(", ", matches));
            }

            return matches[0];
        }

        internal static bool IdentityMatches(AssemblyName requested, AssemblyName definition)
        {
            if (!string.Equals(requested.Name, definition.Name, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (requested.Version != null && requested.Version != definition.Version)
            {
                return false;
            }

            var requestedCulture = requested.CultureName ?? string.Empty;
            var definitionCulture = definition.CultureName ?? string.Empty;
            if (requestedCulture.Length > 0 &&
                !requestedCulture.Equals("neutral", StringComparison.OrdinalIgnoreCase) &&
                !requestedCulture.Equals(definitionCulture, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var requestedToken = requested.GetPublicKeyToken() ?? Array.Empty<byte>();
            var definitionToken = definition.GetPublicKeyToken() ?? Array.Empty<byte>();
            return requestedToken.Length == 0 || requestedToken.SequenceEqual(definitionToken);
        }

        private IReadOnlyList<string> ValidateScope(string owner)
        {
            var errors = new List<string>();
            // Only package-owned files are preflighted. The loader directory may contain unrelated BepInEx
            // plugins/native helpers; framework candidates are identity-checked lazily when actually requested.
            var files = OwnerPrivateDirectories(owner)
                .Where(Directory.Exists)
                .SelectMany(directory => Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var provider in RequiredOwners(owner))
            {
                try
                {
                    files.AddRange(ExportedAssemblyFiles(provider));
                }
                catch (Exception ex)
                {
                    errors.Add("Required dependency '" + provider + "' has an invalid API export: " + ex.Message);
                }
            }

            files = files
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    EnsureRegularFile(file);
                    var definition = AssemblyName.GetAssemblyName(file);
                    var expectedName = Path.GetFileNameWithoutExtension(file);
                    if (!string.Equals(expectedName, definition.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add("Assembly file '" + file + "' declares identity '" + definition.Name
                            + "' instead of its filename '" + expectedName + "'.");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("Assembly file '" + file + "' could not be preflighted: " + ex.Message);
                }
            }

            foreach (var group in files.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var duplicates = group.ToList();
                if (duplicates.Count > 1 && !HaveIdenticalBytes(duplicates))
                {
                    errors.Add("Assembly filename collision in the declared dependency scope: "
                        + string.Join(", ", duplicates) + ".");
                }
            }

            return errors;
        }

        private IEnumerable<string> CandidateFiles(string? owner, string assemblyName)
        {
            if (pluginDirectory.Length > 0)
            {
                yield return Path.Combine(pluginDirectory, assemblyName + ".dll");
            }

            if (string.IsNullOrWhiteSpace(owner))
            {
                yield break;
            }

            foreach (var directory in OwnerPrivateDirectories(owner))
            {
                yield return Path.Combine(directory, assemblyName + ".dll");
            }

            foreach (var provider in RequiredOwners(owner).Concat(OptionalOwners(owner)))
            {
                foreach (var exported in ExportedAssemblyFiles(provider, assemblyName))
                {
                    yield return exported;
                }
            }
        }

        private IEnumerable<string> OwnerPrivateDirectories(string owner)
        {
            if (!packages.TryGetValue(owner, out var package))
            {
                yield break;
            }

            yield return Path.GetFullPath(package.PackagePath);
            var entryDirectory = Path.GetDirectoryName(package.Manifest!.EntryAssembly);
            if (!string.IsNullOrEmpty(entryDirectory))
            {
                yield return Path.GetFullPath(Path.Combine(package.PackagePath, entryDirectory));
            }
        }

        private IEnumerable<string> ExportedAssemblyFiles(string owner, string? assemblyName = null)
        {
            if (!packages.TryGetValue(owner, out var package))
            {
                yield break;
            }

            foreach (var relativePath in package.Manifest!.ApiAssemblies ?? new List<string>())
            {
                if (!string.IsNullOrEmpty(assemblyName) &&
                    !string.Equals(
                        Path.GetFileNameWithoutExtension(relativePath),
                        assemblyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string fullPath;
                try
                {
                    fullPath = PathSafety.CombineRelativeChild(
                        package.PackagePath,
                        relativePath.Replace('/', Path.DirectorySeparatorChar));
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException(
                        "Package '" + owner + "' declares an unsafe apiAssemblies path '" + relativePath + "'.",
                        ex);
                }

                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException(
                        "Package '" + owner + "' exports a missing API assembly '" + relativePath + "'.",
                        fullPath);
                }

                yield return fullPath;
            }
        }

        private IEnumerable<string> VisibleOwners(string owner)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in RequiredOwners(owner).Concat(OptionalOwners(owner)))
            {
                if (seen.Add(dependency))
                {
                    yield return dependency;
                }
            }
        }

        private IEnumerable<string> RequiredOwners(string owner)
        {
            if (!packages.TryGetValue(owner, out var package))
            {
                yield break;
            }

            foreach (var dependency in DependencyResolver.GetRequiredDependencies(package.Manifest!))
            {
                if (packages.ContainsKey(dependency.Id))
                {
                    yield return dependency.Id;
                }
            }
        }

        private IEnumerable<string> OptionalOwners(string owner)
        {
            if (!packages.TryGetValue(owner, out var package))
            {
                yield break;
            }

            foreach (var dependency in package.Manifest!.OptionalDependencies ?? new Dictionary<string, string>())
            {
                if (packages.TryGetValue(dependency.Key, out var provider) &&
                    VersionUtil.AllowsRange(provider.Manifest!.Version, dependency.Value))
                {
                    yield return dependency.Key;
                }
            }
        }

        private static void EnsureRegularFile(string path)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException("Assembly candidates must be regular files: " + path);
            }
        }

        private static bool HaveIdenticalBytes(IReadOnlyList<string> paths)
        {
            if (paths.Count < 2)
            {
                return true;
            }

            var expectedLength = new FileInfo(paths[0]).Length;
            var expected = ComputeSha256(paths[0]);
            for (var index = 1; index < paths.Count; index++)
            {
                if (new FileInfo(paths[index]).Length != expectedLength ||
                    !expected.SequenceEqual(ComputeSha256(paths[index])))
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] ComputeSha256(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(input);
            }
        }
    }
}
