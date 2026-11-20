using System;
using TopiaForge.Mods;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace TopiaForge.ModManager
{
    internal sealed class UnityUiThemeBundleRequest : IAssetNativeRequest<UnityUiThemeBundleResult>
    {
        private byte[]? verifiedBytes;
        private AssetBundleCreateRequest? request;

        internal UnityUiThemeBundleRequest(byte[] verifiedBytes) { this.verifiedBytes = verifiedBytes; }

        public bool HasStarted => request != null;
        public bool IsDone => request!.isDone;

        public void Start() => request = AssetBundle.LoadFromMemoryAsync(verifiedBytes!) ??
            throw new InvalidOperationException("The engine did not return a UI asset bundle request.");

        public OperationResult<UnityUiThemeBundleResult> ReadResult()
        {
            var native = request!.assetBundle;
            return native == null
                ? OperationResult<UnityUiThemeBundleResult>.Failure(ModErrorCode.External, "$.ui.bundlePath: The UI bundle could not be opened.")
                : OperationResult<UnityUiThemeBundleResult>.Success(new UnityUiThemeBundleResult(native));
        }

        public void Dispose()
        {
            request = null;
            verifiedBytes = null;
        }
    }

    internal sealed class UnityUiThemeBundleResult : IDisposable
    {
        private AssetBundle? bundle;

        internal UnityUiThemeBundleResult(AssetBundle bundle) { this.bundle = bundle; }

        internal AssetBundle Transfer()
        {
            var result = bundle ?? throw new ObjectDisposedException(nameof(UnityUiThemeBundleResult));
            bundle = null;
            return result;
        }

        public void Dispose()
        {
            var current = bundle;
            bundle = null;
            if (current != null) current.Unload(true);
        }
    }

    internal sealed class UnityUiThemeEntryRequest<T> : IAssetNativeRequest<UnityUiThemeEntryResult<T>> where T : UnityObject
    {
        private readonly UnityUiThemeBundle bundle;
        private readonly Func<AssetBundleRequest?> begin;
        private readonly Func<AssetBundleRequest, T?> read;
        private AssetBundleRequest? request;
        private IDisposable? pin;

        internal UnityUiThemeEntryRequest(UnityUiThemeBundle bundle, Func<AssetBundleRequest?> begin, Func<AssetBundleRequest, T?> read)
        { this.bundle = bundle; this.begin = begin; this.read = read; }

        public bool HasStarted { get; private set; }
        public bool IsDone => request == null || request.isDone;

        public void Start()
        {
            pin = bundle.PinRequest();
            request = begin();
            HasStarted = true;
        }

        public OperationResult<UnityUiThemeEntryResult<T>> ReadResult()
        {
            if (request == null)
                return OperationResult<UnityUiThemeEntryResult<T>>.Failure(ModErrorCode.InvalidArgument,
                    "$.ui: The admitted resource is absent from its UI bundle.");
            var value = read(request);
            // Even a request which resolved more than one sprite must unload the results when rejected.
            var lease = bundle.LeaseAssets(request.allAssets ?? Array.Empty<UnityObject>(), value);
            return OperationResult<UnityUiThemeEntryResult<T>>.Success(new UnityUiThemeEntryResult<T>(value, lease));
        }

        public void Dispose()
        {
            request = null;
            pin?.Dispose();
            pin = null;
        }
    }

    internal sealed class UnityUiThemeEntryResult<T> : IDisposable where T : UnityObject
    {
        private IDisposable? lease;
        internal UnityUiThemeEntryResult(T? value, IDisposable lease) { Value = value; this.lease = lease; }
        internal T? Value { get; }

        public void Dispose()
        {
            var current = lease;
            lease = null;
            current?.Dispose();
        }
    }
}
