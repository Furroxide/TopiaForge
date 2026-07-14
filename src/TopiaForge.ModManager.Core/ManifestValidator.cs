using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Core
{
    public static class ManifestValidator
    {
        private static readonly Regex IdRegex = new Regex("^[A-Za-z0-9][A-Za-z0-9_.-]{1,63}$", RegexOptions.Compiled);
        private static readonly string[] RetiredEcosystemIdPrefixes =
        {
            StringFromCodeUnits(114, 111, 98, 111, 116, 111, 112, 105, 97, 46),
            StringFromCodeUnits(99, 111, 109, 46, 114, 111, 98, 111, 116, 111, 112, 105, 97, 46),
            StringFromCodeUnits(113, 117, 97, 110, 116, 117, 109, 119, 111, 114, 107, 115, 46)
        };

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

            foreach (var field in manifest.UnsupportedFieldNames())
            {
                errors.Add(field + " is not supported by the TopiaForge manifest contract.");
            }

            if (manifest.SchemaVersion != 3)
            {
                errors.Add("schemaVersion must be 3.");
            }

            if (!IsValidId(manifest.Id))
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
                if (conflict.HasUnsupportedVersion)
                {
                    errors.Add("conflict '" + conflict.Id + "' must use versionRange, not version.");
                }

                if (!IsValidId(conflict.Id))
                {
                    errors.Add("conflicts id '" + conflict.Id + "' must use the safe mod id format.");
                    continue;
                }

                if (!seen.Add(conflict.Id))
                {
                    errors.Add("conflicts contains duplicate id '" + conflict.Id + "'.");
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

            ValidateGameCompatibility(manifest.SupportedGameVersionRange, context, errors);

            ValidateCompatibilityRange(
                manifest.SupportedLoaderVersionRange,
                "supportedLoaderVersionRange",
                "loader",
                context.LoaderVersion,
                errors);

            ValidateCompatibilityRange(
                manifest.SupportedSdkVersionRange,
                "supportedSdkVersionRange",
                "SDK",
                context.SdkVersion,
                errors);

            ValidateLicenseFiles(manifest.LicenseFiles, errors);
            ValidateApiAssemblies(manifest.ApiAssemblies, assemblyPaths, errors);

            return errors;
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
            if (dependency.HasUnsupportedVersion)
            {
                errors.Add("dependency '" + dependency.Id + "' must use versionRange, not version.");
            }

            if (!IsValidId(dependency.Id))
            {
                errors.Add(fieldName + " id '" + dependency.Id + "' must use the safe mod id format.");
                return;
            }

            if (!seen.Add(dependency.Id))
            {
                errors.Add(fieldName + " contains duplicate id '" + dependency.Id + "'.");
            }

            if (!string.IsNullOrWhiteSpace(dependency.VersionRange) && !VersionUtil.TryParseRange(dependency.VersionRange))
            {
                errors.Add("dependency '" + dependency.Id + "' has an invalid versionRange.");
            }
        }

        internal static bool IsValidId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id) || !IdRegex.IsMatch(id))
            {
                return false;
            }

            foreach (var prefix in RetiredEcosystemIdPrefixes)
            {
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static string StringFromCodeUnits(params int[] codeUnits)
        {
            var characters = new char[codeUnits.Length];
            for (var index = 0; index < codeUnits.Length; index++)
            {
                characters[index] = checked((char)codeUnits[index]);
            }

            return new string(characters);
        }
    }
}
