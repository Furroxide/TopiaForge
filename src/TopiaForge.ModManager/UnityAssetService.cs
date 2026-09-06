using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed class OwnerAssetService : IAssetService, IParentAssetScope
    {
        private readonly AssetScopeOwnership ownership;
        private readonly string packagePath;
        private readonly IModLifetime lifetime;
        private readonly UnityEntityRegistry entities;

        public OwnerAssetService(string packagePath, IModLifetime lifetime, UnityEntityRegistry entities)
        {
            this.packagePath = packagePath;
            ownership = new AssetScopeOwnership(packagePath);
            this.lifetime = lifetime;
            this.entities = entities;
        }

        void IParentAssetScope.AttachParent(IAssetService parent)
        {
            if (!(parent is OwnerAssetService source) || !ReferenceEquals(entities, source.entities))
                throw new InvalidOperationException("Parent assets must belong to the same loaded package host.");
            ownership.AttachParent(source.ownership);
        }

        public Task<OperationResult<IAssetBundle>> LoadBundleAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("A package-relative asset bundle path is required.", nameof(relativePath));
            }

            string fullPath;
            try
            {
                fullPath = PathSafety.CombineRelativeChild(packagePath, relativePath);
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException)
            {
                return Task.FromResult(OperationResult<IAssetBundle>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The asset path is not a safe package-relative path: " + exception.Message));
            }

            if (!File.Exists(fullPath))
            {
                return Task.FromResult(OperationResult<IAssetBundle>.Failure(
                    ModErrorCode.NotFound,
                    "Asset bundle '" + relativePath + "' does not exist in this mod package."));
            }

            if (lifetime.IsStopping || cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(OperationResult<IAssetBundle>.Failure(
                    ModErrorCode.Cancelled,
                    "The asset load was cancelled before it started."));
            }

            return AssetNativeOperation<IAssetBundle>.Start(lifetime, "Asset bundle '" + relativePath + "'",
                () => new BundleRequest(this, fullPath, relativePath), cancellationToken);
        }

        public Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(
            IAssetBundle bundle,
            string assetName,
            CancellationToken cancellationToken = default)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (bundle == null)
            {
                throw new ArgumentNullException(nameof(bundle));
            }

            if (string.IsNullOrWhiteSpace(assetName))
            {
                throw new ArgumentException("An asset name is required.", nameof(assetName));
            }

            if (!(bundle is UnityAssetBundleHandle nativeBundle)
                || !ReferenceEquals(nativeBundle.Owner, this))
            {
                return Task.FromResult(OperationResult<IPrefabAsset>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The asset bundle was not created by this mod context."));
            }

            if (!nativeBundle.IsAlive)
            {
                return Task.FromResult(OperationResult<IPrefabAsset>.Failure(
                    ModErrorCode.InvalidState,
                    "The asset bundle has already been released."));
            }

            if (lifetime.IsStopping || cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(OperationResult<IPrefabAsset>.Failure(
                    ModErrorCode.Cancelled,
                    "The prefab load was cancelled before it started."));
            }

            return AssetNativeOperation<IPrefabAsset>.Start(lifetime, "Prefab '" + assetName + "'",
                () => new PrefabRequest(this, nativeBundle, assetName), cancellationToken);
        }

        public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!(request.Prefab is UnityPrefabHandle prefab) || !ownership.AllowsSpawn(prefab.Owner.ownership))
            {
                return OperationResult<ISpawnedEntity>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The prefab was not created by this context or its explicitly attached parent package context.");
            }

            if (!prefab.IsAlive)
            {
                return OperationResult<ISpawnedEntity>.Failure(
                    ModErrorCode.InvalidState,
                    "The prefab or its asset bundle has already been released.");
            }

            if (lifetime.IsStopping)
            {
                return OperationResult<ISpawnedEntity>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod is stopping and cannot spawn new entities.");
            }

            try
            {
                var transform = request.Transform;
                var spawned = AssetSpawnTransaction.Create(
                    () => UnityEngine.Object.Instantiate(prefab.Prefab,
                        UnityPhysicsBackend.ToUnity(transform.Position), ToUnity(transform.Rotation)),
                    instance =>
                    {
                        instance.transform.localScale = UnityPhysicsBackend.ToUnity(transform.Scale);
                        return new UnitySpawnedEntity(instance, entities.GetOrCreate(instance), transform);
                    },
                    instance => UnityEngine.Object.Destroy(instance),
                    lifetime);
                return OperationResult<ISpawnedEntity>.Success(spawned);
            }
            catch (Exception exception)
            {
                return OperationResult<ISpawnedEntity>.Failure(
                    ModErrorCode.External,
                    "The prefab could not be spawned: " + exception.Message);
            }
        }

        private static Quaternion ToUnity(Quat value)
        {
            var normalized = value.Normalized;
            return new Quaternion(normalized.X, normalized.Y, normalized.Z, normalized.W);
        }

        private sealed class UnityAssetBundleHandle : IAssetBundle
        {
            private readonly AssetNativeResource<AssetBundle> bundle;

            public UnityAssetBundleHandle(OwnerAssetService owner, string relativePath, AssetBundle bundle)
            {
                Owner = owner;
                RelativePath = relativePath;
                this.bundle = new AssetNativeResource<AssetBundle>(bundle, value => value.Unload(false));
            }

            public OwnerAssetService Owner { get; }
            public string RelativePath { get; }
            public bool IsAlive => bundle.IsAlive;
            internal AssetNativeResource<AssetBundle>.Pin Acquire() => bundle.Acquire();

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                bundle.Dispose();
            }
        }

        private sealed class UnityPrefabHandle : IPrefabAsset
        {
            private UnityAssetBundleHandle? bundle;
            private GameObject? prefab;

            public UnityPrefabHandle(
                OwnerAssetService owner,
                UnityAssetBundleHandle bundle,
                string name,
                GameObject prefab)
            {
                Owner = owner;
                this.bundle = bundle;
                this.prefab = prefab;
                Name = name;
            }

            public OwnerAssetService Owner { get; }
            public string Name { get; }
            public bool IsAlive => prefab != null && bundle != null && bundle.IsAlive;
            public GameObject Prefab => prefab ?? throw new ObjectDisposedException(nameof(UnityPrefabHandle));

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                Interlocked.Exchange(ref prefab, null);
                Interlocked.Exchange(ref bundle, null);
            }
        }

        private sealed class UnitySpawnedEntity : ISpawnedEntity, IUnityOwnedEntity
        {
            private GameObject? instance;
            private readonly IEntity entity;

            public UnitySpawnedEntity(GameObject instance, IEntity entity, TransformState initialTransform)
            {
                this.instance = instance;
                this.entity = entity;
                InitialTransform = initialTransform;
            }

            public string Id => entity.Id;
            public string Name => entity.Name;
            public bool IsAlive => instance != null && entity.IsAlive;
            public Vec3 Position => entity.Position;
            public TransformState InitialTransform { get; }
            public IEntity InnerEntity => entity;

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                var current = Interlocked.Exchange(ref instance, null);
                if (current != null)
                {
                    UnityEngine.Object.Destroy(current);
                }
            }
        }

        private sealed class BundleRequest : IAssetNativeRequest<IAssetBundle>
        {
            private readonly OwnerAssetService owner;
            private readonly string fullPath;
            private readonly string relativePath;
            private AssetBundleCreateRequest? request;
            internal BundleRequest(OwnerAssetService owner, string fullPath, string relativePath)
            { this.owner = owner; this.fullPath = fullPath; this.relativePath = relativePath; }
            public void Start() => request = AssetBundle.LoadFromFileAsync(fullPath)
                ?? throw new InvalidOperationException("The engine did not return an asset bundle request.");
            public bool HasStarted => request != null;
            public bool IsDone => request!.isDone;
            public OperationResult<IAssetBundle> ReadResult()
            {
                var result = request!.assetBundle;
                if (result == null) return OperationResult<IAssetBundle>.Failure(ModErrorCode.External,
                    "The file is not a compatible asset bundle for this game build.");
                try { return OperationResult<IAssetBundle>.Success(new UnityAssetBundleHandle(owner, relativePath, result)); }
                catch (Exception creationFailure)
                {
                    try { result.Unload(false); }
                    catch (Exception cleanupFailure) { throw new AggregateException(creationFailure, cleanupFailure); }
                    throw;
                }
            }
            public void Dispose() { request = null; }
        }

        private sealed class PrefabRequest : IAssetNativeRequest<IPrefabAsset>
        {
            private readonly OwnerAssetService owner;
            private readonly UnityAssetBundleHandle bundle;
            private readonly string name;
            private AssetNativeResource<AssetBundle>.Pin? pin;
            private AssetBundleRequest? request;
            internal PrefabRequest(OwnerAssetService owner, UnityAssetBundleHandle bundle, string name)
            { this.owner = owner; this.bundle = bundle; this.name = name; }
            public void Start()
            {
                pin = bundle.Acquire();
                request = pin.Value.LoadAssetAsync<GameObject>(name)
                    ?? throw new InvalidOperationException("The engine did not return a prefab request.");
            }
            public bool HasStarted => request != null;
            public bool IsDone => request!.isDone;
            public OperationResult<IPrefabAsset> ReadResult()
            {
                if (!bundle.IsAlive) return OperationResult<IPrefabAsset>.Failure(ModErrorCode.InvalidState,
                    "The asset bundle was released before the prefab finished loading.");
                var prefab = request!.asset as GameObject;
                if (prefab == null) return OperationResult<IPrefabAsset>.Failure(ModErrorCode.NotFound,
                    "Asset '" + name + "' was not found or is not a prefab.");
                return OperationResult<IPrefabAsset>.Success(new UnityPrefabHandle(owner, bundle, name, prefab));
            }
            public void Dispose() { request = null; Interlocked.Exchange(ref pin, null)?.Dispose(); }
        }
    }
}
