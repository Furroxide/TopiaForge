using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.PlayersApiTests;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class PresentationRuntimeTests
    {
        internal static void Run()
        {
            CountdownWidgets();
            RoutesAndPromotes();
            CoalescesAndRestores();
            LocalFailureDoesNotPublish();
            EffectsAreBounded();
            RunMapNotices();
            ForgedAndInteractiveFramesAreRefused();
            SlowPeerDoesNotStopTheOthers();
            RestingPresentationAllocatesNothing();
            BoundsAndLifetime();
            ExactContextScopes();
            ReviewRegressions();
            Console.WriteLine("PresentationRuntimeTests: 22 groups passed.");
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

        internal static void RunMapNotices()
        {
            SharedMapNoticesStayPlain();
            SharedMapNoticesUseSafeFallback();
            Console.WriteLine("Shared map notice tests passed.");
        }

        private static void SharedMapNoticesStayPlain()
        {
            using var rig = new Rig();
            var guest = ObserveGuestToasts(rig);
            rig.Start();
            Assert(rig.Host.Node.Runtime.Realm == NetRealm.ListenHost && rig.Guest.Node.Runtime.Realm == NetRealm.Client,
                "the map notice crosses a listen-host gamemode broadcast");
            var host = new MapNoticeToastProbe();
            var ui = new NetUiService(host, rig.Host.Ui.Replication);
            const string name = "<size=300>Declared world</size>";
            const string notice = "Playing the copy of " + name + " saved on an earlier play; the public map service was too slow.";
            MapNotices.Offline(ui, name, "an earlier play", MapOfflineReason.Slow);
            Assert(host.Map.Count == 1 && host.Map[0] == (notice, UiTone.Warning) && host.Remote.Count == 0 && host.Rich.Count == 0,
                "the shared offline notice preserves literal world markup at the host's plain map sink");
            Assert(ui.ShowMapNotice(notice, UiTone.Warning).TryGetValue(out var repeated) && !repeated,
                "a repeated map notice is deduplicated before delivery");
            Assert(ui.ShowMapNotice(" ", UiTone.Warning).ErrorCode == ModErrorCode.InvalidArgument &&
                ui.ShowMapNotice(new string('x', 513), UiTone.Warning).ErrorCode == ModErrorCode.InvalidArgument &&
                ui.ShowMapNotice("invalid tone", (UiTone)255).ErrorCode == ModErrorCode.InvalidArgument,
                "shared map notices enforce text and tone limits");

            var remoteOnly = new RemoteToastProbe();
            var remoteUi = new NetUiService(remoteOnly, ui.Replication);
            Assert(remoteUi.ShowMapNotice(name, UiTone.Warning).Succeeded && remoteOnly.Remote.Count == 1 &&
                remoteOnly.Remote[0] == (name, UiTone.Warning) && remoteOnly.Rich.Count == 0,
                "a local remote-UI receiver also renders the detailed map notice as plain text");
            var maximum = new string('x', 512);
            Assert(ui.ShowMapNotice(maximum, UiTone.Warning).Succeeded && host.Map.Count == 2 && host.Map[1] == (maximum, UiTone.Warning),
                "a map notice at the shared length limit reaches the plain sink");
            const string trusted = "<b>Ready</b>";
            Assert(ui.ShowToast(trusted, UiTone.Success).Succeeded && host.Rich.Count == 1 && host.Rich[0] == (trusted, UiTone.Success),
                "ordinary authored toasts retain their trusted local renderer");
            Assert(ui.ShowMapNotice("over the shared burst", UiTone.Warning).ErrorCode == ModErrorCode.RateLimited && host.Map.Count == 2,
                "map notices and ordinary toasts share the same rate limit");

            rig.Frames(4);
            Assert(guest.Rich.Count == 0 && guest.Remote.Count == 4 && guest.Remote[0] == (notice, UiTone.Warning) &&
                guest.Remote[1] == (name, UiTone.Warning) && guest.Remote[2] == (maximum, UiTone.Warning) && guest.Remote[3] == (trusted, UiTone.Success),
                "the guest's existing plain toast receiver gets exactly the admitted messages in order");
        }

        private static void SharedMapNoticesUseSafeFallback()
        {
            using var rig = new Rig();
            var guest = ObserveGuestToasts(rig);
            rig.Start();
            const string name = "<size=300>Declared world</size>";
            const string notice = "Playing the copy of " + name + " saved on an earlier play; the public map service was too slow.";
            MapNotices.Offline(rig.Host.Ui, name, "an earlier play", MapOfflineReason.Slow);
            Assert(rig.Host.LocalUi.Toasts.Count == 1 && rig.Host.LocalUi.Toasts[0].Message == notice &&
                rig.Host.LocalUi.Toasts[0].Tone == UiTone.Warning,
                "a plain map sink without the remote receiver capability keeps the detailed notice");

            var host = new RichToastProbe();
            var ui = new NetUiService(host, rig.Host.Ui.Replication);
            MapNotices.Offline(ui, name, "an earlier play", MapOfflineReason.Slow);
            MapNotices.Offline(ui, "<b>Another world</b>", "an earlier play", MapOfflineReason.Unreachable);
            rig.Frames(4);
            const string fallback = "Playing a saved map copy because the public map service is temporarily unavailable.";
            Assert(host.Rich.Count == 1 && host.Rich[0] == (fallback, UiTone.Warning),
                "a host without a plain sink renders only the deduplicated fixed fallback");
            Assert(guest.Rich.Count == 0 && guest.Remote.Count == 2 && guest.Remote[0] == (notice, UiTone.Warning) &&
                guest.Remote[1] == (fallback, UiTone.Warning),
                "the guest gets the detailed plain notice and the fixed fallback once each");
        }

        private static RemoteToastProbe ObserveGuestToasts(Rig rig)
        {
            var guest = new RemoteToastProbe();
            rig.Guest.Players.Presentation = new MultiplayerPresentation(rig.Guest.Players,
                () => new PresentationSink(guest, rig.Guest.ReceivedAudio));
            return guest;
        }

        private class RichToastProbe : IUiService
        {
            internal readonly List<(string Text, UiTone Tone)> Rich = new List<(string, UiTone)>();
            public OperationResult<bool> ShowToast(string text, UiTone tone = UiTone.Neutral)
            { Rich.Add((text, tone)); return OperationResult<bool>.Success(true); }
            public UiAccessibilityPreferences Accessibility => throw new NotSupportedException();
            public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences value) => throw new NotSupportedException();
            public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) => throw new NotSupportedException();
            public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) => throw new NotSupportedException();
        }

        private class RemoteToastProbe : RichToastProbe, IRemoteUiReceiver
        {
            internal readonly List<(string Text, UiTone Tone)> Remote = new List<(string, UiTone)>();
            public OperationResult<bool> RemoteToast(string text, UiTone tone)
            { Remote.Add((text, tone)); return OperationResult<bool>.Success(true); }
            public OperationResult<IUiSurface> RemoteSurface(UiSurfaceRequest request) => throw new NotSupportedException();
            public OperationResult<IUiModal> RemoteModal(UiModalRequest request, Action<bool> completed) => throw new NotSupportedException();
        }

        private sealed class MapNoticeToastProbe : RemoteToastProbe, IInternalMapNoticeUi
        {
            internal readonly List<(string Text, UiTone Tone)> Map = new List<(string, UiTone)>();
            public OperationResult<bool> ShowMapNotice(string text, UiTone tone)
            { Map.Add((text, tone)); return OperationResult<bool>.Success(true); }
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
