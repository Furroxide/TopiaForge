using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Worlds
{
    /// <summary>
    /// Local <c>.roboworld</c> support: the half of Worlds that reads exports off the player's own disk.
    /// </summary>
    /// <remarks>
    /// Kept apart from the scene/session machinery on purpose. This path shares nothing with a published
    /// world — no sign-in, no share link, no backend — and separating the files makes that reviewable rather
    /// than something a reader has to take on trust.
    /// </remarks>
    public sealed partial class WorldsService
    {
        private UgcImportHostBridge? importHost;

        /// <summary>Gets or sets whether local exports may be loaded (WorldsConfig.EnableLocalWorlds).</summary>
        public bool EnableLocalWorlds { get; set; } = true;

        /// <summary>
        /// Gets or sets the folder scanned for local exports. An empty value means the game's own default.
        /// </summary>
        public string LocalWorldFolder { get; set; } = string.Empty;

        /// <summary>Gets whether this game build still exposes a usable local import host.</summary>
        internal bool LocalWorldsAvailable => EnableLocalWorlds && ImportHost.IsAvailable;

        private UgcImportHostBridge ImportHost => importHost ??= new UgcImportHostBridge(logger);

        /// <summary>
        /// Resolves the effective local-world folder: the configured override, else the folder the game
        /// itself scans. Returns an empty string when neither is usable.
        /// </summary>
        internal string ResolveLocalWorldFolder()
        {
            var configured = RoboWorldImportPlan.TryNormalizeFolder(LocalWorldFolder);
            if (configured != null)
            {
                return configured;
            }

            return RoboWorldImportPlan.TryNormalizeFolder(ImportHost.GetDefaultImportFolder()) ?? string.Empty;
        }

        /// <summary>Lists the local exports the game's own scanner finds, including unreadable ones.</summary>
        /// <remarks>
        /// Files that failed to parse are returned rather than filtered out. A player whose export is
        /// missing from a list learns nothing; one who sees it listed with the loader's own error learns
        /// what to fix.
        /// </remarks>
        internal IReadOnlyList<RoboWorldFile> ListLocalWorlds()
        {
            ThrowIfDisposed();
            if (!LocalWorldsAvailable)
            {
                return Array.Empty<RoboWorldFile>();
            }

            var folder = ResolveLocalWorldFolder();
            return folder.Length == 0 ? Array.Empty<RoboWorldFile>() : ImportHost.ScanFolder(folder);
        }

        /// <summary>
        /// Imports one local export into the active scene through the game's own import host.
        /// </summary>
        /// <param name="requestedPath">An absolute path inside the folder, or a file name relative to it.</param>
        /// <param name="error">The reason the import was refused, when it was.</param>
        /// <remarks>Main thread only: this reaches live scene objects.</remarks>
        internal bool TryLoadLocalWorld(string requestedPath, out string error)
        {
            ThrowIfDisposed();

            if (!EnableLocalWorlds)
            {
                error = "Local worlds are disabled in the Worlds configuration.";
                return false;
            }

            if (!ImportHost.IsAvailable)
            {
                error = "This game build does not expose the local world importer.";
                return false;
            }

            if (!RoboWorldImportPlan.TryPlan(
                    ResolveLocalWorldFolder(),
                    requestedPath,
                    System.IO.File.Exists,
                    out var plan,
                    out error)
                || plan == null)
            {
                return false;
            }

            return ImportHost.TryImport(plan, out error);
        }

        private void DisposeLocalWorlds()
        {
            var host = importHost;
            importHost = null;
            host?.Dispose();
        }
    }
}
