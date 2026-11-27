using System;
using System.Collections.Generic;
using TMPro;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.Profiling;

namespace TopiaForge.ModManager
{
    /// <summary>Starts exact-name typed requests and inspects the loaded Unity asset metadata.</summary>
    internal sealed class UnityUiBundleMetadata
    {
        private static readonly OperationResult<bool> WrongSpriteType =
            OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "$.images: Expected a Sprite with a texture.");
        private static readonly OperationResult<bool> WrongFontType =
            OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "$.fonts: Expected a static font asset with usable atlas pages.");

        private readonly UiAdmittedBundle bundle;
        private readonly AssetBundle native;
        private readonly HashSet<string> assetNames;
        private readonly UiBundleNativeAudit audit;

        internal UnityUiBundleMetadata(UiAdmittedBundle bundle, AssetBundle native)
        {
            this.bundle = bundle;
            this.native = native;
            assetNames = new HashSet<string>(StringComparer.Ordinal);
            var entries = native.GetAllAssetNames();
            for (var index = 0; index < entries.Length; index++) assetNames.Add(entries[index]);
            audit = new UiBundleNativeAudit(bundle);
        }

        internal IReadOnlyDictionary<int, long> ResidentTextures => audit.ResidentTextures;

        internal AssetBundleRequest? BeginSprite(UiThemeImage declared) =>
            audit.Belongs(declared) && assetNames.Contains(declared.AssetName) && bundle.Receipt.TryGetSprite(declared.AssetName, out _)
                ? native.LoadAssetWithSubAssetsAsync<Sprite>(declared.AssetName) : null;

        internal static Sprite? ReadSprite(AssetBundleRequest request)
        {
            var sprites = request.allAssets;
            return sprites != null && sprites.Length == 1 ? sprites[0] as Sprite : null;
        }

        internal AssetBundleRequest? BeginFont(UiThemeFont declared) =>
            audit.Belongs(declared) && assetNames.Contains(declared.AssetName) && bundle.Receipt.TryGetFont(declared.AssetName, out _)
                ? native.LoadAssetAsync<TMP_FontAsset>(declared.AssetName) : null;

        internal OperationResult<bool> CheckSprite(UiThemeImage declared, Sprite? loaded)
        {
            if (loaded == null || loaded.texture == null) return WrongSpriteType;
            var rect = loaded.rect;
            return audit.CheckSprite(declared, rect.width, rect.height, Observe(loaded.texture));
        }

        internal OperationResult<bool> CheckFont(UiThemeFont declared, TMP_FontAsset? loaded)
        {
            if (loaded == null || loaded.atlasPopulationMode != AtlasPopulationMode.Static ||
                loaded.atlasTextures == null || loaded.atlasTextures.Length is < 1 or > UiBundleReceipt.MaxTextures)
                return WrongFontType;

            var metrics = loaded.faceInfo;
            if (!(metrics.pointSize > 0 && metrics.lineHeight > 0f && metrics.lineHeight <= float.MaxValue))
                return WrongFontType;
            var atlas = loaded.atlasTextures;
            var observations = new UiObservedTexture[atlas.Length];
            for (var index = 0; index < atlas.Length; index++)
            {
                if (atlas[index] == null) return WrongFontType;
                observations[index] = Observe(atlas[index]);
            }
            return audit.CheckFont(declared, metrics.familyName, metrics.styleName, metrics.ascentLine, metrics.descentLine, observations);
        }

        private static UiObservedTexture Observe(Texture2D texture) =>
            new UiObservedTexture(texture.GetInstanceID(), texture.width, texture.height, texture.mipmapCount,
                texture.format.ToString(), Profiler.GetRuntimeMemorySizeLong(texture));
    }
}
