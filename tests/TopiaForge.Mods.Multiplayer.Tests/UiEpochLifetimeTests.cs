using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class UiEpochLifetimeTests
{
    internal static void Run()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(481);
        using var host = new Endpoint(rig.Host, () => rig.Network.Now);
        var peer = rig.AddPeer("guest");
        using var guest = new Endpoint(peer, () => rig.Network.Now);
        rig.StartHosting(new InternalHostSettings("UI lifetimes", 4));
        rig.JoinAndWait(peer);
        var id = peer.Session.Snapshot.LocalParticipantId
            ?? throw new InvalidOperationException("UI epoch lifetime: the joined peer has no local participant ID.");
        Check(rig.Run(() => (host.Players.Channel?.GetGeneration(id) ?? 0) != 0), "the joined target has an authenticated Players stream");
        Check(host.Directory.TryGet(id, out var target), "the host has a player handle for the joined target");
        var playerUi = target!.Ui;
        var epoch = host.Players.UiClockEpoch;
        var deadline = host.Ui.ClockTime + 300;
        var timer = new UiCountdown(deadline, "Remaining", UiTone.Warning, "timer");
        var clicked = 0;
        var button = new UiButton("confirm", "Confirm", () => clicked++);
        using var window = (RemoteUiSurface)playerUi.CreateSurface(new UiSurfaceRequest("window", "Window", "Original",
            content: new UiColumn(new UiText("Keep"), timer, button))).Value!;
        using var targeted = (RemoteUiSurface)playerUi.CreateSurface(new UiSurfaceRequest("targeted", "Targeted HUD", "Original",
            UiSurfaceKind.Hud, 320, 160, new UiCountdown(deadline, "Targeted", id: "targeted-timer"))).Value!;
        using var shared = host.CreateSharedHud(new UiCountdown(deadline, "Shared", id: "shared-timer"));
        targeted.Hide();
        Check(rig.Run(() => guest.Ui.Surfaces.Count == 3 && !guest.Surface("Targeted HUD").IsVisible), "initial UI and hidden state reach the real recipient");
        var original = guest.Surface("Window");
        var oldStream = window.Stream;
        var rejected = guest.Players.Rejected;
        var identity = timer.ClockIdentity;
        Check(original.TryFindNode("timer", out var node) && ((UiCountdown)node!).EndsAt == deadline, "the original stream carries a live countdown");

        Check(rig.Host.Control.ReplaceSession().Succeeded, "the real listen host replaces its session");
        Check(rig.Run(() => guest.Players.UiClockEpoch != epoch && guest.Ui.Surfaces.Count == 3
            && !ReferenceEquals(original, guest.Surface("Window"))), "the new real provider stream restores the three retained surfaces");
        var current = guest.Surface("Window");
        var currentTargeted = guest.Surface("Targeted HUD");
        var currentShared = guest.Surface("Shared HUD");
        var stream = host.Players.Channel!.GetGeneration(id);
        Check(stream != 0 && stream != oldStream && window.Stream == stream && targeted.Stream == stream && !window.Dirty && !targeted.Dirty,
            "both targeted resends converge on the new stream without remaining dirty");
        Check(!original.IsVisible && current.Content is UiColumn column && column.Children[0] is UiText { Text: "Keep" }
            && column.Children[1] is UiText { Text: "Remaining 0", Tone: UiTone.Warning }
            && currentTargeted.Content is UiText { Text: "Targeted 0" } && !currentTargeted.IsVisible
            && currentShared.Content is UiText { Text: "Shared 0" } && currentShared.IsVisible,
            "the replacement retains text, visibility and all three zero displays without showing the retired copy");
        Check(guest.Players.Rejected == rejected && ReferenceEquals(timer.ClockIdentity, identity) && timer.ClockEpoch == epoch && timer.EndsAt == deadline,
            "the new stream receives no stale baseline and the authored timer keeps its clock identity");
        Check(current.ActivateButton("confirm").Succeeded && rig.Run(() => clicked == 1), "the recovered window routes its original control through the new stream");

        shared.SetBody("Updated");
        var fresh = new UiCountdown(host.Ui.ClockTime + 60, id: "new");
        Check(window.SetContent(new UiColumn(timer, button)).ErrorCode == ModErrorCode.InvalidArgument, "new stale content stays invalid after a real replacement");
        Check(window.SetContent(new UiColumn(fresh, button)).Succeeded, "the original handle can send fresh content");
        Check(rig.Run(() => current.TryFindNode("new", out _) && currentShared.Body == "Updated")
            && current.TryFindNode("new", out node) && ((UiCountdown)node!).EndsAt == fresh.EndsAt,
            "fresh content and shared body changes reach the surviving guest surfaces");
        rig.Run(() => false, maximumMicros: 300_000);
        Check(guest.Players.Rejected == rejected && window.Stream == stream && !window.Dirty && !targeted.Dirty,
            "steady frames after recovery produce no further rejection or dirty retry");
        Console.WriteLine("Retained UI survives real provider session replacement.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("UI epoch lifetime: " + message);
    }

    private sealed class Endpoint : IDisposable
    {
        private readonly FakeModLifetime lifetime = new();
        private readonly MultiplayerReplication replication;
        internal readonly MultiplayerPlayers Players;
        internal readonly PlayerDirectory Directory;
        internal readonly FakeUiService Ui;

        internal Endpoint(ControlPeer peer, Func<long> clock)
        {
            var runtime = peer.Composition;
            runtime.Disconnect();
            replication = new MultiplayerReplication(runtime);
            var presence = MultiplayerPresence.Install(runtime, replication, new FakePresenceBackend { RecordTrails = false }, clock);
            Players = MultiplayerPlayers.Install(runtime, presence);
            replication.Players = Players;
            Ui = new FakeUiService(lifetime);
            var audio = new FakeAudioService(lifetime);
            Players.Presentation = new MultiplayerPresentation(Players, () => new PresentationSink(Ui, audio));
            Check(runtime.Connect(), "the real provider attaches to the Players runtime");
            var context = Players.Bind("ui-epoch-test", true, new CapturedModLogger(), lifetime,
                new FakeLocalPlayerService(lifetime), Ui, new ModInputActivity());
            Directory = new PlayerDirectory(() => peer.Session.Snapshot, () => 0, context);
            context.Directory = Directory;
        }

        internal IUiSurface CreateSharedHud(UiNode content)
        {
            var profile = new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer);
            var context = replication.Bind("ui-epoch-test", profile, lifetime);
            return Players.Presentation!.CreateHud(context, Ui, new UiSurfaceRequest("shared", "Shared HUD", "Original",
                UiSurfaceKind.Hud, 320, 160, content)).Value!;
        }

        internal FakeUiSurface Surface(string title) => Ui.Surfaces.Single(surface => surface.Request.Title == title);

        public void Dispose() { Directory.Dispose(); lifetime.Dispose(); }
    }
}
