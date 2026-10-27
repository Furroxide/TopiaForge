using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using static TopiaForge.ModManager.Tests.PlayersApiTests;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class PresentationRuntimeTests
    {
        internal static void Run()
        {
            RoutesAndPromotes();
            CoalescesAndRestores();
            LocalFailureDoesNotPublish();
            EffectsAreBounded();
            ForgedAndInteractiveFramesAreRefused();
            SlowPeerDoesNotStopTheOthers();
            RestingPresentationAllocatesNothing();
            BoundsAndLifetime();
            ExactContextScopes();
            Console.WriteLine("PresentationRuntimeTests: 9 groups passed.");
        }

        private static void RoutesAndPromotes()
        {
            using var rig = new Rig();
            var hud = rig.Host.Ui.CreateSurface(Hud("score", "Solo")).Value!;
            Assert(rig.Host.LocalUi.Surfaces.Count == 1, "solo HUD remains local");
            rig.Start();
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 1 && rig.Guest.ReceivedUi.Surfaces[0].Body == "Solo", "promotion baselines an existing HUD");
            rig.Host.Ui.CreateSurface(new UiSurfaceRequest("window", "Window", "Host"));
            rig.Host.Ui.ShowModal(new UiModalRequest("Confirm", "Host"), _ => { });
            using var participant = new Author(rig.Host.Node, ModExecutionContext.Participant);
            participant.Ui.CreateSurface(Hud("private", "Participant"));
            participant.Ui.ShowToast("local");
            using var local = new Author(rig.Host.Node, ModExecutionContext.GamemodeServer, ModNetworkClass.ClientLocal);
            local.Ui.CreateSurface(Hud("local", "Local")); local.Ui.ShowToast("local");
            rig.Frames(10);
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 1 && rig.Guest.ReceivedUi.Modals.Count == 0 && rig.Guest.ReceivedUi.Toasts.Count == 0,
                "windows, modals, participant and client-local calls stay local");
            using var serverOnly = new Author(rig.Host.Node, ModExecutionContext.GamemodeServer, ModNetworkClass.ServerOnly);
            serverOnly.Ui.CreateSurface(Hud("server", "Server only")); rig.Frames(10);
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 2, "server-only HUD needs no matching guest mod or callback context");
            hud.Hide(); rig.Frames(10); Assert(!rig.Guest.ReceivedUi.Surfaces[0].IsVisible, "hide is mirrored");
            hud.Show(); rig.Frames(10); Assert(rig.Guest.ReceivedUi.Surfaces[0].IsVisible, "show is mirrored");
        }

        private static void CoalescesAndRestores()
        {
            using var rig = new Rig(); rig.Start();
            var hud = rig.Host.Ui.CreateSurface(Hud("score", "0")).Value!; rig.Frames(10);
            var previous = rig.Guest.ReceivedUi.Surfaces[0].Body;
            long lastChange = -100_000;
            for (var i = 0; i < 100; i++)
            {
                hud.SetBody(i.ToString()); rig.Frames(1);
                var next = rig.Guest.ReceivedUi.Surfaces[0].Body;
                if (next != previous)
                {
                    Assert(i * 16_000L - lastChange >= 96_000, "HUD updates remain coalesced, including the final value");
                    lastChange = i * 16_000L; previous = next;
                }
            }
            rig.Frames(10); Assert(rig.Guest.ReceivedUi.Surfaces[0].Body == "99", "coalescing ends on latest state");
            rig.Network.Network.LoseLink(rig.Guest.Node.Control); rig.Frames(3);
            hud.SetBody("After reconnect"); rig.Network.Network.Restore(rig.Guest.Node.Control); rig.Frames(20);
            Assert(rig.Guest.ReceivedUi.Surfaces.Count == 1 && rig.Guest.ReceivedUi.Surfaces[0].Body == "After reconnect", "new stream receives a fresh baseline");
            rig.Host.Dispose(); rig.Frames(10); Assert(rig.Guest.ReceivedUi.Surfaces.Count == 0, "scope disposal publishes removal");
        }

        private static void LocalFailureDoesNotPublish()
        {
            using var rig = new Rig(); rig.Start();
            var hud = rig.Host.Ui.CreateSurface(Hud("score", "", new UiText("old"))).Value!; rig.Frames(10);
            rig.Host.LocalUi.FailNextContentUpdate("score");
            Assert(!hud.SetContent(new UiText("rejected")).Succeeded, "local content failure is returned");
            rig.Frames(10);
            Assert(((UiText)rig.Guest.ReceivedUi.Surfaces[0].Content!).Text == "old", "failed local update cannot publish remotely");
            Assert(hud.SetContent(new UiText("new")).Succeeded, "next valid update works"); rig.Frames(10);
            Assert(((UiText)rig.Guest.ReceivedUi.Surfaces[0].Content!).Text == "new", "successful content propagates");
            rig.Guest.ReceivedUi.FailNextContentUpdate(rig.Guest.ReceivedUi.Surfaces[0].Id);
            hud.SetContent(new UiText("retry")); rig.Frames(10);
            Assert(((UiText)rig.Guest.ReceivedUi.Surfaces[0].Content!).Text == "new", "recipient render failure retains previous content");
            rig.Frames(70);
            Assert(((UiText)rig.Guest.ReceivedUi.Surfaces[0].Content!).Text == "retry", "recipient retries its bounded latest state without another host write");
        }

        private static void EffectsAreBounded()
        {
            using var rig = new Rig(); rig.Start();
            for (var i = 0; i < 20; i++) rig.Host.Ui.ShowToast("same", UiTone.Success);
            rig.Frames(4);
            Assert(rig.Host.LocalUi.Toasts.Count == 1 && rig.Guest.ReceivedUi.Toasts.Count == 1, "duplicate toast plays once on every game");
            for (var i = 0; i < 20; i++) rig.Host.Ui.ShowToast("toast " + i);
            rig.Frames(4); Assert(rig.Guest.ReceivedUi.Toasts.Count == 4, "toast burst is bounded");
            var cue = new AudioPlayRequest("confirm");
            for (var i = 0; i < 20; i++) rig.Host.Audio.Play(cue);
            rig.Frames(4); Assert(rig.Guest.ReceivedAudio.ActivePlaybacks.Count == 1, "duplicate cue plays once");
            var loop = rig.Host.Audio.Play(new AudioPlayRequest("warning-loop", loop: true)).Value!;
            rig.Frames(10); Assert(rig.Guest.ReceivedAudio.ActivePlaybacks.Count == 2, "loop baseline starts playback");
            loop.Stop(); rig.Frames(10); Assert(rig.Guest.ReceivedAudio.ActivePlaybacks.Count == 1, "stop retires remote loop");
            rig.Guest.Node.Runtime.Disconnect(); Assert(rig.Guest.ReceivedAudio.ActivePlaybacks.Count == 0, "disconnect stops all received audio");
        }

        private static UiSurfaceRequest Hud(string id, string body, UiNode? content = null) =>
            new UiSurfaceRequest(id, "Score", body, UiSurfaceKind.Hud, 320, 160, content);

        private sealed class Rig : IDisposable
        {
            internal readonly PresenceRig Network = new PresenceRig();
            internal readonly Endpoint Host, Guest;
            internal Rig()
            {
                Host = new Endpoint(Network.Host);
                Guest = new Endpoint(Network.AddPlayer("guest"));
            }
            internal void Start() { Network.OpenToMultiplayer(); Network.Join(Guest.Node); Frames(10); }
            internal void Frames(int count) => Network.Frames(count);
            public void Dispose() { Host.Dispose(); Guest.Dispose(); Host.Node.Runtime.Disconnect(); Guest.Node.Runtime.Disconnect(); }
        }

        private sealed class Endpoint : IDisposable
        {
            internal readonly PresenceNode Node;
            private readonly FakeModLifetime receivedLifetime = new FakeModLifetime();
            internal readonly FakeUiService ReceivedUi;
            internal readonly FakeAudioService ReceivedAudio;
            internal readonly MultiplayerPlayers Players;
            internal readonly Author Author;
            internal NetUiService Ui => Author.Ui;
            internal FakeUiService LocalUi => Author.LocalUi;
            internal NetAudioService Audio => Author.Audio;
            internal Endpoint(PresenceNode node)
            {
                Node = node; node.Runtime.Disconnect();
                Players = MultiplayerPlayers.Install(node.Runtime, node.Presence); node.Replication.Players = Players;
                ReceivedUi = new FakeUiService(receivedLifetime); ReceivedAudio = new FakeAudioService(receivedLifetime);
                Players.Presentation = new MultiplayerPresentation(Players, () => new PresentationSink(ReceivedUi, ReceivedAudio));
                node.Runtime.Connect(); Author = new Author(node, ModExecutionContext.GamemodeServer);
            }
            public void Dispose() { Author.Dispose(); receivedLifetime.Dispose(); }
        }

        private sealed class Author : IDisposable
        {
            private readonly FakeModLifetime lifetime = new FakeModLifetime();
            internal int ResourceCount => lifetime.TrackedResourceCount;
            internal readonly FakeUiService LocalUi;
            internal readonly NetUiService Ui;
            internal readonly NetAudioService Audio;
            internal Author(PresenceNode node, ModExecutionContext execution, ModNetworkClass network = ModNetworkClass.Shared)
            {
                var context = node.Replication.Bind("presentation-test", new ModNetProfile(network, execution), lifetime);
                LocalUi = new FakeUiService(lifetime); Ui = new NetUiService(LocalUi, context);
                Audio = new NetAudioService(new FakeAudioService(lifetime), context);
            }
            public void Dispose() => lifetime.Dispose();
        }
    }
}
