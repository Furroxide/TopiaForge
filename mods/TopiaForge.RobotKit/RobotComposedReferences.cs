using System;
using System.Collections.Generic;

namespace TopiaForge.RobotKit
{
    internal enum RobotComposedRefusal
    {
        SceneUnavailable, HostCount, MapRoot, EntryBudget, SceneShape, SceneIdentity,
        EntityShape, AssetReferences, AmbiguousIdentity, NotPresent, NativeBinding
    }

    // 2597's all-asset collector is deliberately not treated as a lore-only collector.
    internal static class RobotComposedReferences
    {
        internal static bool AllAssetsResolved(IReadOnlyList<string>? assets, ISet<string> resolved)
        {
            if (assets == null || assets.Count > 256) return false;
            foreach (var asset in assets)
                if (string.IsNullOrEmpty(asset) || !resolved.Contains(asset)) return false;
            return true;
        }
    }
}
