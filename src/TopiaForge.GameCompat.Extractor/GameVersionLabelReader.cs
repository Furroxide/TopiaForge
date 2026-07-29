using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.GameCompat.Extractor
{
    /// <summary>
    /// Reads best-effort provenance from files owned by the game launcher or app bundle.
    /// The value is advisory, so malformed or concurrently-updated metadata must never
    /// prevent a compatibility capture.
    /// </summary>
    internal static class GameVersionLabelReader
    {
        private const int MaxMetadataBytes = 64 * 1024;
        private const int MaxLabelLength = 128;

        internal static string Read(string managedDir)
        {
            return ReadInfo(managedDir).Label;
        }

        internal static string ReadCanonicalVersion(string managedDir)
        {
            return ReadInfo(managedDir).CanonicalVersion;
        }

        private static GameVersionInfo ReadInfo(string managedDir)
        {
            try
            {
                var cachedBuild = ReadPublicManagedReferenceCacheBuild(managedDir);
                if (cachedBuild.Label.Length > 0)
                {
                    return cachedBuild;
                }

                var layout = ResolveLayout(managedDir);
                if (layout == null)
                {
                    return GameVersionInfo.Empty;
                }

                foreach (var metadataRoot in layout.MetadataRoots)
                {
                    var installedBuild = ReadInstalledBuild(Path.Combine(metadataRoot, "installed-build.json"));
                    if (installedBuild.Label.Length > 0)
                    {
                        return installedBuild;
                    }
                }

                return layout.InfoPlist == null
                    ? GameVersionInfo.Empty
                    : ReadBundleVersion(layout.InfoPlist);
            }
            catch
            {
                // Provenance is advisory. Capture must still work when the install is
                // incomplete, metadata is malformed, or files change under the reader.
                return GameVersionInfo.Empty;
            }
        }

        private static GameVersionInfo ReadPublicManagedReferenceCacheBuild(string managedDir)
        {
            if (string.IsNullOrWhiteSpace(managedDir))
            {
                return GameVersionInfo.Empty;
            }

            var managed = new DirectoryInfo(Path.GetFullPath(managedDir));
            if (!managed.Name.Equals("Managed", StringComparison.OrdinalIgnoreCase)
                || managed.Parent == null)
            {
                return GameVersionInfo.Empty;
            }

            // TopiaForge.ManagedRefs publishes public archives under the stable,
            // platform-independent cache key:
            // public-<build>-<windows|mac>-<sha256>/Managed.
            // Preserve that provenance when the extractor targets the cache
            // directly instead of a launcher installation.
            var segments = managed.Parent.Name.Split('-');
            if (segments.Length != 4
                || !segments[0].Equals("public", StringComparison.Ordinal)
                || !(segments[2].Equals("windows", StringComparison.Ordinal)
                    || segments[2].Equals("mac", StringComparison.Ordinal))
                || segments[3].Length != 64
                || !IsLowerHex(segments[3])
                || !long.TryParse(
                    segments[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var buildId)
                || buildId <= 0)
            {
                return GameVersionInfo.Empty;
            }

            var build = buildId.ToString(CultureInfo.InvariantCulture);
            return GameBuildVersion.TryFromBuildId(build, out var semanticVersion)
                ? new GameVersionInfo("build " + build, semanticVersion)
                : GameVersionInfo.Empty;
        }

        private static bool IsLowerHex(string value)
        {
            foreach (var character in value)
            {
                if (!((character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        private static InstallLayout? ResolveLayout(string managedDir)
        {
            if (string.IsNullOrWhiteSpace(managedDir))
            {
                return null;
            }

            var managed = new DirectoryInfo(Path.GetFullPath(managedDir));
            if (!managed.Name.Equals("Managed", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var data = managed.Parent;
            if (data == null)
            {
                return null;
            }

            // Unity's native macOS layout is:
            // Robotopia.app/Contents/Resources/Data/Managed. The launcher-owned
            // installed-build.json sits beside Robotopia.app, not under Resources.
            var resources = data.Parent;
            var contents = resources?.Parent;
            var appBundle = contents?.Parent;
            if (data.Name.Equals("Data", StringComparison.OrdinalIgnoreCase)
                && resources != null
                && resources.Name.Equals("Resources", StringComparison.OrdinalIgnoreCase)
                && contents != null
                && contents.Name.Equals("Contents", StringComparison.OrdinalIgnoreCase)
                && appBundle != null
                && appBundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                var roots = new List<string>();
                if (appBundle.Parent != null)
                {
                    roots.Add(appBundle.Parent.FullName);
                }

                return new InstallLayout(roots, Path.Combine(contents.FullName, "Info.plist"));
            }

            // Windows and Proton use Robotopia_Data/Managed. The launcher metadata,
            // when present, is stored either at the game install root or beside the
            // launcher-owned Robotopia directory.
            if (data.Name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) && data.Parent != null)
            {
                var roots = new List<string> { data.Parent.FullName };
                if (data.Parent.Name.Equals("Robotopia", StringComparison.OrdinalIgnoreCase)
                    && data.Parent.Parent != null)
                {
                    roots.Add(data.Parent.Parent.FullName);
                }

                return new InstallLayout(roots, infoPlist: null);
            }

            return null;
        }

        private static GameVersionInfo ReadInstalledBuild(string path)
        {
            try
            {
                var bytes = ReadBounded(path);
                if (bytes == null)
                {
                    return GameVersionInfo.Empty;
                }

                using var document = JsonDocument.Parse(
                    bytes,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 8
                    });

                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("id", out var id))
                {
                    return GameVersionInfo.Empty;
                }

                long value;
                if (id.ValueKind == JsonValueKind.Number)
                {
                    if (!id.TryGetInt64(out value))
                    {
                        return GameVersionInfo.Empty;
                    }
                }
                else if (id.ValueKind == JsonValueKind.String)
                {
                    if (!long.TryParse(id.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out value))
                    {
                        return GameVersionInfo.Empty;
                    }
                }
                else
                {
                    return GameVersionInfo.Empty;
                }

                var buildId = value.ToString(CultureInfo.InvariantCulture);
                return value > 0 && GameBuildVersion.TryFromBuildId(buildId, out var semanticVersion)
                    ? new GameVersionInfo("build " + buildId, semanticVersion)
                    : GameVersionInfo.Empty;
            }
            catch
            {
                return GameVersionInfo.Empty;
            }
        }

        private static GameVersionInfo ReadBundleVersion(string path)
        {
            try
            {
                var bytes = ReadBounded(path);
                if (bytes == null)
                {
                    return GameVersionInfo.Empty;
                }

                using var stream = new MemoryStream(bytes, writable: false);
                using var reader = XmlReader.Create(
                    stream,
                    new XmlReaderSettings
                    {
                        // Unity's generated Info.plist declares Apple's public DTD.
                        // Ignore the declaration while keeping resolution disabled so
                        // parsing never performs filesystem or network access.
                        DtdProcessing = DtdProcessing.Ignore,
                        MaxCharactersInDocument = MaxMetadataBytes,
                        XmlResolver = null
                    });
                var document = XDocument.Load(reader, LoadOptions.None);
                var dictionary = document.Root?.Element("dict");
                if (dictionary == null)
                {
                    return GameVersionInfo.Empty;
                }

                var shortVersion = NormalizeLabel(ReadPlistString(dictionary, "CFBundleShortVersionString"));
                var buildVersion = NormalizeLabel(ReadPlistString(dictionary, "CFBundleVersion"));
                if (shortVersion.Length == 0)
                {
                    if (buildVersion.Length == 0)
                    {
                        return GameVersionInfo.Empty;
                    }

                    return GameBuildVersion.TryFromBuildId(buildVersion, out var buildSemanticVersion)
                        ? new GameVersionInfo("build " + buildVersion, buildSemanticVersion)
                        : GameVersionInfo.Empty;
                }

                var label = buildVersion.Length == 0
                    || buildVersion == "0"
                    || buildVersion.Equals(shortVersion, StringComparison.Ordinal)
                    ? shortVersion
                    : shortVersion + " (build " + buildVersion + ")";
                if (GameBuildVersion.TryFromBuildId(buildVersion, out var canonicalBuild))
                {
                    return new GameVersionInfo(label, canonicalBuild);
                }

                if (VersionUtil.TryParse(shortVersion, out _))
                {
                    return new GameVersionInfo(label, shortVersion);
                }

                return new GameVersionInfo(label, string.Empty);
            }
            catch
            {
                // Binary plists and corrupt XML are intentionally non-fatal. The
                // launcher build id remains the preferred source on real installs.
                return GameVersionInfo.Empty;
            }
        }

        private static string ReadPlistString(XElement dictionary, string key)
        {
            var elements = dictionary.Elements();
            var takeNext = false;
            foreach (var element in elements)
            {
                if (takeNext)
                {
                    return element.Name.LocalName == "string" ? element.Value : string.Empty;
                }

                takeNext = element.Name.LocalName == "key"
                    && element.Value.Equals(key, StringComparison.Ordinal);
            }

            return string.Empty;
        }

        private static string NormalizeLabel(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxLabelLength)
            {
                return string.Empty;
            }

            foreach (var character in trimmed)
            {
                if (char.IsControl(character))
                {
                    return string.Empty;
                }
            }

            return trimmed;
        }

        private static byte[]? ReadBounded(string path)
        {
            try
            {
                return ExtractorFileIo.ReadStableBytes(
                    path,
                    MaxMetadataBytes,
                    "game version metadata");
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        private sealed class InstallLayout
        {
            internal InstallLayout(IEnumerable<string> metadataRoots, string? infoPlist)
            {
                MetadataRoots = metadataRoots;
                InfoPlist = infoPlist;
            }

            internal IEnumerable<string> MetadataRoots { get; }

            internal string? InfoPlist { get; }
        }

        private readonly struct GameVersionInfo
        {
            internal static GameVersionInfo Empty { get; } = new GameVersionInfo(string.Empty, string.Empty);

            internal GameVersionInfo(string label, string canonicalVersion)
            {
                Label = label;
                CanonicalVersion = canonicalVersion;
            }

            internal string Label { get; }
            internal string CanonicalVersion { get; }
        }
    }
}
