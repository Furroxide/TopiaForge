using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Only ModContext establishes this direct parent relationship before author callbacks.</summary>
    internal interface IParentAssetScope
    {
        void AttachParent(IAssetService parent);
    }

    internal sealed class AssetScopeOwnership
    {
        private readonly string packagePath;
        private AssetScopeOwnership? parent;
        internal AssetScopeOwnership(string packagePath)
        { this.packagePath = packagePath ?? throw new ArgumentNullException(nameof(packagePath)); }
        internal void AttachParent(AssetScopeOwnership source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (ReferenceEquals(this, source) || parent != null || source.parent != null
                || !string.Equals(packagePath, source.packagePath, StringComparison.Ordinal))
                throw new InvalidOperationException("An asset scope accepts only its direct parent package facade.");
            parent = source;
        }
        internal bool AllowsSpawn(AssetScopeOwnership source) =>
            ReferenceEquals(this, source) || ReferenceEquals(parent, source);
    }
}
