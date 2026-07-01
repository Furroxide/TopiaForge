using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Robotopia.Mods;

namespace Robotopia.Assets
{
    internal sealed class AssetBundleService : IAssetBundleService, IDisposable
    {
        private readonly IModLogger logger;
        private readonly Dictionary<string, AssetBundleHandle> cachedHandles = new Dictionary<string, AssetBundleHandle>(StringComparer.OrdinalIgnoreCase);
        private readonly List<AssetBundleHandle> transientHandles = new List<AssetBundleHandle>();

        public AssetBundleService(IModLogger logger)
        {
            this.logger = logger;
        }

        public AssetBundleLoadResult LoadBundle(AssetBundleLoadRequest request)
        {
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

                UnloadHandle(existing, unloadAllLoadedObjects: false);
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
            return TryGetNativeBundle(bundle, out var nativeBundle, out _)
                ? nativeBundle.GetAllAssetNames()
                : Array.Empty<string>();
        }

        public void UnloadOwner(string ownerModId, bool unloadAllLoadedObjects = false)
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                return;
            }

            foreach (var handle in cachedHandles.Values.Concat(transientHandles).ToList())
            {
                handle.RemoveOwner(ownerModId);
                if (handle.OwnerModIds.Count == 0)
                {
                    UnloadHandle(handle, unloadAllLoadedObjects);
                    cachedHandles.Remove(handle.FullPath);
                    transientHandles.Remove(handle);
                }
            }
        }

        public void Dispose()
        {
            foreach (var handle in cachedHandles.Values.Concat(transientHandles).ToList())
            {
                UnloadHandle(handle, unloadAllLoadedObjects: true);
            }

            cachedHandles.Clear();
            transientHandles.Clear();
        }

        private static bool TryResolvePackagePath(string packagePath, string relativePath, out string fullPath, out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(packagePath))
            {
                error = "Package path is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                error = "AssetBundle relative path is required.";
                return false;
            }

            if (Path.IsPathRooted(relativePath))
            {
                error = "AssetBundle path must be package-relative.";
                return false;
            }

            var root = Path.GetFullPath(packagePath);
            fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "AssetBundle path escapes the mod package directory.";
                fullPath = string.Empty;
                return false;
            }

            return true;
        }

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

            error = "AssetBundle handle was not created by Robotopia.Assets.";
            return false;
        }

        private static void UnloadHandle(AssetBundleHandle handle, bool unloadAllLoadedObjects)
        {
            if (!handle.IsLoaded)
            {
                return;
            }

            handle.NativeBundle.Unload(unloadAllLoadedObjects);
            handle.MarkUnloaded();
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
            public bool IsLoaded { get; private set; } = true;

            public void AddOwner(string ownerModId)
            {
                owners.Add(ownerModId);
            }

            public void RemoveOwner(string ownerModId)
            {
                owners.Remove(ownerModId);
            }

            public void MarkUnloaded()
            {
                IsLoaded = false;
                owners.Clear();
            }
        }
    }
}
