using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// An asset service that loads as the game's does: through the runtime's native asset operation, which finishes a load
    /// on the game thread and runs whatever waits for it afterwards, elsewhere; with handles that may only be let go of on
    /// the game thread; and with an engine that refuses to load a bundle file while another bundle of it is still loaded.
    /// Loads wait until <see cref="CompleteLoads"/>, as the engine's do until it has read them.
    /// </summary>
    internal sealed class NativeWorldAssets : IAssetService
    {
        private readonly FakeWorldAssets spawner;
        private readonly IModLifetime lifetime;
        private readonly int gameThread = Thread.CurrentThread.ManagedThreadId;
        private readonly List<IHeldLoad> waiting = new List<IHeldLoad>();
        private readonly HashSet<string> loadedFiles = new HashSet<string>(StringComparer.Ordinal);
        private int offThreadReleases;

        internal NativeWorldAssets(FakeWorldScene scene, IModLifetime lifetime)
        {
            spawner = new FakeWorldAssets(scene, lifetime);
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        private interface IHeldLoad
        {
            bool IsDone { set; }
        }

        internal int BundleLoads { get; private set; }

        internal int PrefabLoads { get; private set; }

        /// <summary>Gets the loads the engine refused because a bundle of the same file was still loaded.</summary>
        internal int RefusedLoads { get; private set; }

        /// <summary>Gets the bundles loaded now, which the engine would refuse to load again.</summary>
        internal int LoadedBundles => loadedFiles.Count;

        /// <summary>Gets the prefabs loaded and not let go of yet.</summary>
        internal int LivePrefabs { get; private set; }

        /// <summary>Gets how often a handle was let go of on another thread than the game's, where the engine refuses.</summary>
        internal int OffThreadReleases => Volatile.Read(ref offThreadReleases);

        internal List<FakeSpawnedEntity> Spawned => spawner.Spawned;

        /// <summary>Lets every waiting load finish, then runs the runtime's pump of native asset work, as the game's frame does.</summary>
        internal int CompleteLoads()
        {
            var due = waiting.ToArray();
            waiting.Clear();
            foreach (var load in due) load.IsDone = true;
            AssetNativeRequestPump.Poll();
            return due.Length;
        }

        public Task<OperationResult<IAssetBundle>> LoadBundleAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            BundleLoads++;
            var load = new HeldLoad<IAssetBundle>(() =>
            {
                if (!loadedFiles.Add(relativePath))
                {
                    RefusedLoads++;
                    return OperationResult<IAssetBundle>.Failure(ModErrorCode.External, "Another bundle of the same file is loaded.");
                }

                return OperationResult<IAssetBundle>.Success(new Bundle(this, relativePath));
            });
            waiting.Add(load);
            return AssetNativeOperation<IAssetBundle>.Start(lifetime, "Asset bundle '" + relativePath + "'", () => load, cancellationToken);
        }

        public Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(IAssetBundle bundle, string assetName, CancellationToken cancellationToken = default)
        {
            PrefabLoads++;
            var load = new HeldLoad<IPrefabAsset>(() =>
            {
                LivePrefabs++;
                return OperationResult<IPrefabAsset>.Success(new Prefab(this, assetName));
            });
            waiting.Add(load);
            return AssetNativeOperation<IPrefabAsset>.Start(lifetime, "Prefab '" + assetName + "'", () => load, cancellationToken);
        }

        public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request) => spawner.Spawn(request);

        /// <summary>Lets a handle go as the engine does: on the game thread only, refused anywhere else.</summary>
        private void Release(Action release)
        {
            if (Thread.CurrentThread.ManagedThreadId != gameThread) Interlocked.Increment(ref offThreadReleases);
            UnityMainThreadGuard.AssertCurrent();
            release();
        }

        /// <summary>One engine request, done once the test says the engine has read it.</summary>
        private sealed class HeldLoad<T> : IAssetNativeRequest<T>, IHeldLoad where T : class, IDisposable
        {
            private readonly Func<OperationResult<T>> read;

            internal HeldLoad(Func<OperationResult<T>> read)
            {
                this.read = read;
            }

            public bool HasStarted { get; private set; }

            public bool IsDone { get; set; }

            public void Start() => HasStarted = true;

            public OperationResult<T> ReadResult() => read();

            public void Dispose()
            {
            }
        }

        private sealed class Bundle : IAssetBundle
        {
            private readonly NativeWorldAssets owner;

            internal Bundle(NativeWorldAssets owner, string path)
            {
                this.owner = owner;
                RelativePath = path;
            }

            public string RelativePath { get; }

            public bool IsAlive { get; private set; } = true;

            public void Dispose() => owner.Release(() =>
            {
                if (!IsAlive) return;
                IsAlive = false;
                owner.loadedFiles.Remove(RelativePath);
            });
        }

        private sealed class Prefab : IPrefabAsset
        {
            private readonly NativeWorldAssets owner;

            internal Prefab(NativeWorldAssets owner, string name)
            {
                this.owner = owner;
                Name = name;
            }

            public string Name { get; }

            public bool IsAlive { get; private set; } = true;

            public void Dispose() => owner.Release(() =>
            {
                if (!IsAlive) return;
                IsAlive = false;
                owner.LivePrefabs--;
            });
        }
    }
}
