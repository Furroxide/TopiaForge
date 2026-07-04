using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Robotopia.ModManager.Core
{
    public static class ManifestValidator
    {
        private static readonly Regex IdRegex = new Regex("^[A-Za-z0-9][A-Za-z0-9_.-]{1,63}$", RegexOptions.Compiled);

        public static IReadOnlyList<string> Validate(ModManifest manifest)
        {
            var errors = new List<string>();

            if (manifest.SchemaVersion != 2)
            {
                errors.Add("schemaVersion must be 2.");
            }

            if (string.IsNullOrWhiteSpace(manifest.Id) || !IdRegex.IsMatch(manifest.Id))
            {
                errors.Add("name must be 2-64 characters and contain only letters, numbers, underscore, dot, or dash.");
            }

            if (string.IsNullOrWhiteSpace(manifest.Name))
            {
                errors.Add("displayName is required.");
            }

            if (manifest.Author == null || string.IsNullOrWhiteSpace(manifest.Author.Name))
            {
                errors.Add("author.name is required.");
            }

            if (!VersionUtil.TryParse(manifest.Version, out _))
            {
                errors.Add("version must be parseable as a semantic version, for example 1.0.0.");
            }

            if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
            {
                errors.Add("entryAssembly is required for C# mods.");
            }
            else if (Path.IsPathRooted(manifest.EntryAssembly) || manifest.EntryAssembly.Contains(".."))
            {
                errors.Add("entryAssembly must be a relative file name inside the package.");
            }

            if (string.IsNullOrWhiteSpace(manifest.EntryType))
            {
                errors.Add("entryType is required for C# mods.");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in VpmDependencies(manifest))
            {
                ValidateDependency(dependency, "vpmDependencies", seen, errors);
            }

            foreach (var dependency in manifest.Dependencies ?? new List<ModDependency>())
            {
                ValidateDependency(dependency, "dependencies", seen, errors);
            }

            seen.Clear();
            foreach (var dependency in manifest.OptionalDependencies ?? new List<ModDependency>())
            {
                ValidateDependency(dependency, "optionalDependencies", seen, errors);
            }

            seen.Clear();
            foreach (var conflict in manifest.Conflicts ?? new List<ModConflict>())
            {
                if (string.IsNullOrWhiteSpace(conflict.Id))
                {
                    errors.Add("conflicts entries must include id.");
                    continue;
                }

                if (!seen.Add(conflict.Id))
                {
                    errors.Add("conflicts contains duplicate id '" + conflict.Id + "'.");
                }

                if (!string.IsNullOrWhiteSpace(conflict.Version) && !VersionUtil.TryParseRange(conflict.Version))
                {
                    errors.Add("conflict '" + conflict.Id + "' has an invalid version.");
                }

                if (!string.IsNullOrWhiteSpace(conflict.VersionRange) && !VersionUtil.TryParseRange(conflict.VersionRange))
                {
                    errors.Add("conflict '" + conflict.Id + "' has an invalid versionRange.");
                }
            }

            if (!string.IsNullOrWhiteSpace(manifest.SupportedGameVersionRange) && !VersionUtil.TryParseRange(manifest.SupportedGameVersionRange))
            {
                errors.Add("supportedGameVersionRange is invalid.");
            }

            if (!string.IsNullOrWhiteSpace(manifest.SupportedLoaderVersionRange) && !VersionUtil.TryParseRange(manifest.SupportedLoaderVersionRange))
            {
                errors.Add("supportedLoaderVersionRange is invalid.");
            }
            else if (!string.IsNullOrWhiteSpace(manifest.SupportedLoaderVersionRange) &&
                     !VersionUtil.AllowsRange(RobotopiaVersions.LoaderVersion, manifest.SupportedLoaderVersionRange))
            {
                errors.Add("supportedLoaderVersionRange does not include loader " + RobotopiaVersions.LoaderVersion + ".");
            }

            if (!string.IsNullOrWhiteSpace(manifest.SupportedSdkVersionRange) && !VersionUtil.TryParseRange(manifest.SupportedSdkVersionRange))
            {
                errors.Add("supportedSdkVersionRange is invalid.");
            }
            else if (!string.IsNullOrWhiteSpace(manifest.SupportedSdkVersionRange) &&
                     !VersionUtil.AllowsRange(RobotopiaVersions.SdkVersion, manifest.SupportedSdkVersionRange))
            {
                errors.Add("supportedSdkVersionRange does not include SDK " + RobotopiaVersions.SdkVersion + ".");
            }

            return errors;
        }

        private static IEnumerable<ModDependency> VpmDependencies(ModManifest manifest)
        {
            foreach (var entry in manifest.VpmDependencies ?? new Dictionary<string, string>())
            {
                yield return new ModDependency
                {
                    Id = entry.Key,
                    VersionRange = entry.Value
                };
            }
        }

        private static void ValidateDependency(
            ModDependency dependency,
            string fieldName,
            HashSet<string> seen,
            List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(dependency.Id))
            {
                errors.Add(fieldName + " entries must include id.");
                return;
            }

            if (!seen.Add(dependency.Id))
            {
                errors.Add(fieldName + " contains duplicate id '" + dependency.Id + "'.");
            }

            if (!string.IsNullOrWhiteSpace(dependency.Version) && !VersionUtil.TryParse(dependency.Version, out _))
            {
                errors.Add("dependency '" + dependency.Id + "' has an invalid version.");
            }

            if (!string.IsNullOrWhiteSpace(dependency.VersionRange) && !VersionUtil.TryParseRange(dependency.VersionRange))
            {
                errors.Add("dependency '" + dependency.Id + "' has an invalid versionRange.");
            }
        }
    }
}
