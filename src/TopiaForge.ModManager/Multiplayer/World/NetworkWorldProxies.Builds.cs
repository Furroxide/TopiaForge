using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// How a listen host makes a joined player's asset spawn (WS3.7). The mod's own handles come first, as its contexts'
    /// network decorators saw them load: the prefab it loaded, else a prefab loaded from the bundle it holds, waiting while
    /// its own load of that bundle runs. Only a bundle the mod does not hold is loaded by the host, through the mod's
    /// package, and let go of once nothing has used it for <see cref="OwnLoadIdleMicros"/>, or at once when the mod starts
    /// its own load of it (<see cref="YieldBundle"/>), so however often players spawn from it the mod's load is never
    /// refused. Loads are polled, never awaited, and a build that takes longer than
    /// <see cref="SpawnAuthorityRules.HostBuildTimeoutMicros"/> is refused as unbuildable.
    /// </summary>
    internal sealed partial class NetworkWorldProxies
    {
        /// <summary>How long the host keeps a bundle or prefab it loaded itself after the last spawn used it.</summary>
        internal const long OwnLoadIdleMicros = 2_000_000;

        /// <summary>The most bundles the host holds loads of its own for at once, as a joined player's replicas do.</summary>
        internal const int MaximumOwnBundleLoads = NetworkWorldClient.MaximumBundleLoads;

        /// <summary>The most prefabs the host holds loads of its own for at once.</summary>
        internal const int MaximumOwnPrefabLoads = NetworkWorldClient.MaximumPrefabLoads;

        private readonly Dictionary<string, OwnLoad<IAssetBundle>> ownBundles = new Dictionary<string, OwnLoad<IAssetBundle>>(StringComparer.Ordinal);
        private readonly Dictionary<string, OwnLoad<IPrefabAsset>> ownPrefabs = new Dictionary<string, OwnLoad<IPrefabAsset>>(StringComparer.Ordinal);
        private readonly List<string> idleScratch = new List<string>();
        private readonly List<string> yieldScratch = new List<string>();

        private enum BuildStep
        {
            Waiting,
            Ready,
            Failed
        }

        /// <summary>Gets how many bundles and prefabs the host holds loads of its own for.</summary>
        internal int OwnLoads => ownBundles.Count + ownPrefabs.Count;

        private void AdvanceBuilds(long now)
        {
            if (proxies.Count == 0) return;
            for (var index = 0; index < proxies.Count; index++)
            {
                var proxy = proxies[index];
                if (proxy.Spawns.Count == 0) continue;
                spawnScratch.Clear();
                foreach (var spawn in proxy.Spawns.Values)
                {
                    if (spawn.State == ProxySpawnState.Building && spawn.Handler == null) spawnScratch.Add(spawn);
                }

                foreach (var spawn in spawnScratch) AdvanceBuild(proxy, spawn, now);
                spawnScratch.Clear();
            }
        }

        /// <summary>Moves one asset spawn on: its prefab, then the spawn through the service that holds the prefab.</summary>
        private void AdvanceBuild(ProxyParticipant proxy, ProxySpawn spawn, long now)
        {
            if (spawn.State != ProxySpawnState.Building) return;
            if (now - spawn.StartedMicros > SpawnAuthorityRules.HostBuildTimeoutMicros)
            {
                Fail(proxy, spawn);
                return;
            }

            if (spawn.Prefab == null || !Alive(spawn.Prefab))
            {
                spawn.Prefab = null;
                var step = ResolvePrefab(spawn, now);
                if (step == BuildStep.Waiting) return;
                if (step == BuildStep.Failed)
                {
                    Fail(proxy, spawn);
                    return;
                }
            }

            OperationResult<ISpawnedEntity>? made;
            try
            {
                made = spawn.Assets!.Spawn(new AssetSpawnRequest(spawn.Prefab!, spawn.Transform));
            }
            catch (Exception)
            {
                made = null;
            }

            if (made == null || !made.TryGetValue(out var spawned) || spawned == null || !spawned.IsAlive)
            {
                Fail(proxy, spawn);
                return;
            }

            Adopt(proxy, spawn, spawned, spawned);
        }

        /// <summary>Finds or loads the prefab a spawn names, the mod's own handles first.</summary>
        private BuildStep ResolvePrefab(ProxySpawn spawn, long now)
        {
            var mod = spawn.ModId;
            var bundlePath = spawn.BundlePath!;
            var assetName = spawn.AssetName!;
            var loads = replication.AssetLoads;
            if (loads.TryGetPrefab(mod, bundlePath, assetName, out var modAssets, out var modPrefab, out _))
            {
                spawn.Assets = modAssets;
                spawn.Prefab = modPrefab;
                return BuildStep.Ready;
            }

            var prefabKey = PrefabKey(spawn);
            if (ownPrefabs.TryGetValue(prefabKey, out var ownPrefab))
            {
                Touch(ownPrefab, now);
                if (!ownPrefab.Load.IsSettled) return BuildStep.Waiting;
                var loaded = ownPrefab.Load.Value;
                if (loaded != null && Alive(loaded))
                {
                    spawn.Assets = ownPrefab.Assets;
                    spawn.Prefab = loaded;
                    return BuildStep.Ready;
                }

                ReleaseOwnPrefab(prefabKey);
                return BuildStep.Failed;
            }

            IAssetService bundleAssets;
            IAssetBundle? bundle;
            OwnLoad<IAssetBundle>? parent = null;
            if (loads.TryGetBundle(mod, bundlePath, out var modBundleAssets, out var modBundle, out _))
            {
                bundleAssets = modBundleAssets;
                bundle = modBundle;
            }
            else if (loads.IsLoadingBundle(mod, bundlePath))
            {
                // The mod's own load of the bundle lands first; a second load of it would be refused.
                return BuildStep.Waiting;
            }
            else
            {
                var bundleKey = BundleKey(spawn);
                if (!ownBundles.TryGetValue(bundleKey, out parent))
                {
                    var package = FindAssets(mod);
                    if (package == null || ownBundles.Count >= MaximumOwnBundleLoads) return BuildStep.Failed;
                    parent = new OwnLoad<IAssetBundle>(WorldAssetLoad<IAssetBundle>.Start(token => package.LoadBundleAsync(bundlePath, token)), package, null, now);
                    ownBundles.Add(bundleKey, parent);
                }

                Touch(parent, now);
                if (!parent.Load.IsSettled) return BuildStep.Waiting;
                bundle = parent.Load.Value;
                bundleAssets = parent.Assets;
                if (bundle == null || !Alive(bundle))
                {
                    if (parent.Derived == 0) ReleaseOwnBundle(bundleKey);
                    return BuildStep.Failed;
                }
            }

            if (ownPrefabs.Count >= MaximumOwnPrefabLoads) return BuildStep.Failed;
            var from = bundle;
            var started = new OwnLoad<IPrefabAsset>(WorldAssetLoad<IPrefabAsset>.Start(token => bundleAssets.LoadPrefabAsync(from, assetName, token)), bundleAssets, parent, now);
            if (parent != null) parent.Derived++;
            ownPrefabs.Add(prefabKey, started);
            if (!started.Load.IsSettled) return BuildStep.Waiting;
            var ready = started.Load.Value;
            if (ready == null || !Alive(ready))
            {
                ReleaseOwnPrefab(prefabKey);
                return BuildStep.Failed;
            }

            spawn.Assets = started.Assets;
            spawn.Prefab = ready;
            return BuildStep.Ready;
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

        /// <summary>Lets go of the host's own loads that no spawn used for <see cref="OwnLoadIdleMicros"/>, prefabs before their bundles.</summary>
        private void ReleaseIdleLoads(long now)
        {
            if (ownPrefabs.Count == 0 && ownBundles.Count == 0) return;
            idleScratch.Clear();
            foreach (var pair in ownPrefabs)
            {
                if (pair.Value.Load.IsSettled && now - pair.Value.LastUsedMicros >= OwnLoadIdleMicros) idleScratch.Add(pair.Key);
            }

            foreach (var key in idleScratch) ReleaseOwnPrefab(key);
            idleScratch.Clear();
            foreach (var pair in ownBundles)
            {
                if (pair.Value.Derived == 0 && pair.Value.Load.IsSettled && now - pair.Value.LastUsedMicros >= OwnLoadIdleMicros) idleScratch.Add(pair.Key);
            }

            foreach (var key in idleScratch) ReleaseOwnBundle(key);
            idleScratch.Clear();
        }

        /// <summary>
        /// The mod <paramref name="modId"/> starts loading <paramref name="bundlePath"/> itself: the host's own load of it gives
        /// way first, with every prefab loaded from it, since the game refuses a second load of a bundle that is loaded already.
        /// What was made from them stands on, and builds still under way wait for the mod's load and use its handles.
        /// </summary>
        internal void YieldBundle(string modId, string bundlePath)
        {
            if (!IsActive || ownBundles.Count == 0) return;
            var prefix = BundleKey(modId, string.Empty);
            yieldScratch.Clear();
            foreach (var key in ownBundles.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal) && SpawnAuthorityRules.SameBundleFile(key.Substring(prefix.Length), bundlePath)) yieldScratch.Add(key);
            }

            foreach (var key in yieldScratch)
            {
                var bundle = ownBundles[key];
                idleScratch.Clear();
                foreach (var pair in ownPrefabs)
                {
                    if (ReferenceEquals(pair.Value.Parent, bundle)) idleScratch.Add(pair.Key);
                }

                foreach (var prefabKey in idleScratch) ReleaseOwnPrefab(prefabKey);
                idleScratch.Clear();
                ReleaseOwnBundle(key);
                counters.AssetLoadsYielded++;
            }

            yieldScratch.Clear();
        }

        private void ReleaseOwnPrefab(string key)
        {
            if (!ownPrefabs.TryGetValue(key, out var load)) return;
            ownPrefabs.Remove(key);
            load.Load.Release();
            if (load.Parent != null) load.Parent.Derived--;
        }

        private void ReleaseOwnBundle(string key)
        {
            if (!ownBundles.TryGetValue(key, out var load)) return;
            ownBundles.Remove(key);
            load.Load.Release();
        }

        /// <summary>Hosting stopped: every load of the host's own goes, those still running when they land.</summary>
        private void ReleaseOwnLoads()
        {
            foreach (var load in ownPrefabs.Values) load.Load.Release();
            foreach (var load in ownBundles.Values) load.Load.Release();
            ownPrefabs.Clear();
            ownBundles.Clear();
        }

        private static void Touch<T>(OwnLoad<T> load, long now) where T : class, IDisposable
        {
            load.LastUsedMicros = now;
            if (load.Parent != null) load.Parent.LastUsedMicros = now;
        }

        private static string BundleKey(ProxySpawn spawn) => BundleKey(spawn.ModId, spawn.BundlePath!);

        private static string BundleKey(string modId, string bundlePath) => modId.ToLowerInvariant() + "\n" + bundlePath;

        private static string PrefabKey(ProxySpawn spawn) => BundleKey(spawn) + "\n" + spawn.AssetName;

        private static bool Alive(IPrefabAsset prefab)
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

        private static bool Alive(IAssetBundle bundle)
        {
            try
            {
                return bundle.IsAlive;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>A bundle or prefab the host loaded itself: the load, the service it came from, and when a spawn last used it.</summary>
        private sealed class OwnLoad<T> where T : class, IDisposable
        {
            internal OwnLoad(WorldAssetLoad<T> load, IAssetService assets, OwnLoad<IAssetBundle>? parent, long now)
            {
                Load = load;
                Assets = assets;
                Parent = parent;
                LastUsedMicros = now;
            }

            internal WorldAssetLoad<T> Load { get; }

            internal IAssetService Assets { get; }

            /// <summary>Gets the host's own bundle load a prefab came from, or null when it came from the mod's bundle.</summary>
            internal OwnLoad<IAssetBundle>? Parent { get; }

            /// <summary>Gets or sets how many prefab loads of the host's own came from this bundle and are still held.</summary>
            internal int Derived { get; set; }

            internal long LastUsedMicros { get; set; }
        }
    }
}
