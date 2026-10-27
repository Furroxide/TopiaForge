using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.PlayersApiTests;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class PresentationRuntimeTests
    {
        private static void ReviewRegressions()
        {
            var failures = new List<Exception>();
            foreach (var test in new Action[] { WideLayoutsCannotPoisonTheBaseline, ReentrantDismissalKeepsLatestState,
                UnsharedSoloHudsCanStillHide, ExactByteBoundaryAndMaximumLoops, MalformedBaselinesAreAtomic,
                HostCanvasLossRetiresSharedState, GuestCanvasLossRecoversLatestState,
                LoopsAndTransientsAcrossSessionChanges, BaselineReplayAndRetryRetirement,
                SixteenParticipantFanOut, RestingGuestRecoveryAllocatesNothing })
            {
                try { test(); Console.WriteLine(test.Method.Name + ": passed."); }
                catch (Exception exception) { failures.Add(exception); Console.WriteLine(test.Method.Name + ": " + exception.Message); }
            }
            if (failures.Count != 0) throw new AggregateException(failures);
        }

        private static UiNode WideLayout(int count)
        {
            var children = new UiNode[count];
            for (var i = 0; i < count; i++) children[i] = new UiText("row");
            return new UiColumn(children);
        }

        private static void WideLayoutsCannotPoisonTheBaseline()
        {
            using var rig = new Rig(); rig.Start();
            var hud = rig.Host.Ui.CreateSurface(Hud("valid", "before", WideLayout(256))).Value!;
            rig.Frames(10);
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 1, "256 children round-trip");
            var refused = false;
            try { rig.Host.Ui.CreateSurface(Hud("too-wide", "", new UiScroll(WideLayout(257)))); }
            catch (ArgumentException) { refused = true; }
            Assert(refused, "257-child layouts are rejected by the public constructor before local creation");
            hud.SetBody("after"); rig.Frames(10);
            Assert(rig.Host.LocalUi.Surfaces.Count == 1 && rig.Guest.ReceivedUi.Surfaces[0].Body == "after",
                "an unencodable layout cannot stop unrelated valid updates");
        }

        private static void ReentrantDismissalKeepsLatestState()
        {
            using var rig = new Rig(); rig.Start();
            var hud = rig.Host.Ui.CreateSurface(Hud("reentrant", "before")).Value!;
            rig.Frames(10);
            Action reopen = () => hud.Show();
            ((IUiSurfaceDismissalSource)hud).Dismissed += reopen;
            hud.Hide(); rig.Frames(10);
            Assert(hud.IsVisible && rig.Guest.ReceivedUi.Surfaces[0].IsVisible,
                "Show inside Dismissed keeps the host and guest visible");
            ((IUiSurfaceDismissalSource)hud).Dismissed -= reopen;
            ((IUiSurfaceDismissalSource)hud).Dismissed += () => hud.SetBody("from dismissal");
            hud.Hide(); rig.Frames(10);
            Assert(!hud.IsVisible && !rig.Guest.ReceivedUi.Surfaces[0].IsVisible &&
                rig.Guest.ReceivedUi.Surfaces[0].Body == "from dismissal", "a dismissal write is not overwritten by the outer hide");
        }

        private static void UnsharedSoloHudsCanStillHide()
        {
            using var rig = new Rig();
            var huge = rig.Host.Ui.CreateSurface(Hud("solo-large", new string('x', 4097))).Value!;
            for (var i = 0; i < 32; i++) rig.Host.Ui.CreateSurface(Hud("shared-" + i, "body"));
            var overflow = rig.Host.Ui.CreateSurface(Hud("solo-overflow", "body")).Value!;
            rig.Start();
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 32, "only eligible solo surfaces are promoted");
            huge.Hide(); overflow.Hide();
            Assert(!huge.IsVisible && !overflow.IsVisible, "unshared solo surfaces remain locally controllable after promotion");
            huge.Show(); overflow.Show(); rig.Frames(10);
            Assert(huge.IsVisible && overflow.IsVisible && rig.Guest.ReceivedUi.Surfaces.Count == 32,
                "local visibility changes neither require nor consume a shared slot");
        }

        private static void ExactByteBoundaryAndMaximumLoops()
        {
            using var rig = new Rig(); rig.Start();
            var used = 0;
            for (var i = 0; i < 6; i++)
                used += ((MirroredHudPresenter)rig.Host.Ui.CreateSurface(Hud("large-" + i, new string('x', 4096))).Value!).Bytes;
            var last = (MirroredHudPresenter)rig.Host.Ui.CreateSurface(Hud("last", "")).Value!;
            var length = MultiplayerPresentation.MaximumBytes - used - last.Bytes - 1; // The body length now needs two varint bytes.
            last.SetBody(new string('y', length));
            Assert(used + last.Bytes == 28 * 1024, "the accepted encoded HUD state reaches exactly 28 KiB");
            var rejected = false;
            try { last.SetBody(new string('y', length + 1)); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && rig.Host.LocalUi.Surfaces[6].Body.Length == length, "one byte over the shared limit preserves local state");
            for (var i = 0; i < 8; i++)
                Assert(rig.Host.Audio.Play(new AudioPlayRequest(new string('\u754c', 127) + i, loop: true,
                    position: new Vec3(1, 2, 3))).Succeeded, "eight maximum UTF-8 loop ids fit");
            rig.Frames(80);
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 7 && rig.Guest.ReceivedUi.Surfaces[6].Body.Length == length &&
                rig.Guest.ReceivedAudio.ActivePlaybacks.Count == 8, "maximum HUD bytes and maximum loops fit the channel together");
            rig.Network.Clock.Advance(1_000_000);
            Assert(!rig.Host.Audio.Play(new AudioPlayRequest("ninth", loop: true)).Succeeded, "the ninth loop is refused");
        }

        private static void MalformedBaselinesAreAtomic()
        {
            using var rig = new Rig(); rig.Start();
            var hud = (MirroredHudPresenter)rig.Host.Ui.CreateSurface(Hud("safe", "keep")).Value!;
            rig.Frames(10);
            var w = new WorldWireWriter(32768);
            // A complete HUD followed by an invalid loop list must not replace the existing baseline.
            w.Begin(30); w.WriteString(""); w.WriteVarUInt(1000); w.WriteVarUInt(1); w.WriteBytes(hud.Payload!); w.WriteVarUInt(9);
            rig.Host.Players.Channel!.Send(w.Segment, InternalSystemTarget.Everyone); rig.Frames(4);
            Assert(rig.Guest.Players.Rejected > 0 && rig.Guest.ReceivedUi.Surfaces[0].Body == "keep", "invalid loop tails are rejected atomically");
            for (var i = 0; i < 40; i++)
            { w.Begin(255); w.WriteString(""); rig.Host.Players.Channel.Send(w.Segment, InternalSystemTarget.Everyone); }
            hud.SetBody("recovered"); rig.Frames(20);
            Assert(rig.Guest.ReceivedUi.Surfaces[0].Body == "recovered", "unknown kinds cannot consume baseline admission or commit a rejected version");
            var rejections = rig.Guest.Players.Rejected;
            w.Begin(31); w.WriteString(""); w.WriteVarUInt(1000); w.WriteString("bad schema"); w.WriteByte(0);
            var bytes = MultiplayerPresentation.Copy(w); bytes[0] = 255;
            rig.Host.Players.Channel.Send(new ArraySegment<byte>(bytes), InternalSystemTarget.Everyone); rig.Frames(4);
            Assert(rig.Guest.ReceivedUi.Toasts.Count == 0 && rig.Guest.Players.Rejected > rejections, "unknown schemas cannot render effects");
        }
    }
}
