using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed class OwnerAssetService : IAssetService
    {
        private readonly string packagePath;
        private readonly IModLifetime lifetime;
        private readonly UnityEntityRegistry entities;

        public OwnerAssetService(string packagePath, IModLifetime lifetime, UnityEntityRegistry entities)
        {
            this.packagePath = packagePath;
            this.lifetime = lifetime;
            this.entities = entities;
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

            AssetBundleCreateRequest request;
            try
            {
                request = AssetBundle.LoadFromFileAsync(fullPath);
            }
            catch (Exception exception)
            {
                return Task.FromResult(OperationResult<IAssetBundle>.Failure(
                    ModErrorCode.External,
                    "The game rejected asset bundle '" + relativePath + "': " + exception.Message));
            }

            var state = new BundleLoadState(this, request, relativePath, lifetime, cancellationToken);
            lifetime.Track(state);
            return state.Task;
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

            AssetBundleRequest request;
            try
            {
                request = nativeBundle.Bundle.LoadAssetAsync<GameObject>(assetName);
            }
            catch (Exception exception)
            {
                return Task.FromResult(OperationResult<IPrefabAsset>.Failure(
                    ModErrorCode.External,
                    "The game rejected prefab '" + assetName + "': " + exception.Message));
            }

            var state = new PrefabLoadState(
                this,
                nativeBundle,
                request,
                assetName,
                lifetime,
                cancellationToken);
            lifetime.Track(state);
            return state.Task;
        }

        public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!(request.Prefab is UnityPrefabHandle prefab) || !ReferenceEquals(prefab.Owner, this))
            {
                return OperationResult<ISpawnedEntity>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The prefab was not created by this mod context.");
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
                var instance = UnityEngine.Object.Instantiate(
                    prefab.Prefab,
                    UnityPhysicsBackend.ToUnity(transform.Position),
                    ToUnity(transform.Rotation));
                instance.transform.localScale = UnityPhysicsBackend.ToUnity(transform.Scale);
                var entity = entities.GetOrCreate(instance);
                var spawned = new UnitySpawnedEntity(instance, entity, transform);
                lifetime.Track(spawned);
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
            private AssetBundle? bundle;

            public UnityAssetBundleHandle(OwnerAssetService owner, string relativePath, AssetBundle bundle)
            {
                Owner = owner;
                RelativePath = relativePath;
                this.bundle = bundle;
            }

            public OwnerAssetService Owner { get; }
            public string RelativePath { get; }
            public bool IsAlive => bundle != null;
            public AssetBundle Bundle => bundle ?? throw new ObjectDisposedException(nameof(UnityAssetBundleHandle));

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                Interlocked.Exchange(ref bundle, null)?.Unload(false);
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

        private sealed class BundleLoadState : IDisposable
        {
            private readonly OwnerAssetService owner;
            private readonly AssetBundleCreateRequest request;
            private readonly string relativePath;
            private readonly IModLifetime lifetime;
            private readonly TaskCompletionSource<OperationResult<IAssetBundle>> completion =
                new TaskCompletionSource<OperationResult<IAssetBundle>>();
            private readonly CancellationTokenRegistration stoppingRegistration;
            private readonly CancellationTokenRegistration callerRegistration;
            private int cancelled;
            private int completed;

            public BundleLoadState(
                OwnerAssetService owner,
                AssetBundleCreateRequest request,
                string relativePath,
                IModLifetime lifetime,
                CancellationToken callerToken)
            {
                this.owner = owner;
                this.request = request;
                this.relativePath = relativePath;
                this.lifetime = lifetime;
                stoppingRegistration = lifetime.StoppingToken.Register(Cancel);
                callerRegistration = callerToken.CanBeCanceled ? callerToken.Register(Cancel) : default;
                request.completed += OnCompleted;
            }

            public Task<OperationResult<IAssetBundle>> Task => completion.Task;

            public void Dispose()
            {
                Cancel();
            }

            private void Cancel()
            {
                Interlocked.Exchange(ref cancelled, 1);
                Finish(OperationResult<IAssetBundle>.Failure(
                    ModErrorCode.Cancelled,
                    "The asset bundle load was cancelled."));
            }

            private void OnCompleted(AsyncOperation _)
            {
                UnityMainThreadGuard.AssertCurrent();
                request.completed -= OnCompleted;
                var bundle = request.assetBundle;
                if (Volatile.Read(ref cancelled) != 0 || lifetime.IsStopping)
                {
                    bundle?.Unload(false);
                    Finish(OperationResult<IAssetBundle>.Failure(
                        ModErrorCode.Cancelled,
                        "The asset bundle load was cancelled."));
                    return;
                }

                if (bundle == null)
                {
                    Finish(OperationResult<IAssetBundle>.Failure(
                        ModErrorCode.External,
                        "The file is not a compatible asset bundle for this game build."));
                    return;
                }

                var handle = new UnityAssetBundleHandle(owner, relativePath, bundle);
                try
                {
                    lifetime.Track(handle);
                    Finish(OperationResult<IAssetBundle>.Success(handle));
                }
                catch (ObjectDisposedException)
                {
                    handle.Dispose();
                    Finish(OperationResult<IAssetBundle>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod stopped before the asset bundle became available."));
                }
            }

            private void Finish(OperationResult<IAssetBundle> result)
            {
                if (Interlocked.Exchange(ref completed, 1) != 0)
                {
                    return;
                }

                stoppingRegistration.Dispose();
                callerRegistration.Dispose();
                completion.TrySetResult(result);
            }
        }

        private sealed class PrefabLoadState : IDisposable
        {
            private readonly OwnerAssetService owner;
            private readonly UnityAssetBundleHandle bundle;
            private readonly AssetBundleRequest request;
            private readonly string assetName;
            private readonly IModLifetime lifetime;
            private readonly TaskCompletionSource<OperationResult<IPrefabAsset>> completion =
                new TaskCompletionSource<OperationResult<IPrefabAsset>>();
            private readonly CancellationTokenRegistration stoppingRegistration;
            private readonly CancellationTokenRegistration callerRegistration;
            private int cancelled;
            private int completed;

            public PrefabLoadState(
                OwnerAssetService owner,
                UnityAssetBundleHandle bundle,
                AssetBundleRequest request,
                string assetName,
                IModLifetime lifetime,
                CancellationToken callerToken)
            {
                this.owner = owner;
                this.bundle = bundle;
                this.request = request;
                this.assetName = assetName;
                this.lifetime = lifetime;
                stoppingRegistration = lifetime.StoppingToken.Register(Cancel);
                callerRegistration = callerToken.CanBeCanceled ? callerToken.Register(Cancel) : default;
                request.completed += OnCompleted;
            }

            public Task<OperationResult<IPrefabAsset>> Task => completion.Task;

            public void Dispose()
            {
                Cancel();
            }

            private void Cancel()
            {
                Interlocked.Exchange(ref cancelled, 1);
                Finish(OperationResult<IPrefabAsset>.Failure(
                    ModErrorCode.Cancelled,
                    "The prefab load was cancelled."));
            }

            private void OnCompleted(AsyncOperation _)
            {
                UnityMainThreadGuard.AssertCurrent();
                request.completed -= OnCompleted;
                if (Volatile.Read(ref cancelled) != 0 || lifetime.IsStopping)
                {
                    Finish(OperationResult<IPrefabAsset>.Failure(
                        ModErrorCode.Cancelled,
                        "The prefab load was cancelled."));
                    return;
                }

                if (!bundle.IsAlive)
                {
                    Finish(OperationResult<IPrefabAsset>.Failure(
                        ModErrorCode.InvalidState,
                        "The asset bundle was released before the prefab finished loading."));
                    return;
                }

                var prefab = request.asset as GameObject;
                if (prefab == null)
                {
                    Finish(OperationResult<IPrefabAsset>.Failure(
                        ModErrorCode.NotFound,
                        "Asset '" + assetName + "' was not found or is not a prefab."));
                    return;
                }

                var handle = new UnityPrefabHandle(owner, bundle, assetName, prefab);
                try
                {
                    lifetime.Track(handle);
                    Finish(OperationResult<IPrefabAsset>.Success(handle));
                }
                catch (ObjectDisposedException)
                {
                    handle.Dispose();
                    Finish(OperationResult<IPrefabAsset>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod stopped before the prefab became available."));
                }
            }

            private void Finish(OperationResult<IPrefabAsset> result)
            {
                if (Interlocked.Exchange(ref completed, 1) != 0)
                {
                    return;
                }

                stoppingRegistration.Dispose();
                callerRegistration.Dispose();
                completion.TrySetResult(result);
            }
        }
    }
}
