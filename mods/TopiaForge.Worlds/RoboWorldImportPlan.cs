using System;
using System.Collections.Generic;
using System.IO;

namespace TopiaForge.Worlds
{
    /// <summary>
    /// Unity-free policy for turning "the player wants this local export loaded" into a request the game's
    /// import host will accept, or into a refusal with a reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Build 2409 exposes runtime override fields for the folder it scans for <c>.roboworld</c>, <c>.json</c>,
    /// and <c>.json.gz</c> exports. That is a purely local path: no Discord sign-in, no publish, no backend.
    /// Aiming it somewhere is therefore cheap, which is exactly why it needs a policy — a caller that can
    /// name any absolute path can make the game read any file on the machine and report parse errors about
    /// its contents.
    /// </para>
    /// <para>
    /// So an import is confined to one declared folder. The folder is the trust boundary; everything else
    /// here is about proving a candidate is inside it before any game symbol is touched. This type holds no
    /// Unity or GameCode reference so the rules are unit-tested offline; see <c>UgcImportHostBridge</c> for
    /// the reflection half.
    /// </para>
    /// </remarks>
    internal sealed class RoboWorldImportPlan
    {
        /// <summary>The export extensions build 2409's importer recognizes, longest suffix first.</summary>
        /// <remarks>
        /// Taken verbatim from the game's own description of the scanned folder ("the folder scanned for
        /// .roboworld, .json, and .json.gz exports"). <c>.json.gz</c> is a compound suffix, so a plain
        /// <see cref="Path.GetExtension(string)"/> comparison would classify it as <c>.gz</c> and reject it.
        /// </remarks>
        public static readonly IReadOnlyList<string> SupportedExtensions =
            new[] { ".json.gz", ".roboworld", ".json" };

        private RoboWorldImportPlan(string folderPath, string filePath, string fileName)
        {
            FolderPath = folderPath;
            FilePath = filePath;
            FileName = fileName;
        }

        /// <summary>Gets the absolute folder the game's importer should scan.</summary>
        public string FolderPath { get; }

        /// <summary>Gets the absolute path of the export to import.</summary>
        public string FilePath { get; }

        /// <summary>Gets the export's file name, for logs and session labels.</summary>
        public string FileName { get; }

        /// <summary>Gets whether <paramref name="path"/> ends in an extension the importer recognizes.</summary>
        public static bool HasSupportedExtension(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            foreach (var extension in SupportedExtensions)
            {
                if (path!.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Normalizes a configured import folder to a rooted, separator-stable absolute path, or returns
        /// null when the value cannot be one.
        /// </summary>
        public static string? TryNormalizeFolder(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return null;
            }

            try
            {
                var full = Path.GetFullPath(folderPath!);
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception)
            {
                // ArgumentException/NotSupportedException/PathTooLongException/SecurityException all mean the
                // same thing to a caller: this string is not a usable folder.
                return null;
            }
        }

        /// <summary>
        /// Plans an import of <paramref name="requestedPath"/> from <paramref name="folderPath"/>.
        /// </summary>
        /// <param name="folderPath">The declared import folder. Must exist.</param>
        /// <param name="requestedPath">An absolute path, or a file name relative to the folder.</param>
        /// <param name="fileExists">Existence probe, injected so the rules are testable without a real tree.</param>
        /// <param name="plan">The accepted plan, or null.</param>
        /// <param name="error">A player-facing reason when the request is refused.</param>
        public static bool TryPlan(
            string? folderPath,
            string? requestedPath,
            Func<string, bool> fileExists,
            out RoboWorldImportPlan? plan,
            out string error)
        {
            if (fileExists == null)
            {
                throw new ArgumentNullException(nameof(fileExists));
            }

            plan = null;

            var folder = TryNormalizeFolder(folderPath);
            if (folder == null)
            {
                error = "No local world folder is configured.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                error = "No local world was requested.";
                return false;
            }

            string candidate;
            try
            {
                candidate = Path.IsPathRooted(requestedPath)
                    ? Path.GetFullPath(requestedPath!)
                    : Path.GetFullPath(Path.Combine(folder, requestedPath!));
            }
            catch (Exception)
            {
                error = "'" + requestedPath + "' is not a usable file path.";
                return false;
            }

            if (!IsInsideFolder(folder, candidate))
            {
                // Deliberately reported before the extension and existence checks: whether a path outside the
                // folder exists is not something a caller should be able to learn from the error text.
                error = "Local worlds must live in " + folder + ".";
                return false;
            }

            if (!HasSupportedExtension(candidate))
            {
                error = "'" + Path.GetFileName(candidate) + "' is not a "
                    + string.Join(", ", new List<string>(SupportedExtensions).ToArray()) + " export.";
                return false;
            }

            if (!fileExists(candidate))
            {
                error = "'" + Path.GetFileName(candidate) + "' was not found in " + folder + ".";
                return false;
            }

            plan = new RoboWorldImportPlan(folder, candidate, Path.GetFileName(candidate));
            error = string.Empty;
            return true;
        }

        private static bool IsInsideFolder(string folder, string candidate)
        {
            // Compare on the separator-terminated folder so "…\worlds-backup\x.roboworld" cannot pass as a
            // child of "…\worlds". Ordinal-ignore-case matches Windows and the game's own path handling; a
            // case-sensitive filesystem only makes this stricter, never more permissive.
            var prefix = folder + Path.DirectorySeparatorChar;
            return candidate.Length > prefix.Length
                && candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
