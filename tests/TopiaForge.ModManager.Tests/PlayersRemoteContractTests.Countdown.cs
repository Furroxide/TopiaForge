using System;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class PlayersRemoteContractTests
    {
        internal static void CountdownSurvivesReplacement()
        {
            using var peers = new Peers();
            var target = peers.Host.Player(peers.First.Id);
            var deadline = peers.Host.Ui.ClockTime + 30;
            var windowTimer = new UiCountdown(deadline, "Window", UiTone.Warning, "window-timer");
            var hudTimer = new UiCountdown(deadline, "HUD", id: "hud-timer");
            var clicks = 0;
            var button = new UiButton("confirm", "Confirm", () => clicks++);
            var windowContent = new UiColumn(new UiText("Keep me"), windowTimer, button);
            using var window = (RemoteUiSurface)target.Ui.CreateSurface(new UiSurfaceRequest("window", "Window", "Before", content: windowContent)).Value!;
            using var hud = (RemoteUiSurface)target.Ui.CreateSurface(new UiSurfaceRequest("hud", "HUD", "Read only", UiSurfaceKind.Hud, 320, 160, hudTimer)).Value!;
            var oldEpoch = windowTimer.ClockEpoch;
            var identity = windowTimer.ClockIdentity;
            var stream = window.Stream;
            hud.Hide(); peers.Rig.Frames(12);
            var oldWindow = peers.FirstServices.Ui.Surfaces.Single(surface => surface.Request.Kind == UiSurfaceKind.Window);
            That(oldWindow.TryFindNode("window-timer", out _), "the guest sees a live countdown before replacement");

            That(peers.Rig.Host.Control.ReplaceSession().Succeeded, "the listen host replaces the session");
            peers.Rig.Frames(12);
            var restored = peers.FirstServices.Ui.Surfaces.Single(surface => surface.Request.Kind == UiSurfaceKind.Window);
            var restoredHud = peers.FirstServices.Ui.Surfaces.Single(surface => surface.Request.Kind == UiSurfaceKind.Hud);
            That(!ReferenceEquals(restored, oldWindow) && !oldWindow.IsVisible && window.Stream != stream && window.Stream != 0 && !window.Dirty,
                "the replacement stream retires the old copy and converges on the same host handle");
            That(restored.Content is UiColumn column && column.Children[0] is UiText { Text: "Keep me" }
                && column.Children[1] is UiText { Text: "Window 0", Tone: UiTone.Warning }
                && restoredHud.Content is UiText { Text: "HUD 0" } && !hud.IsVisible && !restoredHud.IsVisible,
                "the replacement preserves sibling content, expired display, and hidden HUD state");
            That(ReferenceEquals(window.Content, windowContent) && windowTimer.EndsAt == deadline
                && ReferenceEquals(windowTimer.ClockIdentity, identity) && windowTimer.ClockEpoch == oldEpoch,
                "the author retains its original content, deadline and clock identity");
            That(restored.ActivateButton("confirm").Succeeded, "the recovered window still offers its button");
            window.SetBody("After"); hud.Show(); peers.Rig.Frames(12);
            That(clicks == 1 && restored.Body == "After" && restoredHud.IsVisible, "callbacks, body and visibility remain usable on the replacement stream");

            var revision = window.Revision;
            That(window.SetContent(new UiColumn(windowTimer, button)).ErrorCode == ModErrorCode.InvalidArgument
                && window.Revision == revision && ReferenceEquals(window.Content, windowContent) && !window.Dirty,
                "new stale content is refused without changing the retained revision or send state");
            var invalid = target.Ui.CreateSurface(new UiSurfaceRequest("stale", "Stale", "", content: windowTimer));
            That(invalid.ErrorCode == ModErrorCode.InvalidArgument, "new surfaces also reject the old authored timer");
            var fresh = new UiCountdown(peers.Host.Ui.ClockTime + 45, id: "fresh");
            That(window.SetContent(new UiColumn(fresh, button)).Succeeded && window.Revision == revision + 1,
                "the retained handle accepts fresh content and advances the revision once");
            peers.Rig.Frames(12);
            That(restored.TryFindNode("fresh", out var timer) && ((UiCountdown)timer!).EndsAt == fresh.EndsAt,
                "the fresh timer arrives on the same recovered guest surface with its exact deadline");
        }

        internal static void CountdownWidgets()
        {
            using var peers = new Peers();
            var target = peers.Host.Player(peers.First.Id);
            var deadline = peers.Host.Ui.ClockTime + 30;
            var result = target.Ui.CreateSurface(new UiSurfaceRequest("timer", "Timer", "", content:
                new UiColumn(new UiProgress(.25f, "Tasks", UiTone.Success, "tasks"), new UiCountdown(deadline, "Remaining", id: "timer"))));
            That(result.Succeeded, "targeted widgets are accepted: " + Describe(result));
            peers.Rig.Frames(12);
            That(peers.FirstServices.Ui.Surfaces.Count == 1, "both widgets reach the targeted recipient");
            var remote = peers.FirstServices.Ui.Surfaces[0];
            That(remote.TryFindNode("timer", out var timer) && ((UiCountdown)timer!).EndsAt == deadline, "targeted wire preserves original deadline");
            That(remote.TryFindNode("tasks", out var progress) && ((UiProgress)progress!).Fraction == .25f, "targeted progress keeps its fraction");
            That(remote.TryGetCountdownSeconds("timer", out var before), "remote countdown is readable");
            peers.Rig.Frames(120);
            That(remote.TryGetCountdownSeconds("timer", out var after) && after < before, "targeted countdown advances with no update message");
            result.Value!.SetBody("Changed"); peers.Rig.Frames(12);
            That(remote.TryFindNode("timer", out timer) && ((UiCountdown)timer!).EndsAt == deadline, "body updates never reset the deadline");
            var hostSurface = (RemoteUiSurface)result.Value!;
            void RejectUpdate(UiNode content, ModErrorCode expectedCode, string cause)
            {
                var oldContent = hostSurface.Content;
                var oldRecipientContent = remote.Content;
                var oldRevision = hostSurface.Revision;
                var oldDirty = hostSurface.Dirty;
                var oldStream = hostSurface.Stream;
                var rejected = hostSurface.SetContent(content);
                That(!rejected.Succeeded && rejected.ErrorCode == expectedCode && rejected.ErrorMessage.Contains(cause),
                    "remote content update reports " + expectedCode + " with cause: " + cause);
                That(ReferenceEquals(hostSurface.Content, oldContent) && hostSurface.Revision == oldRevision &&
                    hostSurface.Dirty == oldDirty && hostSurface.Stream == oldStream,
                    "rejected remote update preserves host composition, revision and send state");
                peers.Rig.Frames(12);
                That(ReferenceEquals(remote.Content, oldRecipientContent) &&
                    remote.TryFindNode("timer", out var retained) && ((UiCountdown)retained!).EndsAt == deadline,
                    "rejected remote update leaves the recipient's composition and original deadline unchanged");
            }
            RejectUpdate(new UiColumn(new UiProgress(.5f, id: "dup"), new UiCountdown(deadline, id: "dup")),
                ModErrorCode.InvalidArgument, "duplicate control id 'dup'");
            UiNode deep = new UiText("Deep");
            for (var i = 0; i < 33; i++) deep = new UiScroll(deep);
            RejectUpdate(deep, ModErrorCode.InvalidArgument, "deeper than 32");
            var groups = new UiNode[17];
            for (var group = 0; group < groups.Length; group++)
            {
                var children = new UiNode[256];
                for (var child = 0; child < children.Length; child++) children[child] = new UiText("Item");
                groups[group] = new UiColumn(children);
            }
            RejectUpdate(new UiColumn(groups), ModErrorCode.InvalidArgument, "more than 4096 nodes");
            var malformed = target.Ui.CreateSurface(new UiSurfaceRequest("bad", "Bad", "", content: new UiText(new string('x', 4097))));
            That(malformed.ErrorCode == ModErrorCode.InvalidArgument && malformed.ErrorMessage.Contains("4096"), "invalid shape names its cause instead of UI-full");
            var large = new UiColumn(new UiText(new string('x', 4096)), new UiText(new string('x', 4096)), new UiText(new string('x', 4096)),
                new UiText(new string('x', 4096)), new UiText(new string('x', 4096)), new UiText(new string('x', 4096)), new UiText(new string('x', 4096)), new UiText(new string('x', 4096)));
            That(target.Ui.CreateSurface(new UiSurfaceRequest("large", "Large", "", content: large)).ErrorCode == ModErrorCode.RateLimited, "actual targeted byte exhaustion remains RateLimited");
            RejectUpdate(large, ModErrorCode.RateLimited, "too large");
            var revisionBeforeValidUpdate = hostSurface.Revision;
            var validUpdate = hostSurface.SetContent(new UiColumn(new UiProgress(.5f, "Tasks", id: "tasks"),
                new UiCountdown(deadline, "Remaining", id: "timer")));
            That(validUpdate.Succeeded && hostSurface.Revision == revisionBeforeValidUpdate + 1,
                "a valid update after refused compositions advances the revision once");
            peers.Rig.Frames(12);
            That(remote.TryFindNode("tasks", out progress) && ((UiProgress)progress!).Fraction == .5f &&
                remote.TryFindNode("timer", out timer) && ((UiCountdown)timer!).EndsAt == deadline,
                "the next valid update reaches the recipient with its original deadline");
        }
    }
}
