using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Strict, bounded, schema-dispatched reader for TopiaForge manifest contracts.</summary>
    public static partial class ModManifestJson
    {
        public const long MaxManifestBytes = 1024L * 1024;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Regex ExtensionName = new Regex(
            "^x-[A-Za-z0-9_.-]{1,64}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly HashSet<string> KnownFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "$schema", "schemaVersion", "name", "displayName", "version", "author", "description",
            "entryAssembly", "entryType", "dependencies", "optionalDependencies", "conflicts",
            "loadAfter", "loadBefore", "supportedGameVersionRange", "supportedLoaderVersionRange",
            "supportedSdkVersionRange", "category", "tags", "icon", "screenshots", "homepage",
            "source", "license", "licenseFiles", "hashes", "capabilities", "platforms",
            "architectures", "contentTargets", "builtWith", "contributions",
            "apiAssemblies", "multiplayer",

            // Retired fields are decoded only so validation can return an actionable migration error.
            "vpmDependencies", "permissions", "id", "title", "gameVersion", "gameVersionRange",
            "loaderVersionRange", "sdkVersionRange", "packageHashes", "gamemodes", "worldGamemodes",
            "legacyFolders", "legacyFiles", "legacyPackages"
        };
        private static readonly string[] RequiredFields =
        {
            "schemaVersion", "name", "displayName", "version", "author", "entryAssembly", "entryType",
            "supportedGameVersionRange", "supportedLoaderVersionRange", "supportedSdkVersionRange"
        };
        private static readonly HashSet<string> AuthorFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "email", "url"
        };
        private static readonly HashSet<string> BuiltWithFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "sdkVersion", "loaderVersion", "gameVersion", "toolVersion"
        };
        private static readonly HashSet<string> ConflictFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "versionRange", "reason"
        };
        private static readonly HashSet<string> MultiplayerFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "mode", "presence", "protocol", "synchronizedFiles"
        };
        private static readonly HashSet<string> MultiplayerProtocolFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "version", "peerVersionRange"
        };

        public static ModManifest LoadFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A manifest path is required.", nameof(path));
            }

            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException("Manifest must be a regular file: " + path);
            }

            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > MaxManifestBytes)
                {
                    throw new InvalidDataException("Manifest exceeds the 1 MiB limit: " + path);
                }

                using (var buffer = new MemoryStream(checked((int)input.Length)))
                {
                    var bytes = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = input.Read(bytes, 0, bytes.Length)) > 0)
                    {
                        if (total > MaxManifestBytes - read)
                        {
                            throw new InvalidDataException("Manifest grew beyond the 1 MiB limit: " + path);
                        }

                        buffer.Write(bytes, 0, read);
                        total += read;
                    }

                    return Deserialize(StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)));
                }
            }
        }

        public static ModManifest Deserialize(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            if (StrictUtf8.GetByteCount(json) > MaxManifestBytes)
            {
                throw new InvalidDataException("Manifest exceeds the 1 MiB limit.");
            }

            if (json.Length > 0 && json[0] == '\uFEFF')
            {
                json = json.Substring(1);
            }

            var properties = JsonObjectMerge.ReadProperties(json);
            var names = properties.Select(property => property.Name).ToList();
            if (names.Count > 64)
            {
                throw new InvalidDataException("Manifest cannot contain more than 64 top-level fields.");
            }

            var duplicate = names.GroupBy(name => name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                throw new InvalidDataException("Manifest contains duplicate field '" + duplicate.Key + "'.");
            }

            var present = new HashSet<string>(names, StringComparer.Ordinal);
            if (!present.Contains("schemaVersion"))
            {
                throw new InvalidDataException("Manifest is missing required field 'schemaVersion'.");
            }

            var schemaVersion = ReadSchemaVersion(properties);
            switch (ManifestSchemaDispatch.Resolve(schemaVersion))
            {
                case ManifestSchemaContract.V6:
                    return DeserializeV6(json, properties, names, present);
                default:
                    throw new InvalidDataException(
                        "Manifest schemaVersion " + schemaVersion + " has no registered reader.");
            }
        }

        /// <summary>
        /// The structural rules every manifest must satisfy before anything is deserialized.
        /// <para>
        /// Structure first, always. DataContractJsonSerializer throws its own SerializationException on
        /// a shape it cannot bind -- a string where an object belongs, say -- and that is not an
        /// actionable manifest error, so nothing may reach it that this walk has not already accepted.
        /// </para>
        /// </summary>
        private static void ValidateCommonStructure(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> properties,
            IReadOnlyList<string> names,
            HashSet<string> present)
        {
            var missing = RequiredFields.FirstOrDefault(field => !present.Contains(field));
            if (missing != null)
            {
                throw new InvalidDataException("Manifest is missing required field '" + missing + "'.");
            }

            var extensions = 0;
            foreach (var name in names)
            {
                if (KnownFields.Contains(name))
                {
                    continue;
                }

                if (!ExtensionName.IsMatch(name))
                {
                    throw new InvalidDataException(
                        "Manifest contains unknown field '" + name + "'; extension fields must use an x-* name.");
                }

                extensions++;
                if (extensions > 32)
                {
                    throw new InvalidDataException("Manifest cannot contain more than 32 x-* extension fields.");
                }
            }

            ValidateClosedObject(
                "author",
                RequireRawProperty(properties, "author"),
                AuthorFields,
                new[] { "name" },
                requireAtLeastOne: false);

            var builtWith = properties.FirstOrDefault(property => property.Name == "builtWith");
            if (builtWith != null)
            {
                ValidateClosedObject(
                    "builtWith",
                    builtWith.RawValue,
                    BuiltWithFields,
                    Array.Empty<string>(),
                    requireAtLeastOne: true);
            }

            ValidateClosedObjectArray(properties, "conflicts", ConflictFields, new[] { "id" });

            if (present.Contains("multiplayer"))
            {
                ValidateMultiplayerObject(properties);
            }
        }

        private static ModManifest ReadManifest(string json)
        {
            var manifest = JsonUtil.Deserialize<ModManifest>(json);
            NormalizeCollections(manifest);
            return manifest;
        }

        private static ModManifest DeserializeV6(
            string json,
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> properties,
            IReadOnlyList<string> names,
            HashSet<string> present)
        {
            ValidateCommonStructure(properties, names, present);
            if (present.Contains("contributions"))
            {
                ValidateContributionsObject(properties);
            }

            var manifest = ReadManifest(json);
            NormalizeContributions(manifest);
            return manifest;
        }

        private static void NormalizeCollections(ModManifest manifest)
        {
            manifest.SchemaUrl = manifest.SchemaUrl ?? string.Empty;
            manifest.Description = manifest.Description ?? string.Empty;
            manifest.Category = manifest.Category ?? string.Empty;
            manifest.Icon = manifest.Icon ?? string.Empty;
            manifest.Homepage = manifest.Homepage ?? string.Empty;
            manifest.Source = manifest.Source ?? string.Empty;
            manifest.License = manifest.License ?? string.Empty;
            if (manifest.Author != null)
            {
                manifest.Author.Name = manifest.Author.Name ?? string.Empty;
                manifest.Author.Email = manifest.Author.Email ?? string.Empty;
                manifest.Author.Url = manifest.Author.Url ?? string.Empty;
            }

            manifest.Dependencies = manifest.Dependencies ?? new Dictionary<string, string>();
            manifest.OptionalDependencies = manifest.OptionalDependencies ?? new Dictionary<string, string>();
            manifest.Conflicts = manifest.Conflicts ?? new List<ModConflict>();
            manifest.LoadAfter = manifest.LoadAfter ?? new List<string>();
            manifest.LoadBefore = manifest.LoadBefore ?? new List<string>();
            manifest.Tags = manifest.Tags ?? new List<string>();
            manifest.Screenshots = manifest.Screenshots ?? new List<string>();
            manifest.LicenseFiles = manifest.LicenseFiles ?? new List<string>();
            manifest.Hashes = manifest.Hashes ?? new Dictionary<string, string>();
            manifest.Capabilities = manifest.Capabilities ?? new List<string>();
            manifest.Platforms = manifest.Platforms ?? new List<string>();
            manifest.Architectures = manifest.Architectures ?? new List<string>();
            manifest.ContentTargets = manifest.ContentTargets ?? new List<string>();
            manifest.ApiAssemblies = manifest.ApiAssemblies ?? new List<string>();
            foreach (var conflict in manifest.Conflicts.Where(conflict => conflict != null))
            {
                conflict.Id = conflict.Id ?? string.Empty;
                conflict.VersionRange = conflict.VersionRange ?? string.Empty;
                conflict.Reason = conflict.Reason ?? string.Empty;
            }

            if (manifest.BuiltWith != null)
            {
                manifest.BuiltWith.SdkVersion = manifest.BuiltWith.SdkVersion ?? string.Empty;
                manifest.BuiltWith.LoaderVersion = manifest.BuiltWith.LoaderVersion ?? string.Empty;
                manifest.BuiltWith.GameVersion = manifest.BuiltWith.GameVersion ?? string.Empty;
                manifest.BuiltWith.ToolVersion = manifest.BuiltWith.ToolVersion ?? string.Empty;
            }

            if (manifest.Multiplayer != null)
            {
                manifest.Multiplayer.Mode = manifest.Multiplayer.Mode ?? string.Empty;
                manifest.Multiplayer.Presence = manifest.Multiplayer.Presence ?? string.Empty;
                manifest.Multiplayer.SynchronizedFiles =
                    manifest.Multiplayer.SynchronizedFiles ?? new List<string>();
                if (manifest.Multiplayer.Protocol != null)
                {
                    manifest.Multiplayer.Protocol.Version =
                        manifest.Multiplayer.Protocol.Version ?? string.Empty;
                    manifest.Multiplayer.Protocol.PeerVersionRange =
                        manifest.Multiplayer.Protocol.PeerVersionRange ?? string.Empty;
                }
            }
        }

        private static int ReadSchemaVersion(IReadOnlyList<JsonObjectMerge.RawJsonProperty> properties)
        {
            var raw = RequireRawProperty(properties, "schemaVersion").Trim();
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidDataException("Manifest field 'schemaVersion' must be an integer.");
            }

            return version;
        }

        private static void ValidateMultiplayerObject(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> properties)
        {
            var rawMultiplayer = RequireRawProperty(properties, "multiplayer");
            ValidateClosedObject(
                "multiplayer",
                rawMultiplayer,
                MultiplayerFields,
                new[] { "mode" },
                requireAtLeastOne: false);

            IReadOnlyList<JsonObjectMerge.RawJsonProperty> multiplayerProperties;
            try
            {
                multiplayerProperties = JsonObjectMerge.ReadProperties(rawMultiplayer);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Manifest field 'multiplayer' must be an object.", exception);
            }

            ValidateJsonString(
                "multiplayer.mode",
                RequireRawProperty(multiplayerProperties, "mode"));

            var presence = multiplayerProperties.FirstOrDefault(property => property.Name == "presence");
            if (presence != null)
            {
                ValidateJsonString("multiplayer.presence", presence.RawValue);
            }

            var protocol = multiplayerProperties.FirstOrDefault(property => property.Name == "protocol");
            if (protocol != null)
            {
                ValidateClosedObject(
                    "multiplayer.protocol",
                    protocol.RawValue,
                    MultiplayerProtocolFields,
                    new[] { "version" },
                    requireAtLeastOne: false);

                var protocolProperties = JsonObjectMerge.ReadProperties(protocol.RawValue);
                ValidateJsonString(
                    "multiplayer.protocol.version",
                    RequireRawProperty(protocolProperties, "version"));
                var peerRange = protocolProperties.FirstOrDefault(
                    property => property.Name == "peerVersionRange");
                if (peerRange != null)
                {
                    ValidateJsonString(
                        "multiplayer.protocol.peerVersionRange",
                        peerRange.RawValue);
                }
            }

            var synchronizedFiles = multiplayerProperties.FirstOrDefault(
                property => property.Name == "synchronizedFiles");
            if (synchronizedFiles != null)
            {
                IReadOnlyList<string> items;
                try
                {
                    items = JsonObjectMerge.ReadArrayValues(synchronizedFiles.RawValue);
                }
                catch (FormatException exception)
                {
                    throw new InvalidDataException(
                        "Manifest field 'multiplayer.synchronizedFiles' must be an array.",
                        exception);
                }

                for (var index = 0; index < items.Count; index++)
                {
                    ValidateJsonString(
                        "multiplayer.synchronizedFiles[" + index + "]",
                        items[index]);
                }
            }
        }

        private static void ValidateJsonString(string path, string rawJson)
        {
            try
            {
                JsonUtil.Deserialize<string>(rawJson);
            }
            catch (Exception exception) when (
                exception is InvalidDataException ||
                exception is SerializationException ||
                exception is FormatException ||
                exception is XmlException)
            {
                throw new InvalidDataException("Manifest field '" + path + "' must be a string.", exception);
            }
        }

        private static string RequireRawProperty(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> properties,
            string name)
        {
            return properties.First(property => property.Name == name).RawValue;
        }

        private static void ValidateClosedObjectArray(
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> properties,
            string propertyName,
            ISet<string> allowed,
            IReadOnlyList<string> required)
        {
            var property = properties.FirstOrDefault(item => item.Name == propertyName);
            if (property == null)
            {
                return;
            }

            IReadOnlyList<string> values;
            try
            {
                values = JsonObjectMerge.ReadArrayValues(property.RawValue);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Manifest field '" + propertyName + "' must be an array.", exception);
            }

            for (var index = 0; index < values.Count; index++)
            {
                ValidateClosedObject(
                    propertyName + "[" + index + "]",
                    values[index],
                    allowed,
                    required,
                    requireAtLeastOne: false);
            }
        }

        private static void ValidateClosedObject(
            string path,
            string rawJson,
            ISet<string> allowed,
            IReadOnlyList<string> required,
            bool requireAtLeastOne)
        {
            IReadOnlyList<JsonObjectMerge.RawJsonProperty> properties;
            try
            {
                properties = JsonObjectMerge.ReadProperties(rawJson);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("Manifest field '" + path + "' must be an object.", exception);
            }

            if (requireAtLeastOne && properties.Count == 0)
            {
                throw new InvalidDataException("Manifest field '" + path + "' must contain at least one property.");
            }

            var duplicate = properties.GroupBy(item => item.Name, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
            {
                throw new InvalidDataException("Manifest field '" + path + "' contains duplicate field '" + duplicate.Key + "'.");
            }

            foreach (var property in properties)
            {
                if (!allowed.Contains(property.Name))
                {
                    throw new InvalidDataException(
                        "Manifest field '" + path + "' contains unknown field '" + property.Name + "'.");
                }
            }

            foreach (var name in required)
            {
                if (!properties.Any(property => property.Name == name))
                {
                    throw new InvalidDataException("Manifest field '" + path + "' is missing required field '" + name + "'.");
                }
            }
        }
    }
}
