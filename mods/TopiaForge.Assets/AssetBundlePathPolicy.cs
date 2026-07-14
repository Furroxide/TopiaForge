using System;
using System.IO;

namespace TopiaForge.Assets
{
    /// <summary>Cross-platform containment policy for package-owned AssetBundle files.</summary>
    internal static class AssetBundlePathPolicy
    {
        public static bool TryResolve(string packagePath, string relativePath, out string fullPath, out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(packagePath))
            {
                error = "Package path is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                error = "AssetBundle relative path is required.";
                return false;
            }

            if (Path.IsPathRooted(relativePath))
            {
                error = "AssetBundle path must be package-relative.";
                return false;
            }

            if (!HasPortableSegments(relativePath, out error))
            {
                return false;
            }

            string root;
            try
            {
                root = Path.GetFullPath(packagePath);
                fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is IOException ||
                exception is NotSupportedException ||
                exception is UnauthorizedAccessException)
            {
                error = "AssetBundle path is invalid: " + exception.Message;
                fullPath = string.Empty;
                return false;
            }

            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!fullPath.StartsWith(rootPrefix, comparison))
            {
                error = "AssetBundle path escapes the mod package directory.";
                fullPath = string.Empty;
                return false;
            }

            if (ContainsReparsePoint(root, fullPath))
            {
                error = "AssetBundle path cannot traverse a symbolic link or reparse point.";
                fullPath = string.Empty;
                return false;
            }

            return true;
        }

        private static bool HasPortableSegments(string relativePath, out string error)
        {
            var segments = relativePath.Replace('\\', '/').Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    error = "AssetBundle path contains an empty or relative segment.";
                    return false;
                }

                if (segment.IndexOf(':') >= 0 || segment.EndsWith(" ", StringComparison.Ordinal) || segment.EndsWith(".", StringComparison.Ordinal))
                {
                    error = "AssetBundle path contains a non-portable path segment.";
                    return false;
                }

                var deviceName = segment;
                var dot = deviceName.IndexOf('.');
                if (dot >= 0)
                {
                    deviceName = deviceName.Substring(0, dot);
                }

                if (IsWindowsDeviceName(deviceName))
                {
                    error = "AssetBundle path contains a reserved device name.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private static bool IsWindowsDeviceName(string value)
        {
            if (value.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return value.Length == 4 &&
                (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                value[3] >= '1' && value[3] <= '9';
        }

        private static bool ContainsReparsePoint(string root, string fullPath)
        {
            var relative = fullPath.Substring(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                }
                catch (FileNotFoundException)
                {
                    // The final load reports a missing bundle; absent segments cannot redirect traversal.
                }
                catch (DirectoryNotFoundException)
                {
                    // Same as above.
                }
                catch (IOException)
                {
                    // An unreadable or malformed path component is unsafe to traverse.
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
