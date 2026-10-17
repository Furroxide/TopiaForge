using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>CONNECT and the realm rules: the direct prompt, leaving a running game, and the connecting overlay's phases.</summary>
    internal static partial class MultiplayerSessionsTests
    {
        private const string TypedAddress = "203.0.113.7:27250";

        private static void DirectPromptCancellationContactsNothing()
        {
            // The disclosure comes first: closing it checks nothing at all.
            var first = new SessionsRig(acknowledged: false);
            first.Presenter.Connect.SetAddress(TypedAddress);
            first.Presenter.Connect.JoinAddress();
            first.Frame();
            first.Answer(ScreenPromptKind.Disclosure, false);
            That(first.Control.NetworkCalls == 0, "closing the disclosure contacts nothing");

            var rig = new SessionsRig();
            var connect = rig.Presenter.Connect;
            connect.SetAddress(TypedAddress);
            That(connect.AddressError.Length == 0 && connect.CanJoinAddress, "a typed host and port can be joined");
            connect.JoinAddress();
            rig.Frame();
            That(rig.Control.Calls.Where(call => call.StartsWith("Query", StringComparison.Ordinal)).SequenceEqual(new[] { "Query:Direct:relay" }),
                "the check runs under Hide my IP first, which contacts nothing beyond the local network");
            rig.Control.Queries[0].Complete(InternalLiveFailure.DirectNotAllowed);
            rig.Frame();
            That(rig.Prompt?.Kind == ScreenPromptKind.DirectConnection && rig.Prompt.Paragraphs[0] == MultiplayerPrivacyText.DirectBody,
                "a session that can only be reached directly asks the approved prompt");
            var before = rig.Control.Calls.Count;
            rig.Answer(ScreenPromptKind.DirectConnection, false);
            rig.Frame(10);
            That(rig.Control.Calls.Skip(before).All(call => call == "GetStatus") && rig.Control.NetworkCalls == 1,
                "cancelling contacts nothing: no direct check and no join (" + string.Join(", ", rig.Control.Calls.Skip(before)) + ")");
            That(rig.Presenter.HideMyIp && connect.ResultText == ConnectPresenter.NothingContacted && !connect.ResultText.Contains("203.0.113.7", StringComparison.Ordinal),
                "Hide my IP stays on, and the result says so without the address");

            connect.JoinAddress();
            rig.Frame();
            rig.Control.Queries[1].Complete(InternalLiveFailure.DirectNotAllowed);
            rig.Frame();
            rig.Answer(ScreenPromptKind.DirectConnection, true);
            That(rig.Control.Calls.Last(call => call.StartsWith("Query", StringComparison.Ordinal)) == "Query:Direct:direct", "allowing it checks the host directly");
            rig.Control.Queries[2].Complete(InternalLiveFailure.None, Server('e', "Typed host"));
            rig.Frame();
            That(rig.Control.Calls.Contains("Join:Direct:direct") && rig.Control.LastJoin!.AllowDirect, "and joins it directly, for this session only");
            That(rig.Presenter.Settings.Current.HideMyIp && rig.Presenter.HideMyIp, "Hide my IP stays on for every other session");

            var mismatch = new SessionsRig();
            mismatch.Presenter.Connect.SetAddress("192.0.2.4:27250");
            mismatch.Presenter.Connect.JoinAddress();
            mismatch.Frame();
            mismatch.Control.Queries[0].Complete(InternalLiveFailure.ContentMismatch, Server('f', "Strict host"), new[] { "Lobby 1.0.0 is missing." });
            mismatch.Frame();
            That(!mismatch.Control.Calls.Any(call => call.StartsWith("Join", StringComparison.Ordinal)) && mismatch.Presenter.Connect.ResultText.StartsWith("Needs 1 change", StringComparison.Ordinal) &&
                 mismatch.Presenter.Connect.CanInstallInLauncher, "a server that needs mods is not joined; the launcher can install them");
            mismatch.Presenter.Connect.InstallInLauncher();
            That(mismatch.Environment.Opened.Single() == "topiaforge://join/direct/" + new string('f', 64) + "/192.0.2.4/27250",
                "the hand-off is a direct link with the address the player typed, and no other");

            foreach (var bad in new[] { "not a host!", "host:0", "host:65536" })
            {
                mismatch.Presenter.Connect.SetAddress(bad);
                That(mismatch.Presenter.Connect.AddressError == ConnectPresenter.AddressRule && !mismatch.Presenter.Connect.CanJoinAddress, "'" + bad + "' is refused");
            }

            mismatch.Presenter.Connect.SetAddress("ABCDE-12345");
            That(mismatch.Presenter.Connect.AddressError == LiveFailureMessages.ServiceNotConfigured, "an invite needs the service, which this build does not reach");
            That(!mismatch.Presenter.Connect.CanJoinInvite, "so the invite field cannot join yet");
        }

        private static void JoiningWhileInAGameAsksToLeaveFirst()
        {
            var rig = BrowsingRig(ready: true);
            rig.Environment.Phase = SessionPhase.Running;
            rig.Presenter.Browse.Select(new string('1', 64));
            rig.Presenter.Browse.Join();
            rig.Frame();
            That(rig.Prompt?.Kind == ScreenPromptKind.Destructive && rig.Prompt.Title == ConnectingPresenter.LeaveTitle,
                "joining from a running game asks \"Leave current game?\" through the destructive modal");
            rig.Answer(ScreenPromptKind.Destructive, false);
            That(!rig.Control.Calls.Any(call => call.StartsWith("Join", StringComparison.Ordinal)) && rig.Environment.PendingMenu == null,
                "cancelling keeps the game and joins nothing");
            rig.Presenter.Browse.Join();
            rig.Answer(ScreenPromptKind.Destructive, true);
            That(rig.Environment.PendingMenu != null && !rig.Control.Calls.Any(call => call.StartsWith("Join", StringComparison.Ordinal)) &&
                 rig.Presenter.Connecting.IsVisible && rig.Presenter.Connecting.DetailOf(0) == ConnectingStepMap.LeavingGame,
                "confirming leaves the game first, on the overlay");
            rig.Environment.PendingMenu!(true, "Returned to the main menu.");
            rig.Frame();
            That(!Joined(rig) && rig.Prompt == null, "the join waits while the game still runs, and asks nothing again");
            rig.Environment.Phase = SessionPhase.Stopping;
            rig.Frame(3);
            That(!Joined(rig) && rig.Presenter.Connecting.IsVisible && rig.Presenter.Connecting.DetailOf(0) == ConnectingStepMap.LeavingGame,
                "and while the session has ended but the main menu still loads, which the runtime's rules count as busy");
            rig.Environment.Phase = SessionPhase.Idle;
            rig.Frame();
            That(rig.Control.Calls.Contains("Join:Lan:relay") && rig.Control.Calls.Count(call => call.StartsWith("Join", StringComparison.Ordinal)) == 1,
                "then joins the LAN session once, never directly");

            var busy = BrowsingRig(ready: true);
            busy.Environment.Phase = SessionPhase.LoadingWorld;
            busy.Presenter.Browse.Select(new string('1', 64));
            busy.Presenter.Browse.Join();
            busy.Frame(3);
            That(busy.Prompt == null && busy.Presenter.Connecting.IsVisible && busy.Presenter.Connecting.DetailOf(0) == ConnectingStepMap.WaitingForGame &&
                 busy.Presenter.Connecting.CanCancel && !Joined(busy), "a game still loading is waited for on the overlay, and nothing is joined");
            busy.Environment.Phase = SessionPhase.Running;
            busy.Frame();
            That(busy.Prompt?.Title == ConnectingPresenter.LeaveTitle && !busy.Presenter.Connecting.IsVisible && !Joined(busy),
                "once it runs, the join asks to leave it first");
            busy.Answer(ScreenPromptKind.Destructive, false);
            That(!Joined(busy) && busy.Environment.PendingMenu == null, "and saying no joins nothing");

            var hosting = BrowsingRig(ready: true);
            hosting.Status(InternalSessionRole.ListenHost, host: InternalHostPhase.Hosting);
            hosting.Frame();
            hosting.Presenter.Browse.Select(new string('1', 64));
            hosting.Presenter.Browse.Join();
            hosting.Frame();
            That(hosting.Noticed("Stop hosting before you join") && !Joined(hosting), "a host stops hosting before joining another session");

            var standalone = BrowsingRig(ready: true);
            standalone.Environment.Load("io.example.solo", "standalone");
            standalone.Presenter.Browse.Select(new string('1', 64));
            standalone.Presenter.Browse.Join();
            standalone.Frame();
            That(standalone.Noticed("Solo refuses multiplayer; disable it to join.") && !Joined(standalone), "a standalone mod refuses the join, by name");
        }

        /// <summary>
        /// The realm comes from the runtime, which reads a real orchestrator's <c>RealmPhase</c>: while the main menu loads,
        /// the world session is already idle, and a gate over that raw phase let a join through during the load (#224's fixed
        /// bug). Through the presenters, a join asked for then waits on the overlay, and the provider hears of it only once
        /// the menu has loaded.
        /// </summary>
        private static void AJoinDuringAMainMenuLoadWaits(string root)
        {
            using var fixture = new SessionFixture(Path.Combine(root, "menu-load"));
            var rig = BrowsingRig(ready: true);
            rig.Environment.PhaseSource = () => fixture.Hosted.RealmPhase;
            var menuLoad = new TaskCompletionSource<OperationResult<bool>>();
            fixture.Menu = _ => menuLoad.Task;
            var menu = fixture.Hosted.ReturnToMainMenuAsync();
            fixture.Until(() => fixture.MenuLoads == 1);
            That(fixture.Hosted.Current.Phase == SessionPhase.Idle && fixture.Hosted.RealmPhase == SessionPhase.Stopping,
                "the main menu is loading: the session is idle, but still changing");
            rig.Presenter.Browse.Select(new string('1', 64));
            rig.Presenter.Browse.Join();
            rig.Frame(5);
            That(!Joined(rig) && rig.Prompt == null && rig.Presenter.Connecting.IsVisible && rig.Presenter.Connecting.DetailOf(0) == ConnectingStepMap.WaitingForGame,
                "a join asked for during the load waits on the overlay, and is not allowed (" + string.Join(", ", rig.Control.Calls.Distinct()) + ")");
            rig.Presenter.Host.SetName("Early");
            That(!rig.Presenter.Host.CanHost && rig.Presenter.Host.ModeText.StartsWith("Wait", StringComparison.Ordinal), "nor can hosting start meanwhile");

            menuLoad.SetResult(OperationResult<bool>.Success(true));
            fixture.Wait(menu);
            That(fixture.Hosted.RealmPhase == SessionPhase.Idle, "the menu has loaded");
            rig.Frame();
            That(rig.Control.Calls.Count(call => call.StartsWith("Join", StringComparison.Ordinal)) == 1 && rig.Control.Calls.Contains("Join:Lan:relay"),
                "then the join goes through, once");
        }

        /// <summary>Leaving, ending and cancelling go through the runtime's realm, which never refuses, busy or not.</summary>
        private static void LeavingIsNeverRefused()
        {
            foreach (var phase in Seam.Phases)
            {
                var joined = new SessionsRig();
                joined.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
                joined.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-leave", "p1");
                joined.Frame();
                joined.Environment.Phase = phase;
                joined.Presenter.Session.Leave();
                joined.Answer(ScreenPromptKind.Destructive, true);
                That(joined.Control.Calls.Contains("Leave") && joined.Noticed("You left the session."), "a joined player leaves while the game is " + phase);

                var host = HostingWithAda();
                host.Environment.Phase = phase;
                host.Presenter.Session.EndForEveryone();
                host.Answer(ScreenPromptKind.Destructive, true);
                That(host.Control.Calls.Contains("StopHosting") && host.Control.Calls.Contains("Leave"), "a host ends the session while the game is " + phase);
            }

            var throwing = HostingWithAda();
            throwing.Control.ThrowOnLeave = true;
            throwing.Presenter.Session.EndForEveryone();
            throwing.Answer(ScreenPromptKind.Destructive, true);
            That(throwing.Noticed("The session ended for everyone. The multiplayer provider failed while leaving") && !throwing.Notices.Any(notice => Shows(notice.Text)),
                "a provider that throws while leaving is said in this game's words, with no address");
        }

        private static bool Joined(SessionsRig rig) => rig.Control.Calls.Any(call => call.StartsWith("Join", StringComparison.Ordinal));

        private static void ConnectingWalksThePhases()
        {
            var rig = BrowsingRig(ready: true);
            rig.Presenter.Browse.Select(new string('1', 64));
            rig.Presenter.Browse.Join();
            rig.Frame();
            var overlay = rig.Presenter.Connecting;
            That(overlay.IsVisible && overlay.StepLabels.SequenceEqual(new[] { "RESOLVING", "CONNECTING", "AUTHENTICATING", "VERIFYING MODS", "LOADING WORLD", "SYNCHRONIZING", "SPAWNING" }),
                "the overlay shows the join's seven phases");
            Step(rig, InternalJoinPhase.Resolving, null, ConnectingStep.Resolving, ConnectingStepMap.FindingHost);
            Step(rig, InternalJoinPhase.AwaitingPassword, null, ConnectingStep.Connecting, ConnectingStepMap.PasswordNeeded);
            That(overlay.NeedsPassword, "a session that needs a password asks for it");
            overlay.SubmitPassword(string.Empty);
            That(overlay.PasswordError == ConnectingPresenter.PasswordRule && !rig.Control.Calls.Contains("SupplyPassword"), "an empty password is refused here");
            overlay.SubmitPassword("open sesame");
            That(rig.Control.Calls.Contains("SupplyPassword") && overlay.PasswordError.Length == 0, "a password goes to the join once");
            Step(rig, InternalJoinPhase.Connecting, null, ConnectingStep.Connecting, "LAN");
            Step(rig, InternalJoinPhase.Synchronizing, MultiplayerSessionState.Connecting, ConnectingStep.Authenticating, ConnectingStepMap.WaitingForHost);
            rig.Control.IsReadinessHeld = true;
            Step(rig, InternalJoinPhase.Synchronizing, MultiplayerSessionState.Synchronizing, ConnectingStep.LoadingWorld, ConnectingStepMap.LoadingWorld, held: true);
            rig.Control.IsReadinessHeld = false;
            Step(rig, InternalJoinPhase.Synchronizing, MultiplayerSessionState.Synchronizing, ConnectingStep.Synchronizing, ConnectingStepMap.ReceivingWorld);
            That(!overlay.Steps.IsSkipped((int)ConnectingStep.LoadingWorld), "a world that loaded stays done, not skipped");
            ((ConnectingPresenter)overlay).ReportSynchronization(3, 7);
            rig.Frame();
            That(overlay.DetailOf((int)ConnectingStep.Synchronizing) == "3/7", "the world's synchronization reads n of m once reported");
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Control.Status.Server = Server('1', "LAN game");
            rig.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-ready", "p1");
            rig.Frame();
            That(overlay.Steps.Complete && rig.Noticed("Joined LAN game."), "in session, the steps complete and the join is toasted");
            rig.Frame(30);
            That(!overlay.IsVisible && rig.Store.Stored!.History.Single().ServerId == new string('1', 64), "the overlay closes and the server is remembered by id");

            var direct = new SessionsRig();
            direct.Presenter.Connect.SetAddress("192.0.2.4:27250");
            direct.Presenter.Connect.JoinAddress();
            direct.Frame();
            direct.Control.Queries[0].Complete(InternalLiveFailure.None, Server('2', "Typed"));
            direct.Frame();
            Step(direct, InternalJoinPhase.Connecting, null, ConnectingStep.Connecting, "LAN");
            var none = InternalConnectionRoute.None;
            That(ConnectingStepMap.PathOf(InternalJoinTargetKind.Direct, allowDirect: true, none) == "Direct" &&
                 ConnectingStepMap.PathOf(InternalJoinTargetKind.Direct, allowDirect: false, none) == "LAN" &&
                 ConnectingStepMap.PathOf(InternalJoinTargetKind.Invite, allowDirect: false, none) == "Relay" &&
                 ConnectingStepMap.PathOf(InternalJoinTargetKind.Invite, allowDirect: true, none) == "Relay" &&
                 ConnectingStepMap.PathOf(InternalJoinTargetKind.ListedServer, allowDirect: false, none) == "Relay" &&
                 ConnectingStepMap.PathOf(InternalJoinTargetKind.Lan, allowDirect: true, none) == "LAN",
                "before the link is up, the Connecting step names the path the target takes: LAN, Direct or Relay");
            That(ConnectingStepMap.PathOf(InternalJoinTargetKind.Invite, allowDirect: true, InternalConnectionRoute.Direct) == "Direct" &&
                 ConnectingStepMap.PathOf(InternalJoinTargetKind.Invite, allowDirect: true, InternalConnectionRoute.Relayed) == "Relay",
                "and once it is up, the route the status reports");
        }

        /// <summary>Puts the join in <paramref name="phase"/> and checks the stepper shows <paramref name="step"/> with <paramref name="detail"/>.</summary>
        private static void Step(SessionsRig rig, InternalJoinPhase phase, MultiplayerSessionState? session, ConnectingStep step, string detail, bool held = false)
        {
            rig.Status(phase == InternalJoinPhase.Synchronizing ? InternalSessionRole.Client : InternalSessionRole.Solo, phase);
            rig.Control.Status.IsReadinessHeld = held;
            rig.Environment.SessionSnapshot = session == null ? null : SessionsRig.ClientSnapshot(session.Value, "s-" + phase + session, "p1");
            rig.Frame();
            var steps = rig.Presenter.Connecting.Steps;
            That(steps.Active == (int)step && steps.Failed < 0 && rig.Presenter.Connecting.DetailOf((int)step) == detail,
                phase + " shows " + step + " with \"" + detail + "\" (active " + steps.Active + ", \"" + rig.Presenter.Connecting.DetailOf(steps.Active < 0 ? 0 : steps.Active) + "\")");
        }

        /// <summary>A rig browsing the local network, hearing one Ready session, '1', with this game's mods identified.</summary>
        internal static SessionsRig BrowsingRig(bool ready)
        {
            var rig = new SessionsRig();
            rig.Control.Lan = new[] { Listing('1', "LAN game") };
            rig.Presenter.Browse.StartSearch();
            rig.Frame();
            if (ready)
            {
                rig.Control.Queries.Single().Complete(InternalLiveFailure.None, Server('1', "LAN game"));
                rig.Frame();
            }

            return rig;
        }
    }
}
