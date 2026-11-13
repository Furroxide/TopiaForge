using System;
using System.Collections.Generic;

namespace TopiaForge.RobotKit
{
    internal enum RobotComposedRefusal
    {
        SceneUnavailable, HostCount, MapRoot, EntryBudget, SceneShape, SceneIdentity,
        EntityShape, AssetReferences, AmbiguousIdentity, NotPresent, NativeBinding,
        ProjectCatalogShape, EntityReferencesShape, BodyPartUnavailable, VoiceUnavailable,
        AssetReferenceSetInvalid, CatalogAssetsUnavailable, ContextUnverified, CharacterUnprepared
    }

    internal enum RobotComposedRetirement
    {
        WorldChanged, NativeLifetimeEnded, NativeIdentityChanged, NativeIdentityReadFailed, VerificationTimedOut
    }

    // Native catalog dependencies are distinct from body-part and voice-map resolution.
    internal static class RobotComposedReferences
    {
        internal const string MunicipalId = "municipal";

        internal const string BodyOnly = "MuniciPal is body-only in this world: its personality and lore use game catalogue "
            + "content this game version has not prepared, so it can only be Dormant, without a native brain or native talk.";

        // Fixed author- and player-readable reasons. They name no asset, path or session value.
        internal static string Explain(RobotComposedRefusal reason) => reason switch
        {
            RobotComposedRefusal.NotPresent => "This world has no MuniciPal character.",
            RobotComposedRefusal.BodyPartUnavailable => "A MuniciPal body part is missing from the game's robot part catalogue.",
            RobotComposedRefusal.VoiceUnavailable => "The MuniciPal voice is missing from the game's voice catalogue.",
            RobotComposedRefusal.CatalogAssetsUnavailable =>
                "MuniciPal uses game catalogue content this game version has not prepared, beyond its personality and lore.",
            RobotComposedRefusal.NativeBinding => "This game version does not expose what RobotKit needs to build MuniciPal.",
            RobotComposedRefusal.CharacterUnprepared => BodyOnly,
            RobotComposedRefusal.SceneUnavailable or RobotComposedRefusal.HostCount or RobotComposedRefusal.MapRoot
                or RobotComposedRefusal.ContextUnverified => "MuniciPal needs a loaded map world.",
            _ => "This world's MuniciPal data has a shape RobotKit does not support.",
        } + " Code: " + reason + ".";

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
