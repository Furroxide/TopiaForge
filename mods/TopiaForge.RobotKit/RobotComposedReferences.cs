using System;
using System.Collections.Generic;

namespace TopiaForge.RobotKit
{
    internal enum RobotComposedRefusal
    {
        SceneUnavailable, HostCount, MapRoot, EntryBudget, SceneShape, SceneIdentity,
        EntityShape, AssetReferences, AmbiguousIdentity, NotPresent, NativeBinding,
        ProjectCatalogShape, EntityReferencesShape, BodyPartUnavailable, VoiceUnavailable,
        AssetReferenceSetInvalid, CatalogAssetsUnavailable, ContextUnverified
    }

    internal enum RobotComposedRetirement
    {
        WorldChanged, NativeLifetimeEnded, NativeIdentityChanged, NativeIdentityReadFailed, VerificationTimedOut
    }

    // Native catalog dependencies are distinct from body-part and voice-map resolution.
    internal static class RobotComposedReferences
    {
        internal static bool AllAssetsResolved(IReadOnlyList<string>? assets, ISet<string> resolved,
            out RobotComposedRefusal refusal)
        {
            refusal = RobotComposedRefusal.AssetReferenceSetInvalid;
            if (assets == null || assets.Count > 256) return false;
            foreach (var asset in assets)
                if (string.IsNullOrEmpty(asset)) return false;
            refusal = RobotComposedRefusal.CatalogAssetsUnavailable;
            foreach (var asset in assets)
                if (!resolved.Contains(asset)) return false;
            return true;
        }
    }
}
