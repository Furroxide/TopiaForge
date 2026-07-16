using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    /// <summary>In-memory bundle, prefab, and spawned-entity service.</summary>
    public sealed class FakeAssetService : IAssetService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<FakeAssetBundle> bundles = new List<FakeAssetBundle>();
        private readonly List<FakePrefabAsset> prefabs = new List<FakePrefabAsset>();
        private readonly List<FakeSpawnedEntity> spawned = new List<FakeSpawnedEntity>();
        private int nextEntity;

        /// <summary>Creates a fake asset service.</summary>
        public FakeAssetService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <summary>Gets or sets a stable error used to reject bundle loads.</summary>
        public ModErrorCode BundleLoadErrorCode { get; set; }

        /// <summary>Gets or sets a stable error used to reject prefab loads.</summary>
        public ModErrorCode PrefabLoadErrorCode { get; set; }

        /// <summary>Gets or sets a stable error used to reject prefab spawns.</summary>
        public ModErrorCode SpawnErrorCode { get; set; }

        /// <summary>Gets the number of currently live bundles.</summary>
        public int ActiveBundleCount => bundles.Count;

        /// <summary>Gets the number of currently live prefab handles.</summary>
        public int ActivePrefabCount => prefabs.Count;

        /// <summary>Gets the number of currently live spawned entities.</summary>
        public int ActiveSpawnCount => spawned.Count;

        /// <inheritdoc/>
        public Task<OperationResult<IAssetBundle>> LoadBundleAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("A package-relative bundle path is required.", nameof(relativePath));
            }

            if (cancellationToken.IsCancellationRequested || lifetime.StoppingToken.IsCancellationRequested)
            {
                return Task.FromResult(OperationResult<IAssetBundle>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake bundle load was cancelled."));
            }

            if (BundleLoadErrorCode != ModErrorCode.None)
            {
                return Task.FromResult(OperationResult<IAssetBundle>.Failure(
                    BundleLoadErrorCode,
                    "The bundle load was rejected by the fake service."));
            }

            _ = InMemoryModFileSystem.Normalize(relativePath);
            var bundle = new FakeAssetBundle(relativePath, value => bundles.Remove(value));
            bundles.Add(bundle);
            return Task.FromResult(lifetime.TrackResult<IAssetBundle>(
                bundle,
                "The fake mod stopped before the bundle could be loaded."));
        }

        /// <inheritdoc/>
        public Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(
            IAssetBundle bundle,
            string assetName,
            CancellationToken cancellationToken = default)
        {
            if (bundle == null)
            {
                throw new ArgumentNullException(nameof(bundle));
            }

            if (string.IsNullOrWhiteSpace(assetName))
            {
                throw new ArgumentException("An asset name is required.", nameof(assetName));
            }

            if (cancellationToken.IsCancellationRequested || lifetime.StoppingToken.IsCancellationRequested)
            {
                return Task.FromResult(OperationResult<IPrefabAsset>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake prefab load was cancelled."));
            }

            if (!(bundle is FakeAssetBundle fakeBundle) || !fakeBundle.IsAlive || !bundles.Contains(fakeBundle))
            {
                return Task.FromResult(OperationResult<IPrefabAsset>.Failure(
                    ModErrorCode.InvalidState,
                    "The bundle does not belong to this fake context or is no longer alive."));
            }

            if (PrefabLoadErrorCode != ModErrorCode.None)
            {
                return Task.FromResult(OperationResult<IPrefabAsset>.Failure(
                    PrefabLoadErrorCode,
                    "The prefab load was rejected by the fake service."));
            }

            var prefab = new FakePrefabAsset(fakeBundle, assetName, value => prefabs.Remove(value));
            prefabs.Add(prefab);
            return Task.FromResult(lifetime.TrackResult<IPrefabAsset>(
                prefab,
                "The fake mod stopped before the prefab could be loaded."));
        }

        /// <inheritdoc/>
        public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!(request.Prefab is FakePrefabAsset prefab) || !prefab.IsAlive || !prefabs.Contains(prefab))
            {
                return OperationResult<ISpawnedEntity>.Failure(
                    ModErrorCode.InvalidState,
                    "The prefab does not belong to this fake context or is no longer alive.");
            }

            if (SpawnErrorCode != ModErrorCode.None)
            {
                return OperationResult<ISpawnedEntity>.Failure(
                    SpawnErrorCode,
                    "The prefab spawn was rejected by the fake service.");
            }

            var entity = new FakeSpawnedEntity(
                "spawned-" + (++nextEntity),
                prefab.Name,
                request.Transform,
                value => spawned.Remove(value));
            spawned.Add(entity);
            return lifetime.TrackResult<ISpawnedEntity>(
                entity,
                "The fake mod stopped before the entity could be spawned.");
        }
    }

    /// <summary>Inspectable fake asset-bundle handle.</summary>
    public sealed class FakeAssetBundle : IAssetBundle
    {
        private Action<FakeAssetBundle>? release;

        internal FakeAssetBundle(string relativePath, Action<FakeAssetBundle> release)
        {
            RelativePath = relativePath;
            this.release = release;
        }

        /// <inheritdoc/>
        public string RelativePath { get; }

        /// <inheritdoc/>
        public bool IsAlive => release != null;

        /// <inheritdoc/>
        public void Dispose()
        {
            var callback = release;
            release = null;
            callback?.Invoke(this);
        }
    }

    /// <summary>Inspectable fake prefab handle.</summary>
    public sealed class FakePrefabAsset : IPrefabAsset
    {
        private readonly FakeAssetBundle bundle;
        private Action<FakePrefabAsset>? release;

        internal FakePrefabAsset(FakeAssetBundle bundle, string name, Action<FakePrefabAsset> release)
        {
            this.bundle = bundle;
            Name = name;
            this.release = release;
        }

        /// <inheritdoc/>
        public string Name { get; }

        /// <inheritdoc/>
        public bool IsAlive => release != null && bundle.IsAlive;

        /// <inheritdoc/>
        public void Dispose()
        {
            var callback = release;
            release = null;
            callback?.Invoke(this);
        }
    }

    /// <summary>Inspectable fake entity produced by a prefab spawn.</summary>
    public sealed class FakeSpawnedEntity : ISpawnedEntity
    {
        private Action<FakeSpawnedEntity>? release;

        internal FakeSpawnedEntity(
            string id,
            string name,
            TransformState transform,
            Action<FakeSpawnedEntity> release)
        {
            Id = id;
            Name = name;
            InitialTransform = transform;
            Position = transform.Position;
            this.release = release;
        }

        /// <inheritdoc/>
        public string Id { get; }

        /// <inheritdoc/>
        public string Name { get; }

        /// <inheritdoc/>
        public bool IsAlive => release != null;

        /// <inheritdoc/>
        public Vec3 Position { get; }

        /// <inheritdoc/>
        public TransformState InitialTransform { get; }

        /// <inheritdoc/>
        public void Dispose()
        {
            var callback = release;
            release = null;
            callback?.Invoke(this);
        }
    }
}
