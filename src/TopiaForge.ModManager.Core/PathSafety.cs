using System;
using System.IO;

namespace TopiaForge.ModManager.Core
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

        internal static string RequireOrdinaryDirectory(string path)
        {
            // The caller selects this root. System or user aliases above it are outside
            // the package, but the root entry itself and its package members must be ordinary.
            var directory = Path.GetFullPath(path);
            var volume = Path.GetPathRoot(directory)!;
            if (directory.Length > volume.Length)
            {
                directory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            var attributes = File.GetAttributes(directory);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) !=
                FileAttributes.Directory)
            {
                throw new InvalidDataException("package root is not an ordinary directory");
            }

            return directory;
        }

        internal static string RequireOrdinaryPackageFile(string packageRoot, string relativePath) =>
            ResolveOrdinaryPackageFile(packageRoot, relativePath, allowMissing: false, out _);

        internal static FileStream OpenOrdinaryPackageFile(
            string packageRoot, string relativePath, FileAccess access = FileAccess.Read, bool create = false)
        {
            var path = ResolveOrdinaryPackageFile(packageRoot, relativePath, create, out var exists);
            return new FileStream(path, exists ? FileMode.Open : FileMode.CreateNew, access, FileShare.Read);
        }

        private static string ResolveOrdinaryPackageFile(
            string packageRoot, string relativePath, bool allowMissing, out bool exists)
        {
            if (!PortablePackagePath.TryValidate(relativePath, out var portable, out _, out var error))
            {
                throw new InvalidDataException(error);
            }

            var current = RequireOrdinaryDirectory(packageRoot);
            var segments = portable.Split('/');
            for (var index = 0; index < segments.Length; index++)
            {
                current = CombineRelativeChild(current, segments[index]);
                var isFile = index == segments.Length - 1;
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (FileNotFoundException) when (isFile && allowMissing)
                {
                    exists = false;
                    return current;
                }

                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException("path contains a link or special entry");
                }

                if (isFile == ((attributes & FileAttributes.Directory) != 0))
                {
                    throw new InvalidDataException(isFile ? "path names a directory" : "parent path is not a directory");
                }
            }

            exists = true;
            return current;
        }

        private static StringComparison FileSystemComparison => Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }
}
