using System;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.PlayersApiTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class PlayersNetworkTests
    {
        internal static void Run()
        {
            var rig = new PresenceRig();
            rig.Host.Runtime.Disconnect();
            var hostRuntime = MultiplayerPlayers.Install(rig.Host.Runtime, rig.Host.Presence);
            rig.Host.Runtime.Connect();
            using var host = new PlayersTestServices(hostRuntime, rig.Host.Control, rig.Host.Backend);
            rig.OpenToMultiplayer();
            var firstNode = rig.AddPlayer("p1");
            firstNode.Runtime.Disconnect();
            var firstRuntime = MultiplayerPlayers.Install(firstNode.Runtime, firstNode.Presence);
            firstNode.Runtime.Connect();
            using var first = new PlayersTestServices(firstRuntime, firstNode.Control, firstNode.Backend);
            rig.Join(firstNode); rig.Frames(24);
            var target = host.Player(firstNode.Id);
            Assert(target.Damage(new PlayerDamageRequest(70, "enemy")).Succeeded, "host routes damage");
            rig.Frames(24); Assert(firstNode.Backend.Health == 30, "owning game applies damage");
            Assert(target.Heal(60, "shop").Succeeded, "host routes heal");
            rig.Frames(24); Assert(firstNode.Backend.Health == 90 && target.TryGetHealth(out var health) && health!.Current == 90, "authorized heal passes host plausibility without regeneration delay");
            Assert(target.Teleport(new Vec3(100, 0, 10), 90).Succeeded, "host routes teleport");
            rig.Frames(24); Assert(firstNode.Backend.Feet.X == 100 && rig.Host.Counters.Corrections == 0, "authorized teleport is accepted by presence");

            var clicks = 0;
            var surface = (RemoteUiSurface)target.Ui.CreateSurface(new UiSurfaceRequest("shop", "Shop", "<b>literal</b>", content: new UiButton("buy", "Buy", () => clicks++))).Value!;
            rig.Frames(3);
            Assert(first.Ui.Surfaces.Count == 1 && host.Ui.Surfaces.Count == 0, "only the target sees its window");
            ((UiButton)first.Ui.Surfaces[0].Content!).Activated(); rig.Frames(3);
            Assert(clicks == 1, "remote control runs its host callback");

            var secondNode = rig.AddPlayer("p2");
            secondNode.Runtime.Disconnect();
            var secondRuntime = MultiplayerPlayers.Install(secondNode.Runtime, secondNode.Presence);
            secondNode.Runtime.Connect();
            using var second = new PlayersTestServices(secondRuntime, secondNode.Control, secondNode.Backend);
            rig.Join(secondNode);
            Assert(surface.IsVisible && first.Ui.Surfaces.Count == 1 && second.Ui.Surfaces.Count == 0, "a third join leaves the existing player's UI alone");
            var writer = new WorldWireWriter(32768);
            writer.Begin(14); writer.WriteString("players-test"); writer.WriteVarUInt(surface.Token); writer.WriteVarUInt(surface.Revision); writer.WriteString("buy"); writer.WriteString("");
            secondRuntime.Channel!.Send(writer.Segment, InternalSystemTarget.Host); rig.Frames(3);
            Assert(clicks == 1, "a non-target cannot answer another player's UI");
            ((UiButton)first.Ui.Surfaces[0].Content!).Activated(); rig.Frames(3); Assert(clicks == 2, "the intended target can still answer");
            var oldButton = (UiButton)first.Ui.Surfaces[0].Content!;
            surface.SetContent(new UiButton("new", "New", () => clicks += 10)); rig.Frames(3);
            oldButton.Activated(); rig.Frames(3); Assert(clicks == 2, "a replaced composition cannot answer");

            bool? answered = null;
            var modal = target.Ui.ShowModal(new UiModalRequest("Confirm", "Proceed?"), value => answered = value).Value!;
            rig.Frames(3); first.Ui.Modals[0].Confirm(); rig.Frames(3);
            Assert(answered == true && !modal.IsOpen, "remote modal answers once");
            var reconnectAnswer = 0;
            var reconnectModal = target.Ui.ShowModal(new UiModalRequest("Confirm again", "After reconnect?"), _ => reconnectAnswer++).Value!;
            rig.Frames(3);
            rig.Network.LoseLink(firstNode.Control); rig.Frames(3);
            Assert(target.IsPresent && target.Damage(new PlayerDamageRequest(1, "unreachable")).ErrorCode == ModErrorCode.Unavailable, "a disconnected handle stays present but cannot report an unsent action as success");
            rig.Network.Restore(firstNode.Control); rig.Frames(12);
            Assert(surface.IsVisible && reconnectModal.IsOpen && reconnectAnswer == 0, "a retained target keeps windows and modals through reconnect");
            first.Ui.Modals[first.Ui.Modals.Count - 1].Confirm(); rig.Frames(3);
            Assert(reconnectAnswer == 1 && !reconnectModal.IsOpen, "the recreated modal answers its original host callback");

            rig.Host.Backend.Health = 80;
            rig.Frames(24);
            foreach (var action in new byte[] { 1, 2, 3 })
            {
                writer.Begin(2); writer.WriteString("players-test"); writer.WriteString(rig.Host.Id.Value); writer.WriteByte(action);
                if (action < 3) { writer.WriteSingle(50); writer.WriteString("forged"); }
                else { writer.WriteSingle(50); writer.WriteSingle(0); writer.WriteSingle(0); writer.WriteByte(0); writer.WriteSingle(0); }
                secondRuntime.Channel!.Send(writer.Segment, InternalSystemTarget.Host); rig.Frames(3);
                Assert(rig.Host.Backend.Health == 80 && rig.Host.Backend.Feet.X == 0, "a client cannot apply player action " + action + " to the host");
            }
            Assert(second.Player(firstNode.Id).Damage(new PlayerDamageRequest(1, "forged")).ErrorCode == ModErrorCode.NotAuthoritative, "public action route refuses another player's mutation too");

            first.Input.SetValue("fire", 1); rig.Frame(); first.Input.FinishFrame();
            var sawPress = false;
            for (var i = 0; i < 8; i++) { rig.Frame(); sawPress |= target.Input.WasPressed("fire"); }
            Assert(sawPress && target.Input.IsHeld("fire") && !host.Player(secondNode.Id).Input.IsHeld("fire"), "owner input is attributed only to its player");
            first.Input.SetValue("fire", 0); rig.Frame(); first.Input.FinishFrame();
            var sawRelease = false;
            for (var i = 0; i < 8; i++) { rig.Frame(); sawRelease |= target.Input.WasReleased("fire"); }
            Assert(sawRelease && !target.Input.IsHeld("fire"), "release is reconstructed");
            writer.Begin(1); writer.WriteString("players-test"); writer.WriteString("unregistered"); writer.WriteByte(3);
            var unknownBefore = hostRuntime.Rejected;
            Assert(secondRuntime.Channel!.Send(writer.Segment, InternalSystemTarget.Host).Succeeded, "the hostile input reaches the reliable stream"); rig.Frames(8);
            Assert(hostRuntime.Rejected > unknownBefore && !host.Player(secondNode.Id).Input.IsHeld("unregistered"), "unregistered edges are refused");
            var rejected = hostRuntime.Rejected;
            writer.Begin(1); writer.WriteString("players-test"); writer.WriteString("fire"); writer.WriteByte(3); writer.WriteString(firstNode.Id.Value);
            secondRuntime.Channel!.Send(writer.Segment, InternalSystemTarget.Host); rig.Frames(3);
            Assert(hostRuntime.Rejected > rejected && !target.Input.IsHeld("fire"), "an input payload cannot override its authenticated sender");
            writer.Begin(1); writer.WriteString("players-test"); writer.WriteString("fire"); writer.WriteByte(3);
            rejected = hostRuntime.Rejected;
            for (var burst = 0; burst < 20; burst++)
            {
                for (var packet = 0; packet < 32; packet++) secondRuntime.Channel!.Send(writer.Segment, InternalSystemTarget.Host);
                rig.Frames(2);
            }
            Assert(hostRuntime.Rejected > rejected && !target.Input.IsHeld("fire"), "an edge flood is bounded and cannot affect another player");

            using var column = host.Directory.RegisterScoreboardColumn(new ScoreboardColumnDefinition("score", "Score")).Value!;
            column.SetValue(target.Id, "17", 17); rig.Frames(3);
            Assert(firstRuntime.Scoreboard.Count == 1 && secondRuntime.Scoreboard.Count == 1 && firstRuntime.Scoreboard[0].Values[target.Id].SortKey == 17, "scoreboard values reach every player");
            var before = first.Ui.Surfaces.Count;
            writer.Begin(10); writer.WriteString("players-test"); writer.WriteString(firstNode.Id.Value); writer.WriteVarUInt(999); writer.WriteVarUInt(1);
            writer.WriteString("bad"); writer.WriteString("Bad"); writer.WriteString(""); writer.WriteByte(1); writer.WriteSingle(float.NaN); writer.WriteSingle(200);
            hostRuntime.Channel!.Send(writer.Segment, InternalSystemTarget.Only(firstNode.Id)); rig.Frames(3);
            Assert(first.Ui.Surfaces.Count == before && firstRuntime.Rejected > 0, "hostile host payload never creates a partial surface");
            rig.Network.Leave(firstNode.Control); rig.Frames(3);
            Assert(!target.IsPresent && target.Teleport(default).ErrorCode == ModErrorCode.NotFound && !surface.IsVisible, "departure invalidates actions and UI");
            var scopeWindow = host.Player(secondNode.Id).Ui.CreateSurface(new UiSurfaceRequest("scope", "Scope", "Ends with its scope")).Value!;
            rig.Frames(3);
            Assert(second.Ui.Surfaces.Count == 1, "the scoped window reaches its target before shutdown");
            var receivedScopeWindow = second.Ui.Surfaces[0];
            host.Directory.Dispose(); host.Lifetime.Dispose(); rig.Frames(3);
            Assert(!scopeWindow.IsVisible && !receivedScopeWindow.IsVisible && second.Ui.Surfaces.Count == 0, "scope shutdown closes the remote window even when its directory retires first");
            Console.WriteLine("PlayersNetworkTests: actions, input, UI, scoreboard and abuse passed with three peers.");
        }
    }
}
