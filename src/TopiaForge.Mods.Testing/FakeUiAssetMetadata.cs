using System;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Controls the outcome of loading a declared resource in a theme fixture.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public enum FakeUiAssetOutcome
    {
        /// <summary>The resource and its metadata are available.</summary>
        Available = 0,
        /// <summary>The resource cannot be found.</summary>
        Missing = 1,
        /// <summary>The resource exists with incompatible metadata.</summary>
        Invalid = 2,
        /// <summary>The backing resource could not be loaded.</summary>
        LoadFailure = 3
    }

    /// <summary>Describes a package resource for the fake theme loader.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class FakeUiAssetMetadata
    {
        /// <summary>Creates fixture metadata for a sprite or font in a package-relative bundle.</summary>
        public FakeUiAssetMetadata(string bundlePath, string assetName,
            string contentSha256, int width, int height, long decodedBytes,
            float ascender = 0, float descender = 0,
            FakeUiAssetOutcome outcome = FakeUiAssetOutcome.Available, bool isMask = false)
        {
            if (string.IsNullOrEmpty(bundlePath)) throw new ArgumentException("A bundle path is required.", nameof(bundlePath));
            if (string.IsNullOrEmpty(assetName) || assetName.Length > 256)
                throw new ArgumentException("An asset name of 1 to 256 characters is required.", nameof(assetName));
            if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
            if (height < 1) throw new ArgumentOutOfRangeException(nameof(height));
            if (decodedBytes < 1) throw new ArgumentOutOfRangeException(nameof(decodedBytes));
            if (!IsFinite(ascender)) throw new ArgumentOutOfRangeException(nameof(ascender));
            if (!IsFinite(descender)) throw new ArgumentOutOfRangeException(nameof(descender));
            if (outcome < FakeUiAssetOutcome.Available || outcome > FakeUiAssetOutcome.LoadFailure)
                throw new ArgumentOutOfRangeException(nameof(outcome));

            ContentSha256 = DecodeSha256(contentSha256);
            try { BundlePath = UiThemeData.Path(bundlePath, "bundlePath"); }
            catch (Exception exception) when (exception is UiThemeDataException || exception is System.Text.EncoderFallbackException)
            {
                throw new ArgumentException("Use a normalized package-relative path.", nameof(bundlePath));
            }
            AssetName = assetName;
            Width = width;
            Height = height;
            DecodedBytes = decodedBytes;
            Ascender = ascender;
            Descender = descender;
            Outcome = outcome;
            IsMask = isMask;
        }

        internal string BundlePath { get; }
        internal string AssetName { get; }
        internal byte[] ContentSha256 { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal long DecodedBytes { get; }
        internal float Ascender { get; }
        internal float Descender { get; }
        internal FakeUiAssetOutcome Outcome { get; }
        internal bool IsMask { get; }

        private static byte[] DecodeSha256(string hex)
        {
            if (hex == null) throw new ArgumentNullException(nameof(hex));
            if (hex.Length != 64) throw InvalidHash();
            var hash = new byte[32];
            for (var index = 0; index < hash.Length; index++)
            {
                hash[index] = (byte)((HexDigit(hex[index * 2]) << 4) | HexDigit(hex[index * 2 + 1]));
            }
            return hash;
        }

        private static int HexDigit(char digit)
        {
            if (digit >= '0' && digit <= '9') return digit - '0';
            if (digit >= 'a' && digit <= 'f') return digit - 'a' + 10;
            if (digit >= 'A' && digit <= 'F') return digit - 'A' + 10;
            throw InvalidHash();
        }

        private static ArgumentException InvalidHash() =>
            new ArgumentException("Use a 64-character hexadecimal SHA-256 digest.", "contentSha256");

        private static bool IsFinite(float number) => !float.IsNaN(number) && !float.IsInfinity(number);
    }
}
