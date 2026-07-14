using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Robotopia.ModManager.Core
{
    public static class ManifestValidator
    {
        private static readonly Regex IdRegex = new Regex("^[A-Za-z0-9][A-Za-z0-9_.-]{1,63}$", RegexOptions.Compiled);

        public static IReadOnlyList<string> Validate(ModManifest manifest)
        {
            return Validate(manifest, ManifestValidationContext.Current);
        }

        public static IReadOnlyList<string> Validate(ModManifest manifest, ManifestValidationContext context)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

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

            var assemblyPaths = new HashSet<string>(StringComparer.Ordinal);
            ValidatePortablePath(
                manifest.EntryAssembly,
                "entryAssembly",
                required: true,
                requireDll: true,
                seen: assemblyPaths,
                errors);

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

            foreach (var dependency in manifest.OptionalDependencies ?? new List<ModDependency>())
            {
                ValidateDependency(dependency, "optionalDependencies", seen, errors);
            }

            seen.Clear();
            foreach (var conflict in manifest.Conflicts ?? new List<ModConflict>())
            {
                if (!IsValidId(conflict.Id))
                {
                    errors.Add("conflicts id '" + conflict.Id + "' must use the safe mod id format.");
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

            foreach (var loadAfterId in manifest.LoadAfter ?? new List<string>())
            {
                if (!IsValidId(loadAfterId))
                {
                    errors.Add("loadAfter id '" + loadAfterId + "' must use the safe mod id format.");
                }
            }

            var gameRange = EffectiveGameRange(manifest);
            ValidateGameCompatibility(gameRange, context, errors);

            var loaderRange = FirstNonEmpty(manifest.SupportedLoaderVersionRange, manifest.LoaderVersionRange);
            ValidateCompatibilityRange(
                loaderRange,
                "supportedLoaderVersionRange",
                "loader",
                context.LoaderVersion,
                errors);

            var sdkRange = FirstNonEmpty(manifest.SupportedSdkVersionRange, manifest.SdkVersionRange);
            ValidateCompatibilityRange(
                sdkRange,
                "supportedSdkVersionRange",
                "SDK",
                context.SdkVersion,
                errors);

            ValidateLicenseFiles(manifest.LicenseFiles, errors);
            ValidateApiAssemblies(manifest.ApiAssemblies, assemblyPaths, errors);

            return errors;
        }

        private static string EffectiveGameRange(ModManifest manifest)
        {
            var range = FirstNonEmpty(manifest.SupportedGameVersionRange, manifest.GameVersionRange);
            if (!string.IsNullOrWhiteSpace(range))
            {
                return range;
            }

            if (string.IsNullOrWhiteSpace(manifest.GameVersion))
            {
                return string.Empty;
            }

            return GameBuildVersion.TryFromBuildLabel(manifest.GameVersion, out var buildVersion)
                ? buildVersion
                : manifest.GameVersion;
        }

        private static void ValidateGameCompatibility(
            string range,
            ManifestValidationContext context,
            List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(range))
            {
                return;
            }

            if (!VersionUtil.TryParseRange(range))
            {
                errors.Add("supportedGameVersionRange is invalid.");
                return;
            }

            if (string.IsNullOrWhiteSpace(context.GameVersion))
            {
                if (context.RequireKnownGameVersion)
                {
                    errors.Add("supportedGameVersionRange cannot be checked because the installed game version is unknown.");
                }

                return;
            }

            if (!GameBuildVersion.TryNormalize(context.GameVersion, out var actual))
            {
                errors.Add("Installed game version is invalid: " + context.GameVersion + ".");
            }
            else if (!VersionUtil.AllowsRange(actual, range))
            {
                errors.Add("supportedGameVersionRange does not include game " + actual + ".");
            }
        }

        private static void ValidateCompatibilityRange(
            string range,
            string fieldName,
            string componentName,
            string actualVersion,
            List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(range))
            {
                return;
            }

            if (!VersionUtil.TryParseRange(range))
            {
                errors.Add(fieldName + " is invalid.");
            }
            else if (!VersionUtil.AllowsRange(actualVersion, range))
            {
                errors.Add(fieldName + " does not include " + componentName + " " + actualVersion + ".");
            }
        }

        private static void ValidateLicenseFiles(IEnumerable<string>? paths, List<string> errors)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths ?? Array.Empty<string>())
            {
                ValidatePortablePath(path, "licenseFiles", required: true, requireDll: false, seen, errors);
            }
        }

        private static void ValidateApiAssemblies(
            IEnumerable<string>? paths,
            HashSet<string> seen,
            List<string> errors)
        {
            foreach (var path in paths ?? Array.Empty<string>())
            {
                ValidatePortablePath(path, "apiAssemblies", required: true, requireDll: true, seen, errors);
            }
        }

        private static void ValidatePortablePath(
            string? path,
            string fieldName,
            bool required,
            bool requireDll,
            HashSet<string>? seen,
            List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (required)
                {
                    errors.Add(fieldName + " entry is required.");
                }

                return;
            }

            if (!PortablePackagePath.TryValidate(path, out var portable, out var collisionKey, out var error))
            {
                errors.Add(fieldName + " entry '" + path + "' must be a safe portable relative path (" + error + ").");
                return;
            }

            if (requireDll && !portable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(fieldName + " entry '" + path + "' must name a .dll assembly.");
            }

            if (seen != null && !seen.Add(collisionKey))
            {
                errors.Add(fieldName + " contains duplicate or portable-collision path '" + path + "'.");
            }
        }

        private static string FirstNonEmpty(string first, string second)
        {
            return !string.IsNullOrWhiteSpace(first) ? first : second ?? string.Empty;
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
            if (!IsValidId(dependency.Id))
            {
                errors.Add(fieldName + " id '" + dependency.Id + "' must use the safe mod id format.");
                return;
            }

            if (!seen.Add(dependency.Id))
            {
                errors.Add(fieldName + " contains duplicate id '" + dependency.Id + "'.");
            }

            if (!string.IsNullOrWhiteSpace(dependency.Version) && !VersionUtil.TryParseRange(dependency.Version))
            {
                errors.Add("dependency '" + dependency.Id + "' has an invalid version.");
            }

            if (!string.IsNullOrWhiteSpace(dependency.VersionRange) && !VersionUtil.TryParseRange(dependency.VersionRange))
            {
                errors.Add("dependency '" + dependency.Id + "' has an invalid versionRange.");
            }
        }

        internal static bool IsValidId(string? id)
        {
            return !string.IsNullOrWhiteSpace(id) && IdRegex.IsMatch(id);
        }
    }
}
