using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>Identifies the optional Worlds contract and runtime provider.</summary>
    public static class WorldsModule
    {
        /// <summary>Gets the manifest id used to declare the Worlds runtime module.</summary>
        public const string Id = "io.github.furroxide.topiaforge.worlds";
    }

    /// <summary>Imports local exports as owned content of an existing running session.</summary>
    public interface ILocalWorldService
    {
        /// <summary>Lists local exports, including files with the native scanner's parse errors.</summary>
        OperationResult<IReadOnlyList<LocalWorldFile>> ListLocalWorlds();

        /// <summary>Registers a caller-owned prefab override for subsequent local imports.</summary>
        /// <remarks>Replacing an asset id retires its previous lease. Existing imported entities are unchanged.</remarks>
        OperationResult<IDisposable> RegisterAssetOverride(WorldAssetOverride assetOverride);

        /// <summary>Imports an export into the identified running session and retains its content until cleanup.</summary>
        /// <param name="sessionId">The captured session identity; a stale identity cannot affect a newer session.</param>
        /// <param name="requestedPath">An absolute path inside the configured folder, or a file name within it.</param>
        /// <param name="cancellationToken">Cancels the request while retaining any native work through cleanup.</param>
        /// <remarks>Competing transitions are rejected as Busy. The folder boundary is checked before file existence.</remarks>
        Task<OperationResult<bool>> ImportAsync(string sessionId, string requestedPath,
            CancellationToken cancellationToken = default);
    }

    /// <summary>One local world export found on disk.</summary>
    public sealed class LocalWorldFile
    {
        /// <summary>Creates a description of one scanned export.</summary>
        public LocalWorldFile(string path, string fileName, string projectName, string loadError)
        {
            Path = path ?? string.Empty;
            FileName = fileName ?? string.Empty;
            ProjectName = projectName ?? string.Empty;
            LoadError = loadError ?? string.Empty;
        }

        /// <summary>Gets the absolute path of the export.</summary>
        public string Path { get; }

        /// <summary>Gets the export's file name.</summary>
        public string FileName { get; }

        /// <summary>Gets the project name declared inside the export, when it could be read.</summary>
        public string ProjectName { get; }

        /// <summary>Gets the scanner's own error for this file, or an empty string when it parsed.</summary>
        public string LoadError { get; }

        /// <summary>Gets whether the game's scanner could read this export.</summary>
        public bool IsLoadable => LoadError.Length == 0;
    }

    /// <summary>
    /// Maps an authored asset id to a modder-supplied prefab so imported entities render as real content
    /// rather than the importer's own fallback.
    /// </summary>
    public sealed class WorldAssetOverride
    {
        /// <summary>Creates an override binding one authored asset id to one prefab.</summary>
        /// <param name="assetId">Authored asset id as referenced by exported entities.</param>
        /// <param name="prefab">A prefab loaded through this mod's own asset service.</param>
        /// <param name="localPositionOffset">Optional local-space offset aligning the prefab to the authored origin.</param>
        public WorldAssetOverride(string assetId, IPrefabAsset prefab, Vec3? localPositionOffset = null)
        {
            if (string.IsNullOrWhiteSpace(assetId))
            {
                throw new ArgumentException("An authored asset id is required.", nameof(assetId));
            }

            AssetId = assetId;
            Prefab = prefab ?? throw new ArgumentNullException(nameof(prefab));
            LocalPositionOffset = localPositionOffset;
        }

        /// <summary>Gets the authored asset id this override resolves.</summary>
        public string AssetId { get; }

        /// <summary>Gets the opaque prefab asset that replaces it.</summary>
        public IPrefabAsset Prefab { get; }

        /// <summary>Gets the optional local-space offset; <c>null</c> means zero.</summary>
        public Vec3? LocalPositionOffset { get; }
    }

}
