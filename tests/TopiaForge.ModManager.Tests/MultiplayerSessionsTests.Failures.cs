using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>The failure view: every failure's words and step, cancelling, retrying, trusting, the launcher and copied details.</summary>
    internal static partial class MultiplayerSessionsTests
    {
        private static readonly string[] AddressSecrets = { "203.0.113.7", "198.51.100.4", "2001:db8", "play.example.org", "192.0.2.9" };

        private static void EveryFailureHasAClearMessage()
        {
            var fallback = LiveFailureMessages.ForJoin((InternalLiveFailure)999, InternalAdmissionCode.Other);
            var titles = new HashSet<string>(StringComparer.Ordinal);
            foreach (InternalLiveFailure failure in Enum.GetValues(typeof(InternalLiveFailure)))
            {
                var join = LiveFailureMessages.ForJoin(failure, InternalAdmissionCode.Other);
                var host = LiveFailureMessages.ForHost(failure);
                That(join.Title.Length > 0 && host.Title.Length > 0, failure + " has a title when joining and when hosting");
                if (failure == InternalLiveFailure.None) continue;
                That(join.Message.Length > 0 && host.Message.Length > 0 && join.Title != fallback.Title,
                    failure + " is worded on its own, not by the fallback (" + join.Title + ")");
                That(join.Message.EndsWith(".", StringComparison.Ordinal) && !Shows(join.Message) && !Shows(host.Message), failure + "'s words are a sentence and name no address");
                titles.Add(join.Title);
            }

            That(titles.Count >= 20, "the failures read as different failures (" + titles.Count + " titles)");
            var codes = new HashSet<string>(StringComparer.Ordinal);
            foreach (InternalAdmissionCode code in Enum.GetValues(typeof(InternalAdmissionCode)))
            {
                var refusal = LiveFailureMessages.ForJoin(InternalLiveFailure.Rejected, code);
                That(refusal.Message.Length > 0 && codes.Add(refusal.Message), "the host's " + code + " refusal is worded on its own");
            }

            That(LiveFailureMessages.ForJoin(InternalLiveFailure.ContentUnreadable, InternalAdmissionCode.Other).Message == LiveFailureMessages.UnreadableContent &&
                 LiveFailureMessages.ForHost(InternalLiveFailure.ContentUnreadable).Message == LiveFailureMessages.UnreadableContent,
                "unreadable mods are worded alike for hosting and joining");
            That(LiveFailureMessages.ForJoin(InternalLiveFailure.ContentMismatch, InternalAdmissionCode.Other).LauncherHelps &&
                 LiveFailureMessages.ForJoin(InternalLiveFailure.Rejected, InternalAdmissionCode.Content).LauncherHelps &&
                 LiveFailureMessages.ForJoin(InternalLiveFailure.HostIdentityChanged, InternalAdmissionCode.Other).CanTrust,
                "the launcher helps with mods, and a changed server can be trusted");
        }

        private static void FailuresStopAtTheirStep()
        {
            var cases = new (InternalJoinPhase Reached, InternalLiveFailure Failure, InternalAdmissionCode Code, ConnectingStep Stop, int Skipped)[]
            {
                (InternalJoinPhase.Resolving, InternalLiveFailure.Unreachable, InternalAdmissionCode.Other, ConnectingStep.Resolving, 0),
                (InternalJoinPhase.Resolving, InternalLiveFailure.ContentMismatch, InternalAdmissionCode.Other, ConnectingStep.VerifyingMods, 0b110),
                (InternalJoinPhase.Resolving, InternalLiveFailure.WrongGameBuild, InternalAdmissionCode.Other, ConnectingStep.VerifyingMods, 0b110),
                (InternalJoinPhase.Resolving, InternalLiveFailure.ContentUnreadable, InternalAdmissionCode.Other, ConnectingStep.VerifyingMods, 0b110),
                (InternalJoinPhase.Connecting, InternalLiveFailure.HandshakeFailed, InternalAdmissionCode.Other, ConnectingStep.Authenticating, 0),
                (InternalJoinPhase.Connecting, InternalLiveFailure.Rejected, InternalAdmissionCode.PasswordWrong, ConnectingStep.Authenticating, 0),
                (InternalJoinPhase.Connecting, InternalLiveFailure.Rejected, InternalAdmissionCode.Content, ConnectingStep.VerifyingMods, 0b100),
                (InternalJoinPhase.Connecting, InternalLiveFailure.ConnectionLost, InternalAdmissionCode.Other, ConnectingStep.Connecting, 0)
            };

            foreach (var (reached, failure, code, stop, skipped) in cases)
            {
                var rig = BrowsingRig(ready: true);
                rig.Presenter.Browse.Select(new string('1', 64));
                rig.Presenter.Browse.Join();
                rig.Frame();
                rig.Status(InternalSessionRole.Solo, reached);
                rig.Frame();
                rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, failure, code: code);
                rig.Frame();
                var steps = rig.Presenter.Connecting.Steps;
                That(rig.Presenter.Connecting.HasFailed && steps.Failed == (int)stop && steps.SkippedMask == skipped,
                    failure + "/" + code + " after " + reached + " stops at " + stop + " with " + skipped + " skipped (" + steps.Failed + ", " + steps.SkippedMask + ")");
                var replay = new TopiaForge.Mods.UnityUi.TopiaForgePhaseStepperState(ConnectingSteps.Count);
                ConnectingStepReplay.Apply(steps, new StateOperations(replay));
                That(replay.FailedIndex == (int)stop, "the stepper shows the failure at " + stop);
                for (var step = 0; step < ConnectingSteps.Count; step++)
                {
                    var expected = step == (int)stop ? TopiaForge.Mods.UnityUi.TopiaForgeStepStatus.Failed
                        : (skipped & (1 << step)) != 0 ? TopiaForge.Mods.UnityUi.TopiaForgeStepStatus.Skipped
                        : step < (int)stop ? TopiaForge.Mods.UnityUi.TopiaForgeStepStatus.Done
                        : TopiaForge.Mods.UnityUi.TopiaForgeStepStatus.Pending;
                    That(replay.StatusOf(step) == expected, failure + ": step " + step + " shows " + expected + " (" + replay.StatusOf(step) + ")");
                }
            }
        }

        private static void CancelWorksThroughout()
        {
            foreach (var phase in new[] { InternalJoinPhase.Resolving, InternalJoinPhase.AwaitingPassword, InternalJoinPhase.Connecting, InternalJoinPhase.Synchronizing, InternalJoinPhase.Reconnecting })
            {
                var rig = BrowsingRig(ready: true);
                rig.Presenter.Browse.Select(new string('1', 64));
                rig.Presenter.Browse.Join();
                rig.Status(InternalSessionRole.Solo, phase);
                rig.Frame();
                That(rig.Presenter.Connecting.CanCancel, "a join that is " + phase + " can be cancelled");
                rig.Presenter.Connecting.Cancel();
                rig.Frame();
                That(rig.Control.Calls.Contains("Leave") && !rig.Presenter.Connecting.IsVisible && rig.Noticed("Join cancelled."), "cancelling " + phase + " leaves and closes the overlay");
                rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Cancelled);
                rig.Frame();
                That(!rig.Presenter.Connecting.IsVisible, "the cancelled join's end shows no failure");
            }

            var leaving = BrowsingRig(ready: true);
            leaving.Environment.Phase = TopiaForge.ModManager.Core.SessionPhase.Running;
            leaving.Presenter.Browse.Select(new string('1', 64));
            leaving.Presenter.Browse.Join();
            leaving.Answer(ScreenPromptKind.Destructive, true);
            leaving.Presenter.Connecting.Dismiss();
            leaving.Environment.PendingMenu!(true, "Returned to the main menu.");
            leaving.Environment.Phase = TopiaForge.ModManager.Core.SessionPhase.Idle;
            leaving.Frame(5);
            That(!leaving.Control.Calls.Any(call => call.StartsWith("Join", StringComparison.Ordinal)) && !leaving.Presenter.Connecting.IsVisible,
                "dismissing while the game closes cancels the join that was to follow");
        }

        private static void RetryTrustAndTheLauncher()
        {
            var rig = BrowsingRig(ready: true);
            rig.Presenter.Browse.Select(new string('1', 64));
            rig.Presenter.Browse.Join();
            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.TimedOut);
            rig.Frame();
            var overlay = rig.Presenter.Connecting;
            That(overlay.HasFailed && overlay.CanRetry && overlay.CanCopyDetails && overlay.FailureTitle == "Timed out", "a timed-out join can be retried");
            var joins = rig.Control.Calls.Count(call => call.StartsWith("Join", StringComparison.Ordinal));
            overlay.Retry();
            rig.Frame();
            That(rig.Control.Calls.Count(call => call.StartsWith("Join", StringComparison.Ordinal)) == joins + 1 && overlay.IsVisible && !overlay.HasFailed, "retrying joins again");

            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.ContentMismatch);
            rig.Control.Status.ContentProblems = new[] { "Lobby 1.0.0 is missing." };
            rig.Frame();
            That(!overlay.CanRetry && overlay.CanOpenLauncher && overlay.FailureLines.Single() == "Lobby 1.0.0 is missing.", "mods that differ hand over to the launcher");
            overlay.OpenLauncher();
            That(rig.Environment.Opened.Single() == "topiaforge://join/server/" + new string('1', 64) && !overlay.IsVisible,
                "the hand-off names the server, never an address, and closes the overlay");

            var changed = new SessionsRig();
            changed.Presenter.Connect.SetAddress("192.0.2.9:27250");
            changed.Presenter.Connect.JoinAddress();
            changed.Frame();
            changed.Control.Queries[0].Complete(InternalLiveFailure.None, Server('3', "Typed"));
            changed.Frame();
            changed.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.HostIdentityChanged);
            changed.Control.Status.ChangedServerId = new string('4', 64);
            changed.Frame();
            That(changed.Presenter.Connecting.CanTrust, "a typed host that now serves another server may be trusted");
            changed.Presenter.Connecting.Trust();
            changed.Frame();
            That(changed.Control.LastJoin!.TrustedServerId == new string('4', 64), "trusting joins that one server only");
        }

        /// <summary>
        /// A typed host whose server could not be checked, because this game's record of earlier servers could not be read,
        /// says so in words of its own: CONNECT's check asks the player as it does for a changed server, and a join that
        /// ends that way offers both trust and retry.
        /// </summary>
        private static void AnUncheckedServerMayBeTrustedOrTriedAgain()
        {
            var served = new string('6', 64);
            var rig = new SessionsRig();
            rig.Presenter.Connect.SetAddress("192.0.2.9:27250");
            rig.Presenter.Connect.JoinAddress();
            rig.Frame();
            rig.Control.Queries[0].Complete(InternalLiveFailure.KnownHostsUnreadable, changed: served);
            rig.Frame();
            That(rig.Prompt?.Kind == ScreenPromptKind.Destructive && rig.Prompt.Title == ConnectPresenter.TrustTitle &&
                 rig.Prompt.Paragraphs[0] == LiveFailureMessages.UncheckedServer && rig.Presenter.Connect.ResultText == LiveFailureMessages.UncheckedServer,
                "a server the game could not check asks the player, and says why (" + rig.Presenter.Connect.ResultText + ")");
            rig.Answer(ScreenPromptKind.Destructive, false);
            That(!rig.Control.Calls.Any(call => call.StartsWith("Join", StringComparison.Ordinal)), "declining joins nothing");
            rig.Presenter.Connect.JoinAddress();
            rig.Frame();
            rig.Control.Queries[1].Complete(InternalLiveFailure.KnownHostsUnreadable, changed: served);
            rig.Frame();
            rig.Answer(ScreenPromptKind.Destructive, true);
            That(rig.Control.LastJoin?.TrustedServerId == served, "trusting it joins that one server only");

            var joined = new SessionsRig();
            joined.Presenter.Connect.SetAddress("192.0.2.9:27250");
            joined.Presenter.Connect.JoinAddress();
            joined.Frame();
            joined.Control.Queries[0].Complete(InternalLiveFailure.None, Server('3', "Typed"));
            joined.Frame();
            joined.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.KnownHostsUnreadable);
            joined.Control.Status.ChangedServerId = new string('7', 64);
            joined.Frame();
            var overlay = joined.Presenter.Connecting;
            That(overlay.HasFailed && overlay.FailureTitle == "Server not checked" && overlay.CanTrust && overlay.CanRetry,
                "a join that ends unchecked offers trust and retry (" + overlay.FailureTitle + ")");
            overlay.Trust();
            joined.Frame();
            That(joined.Control.LastJoin!.TrustedServerId == new string('7', 64), "and trusting joins that one server only");
        }

        private static void CopiedDetailsNeverHoldAnAddress()
        {
            var rig = new SessionsRig();
            rig.Presenter.Connect.SetAddress(TypedAddress);
            rig.Presenter.Connect.JoinAddress();
            rig.Frame();
            rig.Control.Queries[0].Complete(InternalLiveFailure.None, Server('5', "play.example.org"));
            rig.Frame();
            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Rejected, code: InternalAdmissionCode.Content);
            rig.Control.Status.HostMessage = "come back to 198.51.100.4:27250 or [2001:db8::5]:27250";
            rig.Control.Status.JoinDetail = "The host refused the join.";
            rig.Control.Status.ContentProblems = new[] { "play.example.org runs Lobby 2.0.0", "ask 192.0.2.9 for help" };
            rig.Control.Status.ContentProblemsFromHost = true;
            rig.Frame();
            var overlay = rig.Presenter.Connecting;
            That(overlay.HasFailed && overlay.CanCopyDetails, "the failure can be copied");
            overlay.CopyDetails();
            var details = rig.Environment.Copied.Single();
            That(details.Contains("Failure: Rejected (12)", StringComparison.Ordinal) && details.Contains("Admission: content", StringComparison.Ordinal) &&
                 details.Contains("Game build: 2545", StringComparison.Ordinal), "the details name the failure, the host's code and the build");
            That(!Shows(details) && !details.Contains("203.0.113.7", StringComparison.Ordinal), "and never an address, typed or the host's (" + details + ")");
            That(!Shows(overlay.HostSays) && overlay.HostSays.StartsWith(HostText.HostSaysPrefix, StringComparison.Ordinal) && !overlay.FailureLines.Any(Shows),
                "the failure view shows the host's words attributed and redacted");
        }

        private static void LauncherLinksCarryNoAddressUnlessTyped()
        {
            var id = new string('a', 64);
            That(LauncherJoinLink.ForServer(id) == "topiaforge://join/server/" + id && LauncherJoinLink.ForServer("nope") == null, "a server link names the server id");
            That(LauncherJoinLink.ForInvite("abcde-12345") == "topiaforge://join/ABCDE12345" && LauncherJoinLink.ForInvite("ABCDE") == null, "an invite link carries the canonical code");
            That(LauncherJoinLink.ForTyped("203.0.113.7:27260", null) == null, "a typed host without a known server has no link");
            That(LauncherJoinLink.ForTyped(" 203.0.113.7:27260 ", id) == "topiaforge://join/direct/" + id + "/203.0.113.7/27260", "a typed host becomes the direct link the player typed");
            That(LauncherJoinLink.ForTyped("[2001:DB8::7]", id) == "topiaforge://join/direct/" + id + "/2001:db8::7/27250", "an IPv6 host is normalized, on the default port");
            That(LauncherJoinLink.ForTyped("Play.Example.org:4000", id) == "topiaforge://join/direct/" + id + "/play.example.org/4000", "a host name is lower case");
            var typedLink = "TOPIAFORGE://join/direct/" + id + "/198.51.100.9/27263";
            That(LauncherJoinLink.ForTyped(typedLink, null) == typedLink, "a typed link passes through as typed");
            foreach (var bad in new[] { "topiaforge://join/server/x", "topiaforge://join/direct/" + id + "/host/0", "topiaforge://join/a?b", "topiaforge://JOIN/ABCDE12345", "http://example.org" })
            {
                That(!LauncherJoinLink.IsSessionLink(bad), "'" + bad + "' is not a session link");
            }

            That(LauncherJoinLink.ForTyped("fe80::1%3", id) == null && LauncherJoinLink.ForTyped("bad_host!", id) == null, "a scoped or malformed host has no link");
        }

        /// <summary>Gets whether <paramref name="text"/> shows any of the addresses the tests plant.</summary>
        internal static bool Shows(string text)
        {
            foreach (var secret in AddressSecrets)
            {
                if (text.IndexOf(secret, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            return false;
        }

        /// <summary>The kit's stepper state behind the replay, as the view's widget is.</summary>
        private sealed class StateOperations : IStepperOperations
        {
            private readonly TopiaForge.Mods.UnityUi.TopiaForgePhaseStepperState state;

            internal StateOperations(TopiaForge.Mods.UnityUi.TopiaForgePhaseStepperState state)
            {
                this.state = state;
            }

            public bool Reset() => state.Reset();

            public bool Skip(int index) => state.Skip(index);

            public bool MoveTo(int index) => state.MoveTo(index);

            public bool Fail() => state.Fail();

            public bool Complete() => state.Complete();
        }
    }
}
