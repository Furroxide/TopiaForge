using System;

namespace TopiaForge.Mods.Internal
{
    internal sealed class AvatarAppearanceControls : IPlayerAppearance
    {
        private readonly Func<AvatarPresentation?> resolve;
        private readonly Func<ModErrorCode> permission;
        private readonly IModLifetime lifetime;
        internal AvatarAppearanceControls(Func<AvatarPresentation?> resolve, Func<ModErrorCode> permission, IModLifetime lifetime)
        { this.resolve = resolve; this.permission = permission; this.lifetime = lifetime; }
        public bool TryGet(out AvatarAppearance? appearance)
        {
            var state = lifetime.IsStopping ? null : resolve();
            appearance = state?.IsAlive == true ? state.Current : null;
            return appearance != null;
        }
        public OperationResult<IPlayerAppearanceLease> Set(AvatarAppearance appearance) =>
            Change(0, appearance ?? throw new ArgumentNullException(nameof(appearance)));
        public OperationResult<IPlayerAppearanceLease> CopyFrom(IPlayer source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var denied = permission();
            if (denied != ModErrorCode.None) return Failure(denied);
            return source.IsPresent && source.Appearance.TryGet(out var appearance) ? Set(appearance!) : Failure(ModErrorCode.NotFound);
        }
        public OperationResult<IPlayerAppearanceLease> SetNameplate(string? text = null, bool visible = true) =>
            ValidName(text) ? Change(1, name: text, flag: visible) : Failure(ModErrorCode.InvalidArgument);
        public OperationResult<IPlayerAppearanceLease> SetPoweredDown(bool poweredDown = true) => Change(2, flag: poweredDown);
        private OperationResult<IPlayerAppearanceLease> Change(byte kind, AvatarAppearance? appearance = null, string? name = null, bool flag = true)
        {
            if (lifetime.IsStopping) return Failure(ModErrorCode.Cancelled);
            var denied = permission();
            if (denied != ModErrorCode.None) return Failure(denied);
            var state = resolve();
            return state == null ? Failure(ModErrorCode.NotFound) : state.Add(lifetime, kind, appearance, name, flag);
        }
        internal static bool ValidName(string? text)
        {
            if (text == null) return true;
            if (text.Length > 32) return false;
            // Keep literal punctuation; the runtime applies its existing address redactor before publication.
            foreach (var c in text)
            {
                if (char.IsControl(c) || c == '\u2028' || c == '\u2029') return false;
            }
            return true;
        }
        private static OperationResult<IPlayerAppearanceLease> Failure(ModErrorCode code) => OperationResult<IPlayerAppearanceLease>.Failure(code,
            code == ModErrorCode.NotAuthoritative ? "Only the host's server context changes participant presentation."
            : code == ModErrorCode.InvalidArgument ? "Use a single-line nameplate of at most 32 characters without control characters."
            : "The participant presentation or its scope is unavailable.");
    }
}
