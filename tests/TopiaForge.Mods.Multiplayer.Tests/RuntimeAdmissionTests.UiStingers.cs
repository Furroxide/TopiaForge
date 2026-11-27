using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.TestSupport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    internal static void RunStingerWire()
    {
        StingerRoutesRespectVersionsQuotaAndIncarnation();
        StingerReceivesExactOwnershipAndLeavesSurfaceGenerationsAlone();
        StingerUsesTheExistingPlayerUiEventBudget();
        StingerAndToastShareEachReceiverBudget();
        StingerBroadcastCoalescesPerViewer();
        StingerRetiresExactlyThePublishedScope();
        StingerRetirementRetriesOnlyItsOwnStream();
        StingerReservationsUseTheLongerExit();
        FullReceivedQueuesEmitBoundedDiagnostics();
        Console.WriteLine("K2 stinger delivery passed for negotiated routes, scope quotas, target incarnation, authentication and one-shot retirement.");
    }

    private static void StingerRoutesRespectVersionsQuotaAndIncarnation()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        var otherPackage = PackageSpec.Implicit("io.example.stinger-owner", "0.2.0", 'b');
        using var rig = new ControlRig(170, Sandbox, otherPackage);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var old = rig.AddPeer("Foundation receiver", Sandbox, otherPackage);
        old.UseRuntime(new UncheckedRuntimeHost(old.Runtime, InternalUiPresentationContract.FoundationVersion));
        var peer = rig.AddPeer("Stinger receiver", Sandbox, otherPackage);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false);
        var second = rig.AddPeer("Second stinger receiver", Sandbox, otherPackage);
        using var otherGuest = new UiDeliveryEndpoint(second, () => rig.Network.Now, author: false);
        rig.StartHosting(new InternalHostSettings("Stinger compatibility", 8));
        rig.JoinAndWait(old);
        That(old.Control.OpenSystemChannel(InternalSystemChannelKind.Players).TryGetValue(out var oldMessages),
            "The older peer can record its own authenticated channel.");
        using var oldChannel = oldMessages!;
        rig.JoinAndWait(peer);
        rig.JoinAndWait(second);
        var oldTarget = OverlayTarget(rig, host, old);
        var player = OverlayTarget(rig, host, peer);
        var secondPlayer = OverlayTarget(rig, host, second);
        var keptUi = player.Ui;
        var reveal = AStinger("WAVE CLEAR");

        var refused = oldTarget.Ui.ShowStinger(reveal);
        That(refused.ErrorCode == ModErrorCode.Unavailable && refused.ErrorMessage == UiPresentationTheme.RequiredControlsRuntime &&
            host.Ui.StingerPreflights == 0, "Targeting a v2 recipient stops before native fit validation or framing.");
        host.Ui.RejectStingerFit = true;
        var fit = keptUi.ShowStinger(AStinger("Cannot fit"));
        That(fit.ErrorCode == ModErrorCode.InvalidArgument && host.Ui.StingerPreflights == 1 && host.Ui.LocalStingers.Count == 0,
            "A targeted stinger must pass native fit validation without a local show.");
        That(host.Stinger(AStinger("Cannot broadcast")).ErrorCode == ModErrorCode.InvalidArgument &&
            host.Ui.StingerPreflights == 2 && host.Ui.LocalStingers.Count == 0 && guest.Ui.RemoteStingers.Count == 0,
            "Broadcast measures the receiver chains before a local enqueue or any dispatch.");
        host.Ui.RejectStingerFit = false;

        That(host.Stinger(reveal).Succeeded && host.Stinger(AStinger(reveal.Title, duration: 5.25d)).Succeeded &&
            rig.Run(() => guest.Ui.RemoteStingers.Count == 1 && otherGuest.Ui.RemoteStingers.Count == 1),
            "One newly admitted announcement reaches each compatible guest and an equal request coalesces.");
        That(host.Ui.LocalStingers.Count == 1 && guest.Ui.RemoteStingers[0].Owner == Sandbox.Id &&
            guest.Ui.RemoteStingers[0].Request.Title == reveal.Title && !OldReceivedStinger(oldChannel),
            "The original appears once on host and capable receivers. The older guest gets no event.");
        var warnings = rig.Host.Log.Lines.Count(line => line.Contains("stinger skipped 1 guest(s) without UI contract 3.", StringComparison.Ordinal));
        That(warnings == 1, "One bounded diagnostic counts the older recipient once.");

        for (var index = 1; index <= 3; index++)
            That(keptUi.ShowStinger(AStinger("Only you " + index)).Succeeded, "One broadcast and three targeted announcements fit the same sender scope.");
        That(keptUi.ShowStinger(AStinger("Only you 2")).Succeeded && keptUi.ShowStinger(AStinger("Full")).ErrorCode == ModErrorCode.RateLimited,
            "The fourth waiting event is refused and a matching queued request still coalesces.");
        That(secondPlayer.Ui.ShowStinger(AStinger("Second only")).Succeeded &&
            rig.Run(() => guest.Ui.RemoteStingers.Count == 4 && otherGuest.Ui.RemoteStingers.Count == 2),
            "Targeted delivery stays on one player's authenticated route and their quotas are independent.");
        That(guest.Ui.RemoteStingers.Skip(1).Select(value => value.Request.Title).SequenceEqual(
            new[] { "Only you 1", "Only you 2", "Only you 3" }) && host.Ui.LocalStingers.Count == 1,
            "Targeted one-shots arrive in order without queuing on the sender's local canvas.");

        using var otherLifetime = new FakeModLifetime();
        var otherScope = host.Scope(otherLifetime, ModExecutionContext.GamemodeServer, otherPackage.Id);
        var otherUi = new ThemeProbe(otherLifetime, author: true);
        That(host.Players.ShowContextStinger(otherScope, otherUi, reveal).Succeeded &&
            rig.Run(() => guest.Ui.RemoteStingers.Count == 5 && otherGuest.Ui.RemoteStingers.Count == 3) &&
            guest.Ui.RemoteStingers[^1].Owner == otherPackage.Id && guest.Ui.ActiveRemoteStingerScopes == 2,
            "An identical event from a second admitted owner keeps its own queue even when the first owner's queue is full.");

        var ownId = rig.Host.Control.SessionSnapshot.LocalParticipantId!.Value;
        That(host.Directory!.TryGet(ownId, out var own), "The local player has the same public player UI wrapper.");
        That(own!.Ui.ShowStinger(AStinger("Only host")).Succeeded && host.Ui.LocalStingers.Count == 2,
            "A targeted request for the host uses the local queue.");

        var elapsed = AllocationProbe.Measure("idle stinger delivery", () => host.Players.Presentation!.Send(),
            frames: 10_000, warmups: 256);
        That(elapsed == 0, "Resting presentation sends allocate nothing while announcements are active.");

        var late = rig.AddPeer("Late stinger recipient", Sandbox, otherPackage);
        using var lateGuest = new UiDeliveryEndpoint(late, () => rig.Network.Now, author: false);
        rig.JoinAndWait(late);
        var lateTarget = OverlayTarget(rig, host, late);
        That(lateGuest.Ui.RemoteStingers.Count == 0 && lateTarget.Ui.ShowStinger(AStinger("After joining")).Succeeded &&
            rig.Run(() => lateGuest.Ui.RemoteStingers.Count == 1),
            "Joining receives future targeted events and none from the presentation baseline.");

        var seen = guest.Ui.RemoteStingers.Count;
        var resets = guest.Ui.StingerResets;
        That(rig.Host.Control.Kick(player.Id, "Retire this test stream.").Succeeded, "The host retires the old stream.");
        That(rig.Run(() => (host.Players.Channel?.GetGeneration(player.Id) ?? 0) == 0 &&
            guest.Ui.StingerResets > resets) && guest.Ui.ActiveRemoteStingerScopes == 0,
            "The retired receiver stream clears its transient queues while the roster can remember the player.");
        rig.JoinAndWait(peer);
        var rejoined = OverlayTarget(rig, host, peer);
        That(guest.Ui.RemoteStingers.Count == seen &&
            rejoined.Ui.ShowStinger(reveal).Succeeded && rig.Run(() => guest.Ui.RemoteStingers.Count == seen + 1),
            "The fresh authenticated stream can receive the same announcement again without replaying the old event.");

        StingerPlayerHandleTracksTheDirectoryIncarnation(rig, host, guest, rejoined.Id);
    }

    private static void StingerPlayerHandleTracksTheDirectoryIncarnation(ControlRig rig, UiDeliveryEndpoint host,
        UiDeliveryEndpoint guest, ParticipantId id)
    {
        using var scope = new FakeModLifetime();
        var ui = new ThemeProbe(scope, author: true);
        var context = host.Players.Bind(Sandbox.Id, true, new CapturedModLogger(), scope,
            new FakeLocalPlayerService(scope), ui, new ModInputActivity());
        ulong incarnation = 1;
        using var directory = new PlayerDirectory(() => rig.Host.Control.SessionSnapshot, () => incarnation, context);
        context.Directory = directory;
        That(directory.TryGet(id, out var original), "The scope has a live public player handle.");
        var oldUi = original!.Ui;
        var received = guest.Ui.RemoteStingers.Count;
        That(oldUi.ShowStinger(AStinger("First incarnation")).Succeeded && rig.Run(() => guest.Ui.RemoteStingers.Count == received + 1),
            "The live public player UI delegates its stinger call to the authenticated backend.");

        incarnation++;
        That(directory.TryGet(id, out var current) && !ReferenceEquals(current, original),
            "The SDK directory replaces a player wrapper when its owning incarnation advances.");
        var validated = ui.StingerPreflights;
        That(oldUi.ShowStinger(AStinger("Retired wrapper")).ErrorCode == ModErrorCode.NotFound && ui.StingerPreflights == validated,
            "The retired public UI cannot reach a backend even when the same player ID is current.");
        That(current!.Ui.ShowStinger(AStinger("Current wrapper")).Succeeded &&
            rig.Run(() => guest.Ui.RemoteStingers.Count == received + 2), "The current wrapper still delivers successfully.");
    }

    private static UiStingerRequest AStinger(string title, double duration = 5.75d) =>
        new(title, "Check the objective", UiGlyph.Check, UiPaintRole.Success, durationSeconds: duration);

    private static bool OldReceivedStinger(IInternalSystemChannel channel)
    {
        var reader = new WorldWireReader();
        while (channel.TryReceive(out var message))
        {
            reader.Reset(message.Payload);
            if (reader.TryReadHeader(out var kind) && kind == UiStingerDelivery.EventKind) return true;
        }
        return false;
    }
}
