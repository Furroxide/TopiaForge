using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.PlayersApiTests;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class PresentationRuntimeTests
    {
        private static void LoopsAndTransientsAcrossSessionChanges()
        {
            using var rig = new Rig(); rig.Start();
            var hud = rig.Host.Ui.CreateSurface(Hud("session", "first")).Value!;
            var loop = rig.Host.Audio.Play(new AudioPlayRequest("active-loop", loop: true)).Value!;
            rig.Host.Audio.Play(new AudioPlayRequest("transient")); rig.Host.Ui.ShowToast("past");
            rig.Frames(10);
            using var late = new Endpoint(rig.Network.AddPlayer("late")); rig.Network.Join(late.Node); rig.Frames(10);
            Assert(late.ReceivedUi.Surfaces.Count == 1 && late.ReceivedUi.Toasts.Count == 0 &&
                late.ReceivedAudio.ActivePlaybacks.Count == 1 && late.ReceivedAudio.ActivePlaybacks[0].Request.Loop,
                "late join gets the active loop and HUD, without historical transients");
            rig.Network.Network.LoseLink(late.Node.Control); rig.Frames(3);
            Assert(late.ReceivedUi.Surfaces.Count == 0 && late.ReceivedAudio.ActivePlaybacks.Count == 0,
                "stream loss clears remote presentation");
            rig.Network.Network.Restore(late.Node.Control); rig.Frames(20);
            Assert(late.ReceivedUi.Surfaces.Count == 1 && late.ReceivedAudio.ActivePlaybacks.Count == 1 && late.ReceivedUi.Toasts.Count == 0,
                "a restored stream baselines active state only");
            rig.Network.Network.StopHosting(); rig.Frames(4);
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 0 && late.ReceivedUi.Surfaces.Count == 0 &&
                late.ReceivedAudio.ActivePlaybacks.Count == 0, "host demotion clears every guest's HUD and audio");
            hud.SetBody("second");
            rig.Network.OpenToMultiplayer(); rig.Network.Join(late.Node); rig.Frames(20);
            Assert(late.ReceivedUi.Surfaces[0].Body == "second" && late.ReceivedAudio.ActivePlaybacks.Count == 1,
                "host promotion republishes surviving author state with a new stream");
            loop.Stop(); rig.Frames(10);
            Assert(late.ReceivedAudio.ActivePlaybacks.Count == 0, "stopping a loop after rehosting retires its current baseline");
        }

        private static void BaselineReplayAndRetryRetirement()
        {
            using var rig = new Rig(); rig.Start();
            var hud = (MirroredHudPresenter)rig.Host.Ui.CreateSurface(Hud("version", "current", new UiText("old"))).Value!;
            rig.Frames(10);
            rig.Guest.ReceivedUi.FailNextContentUpdate(rig.Guest.ReceivedUi.Surfaces[0].Id);
            hud.SetContent(new UiText("pending")); rig.Frames(10);
            hud.Dispose(); rig.Frames(80);
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 0, "retiring a HUD also retires its pending render retry");
            var w = new WorldWireWriter(32768);
            void Send(ulong version, int count)
            {
                w.Begin(30); w.WriteString(""); w.WriteVarUInt(version); w.WriteVarUInt((ulong)count);
                if (count != 0) w.WriteBytes(hud.Payload!);
                w.WriteVarUInt(0);
                rig.Host.Players.Channel!.Send(w.Segment, InternalSystemTarget.Everyone); rig.Frames(8);
            }
            Send(1000, 1);
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 1, "a new authenticated baseline applies");
            Send(999, 0); Send(1000, 0);
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 1, "older and replayed baselines cannot remove current state");
            Send(1001, 0);
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 0, "a newer empty baseline removes current state");
        }

        private static void SixteenParticipantFanOut()
        {
            using var rig = new Rig(); rig.Start();
            var others = new List<Endpoint>();
            try
            {
                for (var i = 0; i < 14; i++)
                {
                    var peer = new Endpoint(rig.Network.AddPlayer("peer-" + i));
                    others.Add(peer); rig.Network.Join(peer.Node);
                }
                var hud = rig.Host.Ui.CreateSurface(Hud("everyone", "first")).Value!;
                rig.Frames(10); hud.SetBody("all 16 participants"); rig.Frames(10);
                Assert(rig.Host.Players.Snapshot!.Participants.Count == 16, "the fixture has exactly 16 canonical participants");
                Assert(rig.Guest.ReceivedUi.Surfaces[0].Body == "all 16 participants", "the original recipient receives updates");
                foreach (var peer in others)
                    Assert(peer.ReceivedUi.Surfaces.Count == 1 && peer.ReceivedUi.Surfaces[0].Body == "all 16 participants",
                        "every canonical recipient receives its independent baseline");
            }
            finally { foreach (var peer in others) peer.Dispose(); }
        }

        private static void RestingGuestRecoveryAllocatesNothing()
        {
            using var rig = new Rig(); rig.Start();
            rig.Host.Ui.CreateSurface(Hud("steady", "unchanged")); rig.Frames(10);
            var presentation = rig.Guest.Players.Presentation!;
            for (var i = 0; i < 100; i++) { rig.Network.Clock.Advance(16_000); presentation.RetryReceived(); presentation.ReconcileReceivedAudio(); }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++) { rig.Network.Clock.Advance(16_000); presentation.RetryReceived(); presentation.ReconcileReceivedAudio(); }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(allocated == 0, "resting guest recovery allocates zero bytes, got " + allocated);
            Console.WriteLine("Guest presentation: 0 B over 10,000 resting recovery and audio checks.");
        }
    }
}
