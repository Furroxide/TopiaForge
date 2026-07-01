using System;
using System.Collections.Generic;

namespace Robotopia.Mods
{
    public interface IAssetBundleService
    {
        AssetBundleLoadResult LoadBundle(AssetBundleLoadRequest request);
        AssetLoadResult LoadAsset(IAssetBundleHandle bundle, string assetName, Type assetType);
        AssetLoadResult<T> LoadAsset<T>(IAssetBundleHandle bundle, string assetName) where T : class;
        SpawnAssetResult SpawnAsset(object prefab);
        SpawnAssetResult<T> SpawnAsset<T>(T prefab) where T : class;
        IReadOnlyList<string> GetAllAssetNames(IAssetBundleHandle bundle);
        void UnloadOwner(string ownerModId, bool unloadAllLoadedObjects = false);
    }

    public interface IAssetBundleHandle
    {
        string FullPath { get; }
        object Bundle { get; }
        IReadOnlyList<string> OwnerModIds { get; }
        bool IsLoaded { get; }
    }

    public sealed class AssetBundleLoadOptions
    {
        public static AssetBundleLoadOptions Default => new AssetBundleLoadOptions();

        public bool Cache { get; set; } = true;
        public bool Reload { get; set; }
    }

    public sealed class AssetBundleLoadRequest
    {
        public AssetBundleLoadRequest(string ownerModId, string packagePath, string relativePath, AssetBundleLoadOptions? options = null)
        {
            OwnerModId = ownerModId ?? string.Empty;
            PackagePath = packagePath ?? string.Empty;
            RelativePath = relativePath ?? string.Empty;
            Options = options ?? AssetBundleLoadOptions.Default;
        }

        public string OwnerModId { get; }
        public string PackagePath { get; }
        public string RelativePath { get; }
        public AssetBundleLoadOptions Options { get; }
    }

    public sealed class AssetBundleLoadResult
    {
        private AssetBundleLoadResult(bool ok, IAssetBundleHandle? bundle, string error)
        {
            Ok = ok;
            Bundle = bundle;
            Error = error;
        }

        public bool Ok { get; }
        public IAssetBundleHandle? Bundle { get; }
        public string Error { get; }

        public static AssetBundleLoadResult Success(IAssetBundleHandle bundle)
        {
            return new AssetBundleLoadResult(true, bundle, string.Empty);
        }

        public static AssetBundleLoadResult Fail(string error)
        {
            return new AssetBundleLoadResult(false, null, error ?? string.Empty);
        }
    }

    public sealed class AssetLoadResult
    {
        private AssetLoadResult(bool ok, object? asset, string error)
        {
            Ok = ok;
            Asset = asset;
            Error = error;
        }

        public bool Ok { get; }
        public object? Asset { get; }
        public string Error { get; }

        public static AssetLoadResult Success(object asset)
        {
            return new AssetLoadResult(true, asset, string.Empty);
        }

        public static AssetLoadResult Fail(string error)
        {
            return new AssetLoadResult(false, null, error ?? string.Empty);
        }
    }

    public sealed class AssetLoadResult<T> where T : class
    {
        private AssetLoadResult(bool ok, T? asset, string error)
        {
            Ok = ok;
            Asset = asset;
            Error = error;
        }

        public bool Ok { get; }
        public T? Asset { get; }
        public string Error { get; }

        public static AssetLoadResult<T> Success(T asset)
        {
            return new AssetLoadResult<T>(true, asset, string.Empty);
        }

        public static AssetLoadResult<T> Fail(string error)
        {
            return new AssetLoadResult<T>(false, null, error ?? string.Empty);
        }
    }

    public sealed class SpawnAssetResult
    {
        private SpawnAssetResult(bool ok, object? instance, string error)
        {
            Ok = ok;
            Instance = instance;
            Error = error;
        }

        public bool Ok { get; }
        public object? Instance { get; }
        public string Error { get; }

        public static SpawnAssetResult Success(object instance)
        {
            return new SpawnAssetResult(true, instance, string.Empty);
        }

        public static SpawnAssetResult Fail(string error)
        {
            return new SpawnAssetResult(false, null, error ?? string.Empty);
        }
    }

    public sealed class SpawnAssetResult<T> where T : class
    {
        private SpawnAssetResult(bool ok, T? instance, string error)
        {
            Ok = ok;
            Instance = instance;
            Error = error;
        }

        public bool Ok { get; }
        public T? Instance { get; }
        public string Error { get; }

        public static SpawnAssetResult<T> Success(T instance)
        {
            return new SpawnAssetResult<T>(true, instance, string.Empty);
        }

        public static SpawnAssetResult<T> Fail(string error)
        {
            return new SpawnAssetResult<T>(false, null, error ?? string.Empty);
        }
    }
}
