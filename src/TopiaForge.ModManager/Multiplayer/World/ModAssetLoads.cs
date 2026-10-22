using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The bundles and prefabs each mod's own code loaded, as its contexts' network decorators saw the loads go by (WS3.7).
    /// A listen host makes a joined player's asset spawn from the mod's own handles first: the game refuses a second load
    /// of a bundle that is loaded already, so a load of its own would fail whenever the mod holds the bundle, and would
    /// keep the mod from loading it while it held it. Entries are checked when read, never awaited, and end with the
    /// context that loaded them. Main thread only.
    /// </summary>
    internal sealed class ModAssetLoads
    {
        /// <summary>The most loads one mod keeps listed; the oldest goes past it, and a dead one goes first.</summary>
        internal const int MaximumLoadsPerMod = 256;

        private readonly Dictionary<string, List<Load>> byMod = new Dictionary<string, List<Load>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets how many loads are listed, dead ones not yet pruned included.</summary>
        internal int Count
        {
            get
            {
                var count = 0;
                foreach (var loads in byMod.Values) count += loads.Count;
                return count;
            }
        }

        /// <summary>Lists a bundle load <paramref name="modId"/>'s context, <paramref name="owner"/>, started through <paramref name="assets"/>.</summary>
        internal void BundleLoading(string modId, object owner, IAssetService assets, string bundlePath, Task<OperationResult<IAssetBundle>> task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            Add(modId, new Load(owner, assets, bundlePath, null, task, null));
        }

        /// <summary>Lists a prefab load <paramref name="modId"/>'s context started from one of its bundles.</summary>
        internal void PrefabLoading(string modId, object owner, IAssetService assets, string bundlePath, string assetName, Task<OperationResult<IPrefabAsset>> task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            Add(modId, new Load(owner, assets, bundlePath, assetName, null, task));
        }

        /// <summary>Finds a live prefab the mod loaded from <paramref name="bundlePath"/>, and the service that spawns it.</summary>
        internal bool TryGetPrefab(string modId, string bundlePath, string assetName, out IAssetService assets, out IPrefabAsset prefab)
        {
            assets = null!;
            prefab = null!;
            if (!byMod.TryGetValue(modId, out var loads)) return false;
            for (var index = loads.Count - 1; index >= 0; index--)
            {
                var load = loads[index];
                if (load.Prefab == null || !string.Equals(load.BundlePath, bundlePath, StringComparison.Ordinal) ||
                    !string.Equals(load.AssetName, assetName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryResult(load.Prefab, out var found) || !Alive(found)) continue;
                assets = load.Assets;
                prefab = found;
                return true;
            }

            return false;
        }

        /// <summary>Finds a live bundle the mod loaded, and the service that loads prefabs from it.</summary>
        internal bool TryGetBundle(string modId, string bundlePath, out IAssetService assets, out IAssetBundle bundle)
        {
            assets = null!;
            bundle = null!;
            if (!byMod.TryGetValue(modId, out var loads)) return false;
            for (var index = loads.Count - 1; index >= 0; index--)
            {
                var load = loads[index];
                if (load.Bundle == null || !string.Equals(load.BundlePath, bundlePath, StringComparison.Ordinal)) continue;
                if (!TryResult(load.Bundle, out var found) || !Alive(found)) continue;
                assets = load.Assets;
                bundle = found;
                return true;
            }

            return false;
        }

        /// <summary>Whether the mod's own load of <paramref name="bundlePath"/> is still running, so its bundle is worth waiting for.</summary>
        internal bool IsLoadingBundle(string modId, string bundlePath)
        {
            if (!byMod.TryGetValue(modId, out var loads)) return false;
            foreach (var load in loads)
            {
                if (load.Bundle != null && !load.Bundle.IsCompleted && string.Equals(load.BundlePath, bundlePath, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>Forgets what <paramref name="owner"/>'s context loaded: its lifetime ended, and its handles with it.</summary>
        internal void ReleaseOwner(object owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            foreach (var loads in byMod.Values) loads.RemoveAll(load => ReferenceEquals(load.Owner, owner));
        }

        private void Add(string modId, Load load)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A mod id is required.", nameof(modId));
            if (!byMod.TryGetValue(modId, out var loads))
            {
                loads = new List<Load>();
                byMod.Add(modId, loads);
            }

            if (loads.Count >= MaximumLoadsPerMod) loads.RemoveAll(IsDead);
            if (loads.Count >= MaximumLoadsPerMod) loads.RemoveAt(0);
            loads.Add(load);
        }

        private static bool IsDead(Load load)
        {
            if (load.Bundle != null) return load.Bundle.IsCompleted && (!TryResult(load.Bundle, out var bundle) || !Alive(bundle));
            return load.Prefab!.IsCompleted && (!TryResult(load.Prefab, out var prefab) || !Alive(prefab));
        }

        private static bool TryResult<T>(Task<OperationResult<T>> task, out T value) where T : class
        {
            value = null!;
            if (task.Status != TaskStatus.RanToCompletion) return false;
            var result = task.Result;
            return result != null && result.TryGetValue(out value!) && value != null;
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

        private sealed class Load
        {
            internal Load(object owner, IAssetService assets, string bundlePath, string? assetName, Task<OperationResult<IAssetBundle>>? bundle,
                Task<OperationResult<IPrefabAsset>>? prefab)
            {
                Owner = owner ?? throw new ArgumentNullException(nameof(owner));
                Assets = assets ?? throw new ArgumentNullException(nameof(assets));
                BundlePath = bundlePath ?? throw new ArgumentNullException(nameof(bundlePath));
                AssetName = assetName;
                Bundle = bundle;
                Prefab = prefab;
            }

            internal object Owner { get; }

            internal IAssetService Assets { get; }

            internal string BundlePath { get; }

            internal string? AssetName { get; }

            internal Task<OperationResult<IAssetBundle>>? Bundle { get; }

            internal Task<OperationResult<IPrefabAsset>>? Prefab { get; }
        }
    }
}
