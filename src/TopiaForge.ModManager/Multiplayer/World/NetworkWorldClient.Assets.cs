using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>A replica of an asset spawn still loading: the owning mod's asset service, and what to load from it.</summary>
    internal sealed class AssetReplicaBuild
    {
        internal AssetReplicaBuild(IAssetService assets, string bundleKey, string prefabKey, string bundlePath, string assetName, long startedMicros)
        {
            Assets = assets;
            BundleKey = bundleKey;
            PrefabKey = prefabKey;
            BundlePath = bundlePath;
            AssetName = assetName;
            StartedMicros = startedMicros;
        }

        internal IAssetService Assets { get; }

        internal string BundleKey { get; }

        internal string PrefabKey { get; }

        internal string BundlePath { get; }

        internal string AssetName { get; }

        internal long StartedMicros { get; }
    }

    /// <summary>
    /// A joined player's asset replicas: a spawn made through a mod's asset service is built here through the owning mod's
    /// own asset service, its bundle and prefab loaded once and shared by every replica of them. Loads are polled each
    /// frame and never awaited, a load that takes longer than <see cref="BuildTimeoutMicros"/> counts as unbuildable, and
    /// leaving lets go of every handle, including those still loading.
    /// </summary>
    internal sealed partial class NetworkWorldClient
    {
        /// <summary>How long one replica may take to load before it counts as unbuildable.</summary>
        internal const long BuildTimeoutMicros = 30_000_000;

        /// <summary>
        /// The most bundles a joined player loads for replicas in one session. A host names each one by its path in the
        /// owning mod's package, so only a host that makes paths up reaches this; whatever it names past the bound is not
        /// loaded, and its replicas count as unbuildable.
        /// </summary>
        internal const int MaximumBundleLoads = 256;

        /// <summary>The most prefabs a joined player loads for replicas in one session, under the same rule.</summary>
        internal const int MaximumPrefabLoads = 4096;

        private readonly Func<string, IAssetService?> findAssets;
        private readonly Dictionary<string, AssetLoad<IAssetBundle>> bundles = new Dictionary<string, AssetLoad<IAssetBundle>>(StringComparer.Ordinal);
        private readonly Dictionary<string, AssetLoad<IPrefabAsset>> prefabs = new Dictionary<string, AssetLoad<IPrefabAsset>>(StringComparer.Ordinal);
        private readonly List<ClientWorldEntity> building = new List<ClientWorldEntity>();
        private int pendingBuilds;

        /// <summary>Gets how many asset replicas are still loading.</summary>
        internal int PendingBuilds => pendingBuilds;

        private void StartAssetBuild(ClientWorldEntity mirrored)
        {
            var descriptor = WorldEntryCodec.DecodeAsset(mirrored.Payload);
            var assets = descriptor.IsPortable ? FindAssets(mirrored.OwnerModId) : null;
            if (assets == null)
            {
                Unbuildable(mirrored);
                return;
            }

            var bundleKey = mirrored.OwnerModId + "\n" + descriptor.BundlePath;
            mirrored.Build = new AssetReplicaBuild(assets, bundleKey, bundleKey + "\n" + descriptor.AssetName, descriptor.BundlePath!, descriptor.AssetName!, clock());
            building.Add(mirrored);
            pendingBuilds++;
            AdvanceBuild(mirrored, clock());
        }

        private IAssetService? FindAssets(string modId)
        {
            try
            {
                return findAssets(modId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void AdvanceBuilds(long now)
        {
            for (var index = building.Count - 1; index >= 0; index--)
            {
                if (index < building.Count) AdvanceBuild(building[index], now);
            }
        }

        /// <summary>Moves one asset replica on: its bundle, then its prefab, then the spawn. Loads are shared and polled, never awaited.</summary>
        private void AdvanceBuild(ClientWorldEntity mirrored, long now)
        {
            var build = mirrored.Build;
            if (build == null) return;
            if (now - build.StartedMicros > BuildTimeoutMicros)
            {
                FinishBuild(mirrored, false);
                return;
            }

            if (!bundles.TryGetValue(build.BundleKey, out var bundle))
            {
                // Each load asks the package's files, and is kept for the session: a host that names paths without end
                // gets no more of either.
                if (bundles.Count >= MaximumBundleLoads)
                {
                    counters.AssetLoadsRefused++;
                    FinishBuild(mirrored, false);
                    return;
                }

                bundle = AssetLoad<IAssetBundle>.Start(token => build.Assets.LoadBundleAsync(build.BundlePath, token));
                bundles.Add(build.BundleKey, bundle);
            }

            if (!bundle.IsSettled) return;
            if (!bundle.Succeeded)
            {
                FinishBuild(mirrored, false);
                return;
            }

            if (!prefabs.TryGetValue(build.PrefabKey, out var prefab))
            {
                if (prefabs.Count >= MaximumPrefabLoads)
                {
                    counters.AssetLoadsRefused++;
                    FinishBuild(mirrored, false);
                    return;
                }

                var loaded = bundle.Value!;
                prefab = AssetLoad<IPrefabAsset>.Start(token => build.Assets.LoadPrefabAsync(loaded, build.AssetName, token));
                prefabs.Add(build.PrefabKey, prefab);
            }

            if (!prefab.IsSettled) return;
            if (!prefab.Succeeded)
            {
                FinishBuild(mirrored, false);
                return;
            }

            OperationResult<ISpawnedEntity>? spawned;
            try
            {
                spawned = build.Assets.Spawn(new AssetSpawnRequest(prefab.Value!, ToTransform(mirrored.EntryPose, mirrored.Scale)));
            }
            catch (Exception)
            {
                spawned = null;
            }

            if (spawned == null || !spawned.TryGetValue(out var replica) || replica == null)
            {
                FinishBuild(mirrored, false);
                return;
            }

            mirrored.AssetReplica = replica;
            mirrored.Binding = ClientEntityBinding.Asset;
            FinishBuild(mirrored, true);
            var adopted = Adopt(replica);
            if (adopted != null) Attach(mirrored, adopted, ClientEntityBinding.Asset);
            counters.ReplicasBuilt++;
        }

        private void FinishBuild(ClientWorldEntity mirrored, bool built)
        {
            mirrored.Build = null;
            building.Remove(mirrored);
            pendingBuilds--;
            ReportProgress();
            if (!built) Unbuildable(mirrored);
        }

        /// <summary>
        /// Lets go of every bundle and prefab loaded for replicas. A load still running is cancelled, and the engine's loader
        /// lets go of its result on the game thread as it lands, so the same bundle loads again for a later join.
        /// </summary>
        private void ReleaseAssetLoads()
        {
            foreach (var mirrored in building) mirrored.Build = null;
            building.Clear();
            pendingBuilds = 0;
            foreach (var prefab in prefabs.Values) prefab.Release();
            foreach (var bundle in bundles.Values) bundle.Release();
            prefabs.Clear();
            bundles.Clear();
        }

        /// <summary>One shared load of a bundle or a prefab, polled each frame. Releasing it disposes what it loaded, now or when it lands.</summary>
        private sealed class AssetLoad<T> where T : class, IDisposable
        {
            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
            private Task<OperationResult<T>>? task;
            private bool released;

            internal bool IsSettled => task == null || task.IsCompleted;

            internal bool Succeeded => Value != null;

            internal T? Value
            {
                get
                {
                    if (released || task == null || task.Status != TaskStatus.RanToCompletion) return null;
                    var result = task.Result;
                    return result != null && result.TryGetValue(out var value) ? value : null;
                }
            }

            internal static AssetLoad<T> Start(Func<CancellationToken, Task<OperationResult<T>>> start)
            {
                var load = new AssetLoad<T>();
                try
                {
                    load.task = start(load.cancellation.Token);
                }
                catch (Exception)
                {
                    load.task = null;
                }

                return load;
            }

            /// <summary>
            /// Lets go of what the load holds. One still running is cancelled first: the engine's loader then lets go of whatever
            /// it loads as it lands, on the game thread. A continuation here could not promise that thread, since the loader runs
            /// what waits for a load elsewhere, and a handle let go of there stays loaded, so a later load of its bundle fails.
            /// </summary>
            internal void Release()
            {
                if (released) return;
                released = true;
                var current = task;
                if (current == null) return;
                if (!current.IsCompleted)
                {
                    try
                    {
                        cancellation.Cancel();
                    }
                    catch (Exception)
                    {
                        // A loader whose cancellation throws is let go of below, like one that takes no notice of it.
                    }
                }

                // A loader that takes no notice of cancellation hands back what it loaded anyway, and that goes as it lands.
                if (current.IsCompleted) DisposeResult(current);
                else current.ContinueWith(landed => DisposeResult(landed), TaskContinuationOptions.ExecuteSynchronously);
            }

            private static void DisposeResult(Task<OperationResult<T>> landed)
            {
                try
                {
                    if (landed.Status == TaskStatus.RanToCompletion && landed.Result != null && landed.Result.TryGetValue(out var value)) value.Dispose();
                }
                catch (Exception)
                {
                    // A handle that cannot be let go of goes with its mod's lifetime.
                }
            }
        }
    }
}
