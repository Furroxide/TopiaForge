using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.ControlPlane;
using TopiaForge.Multiplayer.NatTraversal;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer
{
    /// <summary>
    /// The control's Internet members: which TopiaForge service it reaches, if any, the host's invites, and the
    /// session's standing with the service as the status shows it. Nothing here carries an address.
    /// </summary>
    internal sealed partial class InternalControl
    {
        internal const string NoService =
            "Public and invite-only sessions, invites and listed servers need the TopiaForge service, which this game is not set up to reach. LAN and direct play still work.";

        public OperationResult<bool> CreateInvite()
        {
            engine.RequireOwnerThread();
            var internet = live?.Host?.Internet;
            if (live?.Host == null) return Fail(ModErrorCode.NotAuthoritative, "Only a host makes invites.");
            if (internet == null || !internet.CreateInvite()) return Fail(ModErrorCode.InvalidState, "The session is not registered with the TopiaForge service yet.");
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> RevokeInvite(string code)
        {
            if (code == null) throw new ArgumentNullException(nameof(code));
            engine.RequireOwnerThread();
            var internet = live?.Host?.Internet;
            if (live?.Host == null) return Fail(ModErrorCode.NotAuthoritative, "Only a host withdraws invites.");
            if (!InviteCode.TryNormalize(code, out var canonical)) return Fail(ModErrorCode.InvalidArgument, "That is not an invite code.");
            if (internet == null || !internet.RevokeInvite(canonical)) return Fail(ModErrorCode.InvalidState, "The session is not registered with the TopiaForge service.");
            return OperationResult<bool>.Success(true);
        }

        /// <summary>
        /// Sets the service the live sessions reach once a runtime attaches: the runtime host's origin, which is the session
        /// launch's own when it names one and otherwise the manager's configuration. An origin that does not read as one is
        /// refused whole, so nothing is contacted. Live sessions a test composed with a service of its own keep it.
        /// </summary>
        private void ConfigureService(IInternalMultiplayerRuntimeHost attached)
        {
            if (live == null || live.Services != null) return;
            string? text;
            try
            {
                text = attached.ServiceOrigin;
            }
            catch (Exception exception)
            {
                diagnostics.Report(new InternalMultiplayerDiagnostic(InternalDiagnosticSeverity.Error, "internet.origin-unreadable",
                    "The runtime's service configuration could not be read, so Internet play stays off.", null, exception));
                return;
            }

            if (text == null) return;
            if (!ControlPlaneOrigin.TryParse(text, out var origin))
            {
                Warn("internet.origin-invalid", "The configured TopiaForge service origin is not valid, so Internet play stays off.");
                return;
            }

            live.Services = InternetServices.ForOrigin(origin);
        }

        /// <summary>Whether hosting with <paramref name="visibility"/> needs a service this control does not have.</summary>
        private bool LacksService(InternalSessionVisibility visibility) => visibility != InternalSessionVisibility.Lan && live?.Services == null;

        private InternalInternetStatus InternetStatus()
        {
            var status = new InternalInternetStatus { ServiceConfigured = live?.Services != null };
            var internet = live?.Host?.Internet;
            if (internet != null)
            {
                var nat = internet.NatStatus;
                status.ListingPhase = (InternalListingPhase)(int)internet.Phase;
                status.ListingFailure = ToFailure(internet.RelayRefused && internet.Failure == LiveFailure.None ? LiveFailure.RelayUnavailable : internet.Failure);
                status.Invite = internet.Invite == null ? null : new InternalInvite(internet.Invite.Code, internet.Invite.Display, internet.Invite.Link, internet.Invite.ExpiresAt);
                status.InviteFailure = ToFailure(internet.InviteFailure);
                status.Listed = internet.Listed;
                status.RelayReady = nat.RelayReady;
                status.RelayedPlayers = nat.RelayedSessions;
                status.DirectPlayers = nat.DirectSessions;
                status.RetryAfterSeconds = internet.RetryAfterSeconds;
            }

            var join = live?.Join;
            if (join != null && join.RetryAfterSeconds > 0) status.RetryAfterSeconds = join.RetryAfterSeconds;
            if (join != null && join.Route != null)
            {
                var route = join.Internet;
                status.HostAllowsDirect = route?.HostAllowsDirect ?? !join.Route.HostRelayOnly;
                var nat = route?.Status ?? NatStatus.Idle;
                status.RelayReady = nat.RelayReady;
                status.JoinRoute = join.Phase == JoinPhase.Synchronizing || join.Phase == JoinPhase.InSession
                    ? nat.Route == NatRoute.Direct ? InternalConnectionRoute.Direct : nat.Route == NatRoute.Relayed ? InternalConnectionRoute.Relayed : InternalConnectionRoute.None
                    : InternalConnectionRoute.None;
            }

            return status;
        }
    }
}
