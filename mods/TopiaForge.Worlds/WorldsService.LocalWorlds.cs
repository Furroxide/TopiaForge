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
        private readonly Dictionary<string, AssetOverrideLease> assetOverrides =
            new Dictionary<string, AssetOverrideLease>(StringComparer.Ordinal);

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
        internal IReadOnlyList<RoboWorldFile> ListLocalWorldFiles()
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
        /// Resolves an authored asset id to a prefab this mod supplies, for the next local-world import.
        /// </summary>
        /// <remarks>
        /// Registering the same id twice replaces the earlier override and deactivates its lease, matching
        /// the game's own table, which holds one prefab per id. The alternative — refusing the second
        /// registration — leaves the caller unable to correct an override it already owns.
        /// </remarks>
        public OperationResult<IDisposable> RegisterAssetOverride(WorldAssetOverride assetOverride)
        {
            ThrowIfDisposed();

            if (assetOverride == null)
            {
                throw new ArgumentNullException(nameof(assetOverride));
            }

            if (assetOverrides.TryGetValue(assetOverride.AssetId, out var existing))
            {
                existing.Deactivate();
            }

            var lease = new AssetOverrideLease(this, assetOverride);
            assetOverrides[assetOverride.AssetId] = lease;
            return OperationResult<IDisposable>.Success(lease);
        }

        /// <summary>Snapshots the live overrides so an import is not exposed to concurrent edits.</summary>
        private IReadOnlyList<WorldAssetOverride> SnapshotAssetOverrides()
        {
            if (assetOverrides.Count == 0)
            {
                return Array.Empty<WorldAssetOverride>();
            }

            var snapshot = new List<WorldAssetOverride>(assetOverrides.Count);
            foreach (var lease in assetOverrides.Values)
            {
                snapshot.Add(lease.Override);
            }

            return snapshot;
        }

        private void ReleaseAssetOverride(AssetOverrideLease lease)
        {
            if (assetOverrides.TryGetValue(lease.Override.AssetId, out var current)
                && ReferenceEquals(current, lease))
            {
                assetOverrides.Remove(lease.Override.AssetId);
            }
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

            return ImportHost.TryImport(plan, SnapshotAssetOverrides(), out error);
        }

        /// <inheritdoc />
        public OperationResult<IReadOnlyList<LocalWorldFile>> ListLocalWorlds()
        {
            ThrowIfDisposed();

            if (!EnableLocalWorlds)
            {
                return OperationResult<IReadOnlyList<LocalWorldFile>>.Failure(
                    ModErrorCode.InvalidState,
                    "Local worlds are disabled in the Worlds configuration.");
            }

            if (!ImportHost.IsAvailable)
            {
                return OperationResult<IReadOnlyList<LocalWorldFile>>.Failure(
                    ModErrorCode.Unavailable,
                    "This game build does not expose the local world importer.");
            }

            // Without this the scanner going missing is reported as Success with an empty list, which a
            // caller cannot tell from a folder that simply holds no worlds.
            if (!ImportHost.CanScanFolder)
            {
                return OperationResult<IReadOnlyList<LocalWorldFile>>.Failure(
                    ModErrorCode.Unavailable,
                    "This game build does not expose the local world scanner.");
            }

            var scanned = ListLocalWorldFiles();
            var mapped = new List<LocalWorldFile>(scanned.Count);
            foreach (var file in scanned)
            {
                mapped.Add(new LocalWorldFile(file.Path, file.FileName, file.ProjectName, file.LoadError));
            }

            return OperationResult<IReadOnlyList<LocalWorldFile>>.Success(mapped);
        }

        /// <inheritdoc />
        public OperationResult<bool> LoadLocalWorld(string requestedPath)
        {
            if (!TryLoadLocalWorld(requestedPath, out var error))
            {
                // The importer reports refusals as prose, and the distinction the caller acts on is whether
                // the build can do this at all versus whether this particular file was rejected.
                var code = error.IndexOf("does not expose", StringComparison.Ordinal) >= 0
                    ? ModErrorCode.Unavailable
                    : ModErrorCode.InvalidArgument;
                return OperationResult<bool>.Failure(code, error);
            }

            return OperationResult<bool>.Success(true);
        }

        private void DisposeLocalWorlds()
        {
            foreach (var lease in assetOverrides.Values)
            {
                lease.Deactivate();
            }

            assetOverrides.Clear();

            var host = importHost;
            importHost = null;
            host?.Dispose();
        }

        /// <summary>One registered override; disposing it removes the entry.</summary>
        private sealed class AssetOverrideLease : IDisposable
        {
            private WorldsService? owner;

            public AssetOverrideLease(WorldsService owner, WorldAssetOverride assetOverride)
            {
                this.owner = owner;
                Override = assetOverride;
            }

            public WorldAssetOverride Override { get; }

            /// <summary>Drops the owner link without touching the table, for replacement and teardown.</summary>
            public void Deactivate() => owner = null;

            public void Dispose()
            {
                var service = owner;
                owner = null;
                service?.ReleaseAssetOverride(this);
            }
        }
    }
}
