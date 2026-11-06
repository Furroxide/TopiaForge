using System;
using System.IO;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Filesystem roots the production link rules accept.
    /// </summary>
    /// <remarks>
    /// Manager state readers refuse a path whose components include a reparse point,
    /// because a link can redirect a write out of the tree the manager owns. A real
    /// install never trips that rule, but the system temp directory can: on macOS
    /// <c>/var</c> is a symlink to <c>private/var</c> and every
    /// <see cref="Directory.CreateTempSubdirectory(string)"/> path sits under it, so a
    /// fixture built there is rejected before the behaviour under test is reached.
    /// Resolving the root once keeps the rule itself strict.
    /// </remarks>
    internal static class TestPaths
    {
        /// <summary>Creates a temporary directory at a link-free path.</summary>
        /// <remarks>
        /// A drop-in replacement for <see cref="Directory.CreateTempSubdirectory(string)"/>;
        /// the returned handle deletes the same directory it created, because resolving
        /// the links only changes how the path is spelled.
        /// </remarks>
        internal static DirectoryInfo CreateTempDirectory(string prefix) =>
            new(Canonical(Directory.CreateTempSubdirectory(prefix).FullName));

        /// <summary>Creates a temporary directory and returns its link-free path.</summary>
        internal static string CreateTempRoot(string prefix) =>
            CreateTempDirectory(prefix).FullName;

        /// <summary>Resolves every symlinked component of <paramref name="path"/>.</summary>
        internal static string Canonical(string path)
        {
            var full = Path.GetFullPath(path);
            var current = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(current)) return full;
            foreach (var part in full[current.Length..]
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                // Returns null when the component is not a link, so this leaves an
                // ordinary directory exactly as it was spelled.
                var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null) current = target.FullName;
            }
            return current;
        }
    }
}
