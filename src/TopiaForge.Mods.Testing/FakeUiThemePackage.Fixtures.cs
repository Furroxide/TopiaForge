using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiThemePackage
    {
        private const long MaximumDecodedBytes = 64L * 1024 * 1024;
        private readonly Dictionary<(string Bundle, string Name), FakeUiAssetMetadata> assets =
            new Dictionary<(string Bundle, string Name), FakeUiAssetMetadata>();
        private readonly Dictionary<string, byte[]> bundleHashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        internal OperationResult<bool> Declare(FakeUiThemeScope scope, IReadOnlyList<FakeUiThemeDefinition> definitions,
            string? manifestDefault, bool catalogue)
        {
            lock (gate)
            {
                if (scope.Stopped) return Cancelled;
                if (fixturesFrozen)
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Theme fixtures are frozen after first use.");
                if (definitions.Count > 8 - registrations.Count)
                    return OperationResult<bool>.Failure(ModErrorCode.RateLimited, "A fake package declares at most eight themes.");

                foreach (var definition in definitions)
                {
                    if (registrations.ContainsKey(definition.Descriptor.Id))
                        return OperationResult<bool>.Failure(ModErrorCode.Conflict, "This theme is already declared in the fake package.");
                    foreach (var image in definition.Descriptor.Images)
                        if (assets.TryGetValue((image.BundlePath, image.AssetName), out var fixture) && !MatchesImage(image, fixture))
                            return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument,
                                "$.images: Declared image metadata differs from the fixture receipt.");
                }

                foreach (var definition in definitions)
                    registrations.Add(definition.Descriptor.Id, new FakeUiThemeRegistration(definition));
                if (catalogue) this.manifestDefault = manifestDefault;
                // A fixture catalogue may be installed while the fake has unthemed surfaces.
                Reconcile();
                return Success;
            }
        }

        internal void AddAsset(FakeUiThemeScope scope, FakeUiAssetMetadata asset)
        {
            lock (gate)
            {
                if (scope.Stopped) throw new ObjectDisposedException(nameof(FakeUiService));
                if (fixturesFrozen) throw new InvalidOperationException("Theme fixtures are frozen after first use.");
                if (bundleHashes.TryGetValue(asset.BundlePath, out var hash) && !hash.AsSpan().SequenceEqual(asset.ContentSha256))
                    throw new ArgumentException("A fixture bundle has one content digest.", nameof(asset));
                foreach (var registration in registrations.Values)
                    foreach (var image in registration.Definition.Descriptor.Images)
                        if (image.BundlePath == asset.BundlePath && image.AssetName == asset.AssetName && !MatchesImage(image, asset))
                            throw new ArgumentException("The fixture receipt disagrees with declared image metadata.", nameof(asset));
                if (!assets.TryAdd((asset.BundlePath, asset.AssetName), asset))
                    throw new ArgumentException("The fake bundle asset is already declared.", nameof(asset));
                bundleHashes[asset.BundlePath] = asset.ContentSha256;
            }
        }

        internal FakeUiTextMetrics TextMetrics(UiThemeDescriptor descriptor, string id)
        {
            foreach (var font in descriptor.Fonts)
            {
                if (font.Id != id) continue;
                lock (gate)
                {
                    var asset = assets[(font.BundlePath, font.AssetName)];
                    return new FakeUiTextMetrics(asset.Ascender, asset.Descender);
                }
            }
            // The built-in faces use a normalized fixture em. Native measurements are recorded separately.
            return new FakeUiTextMetrics(0.8f, -0.2f);
        }

        private static bool MatchesImage(UiThemeImage image, FakeUiAssetMetadata asset) =>
            (!image.PixelWidth.HasValue || image.PixelWidth == asset.Width) &&
            (!image.PixelHeight.HasValue || image.PixelHeight == asset.Height) &&
            (!image.IsMask.HasValue || image.IsMask == asset.IsMask);

        private OperationResult<UiThemeDescriptor> VerifyDefinition(FakeUiThemeRegistration registration)
        {
            if (registration.Verified != null) return registration.Verified;
            var definition = registration.Definition;
            var descriptor = definition.Descriptor;
            foreach (var font in descriptor.Fonts)
                if (!assets.ContainsKey((font.BundlePath, font.AssetName))) return AssetFailure(ModErrorCode.NotFound, "fonts");
            var verified = descriptor.Images.Count == 0 && descriptor.Fonts.Count == 0
                ? OperationResult<UiThemeDescriptor>.Success(descriptor)
                : UiThemePackParser.Parse(definition.Bytes, expectedId: descriptor.Id,
                    fileHash: path => bundleHashes.TryGetValue(path, out var digest) ? digest : null,
                    imageMetadata: image => assets.TryGetValue((image.BundlePath, image.AssetName), out var receipt)
                        ? new UiThemeImageMetadata(receipt.Width, receipt.Height, receipt.IsMask) : (UiThemeImageMetadata?)null);
            if (verified.Succeeded) registration.Verified = verified;
            return verified;
        }

        private OperationResult<UiThemeDescriptor> Acquire(FakeUiThemeRegistration registration)
        {
            var verified = VerifyDefinition(registration);
            if (!verified.TryGetValue(out var descriptor)) return verified;
            var budget = new FakeUiThemeResourceBudget();
            foreach (var resident in registrations.Values)
            {
                if (resident.Loaded != null) AddResident(resident.Loaded, budget);
            }

            foreach (var image in descriptor.Images)
            {
                var key = (image.BundlePath, image.AssetName);
                var error = ValidateAsset(key, font: false);
                if (error != ModErrorCode.None) return AssetFailure(error, "images");
                budget.AddImage(key, assets[key]);
            }

            foreach (var font in descriptor.Fonts)
            {
                var key = (font.BundlePath, font.AssetName);
                var error = ValidateAsset(key, font: true);
                if (error != ModErrorCode.None) return AssetFailure(error, "fonts");
                budget.AddFont(key, assets[key]);
            }

            if (budget.Exceeds(MaximumDecodedBytes))
                return OperationResult<UiThemeDescriptor>.Failure(ModErrorCode.RateLimited, "The fake package exceeds its resident UI asset budget.");

            return verified;
        }

        private void AddResident(UiThemeDescriptor descriptor, FakeUiThemeResourceBudget budget)
        {
            foreach (var image in descriptor.Images)
            {
                var key = (image.BundlePath, image.AssetName);
                budget.AddImage(key, assets[key]);
            }
            foreach (var font in descriptor.Fonts)
            {
                var key = (font.BundlePath, font.AssetName);
                budget.AddFont(key, assets[key]);
            }
        }

        private ModErrorCode ValidateAsset((string Bundle, string Name) key, bool font)
        {
            if (!assets.TryGetValue(key, out var asset) || asset.Outcome == FakeUiAssetOutcome.Missing)
                return ModErrorCode.NotFound;
            if (asset.Outcome == FakeUiAssetOutcome.LoadFailure) return ModErrorCode.External;
            if (asset.Outcome == FakeUiAssetOutcome.Invalid) return ModErrorCode.InvalidArgument;
            var maximumAxis = font ? 4096 : 2048;
            if (asset.Width > maximumAxis || asset.Height > maximumAxis) return ModErrorCode.RateLimited;
            if (font && (asset.Ascender <= asset.Descender || float.IsInfinity(asset.Ascender - asset.Descender)))
                return ModErrorCode.InvalidArgument;
            return ModErrorCode.None;
        }

        private static OperationResult<UiThemeDescriptor> AssetFailure(ModErrorCode error, string field) =>
            OperationResult<UiThemeDescriptor>.Failure(error, "Could not acquire fixture assets at $." + field + ".");

        private sealed class FakeUiThemeResourceBudget
        {
            private readonly HashSet<(string Bundle, string Name)> fonts = new HashSet<(string Bundle, string Name)>();
            private readonly HashSet<(string Bundle, string Name)> resident = new HashSet<(string Bundle, string Name)>();
            private int imageCount;
            private long decodedBytes;

            internal void AddImage((string Bundle, string Name) key, FakeUiAssetMetadata asset)
            {
                imageCount++;
                AddBytes(key, asset);
            }

            internal void AddFont((string Bundle, string Name) key, FakeUiAssetMetadata asset)
            {
                fonts.Add(key);
                AddBytes(key, asset);
            }

            internal bool Exceeds(long maximumDecodedBytes) =>
                imageCount > 256 || fonts.Count > 16 || decodedBytes > maximumDecodedBytes;

            private void AddBytes((string Bundle, string Name) key, FakeUiAssetMetadata asset)
            {
                if (resident.Add(key))
                    decodedBytes += Math.Min(asset.DecodedBytes, MaximumDecodedBytes + 1);
            }
        }
    }
}
