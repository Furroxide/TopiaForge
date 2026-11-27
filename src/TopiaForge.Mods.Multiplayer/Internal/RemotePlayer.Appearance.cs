using System;

namespace TopiaForge.Mods.Internal
{
    internal sealed partial class RemotePlayer : IPlayerAppearance
    {
        private IPlayerAppearance? appearanceBackend;
        private IPlayerAppearance AppearanceBackend => appearanceBackend ??= backend!.Appearance(Id);
        public IPlayerAppearance Appearance { get { directory.CheckThread(); return this; } }
        public bool TryGet(out AvatarAppearance? appearance)
        {
            appearance = null;
            return IsPresent && backend != null && AppearanceBackend.TryGet(out appearance);
        }
        public OperationResult<IPlayerAppearanceLease> Set(AvatarAppearance appearance) => IsPresent && backend != null
            ? AppearanceBackend.Set(appearance) : Fail<IPlayerAppearanceLease>();
        public OperationResult<IPlayerAppearanceLease> CopyFrom(IPlayer source)
        {
            if (!IsPresent || backend == null) return Fail<IPlayerAppearanceLease>();
            if (source is not RemotePlayer other || !ReferenceEquals(other.directory, directory) || !other.IsPresent)
                return OperationResult<IPlayerAppearanceLease>.Failure(ModErrorCode.InvalidArgument, "Copy from a current player in this directory.");
            return AppearanceBackend.CopyFrom(source);
        }
        public OperationResult<IPlayerAppearanceLease> SetNameplate(string? text = null, bool visible = true) => IsPresent && backend != null
            ? AppearanceBackend.SetNameplate(text, visible) : Fail<IPlayerAppearanceLease>();
        public OperationResult<IPlayerAppearanceLease> SetPoweredDown(bool poweredDown = true) => IsPresent && backend != null
            ? AppearanceBackend.SetPoweredDown(poweredDown) : Fail<IPlayerAppearanceLease>();
    }
}
