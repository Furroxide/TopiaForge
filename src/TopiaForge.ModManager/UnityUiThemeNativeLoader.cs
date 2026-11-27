using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace TopiaForge.ModManager
{
    internal sealed class UnityUiThemeNativeLoader : IUiThemeNativeLoader
    {
        private readonly IHostDispatcher dispatcher;
        private readonly AdmittedUiCatalogue catalogue;
        private readonly Dictionary<UiAdmittedBundle, UnityUiThemeBundle> resident =
            new Dictionary<UiAdmittedBundle, UnityUiThemeBundle>();
        private readonly SemaphoreSlim staging = new SemaphoreSlim(1, 1);

        internal UnityUiThemeNativeLoader(IHostDispatcher dispatcher, AdmittedUiCatalogue catalogue)
        { this.dispatcher = dispatcher; this.catalogue = catalogue; }

        public async Task<OperationResult<UiThemeRegistration>> LoadAsync(UiThemeDescriptor descriptor, OwnerModLifetime owner)
        {
            // Packages can reference one native bundle from different descriptors. Only one transaction opens or
            // stages assets in that package at a time. Failure releases this gate after native drain and cleanup.
            try { await staging.WaitAsync(owner.StoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return Cancelled<UiThemeRegistration>(); }
            var gate = new StagingLease(staging);
            try { await dispatcher.InvokeAsync(() => owner.Track(gate)).ConfigureAwait(false); }
            catch (ObjectDisposedException) when (owner.IsStopping) { return Cancelled<UiThemeRegistration>(); }
            catch { gate.Dispose(); throw; }

            var bundles = new Dictionary<UiAdmittedBundle, LoadContext>();
            var images = new Dictionary<string, object>(StringComparer.Ordinal);
            var fontAssets = new Dictionary<string, TMP_FontAsset>(StringComparer.Ordinal);
            foreach (var declared in descriptor.Images)
            {
                var context = await GetBundleAsync(declared, owner, bundles).ConfigureAwait(false);
                if (!context.TryGetValue(out var bundle)) return Failure<UiThemeRegistration, LoadContext>(context);
                var result = await LoadEntryAsync(owner, "$.images", bundle,
                    () => bundle.Metadata.BeginSprite(declared), UnityUiBundleMetadata.ReadSprite,
                    sprite => bundle.Metadata.CheckSprite(declared, sprite)).ConfigureAwait(false);
                if (!result.TryGetValue(out var image)) return Failure<UiThemeRegistration, Sprite>(result);
                images.Add(declared.Id, image);
            }

            foreach (var declared in descriptor.Fonts)
            {
                var context = await GetBundleAsync(declared, owner, bundles).ConfigureAwait(false);
                if (!context.TryGetValue(out var bundle)) return Failure<UiThemeRegistration, LoadContext>(context);
                var result = await LoadEntryAsync(owner, "$.fonts", bundle,
                    () => bundle.Metadata.BeginFont(declared), request => request.asset as TMP_FontAsset,
                    font => bundle.Metadata.CheckFont(declared, font)).ConfigureAwait(false);
                if (!result.TryGetValue(out var font)) return Failure<UiThemeRegistration, TMP_FontAsset>(result);
                fontAssets.Add(declared.Id, font);
            }

            return await dispatcher.InvokeAsync(() =>
            {
                if (owner.IsStopping) return Cancelled<UiThemeRegistration>();
                var textures = new Dictionary<int, long>();
                foreach (var bundle in bundles.Values)
                    foreach (var texture in bundle.Metadata.ResidentTextures)
                        if (!textures.TryGetValue(texture.Key, out var bytes) || bytes < texture.Value)
                            textures[texture.Key] = texture.Value;
                var fonts = ResolveFonts(descriptor, fontAssets, owner);
                return OperationResult<UiThemeRegistration>.Success(new UiThemeRegistration(images, fonts, textures, gate.Dispose));
            }).ConfigureAwait(false);
        }

        private async Task<OperationResult<LoadContext>> GetBundleAsync(UiThemeAsset asset, OwnerModLifetime owner,
            Dictionary<UiAdmittedBundle, LoadContext> current)
        {
            if (owner.IsStopping) return Cancelled<LoadContext>();
            if (!catalogue.TryGetBundle(asset, out var admitted))
                return OperationResult<LoadContext>.Failure(ModErrorCode.InvalidArgument, "$.ui.bundlePath: The resource has no admitted bundle receipt.");
            if (current.TryGetValue(admitted, out var staged)) return OperationResult<LoadContext>.Success(staged);

            var native = await dispatcher.InvokeAsync(() =>
            {
                if (owner.IsStopping || !resident.TryGetValue(admitted, out var existing)) return null;
                owner.Track(existing.Lease());
                return existing;
            }).ConfigureAwait(false);
            if (native == null)
            {
                if (owner.IsStopping) return Cancelled<LoadContext>();
                var file = await Task.Run(admitted.ReadVerifiedBytes).ConfigureAwait(false);
                if (!file.TryGetValue(out var bytes)) return Failure<LoadContext, byte[]>(file);
                var loaded = await StartNativeAsync(owner, "$.ui.bundlePath",
                    () => new UnityUiThemeBundleRequest(bytes)).ConfigureAwait(false);
                if (!loaded.TryGetValue(out var opened)) return Failure<LoadContext, UnityUiThemeBundleResult>(loaded);
                native = await dispatcher.InvokeAsync(() =>
                {
                    if (owner.IsStopping) return null;
                    var added = new UnityUiThemeBundle(opened.Transfer(), () => resident.Remove(admitted));
                    resident.Add(admitted, added);
                    owner.Track(added.Lease());
                    return added;
                }).ConfigureAwait(false);
                if (native == null) return Cancelled<LoadContext>();
            }

            return await dispatcher.InvokeAsync(() =>
            {
                if (owner.IsStopping) return Cancelled<LoadContext>();
                var opened = new LoadContext(native, new UnityUiBundleMetadata(admitted, native.Native));
                current.Add(admitted, opened);
                return OperationResult<LoadContext>.Success(opened);
            }).ConfigureAwait(false);
        }

        private async Task<OperationResult<T>> LoadEntryAsync<T>(OwnerModLifetime owner, string field,
            LoadContext bundle, Func<AssetBundleRequest?> begin, Func<AssetBundleRequest, T?> read,
            Func<T?, OperationResult<bool>> check) where T : UnityObject
        {
            var loaded = await StartNativeAsync(owner, field,
                () => new UnityUiThemeEntryRequest<T>(bundle.Bundle, begin, read)).ConfigureAwait(false);
            if (!loaded.TryGetValue(out var entry)) return Failure<T, UnityUiThemeEntryResult<T>>(loaded);
            return await dispatcher.InvokeAsync(() =>
            {
                if (owner.IsStopping) return Cancelled<T>();
                var audited = check(entry.Value);
                return audited.Succeeded ? OperationResult<T>.Success(entry.Value!) : Failure<T, bool>(audited);
            }).ConfigureAwait(false);
        }

        private async Task<OperationResult<T>> StartNativeAsync<T>(OwnerModLifetime owner, string field,
            Func<IAssetNativeRequest<T>> create) where T : class, IDisposable
        {
            var pending = await dispatcher.InvokeAsync(() => AssetNativeOperation<T>.Start(owner, field, create, default)).ConfigureAwait(false);
            var result = await pending.ConfigureAwait(false);
            // The underlying general asset API can report an engine exception. Keep UI errors bounded and path-free.
            return result.ErrorCode == ModErrorCode.External
                ? OperationResult<T>.Failure(ModErrorCode.External, field + ": The native UI asset request failed.")
                : result;
        }

        private static Dictionary<UiTextStyle, object> ResolveFonts(UiThemeDescriptor descriptor,
            Dictionary<string, TMP_FontAsset> declared, OwnerModLifetime owner)
        {
            var fonts = new Dictionary<UiTextStyle, object>();
            if (declared.Count == 0) return fonts;
            var variants = new Dictionary<(TMP_FontAsset, TMP_FontAsset?), TMP_FontAsset>();
            for (var index = 0; index <= (int)UiTextStyle.Mono; index++)
            {
                var role = (UiTextStyle)index;
                if (!declared.TryGetValue(descriptor.GetText(role).Font, out var source)) continue;
                var face = TopiaForgeFonts.For(KitRole(role));
                var key = (source, face);
                if (!variants.TryGetValue(key, out var selected))
                {
                    selected = UnityObject.Instantiate(source);
                    owner.Defer(() => UnityObject.Destroy(selected));
                    var fallback = new List<TMP_FontAsset>(2);
                    if (face != null) fallback.Add(face);
                    if (TopiaForgeFonts.Body is { } body && body != null && body != face) fallback.Add(body);
                    // Preserve the admitted static atlases, but do not follow the source font's alternate asset references.
                    selected.fallbackFontAssetTable = fallback;
                    if (selected.fontWeightTable is { } weights)
                        Array.Clear(weights, 0, weights.Length);
                    variants.Add(key, selected);
                }
                fonts.Add(role, selected);
            }
            return fonts;
        }

        private static TopiaForgeTextStyle KitRole(UiTextStyle role) => role switch
        {
            UiTextStyle.Heading => TopiaForgeTextStyle.Heading,
            UiTextStyle.Caption => TopiaForgeTextStyle.Caption,
            UiTextStyle.Banner => TopiaForgeTextStyle.Banner,
            UiTextStyle.Display => TopiaForgeTextStyle.Display,
            UiTextStyle.Title => TopiaForgeTextStyle.Title,
            UiTextStyle.Label => TopiaForgeTextStyle.Label,
            UiTextStyle.Numeral => TopiaForgeTextStyle.Numeral,
            UiTextStyle.Mono => TopiaForgeTextStyle.Mono,
            _ => TopiaForgeTextStyle.Body,
        };

        private static OperationResult<T> Cancelled<T>() where T : notnull =>
            OperationResult<T>.Failure(ModErrorCode.Cancelled, "$.ui: The UI resource acquisition was cancelled.");

        private static OperationResult<T> Failure<T, TSource>(OperationResult<TSource> source)
            where T : notnull where TSource : notnull => OperationResult<T>.Failure(source.ErrorCode, source.ErrorMessage);

        private sealed class LoadContext
        {
            internal LoadContext(UnityUiThemeBundle bundle, UnityUiBundleMetadata metadata) { Bundle = bundle; Metadata = metadata; }
            internal readonly UnityUiThemeBundle Bundle;
            internal readonly UnityUiBundleMetadata Metadata;
        }

        private sealed class StagingLease : IDisposable
        {
            private SemaphoreSlim? gate;
            internal StagingLease(SemaphoreSlim gate) { this.gate = gate; }
            public void Dispose() => Interlocked.Exchange(ref gate, null)?.Release();
        }
    }
}
