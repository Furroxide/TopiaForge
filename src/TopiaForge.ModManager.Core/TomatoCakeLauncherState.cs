using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Reads what the official Tomato Cake launcher records about its Windows Robotopia install. The launcher keeps
    /// its state in %LOCALAPPDATA%\Tomato Cake\launcher. By default the game lives in that directory's Robotopia
    /// child, with installed-build.json beside it. When a player moves the game in the official launcher,
    /// launcher-config.json names the new parent directory in game_dir. The game then lives at game_dir\Robotopia
    /// with its current filelist.json beside it, while installed-build.json stays in the state directory. A
    /// filelist.json left in the state directory describes an earlier build, so nothing here reads it.
    /// </summary>
    /// <remarks>
    /// The relocation record is trusted for two decisions only: offering game_dir\Robotopia as an install, and
    /// attributing the state directory's installed-build.json to exactly that directory. Other hosts report no state
    /// directory, which leaves the macOS and Proton layouts unchanged. The launcher, the in-game loader and the
    /// GameCompat extractor apply the same rules.
    /// </remarks>
    internal static class TomatoCakeLauncherState
    {
        internal const string ConfigFileName = "launcher-config.json";
        internal const string InstalledBuildFileName = "installed-build.json";
        internal const string GameDirectoryName = "Robotopia";

        /// <summary>The bound every TopiaForge reader also applies to installed-build.json.</summary>
        internal const int MaxConfigBytes = 64 * 1024;

        private const int MaxGameDirectoryLength = 32767;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly char[] WindowsForbiddenCharacters = { ':', '*', '?', '"', '<', '>', '|' };

        private static bool WindowsPaths => Path.DirectorySeparatorChar == '\\';

        /// <summary>
        /// %LOCALAPPDATA%\Tomato Cake\launcher on Windows, or null on other hosts and when the folder is unknown.
        /// </summary>
        internal static string? DefaultStateDirectory()
        {
            if (!WindowsPaths)
            {
                return null;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(localAppData) || !Path.IsPathRooted(localAppData)
                ? null
                : Path.Combine(localAppData, "Tomato Cake", "launcher");
        }

        /// <summary>
        /// Reads launcher-config.json strictly: a regular file of at most <see cref="MaxConfigBytes"/> bytes holding
        /// one strict UTF-8 JSON object with no byte order mark and no duplicate properties at any depth. Unknown
        /// properties are tolerated, as every installed-build.json reader already tolerates the launcher's extra
        /// "dev" field. game_dir must name an existing local directory by absolute path, and neither it nor any
        /// ancestor may be a reparse point. Returns false with an empty error when there is no record, and false
        /// with the reason when the record is unusable.
        /// </summary>
        internal static bool TryReadGameDirectory(string? stateDirectory, out string gameDirectory, out string error)
        {
            gameDirectory = string.Empty;
            error = string.Empty;
            if (string.IsNullOrEmpty(stateDirectory))
            {
                return false;
            }

            var path = Path.Combine(stateDirectory, ConfigFileName);
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    return false;
                }

                var bytes = ReadBounded(path);
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    throw new InvalidDataException("it must not start with a byte order mark.");
                }

                var properties = JsonObjectMerge.ReadProperties(StrictUtf8.GetString(bytes));
                RejectDuplicateProperties(properties);
                var raw = properties
                    .Where(property => property.Name.Equals("game_dir", StringComparison.Ordinal))
                    .Select(property => property.RawValue.Trim())
                    .FirstOrDefault();
                if (raw == null || raw.Length == 0 || raw[0] != '"')
                {
                    throw new InvalidDataException("it has no string game_dir.");
                }

                var value = JsonUtil.Deserialize<string>(raw);
                var problem = GameDirectoryProblem(value, WindowsPaths);
                if (problem != null)
                {
                    throw new InvalidDataException(problem);
                }

                gameDirectory = RealDirectory(value);
                return true;
            }
            catch (Exception ex)
            {
                gameDirectory = string.Empty;
                error = ConfigFileName + " was rejected: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// game_dir\Robotopia when launcher-config.json is valid and that directory exists without being a reparse
        /// point; otherwise null.
        /// </summary>
        internal static string? RelocatedGameRoot(string? stateDirectory)
        {
            if (!TryReadGameDirectory(stateDirectory, out var gameDirectory, out _))
            {
                return null;
            }

            try
            {
                var root = new DirectoryInfo(Path.Combine(gameDirectory, GameDirectoryName));
                return root.Exists && (root.Attributes & FileAttributes.ReparsePoint) == 0 ? root.FullName : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The state directory's installed-build.json, but only for the install launcher-config.json points at:
        /// <paramref name="gameRoot"/> must be a directory named Robotopia whose parent is game_dir itself, with no
        /// reparse point anywhere on its path. Anything else returns null, so the marker can never be attributed to
        /// a different install.
        /// </summary>
        internal static string? InstalledBuildMarkerFor(string gameRoot, string? stateDirectory)
        {
            if (string.IsNullOrEmpty(stateDirectory) || string.IsNullOrWhiteSpace(gameRoot) ||
                !TryReadGameDirectory(stateDirectory, out var gameDirectory, out _))
            {
                return null;
            }

            try
            {
                var root = RealDirectory(gameRoot);
                var parent = Path.GetDirectoryName(root);
                if (!Path.GetFileName(root).Equals(GameDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                    parent == null ||
                    !string.Equals(
                        TrimTrailingSeparators(parent),
                        gameDirectory,
                        WindowsPaths ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    return null;
                }

                return Path.Combine(stateDirectory, InstalledBuildFileName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Describes why <paramref name="value"/> is not an absolute local directory path, or returns null when its
        /// shape is acceptable. With <paramref name="windowsPaths"/> the path must start with a drive letter such as
        /// D:\, so UNC shares and \\?\ or \\.\ device paths are refused and reading the record never reaches a network
        /// host. Otherwise the path must start with /. Either way empty, "." and ".." segments are refused. This
        /// only checks the text; <see cref="TryReadGameDirectory"/> also requires the directory to exist without
        /// reparse points.
        /// </summary>
        internal static string? GameDirectoryProblem(string value, bool windowsPaths)
        {
            if (value.Length == 0)
            {
                return "game_dir is empty.";
            }

            if (value.Length > MaxGameDirectoryLength)
            {
                return "game_dir exceeds " + MaxGameDirectoryLength + " characters.";
            }

            if (!value.Trim().Equals(value, StringComparison.Ordinal))
            {
                return "game_dir has surrounding whitespace.";
            }

            if (value.Any(character => character < 0x20 || character == 0x7F))
            {
                return "game_dir contains control characters.";
            }

            string[] segments;
            if (windowsPaths)
            {
                if (value.Length < 3 ||
                    !((value[0] >= 'A' && value[0] <= 'Z') || (value[0] >= 'a' && value[0] <= 'z')) ||
                    value[1] != ':' ||
                    (value[2] != '\\' && value[2] != '/'))
                {
                    return "game_dir must be an absolute path on a local drive letter.";
                }

                var rest = value.Substring(3);
                if (rest.IndexOfAny(WindowsForbiddenCharacters) >= 0)
                {
                    return "game_dir contains characters Windows paths cannot hold.";
                }

                segments = rest.Split('\\', '/');
            }
            else
            {
                if (value[0] != '/')
                {
                    return "game_dir must be an absolute path.";
                }

                segments = value.Substring(1).Split('/');
            }

            var count = segments.Length;
            if (count > 0 && segments[count - 1].Length == 0)
            {
                count--;
            }

            for (var index = 0; index < count; index++)
            {
                var segment = segments[index];
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    return "game_dir must not contain empty, \".\" or \"..\" segments.";
                }

                if (windowsPaths && (segment.EndsWith(".", StringComparison.Ordinal) ||
                                     segment.EndsWith(" ", StringComparison.Ordinal)))
                {
                    return "game_dir segments must not end with a dot or a space.";
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the full path of an existing directory after checking that no component from the volume root
        /// down to it is a reparse point.
        /// </summary>
        private static string RealDirectory(string path)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            var chain = new List<DirectoryInfo>();
            for (var current = directory; current != null; current = current.Parent)
            {
                chain.Add(current);
            }

            chain.Reverse();
            foreach (var component in chain)
            {
                if (!component.Exists)
                {
                    throw new DirectoryNotFoundException(component.FullName + " is not an existing directory.");
                }

                if ((component.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException(component.FullName + " is a link or other reparse point.");
                }
            }

            return TrimTrailingSeparators(directory.FullName);
        }

        private static string TrimTrailingSeparators(string path)
        {
            var root = Path.GetPathRoot(path) ?? string.Empty;
            var trimmed = path;
            while (trimmed.Length > root.Length &&
                   (trimmed[trimmed.Length - 1] == Path.DirectorySeparatorChar ||
                    trimmed[trimmed.Length - 1] == Path.AltDirectorySeparatorChar))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            return trimmed;
        }

        private static void RejectDuplicateProperties(IReadOnlyList<JsonObjectMerge.RawJsonProperty> properties)
        {
            if (properties.GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            {
                throw new InvalidDataException("it contains duplicate properties.");
            }

            foreach (var property in properties)
            {
                RejectNestedDuplicates(property.RawValue.Trim());
            }
        }

        private static void RejectNestedDuplicates(string rawValue)
        {
            if (rawValue.StartsWith("{", StringComparison.Ordinal))
            {
                RejectDuplicateProperties(JsonObjectMerge.ReadProperties(rawValue));
            }
            else if (rawValue.StartsWith("[", StringComparison.Ordinal))
            {
                foreach (var element in JsonObjectMerge.ReadArrayValues(rawValue))
                {
                    RejectNestedDuplicates(element.Trim());
                }
            }
        }

        private static byte[] ReadBounded(string path)
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException("it must be a regular file, not a directory or reparse point.");
            }

            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (input.Length <= 0 || input.Length > MaxConfigBytes)
                {
                    throw new InvalidDataException("it must be between 1 and " + MaxConfigBytes + " bytes.");
                }

                var bytes = new byte[checked((int)input.Length)];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var count = input.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0)
                    {
                        throw new EndOfStreamException("it changed while it was read.");
                    }

                    offset += count;
                }

                if (input.ReadByte() != -1)
                {
                    throw new InvalidDataException("it grew while it was read.");
                }

                return bytes;
            }
        }
    }
}
