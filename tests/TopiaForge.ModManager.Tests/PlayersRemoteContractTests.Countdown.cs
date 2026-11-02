using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class PlayersRemoteContractTests
    {
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
