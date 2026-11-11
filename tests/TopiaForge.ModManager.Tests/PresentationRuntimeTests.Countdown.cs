using System;
using System.IO;
using System.Linq;
using static TopiaForge.ModManager.Tests.PlayersApiTests;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class PresentationRuntimeTests
    {
        private static void OfflineWorldReplacement(bool withoutProvider)
        {
            using var rig = new Rig();
            if (withoutProvider) rig.Host.Node.Runtime.Disconnect();
            using var session = new SessionFixture(Path.Combine(Path.GetTempPath(), "ui-world-" + Guid.NewGuid().ToString("N")), replication: rig.Host.Node.Replication);
            rig.Host.Node.Runtime.FollowUiWorldSessions(session.Hosted);
            session.Launch();
            var oldEpoch = rig.Host.Node.Runtime.UiClockEpoch;
            var deadline = rig.Host.Ui.ClockTime + 30;
            var timer = new UiCountdown(deadline, "Old", UiTone.Warning, "old");
            var retained = rig.Host.Ui.CreateSurface(Hud("before", "Retained score", new UiColumn(new UiText("Score"), timer))).Value!;
            var originalIdentity = timer.ClockIdentity;
            retained.Hide();
            var restarted = SessionFixture.ModeSession!.RestartAsync(); session.Wait(restarted);
            Assert(restarted.Result.Succeeded && rig.Host.Node.Runtime.UiClockEpoch != oldEpoch, "same-runtime solo world restart retires the clock epoch");
            var stale = rig.Host.Ui.CreateSurface(Hud("after", "", timer));
            Assert(stale.ErrorCode == ModErrorCode.InvalidArgument && stale.ErrorMessage.Contains("epoch"), "the new solo world refuses a previously bound timer");
            Assert(rig.Host.Ui.CreateSurface(Hud("fresh", "", new UiCountdown(rig.Host.Ui.ClockTime + 5, id: "fresh"))).Succeeded, "the new solo world accepts a fresh deadline");
            Assert(!retained.IsVisible && rig.Host.LocalUi.Surfaces[0].TryGetCountdownSeconds("old", out var remaining) && remaining == 0,
                "the host retains its hidden surface and expires only its old timer");
            if (!withoutProvider)
            {
                rig.Start(); rig.Frames(15);
                Assert(rig.Guest.ReceivedUi.Surfaces.Count == 2 && rig.Guest.ReceivedUi.Surfaces.Any(surface => surface.TryFindNode("fresh", out _)),
                    "a promoted baseline retains the expired HUD alongside the fresh HUD");
                var guest = rig.Guest.ReceivedUi.Surfaces.Single(surface => surface.Body == "Retained score");
                Assert(!retained.IsVisible && !guest.IsVisible && guest.Content is UiColumn content
                    && content.Children[0] is UiText { Text: "Score" }
                    && content.Children[1] is UiText { Text: "Old 0", Tone: UiTone.Warning },
                    "promotion preserves the hidden HUD and renders only its stale timer as zero");
                retained.Show(); retained.SetBody("Visible score"); rig.Frames(15);
                Assert(retained.IsVisible && guest.IsVisible && guest.Body == "Visible score", "the original host handle still updates both copies");
                Assert(retained.SetContent(new UiColumn(timer)).ErrorCode == ModErrorCode.InvalidArgument,
                    "a new composition containing the retired node is still refused after promotion");
                var replacement = new UiCountdown(rig.Host.Ui.ClockTime + 45, id: "new");
                Assert(retained.SetContent(new UiColumn(new UiText("New score"), replacement)).Succeeded,
                    "the retained HUD accepts new content through its original handle");
                rig.Frames(15);
                Assert(guest.TryFindNode("new", out var node) && ((UiCountdown)node!).EndsAt == replacement.EndsAt,
                    "the fresh deadline reaches the same guest HUD without rebasing");
            }
            else
            {
                retained.Show(); retained.SetBody("Local score");
                Assert(retained.IsVisible && rig.Host.LocalUi.Surfaces[0].Body == "Local score", "the same local handle remains usable without a provider");
            }
            Assert(timer.EndsAt == deadline && timer.ClockEpoch == oldEpoch && ReferenceEquals(timer.ClockIdentity, originalIdentity),
                "retirement never rewrites the original authored deadline or clock identity");
        }

        internal static void CountdownWidgets()
        {
            OfflineWorldReplacement(false);
            OfflineWorldReplacement(true);
            using var rig = new Rig(); rig.Start();
            var deadline = rig.Host.Ui.ClockTime + 30;
            var created = rig.Host.Ui.CreateSurface(Hud("widgets", "", new UiColumn(new UiProgress(.6f, "Tasks", id: "progress"), new UiCountdown(deadline, "Remaining", id: "timer"))));
            Assert(created.Succeeded, "mirrored widgets are accepted: " + created.ErrorMessage);
            rig.Frames(15);
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 1, "shared widgets are not silently dropped");
            var surface = rig.Guest.ReceivedUi.Surfaces[0];
            Assert(surface.TryFindNode("progress", out var progress) && ((UiProgress)progress!).Fraction == .6f, "shared progress keeps its value");
            Assert(surface.TryFindNode("timer", out var node) && ((UiCountdown)node!).EndsAt == deadline, "shared countdown keeps host deadline");
            rig.Frames(120);
            using var late = new Endpoint(rig.Network.AddPlayer("late-timer")); rig.Network.Join(late.Node); rig.Frames(20);
            Assert(late.ReceivedUi.Surfaces.Count == 1 && late.ReceivedUi.Surfaces[0].TryFindNode("timer", out node) && ((UiCountdown)node!).EndsAt == deadline,
                "late join retains original deadline instead of granting a fresh duration");
            Assert(surface.TryGetCountdownSeconds("timer", out var originalSeconds) && late.ReceivedUi.Surfaces[0].TryGetCountdownSeconds("timer", out var lateSeconds) && originalSeconds == lateSeconds,
                "late and original recipients agree on the shared timeline");
            created.Value!.SetBody("Updated"); rig.Frames(15);
            Assert(late.ReceivedUi.Surfaces[0].TryFindNode("timer", out node) && ((UiCountdown)node!).EndsAt == deadline, "shared updates do not re-anchor");
            var invalid = rig.Host.Ui.CreateSurface(Hud("invalid", "", new UiText(new string('x', 4097))));
            Assert(invalid.ErrorCode == ModErrorCode.InvalidArgument && invalid.ErrorMessage.Contains("4096"), "shared invalid shape returns actionable InvalidArgument");
            Assert(rig.Host.LocalUi.Surfaces.Count == 1, "invalid whole composition creates no local surface");
        }
    }
}
