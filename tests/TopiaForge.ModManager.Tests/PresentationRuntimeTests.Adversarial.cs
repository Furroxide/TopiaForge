using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.PlayersApiTests;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class PresentationRuntimeTests
    {
        private static void ForgedAndInteractiveFramesAreRefused()
        {
            using var rig = new Rig(); rig.Start();
            var w = new WorldWireWriter(32768);
            w.Begin(31); w.WriteString(""); w.WriteVarUInt(1); w.WriteString("forged"); w.WriteByte(0);
            rig.Guest.Players.Channel!.Send(w.Segment, InternalSystemTarget.Host); rig.Frames(4);
            Assert(rig.Host.ReceivedUi.Toasts.Count == 0 && rig.Host.Players.Rejected > 0, "client cannot broadcast effects as the host");
            w.Begin(30); w.WriteString(""); w.WriteVarUInt(100); w.WriteVarUInt(1);
            var hud = new WorldWireWriter(4096);
            hud.Begin(0); hud.WriteVarUInt(999); hud.WriteString("Bad"); hud.WriteString("Interactive");
            hud.WriteSingle(320); hud.WriteSingle(160); hud.WriteByte(0); hud.WriteByte(0); hud.WriteByte(1);
            UiNodeWireCodec.Write(hud, new UiButton("bad", "Bad", () => { }));
            w.WriteBytes(MultiplayerPresentation.Copy(hud)); w.WriteVarUInt(0);
            rig.Host.Players.Channel!.Send(w.Segment, InternalSystemTarget.Everyone); rig.Frames(4);
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 0 && rig.Guest.Players.Rejected > 0, "even a host cannot make an interactive broadcast HUD");
            w.Begin(31); w.WriteString(""); w.WriteVarUInt(200); w.WriteString("once"); w.WriteByte(0);
            rig.Host.Players.Channel.Send(w.Segment, InternalSystemTarget.Everyone); rig.Frames(4);
            rig.Network.Clock.Advance(3_000_000);
            rig.Host.Players.Channel.Send(w.Segment, InternalSystemTarget.Everyone); rig.Frames(4);
            Assert(rig.Guest.ReceivedUi.Toasts.Count == 1, "replayed effect sequence cannot play again after the dedupe window");
        }

        private static void SlowPeerDoesNotStopTheOthers()
        {
            using var rig = new Rig(); rig.Start();
            using var second = new Endpoint(rig.Network.AddPlayer("second")); rig.Network.Join(second.Node); rig.Frames(10);
            var hud = rig.Host.Ui.CreateSurface(Hud("score", "before")).Value!; rig.Frames(10);
            rig.Guest.Node.Paused = true;
            var channel = rig.Host.Players.Channel!;
            var target = InternalSystemTarget.Only(rig.Guest.Node.Id);
            var fill = new byte[32768]; fill[0] = 1; fill[1] = 99;
            var sends = 0;
            while (channel.CanSend(target, fill.Length) && sends++ < 256) channel.Send(new ArraySegment<byte>(fill), target);
            Assert(!channel.CanSend(target, fill.Length), "the slow peer's window is full");
            hud.SetBody("latest"); rig.Frames(10);
            Assert(second.ReceivedUi.Surfaces[0].Body == "latest", "one full peer window cannot stall a healthy recipient");
            rig.Guest.Node.Paused = false; rig.Frames(30);
            Assert(rig.Guest.ReceivedUi.Surfaces[0].Body == "latest", "recovered peer gets the newest baseline");
            rig.Guest.Node.Paused = true;
            for (var i = 0; i < 80; i++) { hud.SetBody("queued-" + i); rig.Frames(7); }
            rig.Guest.Node.Paused = false; rig.Frames(30);
            Assert(rig.Guest.ReceivedUi.Surfaces[0].Body == "queued-79", "valid queued baselines cannot consume the recovery baseline's receive budget");
        }

        private static void RestingPresentationAllocatesNothing()
        {
            using var rig = new Rig(); rig.Start();
            var hud = rig.Host.Ui.CreateSurface(Hud("score", "steady")).Value!; rig.Frames(20);
            var presentation = rig.Host.Players.Presentation!;
            for (var i = 0; i < 100; i++) { hud.Show(); hud.SetBody("steady"); presentation.Send(); }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++) { hud.Show(); hud.SetBody("steady"); presentation.Send(); }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(allocated == 0, "resting presentation allocates zero bytes, got " + allocated);
            Console.WriteLine("Shared presentation: 0 B over 10,000 resting sends.");
        }
    }
}
