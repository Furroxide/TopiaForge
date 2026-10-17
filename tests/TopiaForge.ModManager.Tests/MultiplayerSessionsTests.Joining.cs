using System;
using System.Linq;
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
            rig.Environment.GamePhase = WorldSessionPhase.Running;
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
            That(!rig.Control.Calls.Any(call => call.StartsWith("Join", StringComparison.Ordinal)), "the join waits until the game is idle");
            rig.Environment.GamePhase = WorldSessionPhase.Idle;
            rig.Frame();
            That(rig.Control.Calls.Contains("Join:Lan:relay"), "then joins the LAN session, never directly");

            var busy = BrowsingRig(ready: true);
            busy.Environment.GamePhase = WorldSessionPhase.LoadingWorld;
            busy.Presenter.Browse.Select(new string('1', 64));
            busy.Presenter.Browse.Join();
            busy.Frame();
            That(busy.Prompt == null && busy.Noticed("Wait for the current game") && !busy.Control.Calls.Any(call => call.StartsWith("Join", StringComparison.Ordinal)),
                "a game still loading is waited for");
            var hosting = BrowsingRig(ready: true);
            hosting.Status(InternalSessionRole.ListenHost, host: InternalHostPhase.Hosting);
            hosting.Frame();
            hosting.Presenter.Browse.Select(new string('1', 64));
            hosting.Presenter.Browse.Join();
            hosting.Frame();
            That(hosting.Noticed("Stop hosting before you join") && !hosting.Control.Calls.Any(call => call.StartsWith("Join", StringComparison.Ordinal)),
                "a host stops hosting before joining another session");
            var decision = SessionRealmGate.ForJoin(InternalSessionRole.ListenHost, WorldSessionPhase.Running, false);
            That(decision == RealmDecision.LeaveSessionFirst && SessionRealmGate.ForJoin(InternalSessionRole.Client, WorldSessionPhase.Running, false) == RealmDecision.LeaveSessionFirst,
                "a host or a joined player ends that session before joining another");
            That(SessionRealmGate.ForHost(InternalSessionRole.Solo, WorldSessionPhase.Running, false) == RealmDecision.PromoteInPlace &&
                 SessionRealmGate.ForHost(InternalSessionRole.Solo, WorldSessionPhase.Idle, false) == RealmDecision.StartAsHost &&
                 SessionRealmGate.ForJoin(InternalSessionRole.Solo, WorldSessionPhase.Idle, true) == RealmDecision.StandaloneRefused,
                "a solo game opens in place, the menu starts one, and a standalone mod refuses both");
            foreach (InternalSessionRole role in Enum.GetValues(typeof(InternalSessionRole)))
            {
                That(SessionRealmGate.MayLeave(role), "leaving is never refused (" + role + ")");
            }
        }

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
            That(ConnectingStepMap.PathOf(InternalJoinTargetKind.Direct, allowDirect: true) == "Direct" &&
                 ConnectingStepMap.PathOf(InternalJoinTargetKind.Invite, allowDirect: false) == "Relay" &&
                 ConnectingStepMap.PathOf(InternalJoinTargetKind.Lan, allowDirect: true) == "LAN",
                "the Connecting step names the path: LAN, Direct or Relay");
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
