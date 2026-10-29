using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed class PlayerAppearanceControls : IPlayerAppearance
    {
        private readonly AvatarAppearanceControls controls;
        internal PlayerAppearanceControls(PlayerContext context, ParticipantId id)
        {
            controls = new AvatarAppearanceControls(() => context.Runtime.Presence.PresentationFor(id), () =>
            {
                UnityMainThreadGuard.AssertCurrent();
                if (context.Lifetime.IsStopping) return ModErrorCode.Cancelled;
                if (!context.Runtime.IsHost || context.AppearanceProfile.Context == ModExecutionContext.Participant
                    || context.Runtime.Realm != NetRealm.Offline && (context.AppearanceProfile.NetworkClass == ModNetworkClass.ClientLocal
                        || context.AppearanceProfile.NetworkClass == ModNetworkClass.Standalone)) return ModErrorCode.NotAuthoritative;
                return ModErrorCode.None;
            }, context.Lifetime);
        }
        public bool TryGet(out AvatarAppearance? appearance) => controls.TryGet(out appearance);
        public OperationResult<IPlayerAppearanceLease> Set(AvatarAppearance appearance) => controls.Set(appearance);
        public OperationResult<IPlayerAppearanceLease> CopyFrom(IPlayer source) => controls.CopyFrom(source);
        public OperationResult<IPlayerAppearanceLease> SetNameplate(string? text = null, bool visible = true) =>
            !AvatarAppearanceControls.ValidName(text) ? controls.SetNameplate(text, visible)
            : controls.SetNameplate(text == null ? null : Sessions.HostText.ForDisplay(text, 32), visible);
        public OperationResult<IPlayerAppearanceLease> SetPoweredDown(bool poweredDown = true) => controls.SetPoweredDown(poweredDown);
    }
}
