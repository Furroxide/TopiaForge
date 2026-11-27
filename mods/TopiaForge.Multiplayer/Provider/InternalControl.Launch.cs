using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer
{
    /// <summary>
    /// The control's session launches: a launch's session planned onto live sessions, and the hosting it plans held to the
    /// launch's own game port and privacy. Nothing here starts a session; the runtime opens or joins a plan through the
    /// realm. See docs/internal/MultiplayerLaunchContract.md, section 8.
    /// </summary>
    internal sealed partial class InternalControl
    {
        internal const string LaunchUnreadable = "The launched session names a server this game cannot read.";

        // The hosting a launch planned, so only that plan's own settings take the launch's port; a later plan replaces it.
        private LaunchSessionPlan? hostPlan;

        public OperationResult<IInternalLaunchPlan> PlanLaunchSession(InternalLaunchSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            engine.RequireOwnerThread();
            var unavailable = RequireLive();
            if (unavailable != null) return OperationResult<IInternalLaunchPlan>.Failure(unavailable.ErrorCode, unavailable.ErrorMessage);
            var plan = LaunchSessionPlan.From(session, live!.Services != null, out var unsupported);
            if (plan == null)
            {
                return unsupported == LaunchSessionUnsupported.NeedsService
                    ? OperationResult<IInternalLaunchPlan>.Failure(ModErrorCode.Unavailable, NoService)
                    : OperationResult<IInternalLaunchPlan>.Failure(ModErrorCode.InvalidArgument, LaunchUnreadable);
            }

            if (plan.Host != null) hostPlan = plan;
            return OperationResult<IInternalLaunchPlan>.Success(plan);
        }

        /// <summary>Holds <paramref name="host"/> to the launch's port and privacy when <paramref name="settings"/> are a launch plan's own.</summary>
        private void ApplyLaunchPlan(InternalHostSettings settings, HostSettings host)
        {
            var plan = hostPlan;
            if (plan != null && ReferenceEquals(plan.Host, settings)) plan.ApplyTo(host);
        }

        /// <summary>The live join settings for <paramref name="target"/>: a planned target keeps the launch's privacy and descriptor pin.</summary>
        private static JoinSettings JoinSettingsFor(ProviderJoinTarget target, InternalJoinSettings settings, string name) =>
            new JoinSettings(name)
            {
                Password = settings.Password,
                AllowDirect = target.PlannedAllowDirect ?? settings.AllowDirect,
                TrustedServerId = settings.TrustedServerId,
                ExpectedDescriptorSha256 = target.ExpectedDescriptorSha256
            };
    }
}
