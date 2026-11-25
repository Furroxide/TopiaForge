namespace TopiaForge.Mods
{
    internal abstract class UiThemeAsset
    {
        protected UiThemeAsset(string id, string bundlePath, string assetName, string licensePath, string? bundleSha256)
        {
            Id = id;
            BundlePath = bundlePath;
            AssetName = assetName;
            LicensePath = licensePath;
            BundleSha256 = bundleSha256;
        }

        internal string Id { get; }
        internal string BundlePath { get; }
        internal string AssetName { get; }
        internal string LicensePath { get; }
        internal string? BundleSha256 { get; }
    }

    internal sealed class UiThemeFont : UiThemeAsset
    {
        internal UiThemeFont(string id, string bundlePath, string assetName, string licensePath, string? bundleSha256)
            : base(id, bundlePath, assetName, licensePath, bundleSha256) { }
    }

    internal sealed class UiThemeImage : UiThemeAsset
    {
        internal UiThemeImage(string id, string bundlePath, string assetName, string licensePath, string? bundleSha256,
            int? pixelWidth, int? pixelHeight, bool? isMask)
            : base(id, bundlePath, assetName, licensePath, bundleSha256)
        {
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
            IsMask = isMask;
        }

        internal int? PixelWidth { get; }
        internal int? PixelHeight { get; }
        internal bool? IsMask { get; }

        internal UiThemeImage WithMetadata(UiThemeImageMetadata metadata) =>
            new UiThemeImage(Id, BundlePath, AssetName, LicensePath, BundleSha256,
                metadata.PixelWidth, metadata.PixelHeight, metadata.IsMask);
    }

    internal readonly struct UiThemeImageMetadata
    {
        internal UiThemeImageMetadata(int pixelWidth, int pixelHeight, bool isMask)
        {
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
            IsMask = isMask;
        }

        internal int PixelWidth { get; }
        internal int PixelHeight { get; }
        internal bool IsMask { get; }
    }
}
