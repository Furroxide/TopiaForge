using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Strict, bounded reader for the schema-V4 TopiaForge manifest contract.</summary>
    public static class ModManifestJson
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
            "architectures", "contentTargets", "builtWith", "worldGamemodes", "apiAssemblies",

            // Retired fields are decoded only so validation can return an actionable migration error.
            "vpmDependencies", "permissions", "id", "title", "gameVersion", "gameVersionRange",
            "loaderVersionRange", "sdkVersionRange", "packageHashes", "gamemodes", "legacyFolders",
            "legacyFiles", "legacyPackages"
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
        private static readonly HashSet<string> GamemodeFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "name", "description"
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
            ValidateClosedObjectArray(properties, "worldGamemodes", GamemodeFields, new[] { "id", "name" });

            return JsonUtil.Deserialize<ModManifest>(json);
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
