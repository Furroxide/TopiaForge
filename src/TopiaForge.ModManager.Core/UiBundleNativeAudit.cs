using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Checks one native load's metadata and counts shared backing textures once.</summary>
    internal sealed class UiBundleNativeAudit
    {
        private static readonly OperationResult<bool> Valid = OperationResult<bool>.Success(true);
        private static readonly OperationResult<bool> WrongSprite =
            OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "$.images: Sprite metadata differs from the admitted bundle receipt.");
        private static readonly OperationResult<bool> WrongFont =
            OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "$.fonts: Font metadata differs from the admitted bundle receipt.");
        private static readonly OperationResult<bool> TextureBudget =
            OperationResult<bool>.Failure(ModErrorCode.RateLimited, "$.uiReceipt.textures: Native UI texture residency exceeds 64 MiB.");

        private readonly UiAdmittedBundle bundle;
        private readonly int?[] nativeIdentities;
        private readonly Dictionary<int, long> resident = new Dictionary<int, long>();
        private long residentBytes;

        internal UiBundleNativeAudit(UiAdmittedBundle bundle)
        {
            this.bundle = bundle;
            nativeIdentities = new int?[bundle.Receipt.Textures.Count];
            ResidentTextures = new ReadOnlyDictionary<int, long>(resident);
        }

        internal IReadOnlyDictionary<int, long> ResidentTextures { get; }

        internal OperationResult<bool> CheckSprite(UiThemeImage declared, float width, float height, UiObservedTexture texture)
        {
            if (!Belongs(declared) || !bundle.Receipt.TryGetSprite(declared.AssetName, out var expected) ||
                expected.Width != width || expected.Height != height ||
                declared.PixelWidth != expected.Width || declared.PixelHeight != expected.Height ||
                declared.IsMask != string.Equals(texture.Format, "Alpha8", StringComparison.Ordinal) ||
                !CheckTexture(expected.Texture, texture))
                return WrongSprite;
            var bytes = NewBytes(expected.Texture, texture);
            if (bytes > UiBundleReceipt.MaxDecodedBytes - residentBytes) return TextureBudget;
            RecordTexture(expected.Texture, texture, bytes);
            return Valid;
        }

        internal OperationResult<bool> CheckFont(UiThemeFont declared, string family, string style,
            float ascent, float descent, ReadOnlySpan<UiObservedTexture> textures)
        {
            if (!Belongs(declared) || !bundle.Receipt.TryGetFont(declared.AssetName, out var expected) ||
                !string.Equals(family, expected.Family, StringComparison.Ordinal) ||
                !string.Equals(style, expected.Style, StringComparison.Ordinal) ||
                ascent != expected.Ascent || descent != expected.Descent || textures.Length != expected.Textures.Count)
                return WrongFont;

            long bytes = 0;
            for (var index = 0; index < textures.Length; index++)
            {
                var texture = textures[index];
                var slot = expected.Textures[index];
                if (!CheckTexture(slot, texture)) return WrongFont;
                for (var earlier = 0; earlier < index; earlier++)
                    if (textures[earlier].Identity == texture.Identity) return WrongFont;
                var added = NewBytes(slot, texture);
                if (added > UiBundleReceipt.MaxDecodedBytes - residentBytes - bytes) return TextureBudget;
                bytes += added;
            }
            for (var index = 0; index < textures.Length; index++)
                RecordTexture(expected.Textures[index], textures[index], NewBytes(expected.Textures[index], textures[index]));
            return Valid;
        }

        internal bool Belongs(UiThemeAsset declared) =>
            string.Equals(declared.BundlePath, bundle.BundlePath, StringComparison.Ordinal) &&
            string.Equals(declared.BundleSha256, bundle.Sha256, StringComparison.Ordinal);

        private bool CheckTexture(int slot, UiObservedTexture texture)
        {
            var expected = bundle.Receipt.Textures[slot];
            if (texture.Identity == 0 || texture.ResidentBytes <= 0 ||
                texture.Width != expected.Width || texture.Height != expected.Height ||
                texture.MipCount != expected.MipCount ||
                !string.Equals(texture.Format, expected.Format, StringComparison.Ordinal))
                return false;
            return nativeIdentities[slot] is int bound ? bound == texture.Identity : !resident.ContainsKey(texture.Identity);
        }

        private long NewBytes(int slot, UiObservedTexture texture) =>
            nativeIdentities[slot].HasValue ? 0 : Math.Max(texture.ResidentBytes, bundle.Receipt.Textures[slot].DecodedBytes);

        private void RecordTexture(int slot, UiObservedTexture texture, long bytes)
        {
            if (bytes == 0) return;
            nativeIdentities[slot] = texture.Identity;
            resident.Add(texture.Identity, bytes);
            residentBytes += bytes;
        }
    }

    internal readonly struct UiObservedTexture
    {
        internal UiObservedTexture(int identity, int width, int height, int mipCount, string format, long residentBytes)
        {
            Identity = identity;
            Width = width;
            Height = height;
            MipCount = mipCount;
            Format = format;
            ResidentBytes = residentBytes;
        }

        internal int Identity { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal int MipCount { get; }
        internal string Format { get; }
        internal long ResidentBytes { get; }
    }
}
