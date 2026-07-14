using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.Assets
{
    internal sealed class AssetBundleService : IAssetBundleService, IDisposable
    {
        private readonly IModLogger logger;
        private readonly Dictionary<string, AssetBundleHandle> cachedHandles = new Dictionary<string, AssetBundleHandle>(StringComparer.Ordinal);
        private readonly List<AssetBundleHandle> transientHandles = new List<AssetBundleHandle>();
        private bool disposed;

        public AssetBundleService(IModLogger logger)
        {
            this.logger = logger;
        }

        public AssetBundleLoadResult LoadBundle(AssetBundleLoadRequest request)
        {
            if (disposed)
            {
                return AssetBundleLoadResult.Fail("AssetBundle service is disposed.");
            }

            if (request == null)
            {
                return AssetBundleLoadResult.Fail("AssetBundleLoadRequest is required.");
            }

            if (string.IsNullOrWhiteSpace(request.OwnerModId))
            {
                return AssetBundleLoadResult.Fail("Owner mod id is required.");
            }

            if (!TryResolvePackagePath(request.PackagePath, request.RelativePath, out var fullPath, out var error))
            {
                return AssetBundleLoadResult.Fail(error);
            }

            if (!File.Exists(fullPath))
            {
                return AssetBundleLoadResult.Fail("AssetBundle file was not found: " + request.RelativePath);
            }

            var options = request.Options ?? AssetBundleLoadOptions.Default;
            if (options.Cache && cachedHandles.TryGetValue(fullPath, out var existing) && existing.IsLoaded)
            {
                if (!options.Reload)
                {
                    existing.AddOwner(request.OwnerModId);
                    return AssetBundleLoadResult.Success(existing);
                }

                if (existing.HasOwnerOtherThan(request.OwnerModId))
                {
                    return AssetBundleLoadResult.Fail("Cannot reload a cached AssetBundle while another mod is using it.");
                }

                if (!TryUnloadHandle(existing, unloadAllLoadedObjects: false))
                {
                    return AssetBundleLoadResult.Fail("Could not unload the cached AssetBundle before reloading it.");
                }

                cachedHandles.Remove(fullPath);
            }

            try
            {
                var bundle = UnityEngine.AssetBundle.LoadFromFile(fullPath);
                if (bundle == null)
                {
                    return AssetBundleLoadResult.Fail("Unity returned null while loading AssetBundle: " + request.RelativePath);
                }

                var handle = new AssetBundleHandle(fullPath, bundle);
                handle.AddOwner(request.OwnerModId);
                if (options.Cache)
                {
                    cachedHandles[fullPath] = handle;
                }
                else
                {
                    transientHandles.Add(handle);
                }

                return AssetBundleLoadResult.Success(handle);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to load AssetBundle: " + request.RelativePath);
                return AssetBundleLoadResult.Fail(ex.Message);
            }
        }

        public AssetLoadResult LoadAsset(IAssetBundleHandle bundle, string assetName, Type assetType)
        {
            if (disposed)
            {
                return AssetLoadResult.Fail("AssetBundle service is disposed.");
            }

            if (!TryGetNativeBundle(bundle, out var nativeBundle, out var error))
            {
                return AssetLoadResult.Fail(error);
            }

            if (string.IsNullOrWhiteSpace(assetName))
            {
                return AssetLoadResult.Fail("Asset name is required.");
            }

            if (assetType == null)
            {
                return AssetLoadResult.Fail("Asset type is required.");
            }

            try
            {
                var asset = nativeBundle.LoadAsset(assetName, assetType);
                return asset == null
                    ? AssetLoadResult.Fail("Asset '" + assetName + "' was not found in bundle.")
                    : AssetLoadResult.Success(asset);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to load asset '" + assetName + "'.");
                return AssetLoadResult.Fail(ex.Message);
            }
        }

        public AssetLoadResult<T> LoadAsset<T>(IAssetBundleHandle bundle, string assetName) where T : class
        {
            var result = LoadAsset(bundle, assetName, typeof(T));
            if (!result.Ok)
            {
                return AssetLoadResult<T>.Fail(result.Error);
            }

            return result.Asset is T typed
                ? AssetLoadResult<T>.Success(typed)
                : AssetLoadResult<T>.Fail("Asset '" + assetName + "' is not a " + typeof(T).FullName + ".");
        }

        public SpawnAssetResult SpawnAsset(object prefab)
        {
            if (disposed)
            {
                return SpawnAssetResult.Fail("AssetBundle service is disposed.");
            }

            if (prefab == null)
            {
                return SpawnAssetResult.Fail("Prefab is required.");
            }

            if (!(prefab is UnityEngine.Object unityObject))
            {
                return SpawnAssetResult.Fail("Prefab must be a UnityEngine.Object.");
            }

            try
            {
                var instance = UnityEngine.Object.Instantiate(unityObject);
                return instance == null
                    ? SpawnAssetResult.Fail("Unity returned null while instantiating prefab.")
                    : SpawnAssetResult.Success(instance);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to instantiate prefab.");
                return SpawnAssetResult.Fail(ex.Message);
            }
        }

        public SpawnAssetResult<T> SpawnAsset<T>(T prefab) where T : class
        {
            var result = SpawnAsset((object)prefab);
            if (!result.Ok)
            {
                return SpawnAssetResult<T>.Fail(result.Error);
            }

            return result.Instance is T typed
                ? SpawnAssetResult<T>.Success(typed)
                : SpawnAssetResult<T>.Fail("Spawned asset is not a " + typeof(T).FullName + ".");
        }

        public IReadOnlyList<string> GetAllAssetNames(IAssetBundleHandle bundle)
        {
            if (disposed)
            {
                return Array.Empty<string>();
            }

            if (!TryGetNativeBundle(bundle, out var nativeBundle, out _))
            {
                return Array.Empty<string>();
            }

            try
            {
                return nativeBundle.GetAllAssetNames();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to list AssetBundle contents.");
                return Array.Empty<string>();
            }
        }

        public void UnloadOwner(string ownerModId, bool unloadAllLoadedObjects = false)
        {
            if (disposed || string.IsNullOrWhiteSpace(ownerModId))
            {
                return;
            }

            foreach (var handle in cachedHandles.Values.Concat(transientHandles).ToList())
            {
                handle.RemoveOwner(ownerModId);
                if (handle.OwnerCount == 0 && TryUnloadHandle(handle, unloadAllLoadedObjects))
                {
                    cachedHandles.Remove(handle.FullPath);
                    transientHandles.Remove(handle);
                }
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var handle in cachedHandles.Values.Concat(transientHandles).ToList())
            {
                TryUnloadHandle(handle, unloadAllLoadedObjects: true);
            }

            cachedHandles.Clear();
            transientHandles.Clear();
        }

        private static bool TryResolvePackagePath(string packagePath, string relativePath, out string fullPath, out string error) =>
            AssetBundlePathPolicy.TryResolve(packagePath, relativePath, out fullPath, out error);

        private static bool TryGetNativeBundle(IAssetBundleHandle handle, out UnityEngine.AssetBundle bundle, out string error)
        {
            bundle = null!;
            error = string.Empty;

            if (handle == null)
            {
                error = "AssetBundle handle is required.";
                return false;
            }

            if (!handle.IsLoaded)
            {
                error = "AssetBundle is unloaded.";
                return false;
            }

            if (handle.Bundle is UnityEngine.AssetBundle nativeBundle)
            {
                bundle = nativeBundle;
                return true;
            }

            error = "AssetBundle handle was not created by TopiaForge.Assets.";
            return false;
        }

        private bool TryUnloadHandle(AssetBundleHandle handle, bool unloadAllLoadedObjects)
        {
            if (!handle.IsLoaded)
            {
                return true;
            }

            try
            {
                handle.NativeBundle.Unload(unloadAllLoadedObjects);
                handle.MarkUnloaded();
                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to unload AssetBundle '" + Path.GetFileName(handle.FullPath) + "'.");
                return false;
            }
        }

        private sealed class AssetBundleHandle : IAssetBundleHandle
        {
            private readonly HashSet<string> owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public AssetBundleHandle(string fullPath, UnityEngine.AssetBundle bundle)
            {
                FullPath = fullPath;
                NativeBundle = bundle;
            }

            public string FullPath { get; }
            public UnityEngine.AssetBundle NativeBundle { get; }
            public object Bundle => NativeBundle;
            public IReadOnlyList<string> OwnerModIds => owners.OrderBy(o => o, StringComparer.OrdinalIgnoreCase).ToList();
            public bool IsLoaded => isLoaded && NativeBundle != null;
            public int OwnerCount => owners.Count;

            private bool isLoaded = true;

            public void AddOwner(string ownerModId)
            {
                owners.Add(ownerModId);
            }

            public void RemoveOwner(string ownerModId)
            {
                owners.Remove(ownerModId);
            }

            public bool HasOwnerOtherThan(string ownerModId)
            {
                foreach (var owner in owners)
                {
                    if (!owner.Equals(ownerModId, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            public void MarkUnloaded()
            {
                isLoaded = false;
                owners.Clear();
            }
        }
    }
}
