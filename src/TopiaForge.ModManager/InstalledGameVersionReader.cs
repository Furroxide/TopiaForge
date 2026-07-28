using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    /// <summary>Reads the launcher-owned game build marker without loading any game or Unity assemblies.</summary>
    internal static class InstalledGameVersionReader
    {
        private const int MaxMetadataBytes = 64 * 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static bool TryRead(string gameRoot, out string gameVersion, out string error)
        {
            gameVersion = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(gameRoot))
            {
                error = "The game root is unavailable.";
                return false;
            }

            IReadOnlyList<string> candidates;
            try
            {
                candidates = CandidateMetadataFiles(gameRoot);
            }
            catch (Exception ex)
            {
                error = "The game root is invalid: " + ex.Message;
                return false;
            }

            foreach (var candidate in candidates)
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException("installed-build.json cannot be a symbolic link or reparse point.");
                    }

                    string json;
                    using (var input = new FileStream(
                        candidate,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read | FileShare.Delete))
                    {
                        if (input.Length <= 0 || input.Length > MaxMetadataBytes)
                        {
                            throw new InvalidDataException(
                                "installed-build.json must be between 1 and " + MaxMetadataBytes + " bytes.");
                        }

                        var bytes = new byte[checked((int)input.Length)];
                        var offset = 0;
                        while (offset < bytes.Length)
                        {
                            var count = input.Read(bytes, offset, bytes.Length - offset);
                            if (count == 0)
                            {
                                throw new EndOfStreamException("installed-build.json changed while it was read.");
                            }

                            offset += count;
                        }

                        if (input.ReadByte() != -1)
                        {
                            throw new InvalidDataException("installed-build.json grew while it was read.");
                        }

                        json = StrictUtf8.GetString(bytes);
                    }

                    JsonObjectMerge.ValidateObject(json);
                    var metadata = JsonUtil.Deserialize<InstalledBuildMetadata>(json);
                    if (!TryFormatId(metadata.Id, out var buildId) ||
                        !GameBuildVersion.TryFromBuildId(buildId, out gameVersion))
                    {
                        throw new InvalidDataException("installed-build.json has no positive integer id.");
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    error = Path.GetFileName(candidate) + " was rejected: " + ex.Message;
                    return false;
                }
            }

            error = "installed-build.json was not found beside the game installation.";
            return false;
        }

        private static IReadOnlyList<string> CandidateMetadataFiles(string gameRoot)
        {
            var root = new DirectoryInfo(Path.GetFullPath(gameRoot));
            var roots = new List<string> { root.FullName };

            if (root.Name.Equals("Robotopia", StringComparison.OrdinalIgnoreCase) && root.Parent != null)
            {
                roots.Add(root.Parent.FullName);
            }
            else if (root.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && root.Parent != null)
            {
                roots.Add(root.Parent.FullName);
            }
            else if (root.Name.Equals("Contents", StringComparison.OrdinalIgnoreCase) &&
                     root.Parent?.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true &&
                     root.Parent.Parent != null)
            {
                roots.Add(root.Parent.Parent.FullName);
            }
            else if (root.Name.Equals("MacOS", StringComparison.OrdinalIgnoreCase) &&
                     root.Parent?.Name.Equals("Contents", StringComparison.OrdinalIgnoreCase) == true &&
                     root.Parent.Parent?.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true &&
                     root.Parent.Parent.Parent != null)
            {
                roots.Add(root.Parent.Parent.Parent.FullName);
            }

            return roots
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.Combine(path, "installed-build.json"))
                .ToList();
        }

        private static bool TryFormatId(object? id, out string buildId)
        {
            buildId = string.Empty;
            switch (id)
            {
                case string text:
                    buildId = text;
                    return true;
                case int integer:
                    buildId = integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                case long integer:
                    buildId = integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                case decimal number when decimal.Truncate(number) == number:
                    buildId = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                default:
                    return false;
            }
        }

        [DataContract]
        private sealed class InstalledBuildMetadata
        {
            [DataMember(Name = "id")]
            public object? Id { get; set; }
        }
    }
}
