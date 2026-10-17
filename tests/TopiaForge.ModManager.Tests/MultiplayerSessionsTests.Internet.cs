using System;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Sessions through the TopiaForge service: invites and listed servers, the relay and the approved prompt when it is
    /// unavailable, the service's own failures, and what the host sees. Without a service, none of it is offered.
    /// </summary>
    internal static partial class MultiplayerSessionsTests
    {
        private const string Invite = "ABCDE-12345";

        private static void InvitesAndListedServersNeedTheService()
        {
            var bare = new SessionsRig();
            var connect = bare.Presenter.Connect;
            connect.SetInviteCode(Invite);
            That(!connect.CanJoinInvite && connect.InviteNote == LiveFailureMessages.ServiceNotConfigured, "without a service, an invite cannot be joined, and CONNECT says why");
            connect.JoinInvite();
            bare.Frame();
            That(bare.Noticed(LiveFailureMessages.ServiceNotConfigured) && bare.Control.NetworkCalls == 0, "and asking anyway contacts nothing");
            connect.SetAddress(LauncherJoinLink.ForServer(new string('7', 64))!);
            That(connect.AddressError == LiveFailureMessages.ServiceNotConfigured && !connect.CanJoinAddress, "nor can a pasted server link");
            That(bare.Presenter.Browse.Rows.Count == 0 && ListEmptyText(bare, BrowseList.Internet) == BrowsePresenter.InternetEmpty,
                "and the Internet list says the service is not set up");

            var rig = new SessionsRig(service: true);
            connect = rig.Presenter.Connect;
            That(connect.InviteNote == ConnectPresenter.InviteHelp && ListEmptyText(rig, BrowseList.Internet) == BrowsePresenter.InternetInLauncher,
                "with a service, CONNECT takes invites and the Internet list points to the launcher");
            connect.SetInviteCode(Invite);
            That(connect.CanJoinInvite, "an invite can be joined");
            connect.JoinInvite();
            rig.Frame();
            That(rig.Control.Calls.Where(call => call.StartsWith("Query", StringComparison.Ordinal)).SequenceEqual(new[] { "Query:Invite:relay" }),
                "the invite is checked through the service alone");
            rig.Control.Queries[0].Complete(InternalLiveFailure.None, Server('7', "Night shift"));
            rig.Frame();
            That(rig.Control.Calls.Contains("Join:Invite:relay") && !rig.Control.LastJoin!.AllowDirect, "then joined through the relay while Hide my IP is on");
            var overlay = rig.Presenter.Connecting;
            That(overlay.Title == "Night shift", "named by its server, never as a direct session");
            Step(rig, InternalJoinPhase.Connecting, null, ConnectingStep.Connecting, "Relay");
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.Synchronizing);
            rig.Control.Status.Internet = new InternalInternetStatus { ServiceConfigured = true, JoinRoute = InternalConnectionRoute.Direct };
            rig.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Connecting, "s-route", "p1");
            rig.Frame();
            That(overlay.DetailOf((int)ConnectingStep.Connecting) == "Direct", "once connected, the Connecting step says the route the status reports");

            var unnamed = new SessionsRig(service: true);
            unnamed.Presenter.Connect.SetAddress(LauncherJoinLink.ForServer(new string('8', 64))!);
            That(unnamed.Presenter.Connect.AddressError.Length == 0 && unnamed.Presenter.Connect.CanJoinAddress, "with a service, a pasted server link can be joined");
            unnamed.Presenter.Connect.JoinAddress();
            unnamed.Frame();
            unnamed.Control.Queries[0].Complete(InternalLiveFailure.None);
            unnamed.Frame();
            That(unnamed.Control.Calls.Contains("Join:ListedServer:relay") && unnamed.Presenter.Connecting.Title == "Listed server",
                "it is joined through the relay, and a server without a name reads as a listed server");
        }

        /// <summary>
        /// A relay-only join the relay cannot carry, to a host that takes direct connections, asks the approved per-session
        /// prompt: allowing it joins again directly, for that join only; cancelling keeps Hide my IP on and contacts nothing.
        /// A host that keeps its address hidden makes no such offer.
        /// </summary>
        private static void AnUnavailableRelayOffersTheDirectPrompt()
        {
            var cancelled = RelayFailed(hostAllowsDirect: true);
            That(cancelled.Presenter.Connecting.HasFailed && cancelled.Presenter.Connecting.FailureMessage == LiveFailureMessages.RelayKeepsHideMyIp,
                "the failure says the relay is unavailable and Hide my IP kept the session on it");
            That(cancelled.Prompt?.Kind == ScreenPromptKind.DirectConnection && cancelled.Prompt.Title == MultiplayerPrivacyText.DirectTitle &&
                 cancelled.Prompt.Paragraphs.SequenceEqual(new[] { MultiplayerPrivacyText.DirectBody }), "and the approved prompt asks, word for word");
            var before = cancelled.Control.Calls.Count;
            cancelled.Answer(ScreenPromptKind.DirectConnection, false);
            cancelled.Frame(5);
            That(cancelled.Control.Calls.Skip(before).All(call => call == "GetStatus") && cancelled.Presenter.HideMyIp && cancelled.Presenter.Connecting.HasFailed &&
                 cancelled.Noticed(ConnectPresenter.NothingContacted), "cancelling contacts nothing, keeps Hide my IP on, and leaves the failure in view");

            var allowed = RelayFailed(hostAllowsDirect: true);
            allowed.Answer(ScreenPromptKind.DirectConnection, true);
            allowed.Frame();
            That(allowed.Control.Calls.Count(call => call.StartsWith("Join", StringComparison.Ordinal)) == 2 && allowed.Control.Calls.Last(call => call.StartsWith("Join", StringComparison.Ordinal)) == "Join:Invite:direct",
                "allowing it starts the join again, directly");
            That(allowed.Presenter.HideMyIp && allowed.Presenter.Settings.Current.HideMyIp && allowed.Presenter.Connecting.IsVisible && !allowed.Presenter.Connecting.HasFailed,
                "for this join only: Hide my IP stays on for every other session");

            var hidden = RelayFailed(hostAllowsDirect: false);
            That(hidden.Prompt == null && hidden.Presenter.Connecting.FailureMessage == LiveFailureMessages.RelayHostHidden,
                "a host that keeps its address hidden gets no offer, since a direct connection would not help");

            var closed = RelayFailed(hostAllowsDirect: true);
            closed.Presenter.Connecting.Dismiss();
            closed.Frame();
            That(closed.Prompt == null && closed.Control.Calls.Count(call => call.StartsWith("Join", StringComparison.Ordinal)) == 1,
                "closing the failure view withdraws the offer");
        }

        /// <summary>A join through an invite, refused because the relay is unavailable, with the host's choice about direct connections.</summary>
        private static SessionsRig RelayFailed(bool hostAllowsDirect)
        {
            var rig = new SessionsRig(service: true);
            rig.Presenter.Connect.SetInviteCode(Invite);
            rig.Presenter.Connect.JoinInvite();
            rig.Frame();
            rig.Control.Queries[0].Complete(InternalLiveFailure.None, Server('7', "Night shift"));
            rig.Frame();
            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.RelayUnavailable);
            rig.Control.Status.Internet = new InternalInternetStatus { ServiceConfigured = true, HostAllowsDirect = hostAllowsDirect };
            rig.Frame();
            return rig;
        }

        private static void TheServicesFailuresAreHandledAsTheirCodesSay()
        {
            var limited = Failed(InternalLiveFailure.ServiceRateLimited, retryAfter: 3);
            var overlay = limited.Presenter.Connecting;
            That(overlay.HasFailed && !overlay.CanRetry && overlay.StatusText.EndsWith("try again in 3 seconds.", StringComparison.Ordinal),
                "a service turning requests away counts down its named delay before RETRY (" + overlay.StatusText + ")");
            limited.Frame(10, deltaTime: 0.25f);
            That(!overlay.CanRetry && overlay.StatusText.EndsWith("try again in 1 second.", StringComparison.Ordinal), "the count goes down (" + overlay.StatusText + ")");
            limited.Frame(5, deltaTime: 0.25f);
            That(overlay.CanRetry && overlay.StatusText == overlay.FailureTitle, "and RETRY comes back once it ends");

            var endless = Failed(InternalLiveFailure.ServiceRateLimited, retryAfter: int.MaxValue);
            var waiting = endless.Presenter.Connecting;
            That(!waiting.CanRetry && waiting.StatusText.EndsWith("try again in 86400 seconds.", StringComparison.Ordinal),
                "a named delay past a day waits a day (" + waiting.StatusText + ")");
            endless.Frame(1, deltaTime: 86_400f);
            That(waiting.CanRetry, "and no longer");

            var unlisted = new SessionsRig(service: true);
            var serverId = new string('9', 64);
            unlisted.Presenter.Settings.Update(unlisted.Presenter.Settings.Current.RecordJoin(new ServerBookmark(serverId, "Gone server", 1)));
            unlisted.Frame(2);
            unlisted.Presenter.Connect.SetAddress(LauncherJoinLink.ForServer(serverId)!);
            unlisted.Presenter.Connect.JoinAddress();
            unlisted.Frame();
            unlisted.Control.Queries[0].Complete(InternalLiveFailure.None, Server('9', "Gone server"));
            unlisted.Frame();
            unlisted.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.ServerNotListed);
            unlisted.Frame(3);
            That(unlisted.Presenter.Connecting.HasFailed && unlisted.Store.Stored!.History.All(bookmark => bookmark.ServerId != serverId),
                "a server the service no longer lists leaves the recent servers");

            var refused = Failed(InternalLiveFailure.InviteNotFound);
            refused.Control.Status.JoinDetail = "That invite code does not work.";
            refused.Presenter.Connecting.CopyDetails();
            var copied = refused.Environment.Copied.Single();
            That(!refused.Presenter.Connecting.CanRetry && copied.IndexOf("ABCDE", StringComparison.OrdinalIgnoreCase) < 0 && copied.IndexOf("12345", StringComparison.Ordinal) < 0 &&
                 refused.Presenter.Connecting.Title.IndexOf("ABCDE", StringComparison.Ordinal) < 0,
                "a wrong invite offers no retry, and neither the copied details nor the overlay hold the code (" + copied + ")");
        }

        /// <summary>An invite join that ends with <paramref name="failure"/>, with the service's named delay.</summary>
        private static SessionsRig Failed(InternalLiveFailure failure, int retryAfter = 0)
        {
            var rig = new SessionsRig(service: true);
            rig.Presenter.Connect.SetInviteCode(Invite);
            rig.Presenter.Connect.JoinInvite();
            rig.Frame();
            rig.Control.Queries[0].Complete(InternalLiveFailure.None);
            rig.Frame();
            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, failure);
            rig.Control.Status.Internet = new InternalInternetStatus { ServiceConfigured = true, RetryAfterSeconds = retryAfter };
            rig.Frame();
            return rig;
        }

        private static void AHostSeesItsInvite()
        {
            var rig = new SessionsRig(service: true);
            rig.Status(InternalSessionRole.ListenHost, host: InternalHostPhase.Hosting);
            rig.Control.Status.Internet = new InternalInternetStatus { ServiceConfigured = true, ListingPhase = InternalListingPhase.Registering };
            rig.Frame();
            That(rig.Presenter.Session.InviteText == SessionPresenter.InvitePending, "the invite code shows once the service has registered the session");
            rig.Status(InternalSessionRole.ListenHost, host: InternalHostPhase.Hosting);
            rig.Control.Status.Internet = new InternalInternetStatus
            {
                ServiceConfigured = true,
                ListingPhase = InternalListingPhase.Online,
                Invite = new InternalInvite("ABCDE12345", Invite, "topiaforge://join/ABCDE12345", 1_800_000_600)
            };
            rig.Frame();
            That(rig.Presenter.Session.InviteText == SessionPresenter.InvitePrefix + Invite + SessionPresenter.InviteSuffix, "then SESSION shows it to the host");
            rig.Status(InternalSessionRole.Solo);
            rig.Frame();
            That(rig.Presenter.Session.InviteText.Length == 0, "and nothing once the session ended");
        }

        private static string ListEmptyText(SessionsRig rig, BrowseList list)
        {
            rig.Presenter.Browse.SelectList(list);
            rig.Frame();
            var text = rig.Presenter.Browse.EmptyText;
            rig.Presenter.Browse.SelectList(BrowseList.Lan);
            return text;
        }
    }
}
