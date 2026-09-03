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
        private static readonly Regex ContentTargetRegex = new Regex("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.Compiled);
        private static readonly Regex Sha256Regex = new Regex("^[A-Fa-f0-9]{64}$", RegexOptions.Compiled);
        private static readonly HashSet<string> KnownCapabilities = new HashSet<string>(StringComparer.Ordinal)
        {
            "asset-bundles", "filesystem", "filesystem-watch", "harmony-patch", "hud", "input",
            "navigation", "network", "microphone", "particles", "physics", "physics-settings",
            "player-control", "player-token", "prompt-overrides", "quality-settings", "remote-ai",
            "render-settings", "robot-spawning", "scene-management", "speech-to-text", "time",
            "ugc-livesync", "unsafe-native", "world-service"
        };
        private static readonly HashSet<string> KnownPlatforms = new HashSet<string>(StringComparer.Ordinal)
        {
            "windows", "macos", "linux"
        };
        private static readonly HashSet<string> KnownArchitectures = new HashSet<string>(StringComparer.Ordinal)
        {
            "x64", "arm64"
        };
        private const int MaxDependencies = 128;
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

            if (manifest.SchemaVersion == 4)
            {
                errors.Add(
                    "schemaVersion 4 was retired before TopiaForge 1.0; migrate this manifest to " +
                    "schemaVersion 5 with 'topiaforge migrate-manifest --project <path>'. " +
                    "The multiplayer field is optional for standalone-only mods.");
                return errors;
            }
            else if (manifest.SchemaVersion == ModManifest.ManifestV5SchemaVersion)
            {
                errors.Add(
                    "schemaVersion 5 was retired before TopiaForge 1.0; migrate this manifest to " +
                    "schemaVersion 6 with 'topiaforge migrate-manifest --project <path>'. Its " +
                    "worldGamemodes list becomes contributions.gamemodes and contributions.launchTargets.");
                return errors;
            }
            else if (!ModManifest.IsSupportedSchemaVersion(manifest.SchemaVersion))
            {
                errors.Add("schemaVersion must be 6.");
                return errors;
            }

            foreach (var field in manifest.UnsupportedFieldNames())
            {
                errors.Add(field + " is not supported by the TopiaForge manifest contract.");
            }

            ValidateStringLength(manifest.SchemaUrl, "$schema", 0, 512, required: false, errors);

            if (!IsValidId(manifest.Id))
            {
                errors.Add("name must be 2-64 characters and contain only letters, numbers, underscore, dot, or dash.");
            }

            ValidateStringLength(manifest.Name, "displayName", 1, 128, required: true, errors);

            if (manifest.Author == null || string.IsNullOrWhiteSpace(manifest.Author.Name))
            {
                errors.Add("author.name is required.");
            }
            else
            {
                ValidateStringLength(manifest.Author.Name, "author.name", 1, 128, required: true, errors);
                ValidateStringLength(manifest.Author.Email, "author.email", 0, 254, required: false, errors);
                ValidateStringLength(manifest.Author.Url, "author.url", 0, 2048, required: false, errors);
            }

            ValidateStringLength(manifest.Description, "description", 0, 4096, required: false, errors);

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

            ValidateStringLength(manifest.EntryType, "entryType", 1, 512, required: true, errors);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ValidateDependencies(manifest.Dependencies, "dependencies", seen, errors);
            ValidateDependencies(manifest.OptionalDependencies, "optionalDependencies", seen, errors);

            seen.Clear();
            ValidateCount(manifest.Conflicts, "conflicts", MaxDependencies, errors);
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

                ValidateStringLength(conflict.VersionRange, "conflicts.versionRange", 1, 256, required: false, errors);
                ValidateStringLength(conflict.Reason, "conflicts.reason", 0, 512, required: false, errors);
            }

            ValidateRelatedIds(manifest.Id, manifest.LoadAfter, "loadAfter", errors);
            ValidateRelatedIds(manifest.Id, manifest.LoadBefore, "loadBefore", errors);

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

            ValidateStringLength(manifest.Category, "category", 0, 64, required: false, errors);
            ValidateStringLength(manifest.Homepage, "homepage", 0, 2048, required: false, errors);
            ValidateStringLength(manifest.Source, "source", 0, 2048, required: false, errors);
            ValidateStringLength(
                manifest.License,
                "license",
                1,
                256,
                required: manifest.LicenseWasPresent,
                errors);
            ValidateOptionalPortablePath(manifest.Icon, "icon", manifest.IconWasPresent, errors);
            ValidateLicenseFiles(manifest.LicenseFiles, manifest.LicenseFilesWasPresent, errors);
            ValidateApiAssemblies(manifest.ApiAssemblies, assemblyPaths, errors);
            ValidateEnumList(manifest.Capabilities, "capabilities", KnownCapabilities, 64, errors);
            ValidateEnumList(manifest.Platforms, "platforms", KnownPlatforms, 3, errors);
            ValidateEnumList(manifest.Architectures, "architectures", KnownArchitectures, 2, errors);
            ValidateContentTargets(manifest.ContentTargets, errors);
            errors.AddRange(ManifestRuntimeCompatibility.Evaluate(manifest, context).Errors);
            ValidateStringList(manifest.Tags, "tags", 64, 1, 64, validatePaths: false, errors);
            ValidateStringList(manifest.Screenshots, "screenshots", 32, 1, 1024, validatePaths: true, errors);
            ValidateHashes(manifest.Hashes, errors);
            ManifestContributionValidator.Validate(manifest, errors);
            ValidateBuiltWith(manifest.BuiltWith, errors);
            ValidateMultiplayer(manifest, errors);

            return errors;
        }

        private static void ValidateMultiplayer(ModManifest manifest, List<string> errors)
        {
            if (!ModManifest.IsSupportedSchemaVersion(manifest.SchemaVersion))
            {
                return;
            }

            var multiplayer = manifest.Multiplayer;
            if (multiplayer == null)
            {
                return;
            }

            var isClientLocal = string.Equals(
                multiplayer.Mode,
                ModMultiplayerMetadata.ClientLocalMode,
                StringComparison.Ordinal);
            var isServerOnly = string.Equals(
                multiplayer.Mode,
                ModMultiplayerMetadata.ServerOnlyMode,
                StringComparison.Ordinal);
            var isSession = string.Equals(
                multiplayer.Mode,
                ModMultiplayerMetadata.SessionMode,
                StringComparison.Ordinal);
            if (!isClientLocal && !isServerOnly && !isSession)
            {
                errors.Add("multiplayer.mode must be client-local, server-only, or session.");
            }

            if (!isSession)
            {
                if (multiplayer.PresenceWasPresent || !string.IsNullOrEmpty(multiplayer.Presence))
                {
                    errors.Add("multiplayer.presence is only valid when multiplayer.mode is session.");
                }

                if (multiplayer.Protocol != null)
                {
                    errors.Add("multiplayer.protocol is only valid when multiplayer.mode is session.");
                }

                if (multiplayer.SynchronizedFilesWasPresent ||
                    (multiplayer.SynchronizedFiles?.Count ?? 0) != 0)
                {
                    errors.Add("multiplayer.synchronizedFiles is only valid when multiplayer.mode is session.");
                }

                return;
            }

            if (!string.Equals(multiplayer.Presence, ModMultiplayerMetadata.RequiredPresence, StringComparison.Ordinal) &&
                !string.Equals(multiplayer.Presence, ModMultiplayerMetadata.OptionalPresence, StringComparison.Ordinal))
            {
                errors.Add("multiplayer.presence must be required or optional when multiplayer.mode is session.");
            }

            var protocol = multiplayer.Protocol;
            if (protocol == null)
            {
                errors.Add("multiplayer.protocol is required when multiplayer.mode is session.");
            }
            else
            {
                ValidateStringLength(
                    protocol.Version,
                    "multiplayer.protocol.version",
                    1,
                    256,
                    required: true,
                    errors);
                if (!VersionUtil.TryParse(protocol.Version, out _))
                {
                    errors.Add("multiplayer.protocol.version must be an exact semantic version.");
                }

                if (protocol.PeerVersionRangeWasPresent || !string.IsNullOrEmpty(protocol.PeerVersionRange))
                {
                    ValidateStringLength(
                        protocol.PeerVersionRange,
                        "multiplayer.protocol.peerVersionRange",
                        1,
                        256,
                        required: true,
                        errors);
                    if (!VersionUtil.TryParseRange(protocol.PeerVersionRange))
                    {
                        errors.Add("multiplayer.protocol.peerVersionRange must be a valid semantic version range.");
                    }
                }
            }

            var synchronizedFiles = (multiplayer.SynchronizedFiles ?? new List<string>()).ToList();
            ValidateCount(
                synchronizedFiles,
                "multiplayer.synchronizedFiles",
                ModMultiplayerMetadata.MaxSynchronizedFiles,
                errors);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in synchronizedFiles)
            {
                ValidatePortablePath(
                    path,
                    "multiplayer.synchronizedFiles",
                    required: true,
                    requireDll: false,
                    seen,
                    errors);
                if (string.Equals(path, "topiaforge.mod.json", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, PackageInstallReceipt.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        "multiplayer.synchronizedFiles cannot include generated package metadata '" + path + "'.");
                }
            }
        }

        private static void ValidateGameCompatibility(
            string range,
            ManifestValidationContext context,
            List<string> errors)
        {
            ValidateStringLength(range, "supportedGameVersionRange", 1, 256, required: true, errors);
            if (string.IsNullOrWhiteSpace(range))
            {
                errors.Add("supportedGameVersionRange is required for publishable manifests.");
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
            ValidateStringLength(range, fieldName, 1, 256, required: true, errors);
            if (string.IsNullOrWhiteSpace(range))
            {
                errors.Add(fieldName + " is required for publishable manifests.");
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

        private static void ValidateLicenseFiles(
            IEnumerable<string>? paths,
            bool wasPresent,
            List<string> errors)
        {
            var entries = (paths ?? Array.Empty<string>()).ToList();
            if (wasPresent && entries.Count == 0)
            {
                errors.Add("licenseFiles must contain at least one path when present.");
            }

            ValidateCount(entries, "licenseFiles", 32, errors);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in entries)
            {
                ValidatePortablePath(path, "licenseFiles", required: true, requireDll: false, seen, errors);
            }
        }

        private static void ValidateOptionalPortablePath(
            string? path,
            string fieldName,
            bool wasPresent,
            List<string> errors)
        {
            if (!wasPresent && string.IsNullOrEmpty(path))
            {
                return;
            }

            ValidatePortablePath(path, fieldName, required: true, requireDll: false, seen: null, errors);
        }

        private static void ValidateApiAssemblies(
            IEnumerable<string>? paths,
            HashSet<string> seen,
            List<string> errors)
        {
            var entries = (paths ?? Array.Empty<string>()).ToList();
            ValidateCount(entries, "apiAssemblies", 64, errors);
            foreach (var path in entries)
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

        private static void ValidateDependencies(
            IDictionary<string, string>? dependencies,
            string fieldName,
            HashSet<string> seen,
            List<string> errors)
        {
            ValidateCount(dependencies, fieldName, MaxDependencies, errors);
            foreach (var entry in dependencies ?? new Dictionary<string, string>())
            {
                ValidateDependency(entry.Key, entry.Value, fieldName, seen, errors);
            }
        }

        private static void ValidateDependency(
            string id,
            string versionRange,
            string fieldName,
            HashSet<string> seen,
            List<string> errors)
        {
            if (!IsValidId(id))
            {
                errors.Add(fieldName + " id '" + id + "' must use the safe mod id format.");
                return;
            }

            if (!seen.Add(id))
            {
                errors.Add(fieldName + " contains duplicate id '" + id + "'.");
            }

            if (string.IsNullOrWhiteSpace(versionRange) || !VersionUtil.TryParseRange(versionRange))
            {
                errors.Add("dependency '" + id + "' has an invalid version range.");
            }

            ValidateStringLength(versionRange, fieldName + "." + id, 1, 256, required: true, errors);
        }

        private static void ValidateRelatedIds(
            string ownerId,
            IEnumerable<string>? ids,
            string fieldName,
            List<string> errors)
        {
            var values = (ids ?? Array.Empty<string>()).ToList();
            ValidateCount(values, fieldName, MaxDependencies, errors);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in values)
            {
                if (!IsValidId(id))
                {
                    errors.Add(fieldName + " id '" + id + "' must use the safe mod id format.");
                }
                else if (!seen.Add(id))
                {
                    errors.Add(fieldName + " contains duplicate id '" + id + "'.");
                }
                else if (string.Equals(ownerId, id, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(fieldName + " cannot reference the owning mod.");
                }
            }
        }

        private static void ValidateEnumList(
            IEnumerable<string>? values,
            string fieldName,
            ISet<string> known,
            int maximum,
            List<string> errors)
        {
            var entries = (values ?? Array.Empty<string>()).ToList();
            ValidateCount(entries, fieldName, maximum, errors);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in entries)
            {
                if (!seen.Add(value))
                {
                    errors.Add(fieldName + " contains duplicate value '" + value + "'.");
                }
                else if (!known.Contains(value))
                {
                    errors.Add(fieldName + " contains unknown value '" + value + "'.");
                }
            }
        }

        private static void ValidateContentTargets(IEnumerable<string>? values, List<string> errors)
        {
            var entries = (values ?? Array.Empty<string>()).ToList();
            ValidateCount(entries, "contentTargets", 64, errors);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in entries)
            {
                if (!ContentTargetRegex.IsMatch(value))
                {
                    errors.Add("contentTargets value '" + value + "' must use lowercase letters, numbers, dot, dash, or underscore.");
                }
                else if (!seen.Add(value))
                {
                    errors.Add("contentTargets contains duplicate value '" + value + "'.");
                }
            }
        }

        private static void ValidateWorldGamemodes(IEnumerable<ModGamemode>? values, List<string> errors)
        {
            var entries = (values ?? Array.Empty<ModGamemode>()).ToList();
            ValidateCount(entries, "worldGamemodes", 64, errors);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var gamemode in entries)
            {
                if (!IsValidId(gamemode.Id))
                {
                    errors.Add("worldGamemodes id '" + gamemode.Id + "' must use the safe mod id format.");
                }
                else if (!seen.Add(gamemode.Id))
                {
                    errors.Add("worldGamemodes contains duplicate id '" + gamemode.Id + "'.");
                }

                ValidateStringLength(
                    gamemode.Name,
                    "worldGamemodes name for '" + gamemode.Id + "'",
                    1,
                    128,
                    required: true,
                    errors);
                ValidateStringLength(
                    gamemode.Description,
                    "worldGamemodes description for '" + gamemode.Id + "'",
                    0,
                    1024,
                    required: false,
                    errors);
            }
        }

        private static void ValidateStringList(
            IEnumerable<string>? values,
            string fieldName,
            int maximumCount,
            int minimumLength,
            int maximumLength,
            bool validatePaths,
            List<string> errors)
        {
            var entries = (values ?? Array.Empty<string>()).ToList();
            ValidateCount(entries, fieldName, maximumCount, errors);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in entries)
            {
                if (validatePaths)
                {
                    ValidatePortablePath(value, fieldName, required: true, requireDll: false, seen, errors);
                    continue;
                }

                ValidateStringLength(value, fieldName, minimumLength, maximumLength, required: true, errors);
                if (!seen.Add(value))
                {
                    errors.Add(fieldName + " contains duplicate value '" + value + "'.");
                }
            }
        }

        private static void ValidateHashes(IDictionary<string, string>? hashes, List<string> errors)
        {
            ValidateCount(hashes, "hashes", 8192, errors);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in hashes ?? new Dictionary<string, string>())
            {
                ValidatePortablePath(
                    entry.Key,
                    "hashes",
                    required: true,
                    requireDll: false,
                    seen,
                    errors);
                if (entry.Value == null || !Sha256Regex.IsMatch(entry.Value))
                {
                    errors.Add("hashes value for '" + entry.Key + "' must be a 64-character SHA-256 digest.");
                }
            }
        }

        private static void ValidateStringLength(
            string? value,
            string fieldName,
            int minimum,
            int maximum,
            bool required,
            List<string> errors)
        {
            if (string.IsNullOrEmpty(value) || (required && string.IsNullOrWhiteSpace(value)))
            {
                if (required)
                {
                    errors.Add(fieldName + " is required.");
                }

                return;
            }

            var length = UnicodeScalarLength(value!);
            if (length < minimum || length > maximum)
            {
                errors.Add(
                    fieldName + " must contain between " + minimum + " and " + maximum + " Unicode characters.");
            }
        }

        private static int UnicodeScalarLength(string value)
        {
            var length = 0;
            for (var index = 0; index < value.Length; index++)
            {
                if (char.IsHighSurrogate(value[index]) &&
                    index + 1 < value.Length &&
                    char.IsLowSurrogate(value[index + 1]))
                {
                    index++;
                }

                length++;
            }

            return length;
        }

        private static void ValidateBuiltWith(ModBuildMetadata? metadata, List<string> errors)
        {
            if (metadata == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(metadata.SdkVersion) &&
                string.IsNullOrWhiteSpace(metadata.LoaderVersion) &&
                string.IsNullOrWhiteSpace(metadata.GameVersion) &&
                string.IsNullOrWhiteSpace(metadata.ToolVersion))
            {
                errors.Add("builtWith must contain at least one version.");
                return;
            }

            ValidateExactVersion(metadata.SdkVersion, "builtWith.sdkVersion", errors);
            ValidateExactVersion(metadata.LoaderVersion, "builtWith.loaderVersion", errors);
            ValidateExactVersion(metadata.GameVersion, "builtWith.gameVersion", errors);
            ValidateExactVersion(metadata.ToolVersion, "builtWith.toolVersion", errors);
        }

        private static void ValidateExactVersion(string value, string fieldName, List<string> errors)
        {
            if (!string.IsNullOrWhiteSpace(value) && !VersionUtil.TryParse(value, out _))
            {
                errors.Add(fieldName + " must be an exact semantic version.");
            }
        }

        private static void ValidateCount<T>(ICollection<T>? values, string fieldName, int maximum, List<string> errors)
        {
            if (values != null && values.Count > maximum)
            {
                errors.Add(fieldName + " cannot contain more than " + maximum + " entries.");
            }
        }

        private static void ValidateCount<TKey, TValue>(IDictionary<TKey, TValue>? values, string fieldName, int maximum, List<string> errors)
        {
            if (values != null && values.Count > maximum)
            {
                errors.Add(fieldName + " cannot contain more than " + maximum + " entries.");
            }
        }

        internal static bool IsRetiredEcosystemId(string id)
        {
            foreach (var prefix in RetiredEcosystemIdPrefixes)
            {
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
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
