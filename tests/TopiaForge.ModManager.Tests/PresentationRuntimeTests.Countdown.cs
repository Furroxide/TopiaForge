using System;
using System.IO;
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
            var timer = new UiCountdown(rig.Host.Ui.ClockTime + 30, id: "old");
            Assert(rig.Host.Ui.CreateSurface(Hud("before", "", timer)).Succeeded, "first solo world binds its timer");
            var restarted = SessionFixture.ModeSession!.RestartAsync(); session.Wait(restarted);
            Assert(restarted.Result.Succeeded && rig.Host.Node.Runtime.UiClockEpoch != oldEpoch, "same-runtime solo world restart retires the clock epoch");
            var stale = rig.Host.Ui.CreateSurface(Hud("after", "", timer));
            Assert(stale.ErrorCode == ModErrorCode.InvalidArgument && stale.ErrorMessage.Contains("epoch"), "the new solo world refuses a previously bound timer");
            Assert(rig.Host.Ui.CreateSurface(Hud("fresh", "", new UiCountdown(rig.Host.Ui.ClockTime + 5, id: "fresh"))).Succeeded, "the new solo world accepts a fresh deadline");
            if (!withoutProvider)
            {
                rig.Start(); rig.Frames(15);
                Assert(rig.Guest.ReceivedUi.Surfaces.Count == 1 && rig.Guest.ReceivedUi.Surfaces[0].TryFindNode("fresh", out _),
                    "retired process-scope timers cannot poison a fresh world's promoted baseline");
            }
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
