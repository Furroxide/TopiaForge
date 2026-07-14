using System;
using System.IO;

namespace Robotopia.ModManager.Core
{
    /// <summary>
    /// Resolves untrusted relative paths without relying on a string-prefix check.
    /// Filesystem path comparisons are case-insensitive on Windows and case-sensitive elsewhere.
    /// </summary>
    public static class PathSafety
    {
        public static string CombineRelativeChild(string root, string relativePath)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            if (relativePath == null)
            {
                throw new ArgumentNullException(nameof(relativePath));
            }

            if (Path.IsPathRooted(relativePath))
            {
                throw new InvalidOperationException("Path must be relative.");
            }

            var rootFullPath = Path.GetFullPath(root);
            var combined = Path.GetFullPath(Path.Combine(rootFullPath, relativePath));
            if (!IsSameOrChild(rootFullPath, combined))
            {
                throw new InvalidOperationException("Path escapes the allowed directory.");
            }

            return combined;
        }

        public static bool IsSameOrChild(string root, string candidate)
        {
            var rootFullPath = Path.GetFullPath(root);
            var candidateFullPath = Path.GetFullPath(candidate);
            if (string.Equals(rootFullPath, candidateFullPath, FileSystemComparison))
            {
                return true;
            }

            var rootWithSeparator = rootFullPath.EndsWith(
                Path.DirectorySeparatorChar.ToString(),
                StringComparison.Ordinal)
                ? rootFullPath
                : rootFullPath + Path.DirectorySeparatorChar;
            return candidateFullPath.StartsWith(rootWithSeparator, FileSystemComparison);
        }

        public static bool AreSame(string first, string second)
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                FileSystemComparison);
        }

        private static StringComparison FileSystemComparison => Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }
}
