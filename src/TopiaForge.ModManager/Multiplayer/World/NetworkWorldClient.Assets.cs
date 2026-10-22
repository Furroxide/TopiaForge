using System;
using System.Collections.Generic;
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
    /// own asset service, its bundle and prefab loaded once and shared by every replica of them. The mod's own handles come
    /// first (WS3.7): a prefab it loaded, or a bundle it holds, which the game would refuse to load a second time, as a
    /// listen host's builds of its players' spawns do. Loads are polled each frame and never awaited, a load that takes
    /// longer than <see cref="BuildTimeoutMicros"/> counts as unbuildable, and leaving lets go of every handle, including
    /// those still loading.
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
        private readonly ModAssetLoads modLoads;
        private readonly Dictionary<string, WorldAssetLoad<IAssetBundle>> bundles = new Dictionary<string, WorldAssetLoad<IAssetBundle>>(StringComparer.Ordinal);
        private readonly Dictionary<string, WorldAssetLoad<IPrefabAsset>> prefabs = new Dictionary<string, WorldAssetLoad<IPrefabAsset>>(StringComparer.Ordinal);
        private readonly Dictionary<string, IAssetService> prefabServices = new Dictionary<string, IAssetService>(StringComparer.Ordinal);
        private readonly List<ClientWorldEntity> building = new List<ClientWorldEntity>();
        private int pendingBuilds;

        private enum BuildStep
        {
            Waiting,
            Ready,
            Failed
        }

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

        /// <summary>
        /// Moves one asset replica on: its prefab, the mod's own first, else its bundle, the mod's own first, then the
        /// prefab from it; then the spawn, through the service that holds the prefab. Loads are shared and polled, never
        /// awaited.
        /// </summary>
        private void AdvanceBuild(ClientWorldEntity mirrored, long now)
        {
            var build = mirrored.Build;
            if (build == null) return;
            if (now - build.StartedMicros > BuildTimeoutMicros)
            {
                FinishBuild(mirrored, false);
                return;
            }

            if (modLoads.TryGetPrefab(mirrored.OwnerModId, build.BundlePath, build.AssetName, out var modAssets, out var modPrefab))
            {
                SpawnReplica(mirrored, modAssets, modPrefab);
                return;
            }

            if (prefabs.TryGetValue(build.PrefabKey, out var prefab) && prefab.IsSettled && prefab.Succeeded && !IsAlive(prefab.Value!))
            {
                // Loaded from a bundle the mod has let go of since: it goes, and loads again from what holds the bundle now.
                ForgetPrefab(build.PrefabKey);
                prefab = null;
            }

            if (prefab == null)
            {
                var step = LoadPrefab(mirrored, build);
                if (step != BuildStep.Ready) return;
                prefab = prefabs[build.PrefabKey];
            }

            if (!prefab.IsSettled) return;
            if (!prefab.Succeeded)
            {
                FinishBuild(mirrored, false);
                return;
            }

            SpawnReplica(mirrored, prefabServices[build.PrefabKey], prefab.Value!);
        }

        /// <summary>Starts loading a replica's prefab: from the bundle the mod holds, waiting while its own load runs, else from one loaded here.</summary>
        private BuildStep LoadPrefab(ClientWorldEntity mirrored, AssetReplicaBuild build)
        {
            IAssetService prefabAssets;
            IAssetBundle from;
            if (!bundles.ContainsKey(build.BundleKey) && modLoads.TryGetBundle(mirrored.OwnerModId, build.BundlePath, out var bundleAssets, out var modBundle))
            {
                prefabAssets = bundleAssets;
                from = modBundle;
            }
            else if (!bundles.ContainsKey(build.BundleKey) && modLoads.IsLoadingBundle(mirrored.OwnerModId, build.BundlePath))
            {
                // The mod's own load of the bundle lands first; a second load of it would be refused.
                return BuildStep.Waiting;
            }
            else
            {
                if (!bundles.TryGetValue(build.BundleKey, out var bundle))
                {
                    // Each load asks the package's files, and is kept for the session: a host that names paths without end
                    // gets no more of either.
                    if (bundles.Count >= MaximumBundleLoads)
                    {
                        counters.AssetLoadsRefused++;
                        FinishBuild(mirrored, false);
                        return BuildStep.Failed;
                    }

                    bundle = WorldAssetLoad<IAssetBundle>.Start(token => build.Assets.LoadBundleAsync(build.BundlePath, token));
                    bundles.Add(build.BundleKey, bundle);
                }

                if (!bundle.IsSettled) return BuildStep.Waiting;
                if (!bundle.Succeeded)
                {
                    FinishBuild(mirrored, false);
                    return BuildStep.Failed;
                }

                prefabAssets = build.Assets;
                from = bundle.Value!;
            }

            if (prefabs.Count >= MaximumPrefabLoads)
            {
                counters.AssetLoadsRefused++;
                FinishBuild(mirrored, false);
                return BuildStep.Failed;
            }

            prefabs.Add(build.PrefabKey, WorldAssetLoad<IPrefabAsset>.Start(token => prefabAssets.LoadPrefabAsync(from, build.AssetName, token)));
            prefabServices.Add(build.PrefabKey, prefabAssets);
            return BuildStep.Ready;
        }

        private void ForgetPrefab(string key)
        {
            if (prefabs.TryGetValue(key, out var load)) load.Release();
            prefabs.Remove(key);
            prefabServices.Remove(key);
        }

        private static bool IsAlive(IPrefabAsset prefab)
        {
            try
            {
                return prefab.IsAlive;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Spawns a replica from <paramref name="prefab"/> through <paramref name="assets"/>, the service that holds it.</summary>
        private void SpawnReplica(ClientWorldEntity mirrored, IAssetService assets, IPrefabAsset prefab)
        {
            OperationResult<ISpawnedEntity>? spawned;
            try
            {
                spawned = assets.Spawn(new AssetSpawnRequest(prefab, ToTransform(mirrored.EntryPose, mirrored.Scale)));
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
            prefabServices.Clear();
            bundles.Clear();
        }
    }
}
