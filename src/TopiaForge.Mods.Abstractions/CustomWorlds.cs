using System;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>Stable ids supplied by the first-party Worlds provider.</summary>
    public static class WellKnownWorldIds
    {
        /// <summary>Gets the freeform sandbox gamemode id.</summary>
        public const string SandboxGamemode = "io.github.furroxide.topiaforge.worlds.sandbox";

        /// <summary>Gets the generated open-sandbox world id.</summary>
        public const string OpenSandboxWorld = "io.github.furroxide.topiaforge.worlds.open_sandbox";
    }

    /// <summary>Creates one lifetime-owned SDK entity tree for a custom-world launch.</summary>
    public interface ICustomWorldContent
    {
        /// <summary>Gets placement and teardown behavior.</summary>
        CustomWorldOptions Options { get; }

        /// <summary>
        /// Creates content for one launch.
        /// </summary>
        /// <remarks>
        /// The returned task completes on the game's main thread. Never block on it — waiting from the main
        /// thread stops the frame loop that would have completed it, and the game hangs with no recovery.
        /// Keep the task, poll <see cref="Task.IsCompleted"/> from your per-frame update, and read the result
        /// there. The analyzer reports a blocking wait as TF1008.
        /// </remarks>
        Task<OperationResult<IWorldContent>> CreateAsync(
            CancellationToken cancellationToken = default);
    }

    /// <summary>Represents the entity tree owned by one custom-world session.</summary>
    public interface IWorldContent : IDisposable
    {
        /// <summary>Gets the opaque root entity.</summary>
        IEntity Root { get; }

        /// <summary>Gets whether the root remains alive.</summary>
        bool IsAlive { get; }
    }

    /// <summary>Immutable custom-world placement and safety settings.</summary>
    public sealed class CustomWorldOptions
    {
        /// <summary>Creates custom-world options.</summary>
        public CustomWorldOptions(
            string spawnPointName = "SpawnPoint",
            bool applyDefaultEnvironment = true,
            bool enableKillPlane = true,
            float killPlaneDepth = 100f)
        {
            if (killPlaneDepth <= 0f || float.IsNaN(killPlaneDepth) || float.IsInfinity(killPlaneDepth))
            {
                throw new ArgumentOutOfRangeException(nameof(killPlaneDepth));
            }

            SpawnPointName = spawnPointName ?? string.Empty;
            ApplyDefaultEnvironment = applyDefaultEnvironment;
            EnableKillPlane = enableKillPlane;
            KillPlaneDepth = killPlaneDepth;
        }

        /// <summary>Gets the optional descendant marking the player spawn.</summary>
        public string SpawnPointName { get; }

        /// <summary>Gets whether the provider may supply its accessible default environment.</summary>
        public bool ApplyDefaultEnvironment { get; }

        /// <summary>Gets whether the provider should respawn a fallen player.</summary>
        public bool EnableKillPlane { get; }

        /// <summary>Gets the positive fall distance that triggers respawn.</summary>
        public float KillPlaneDepth { get; }
    }

    /// <summary>SDK-only custom-world content backed by a package prefab.</summary>
    public sealed class BundleWorldContent : ICustomWorldContent
    {
        private readonly IAssetService assets;
        private readonly string bundleRelativePath;
        private readonly string prefabAssetName;
        private readonly TransformState initialTransform;

        /// <summary>Creates bundle-backed world content using an owner-bound asset service.</summary>
        public BundleWorldContent(
            IAssetService assets,
            string bundleRelativePath,
            string prefabAssetName,
            TransformState initialTransform,
            CustomWorldOptions? options = null)
        {
            this.assets = assets ?? throw new ArgumentNullException(nameof(assets));
            if (string.IsNullOrWhiteSpace(bundleRelativePath))
            {
                throw new ArgumentException("A package-relative bundle path is required.", nameof(bundleRelativePath));
            }

            if (string.IsNullOrWhiteSpace(prefabAssetName))
            {
                throw new ArgumentException("A prefab asset name is required.", nameof(prefabAssetName));
            }

            this.bundleRelativePath = bundleRelativePath;
            this.prefabAssetName = prefabAssetName;
            this.initialTransform = initialTransform;
            Options = options ?? new CustomWorldOptions();
        }

        /// <inheritdoc />
        public CustomWorldOptions Options { get; }

        /// <inheritdoc />
        public async Task<OperationResult<IWorldContent>> CreateAsync(
            CancellationToken cancellationToken = default)
        {
            var bundleResult = await assets.LoadBundleAsync(bundleRelativePath, cancellationToken);
            if (!bundleResult.TryGetValue(out var bundle))
            {
                return OperationResult<IWorldContent>.Failure(bundleResult.ErrorCode, bundleResult.ErrorMessage);
            }

            var prefabResult = await assets.LoadPrefabAsync(bundle, prefabAssetName, cancellationToken);
            if (!prefabResult.TryGetValue(out var prefab))
            {
                bundle.Dispose();
                return OperationResult<IWorldContent>.Failure(prefabResult.ErrorCode, prefabResult.ErrorMessage);
            }

            var spawnResult = assets.Spawn(new AssetSpawnRequest(prefab, initialTransform));
            if (!spawnResult.TryGetValue(out var entity))
            {
                prefab.Dispose();
                bundle.Dispose();
                return OperationResult<IWorldContent>.Failure(spawnResult.ErrorCode, spawnResult.ErrorMessage);
            }

            return OperationResult<IWorldContent>.Success(new SpawnedWorldContent(bundle, prefab, entity));
        }

        private sealed class SpawnedWorldContent : IWorldContent
        {
            private IAssetBundle? bundle;
            private IPrefabAsset? prefab;
            private ISpawnedEntity? entity;

            public SpawnedWorldContent(IAssetBundle bundle, IPrefabAsset prefab, ISpawnedEntity entity)
            {
                this.bundle = bundle;
                this.prefab = prefab;
                this.entity = entity;
            }

            public IEntity Root => entity ?? throw new ObjectDisposedException(nameof(SpawnedWorldContent));
            public bool IsAlive => entity?.IsAlive == true;

            public void Dispose()
            {
                entity?.Dispose();
                entity = null;
                prefab?.Dispose();
                prefab = null;
                bundle?.Dispose();
                bundle = null;
            }
        }
    }
}
