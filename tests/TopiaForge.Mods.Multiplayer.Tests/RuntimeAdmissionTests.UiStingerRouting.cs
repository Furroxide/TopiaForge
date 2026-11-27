using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    private static void StingerBroadcastCoalescesPerViewer()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(195, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var peer = rig.AddPeer("First guest", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false);
        rig.StartHosting(new InternalHostSettings("Per-viewer stingers", 4));
        rig.JoinAndWait(peer);
        _ = OverlayTarget(rig, host, peer);

        var hostId = rig.Host.Control.SessionSnapshot.LocalParticipantId!.Value;
        That(host.Directory!.TryGet(hostId, out var own), "The host author can address its own display.");
        var request = AStinger("A current announcement", duration: 6d);
        That(own!.Ui.ShowStinger(request).Succeeded && host.Ui.LocalStingers.Count == 1 &&
            guest.Ui.RemoteStingers.Count == 0, "The local route shows the announcement on the host alone.");
        That(host.Stinger(request).Succeeded && rig.Run(() => guest.Ui.RemoteStingers.Count == 1) &&
            host.Ui.LocalStingers.Count == 1, "A guest with room gets the explicitly broadcast announcement while the host coalesces.");

        var late = rig.AddPeer("Later guest", Sandbox);
        using var newcomer = new UiDeliveryEndpoint(late, () => rig.Network.Now, author: false);
        rig.JoinAndWait(late);
        _ = OverlayTarget(rig, host, late);
        That(newcomer.Ui.RemoteStingers.Count == 0, "Joining does not replay the current local announcement.");
        That(host.Stinger(request).Succeeded && rig.Run(() => newcomer.Ui.RemoteStingers.Count == 1) &&
            guest.Ui.RemoteStingers.Count == 1 && host.Ui.LocalStingers.Count == 1,
            "An explicit new offer reaches the later guest and coalesces independently on both original viewers.");
    }

    private static void StingerAndToastShareEachReceiverBudget()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(196, Sandbox);
        long? frozen = null;
        using var host = new UiDeliveryEndpoint(rig.Host, () => frozen ?? rig.Network.Now, author: true);
        var peer = rig.AddPeer("Shared-budget guest", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => frozen ?? rig.Network.Now, author: false);
        var other = rig.AddPeer("Independent-budget guest", Sandbox);
        using var otherGuest = new UiDeliveryEndpoint(other, () => frozen ?? rig.Network.Now, author: false);
        rig.StartHosting(new InternalHostSettings("Mixed presentation budget", 5));
        rig.JoinAndWait(peer);
        rig.JoinAndWait(other);
        var recipient = OverlayTarget(rig, host, peer);
        var second = OverlayTarget(rig, host, other);
        frozen = rig.Network.Now;
        var rejected = guest.Players.Rejected;
        That(host.Toast("A shared toast").Succeeded &&
            rig.Run(() => guest.Ui.ToastCount == 1 && otherGuest.Ui.ToastCount == 1),
            "The existing shared toast spends one presentation event on each receiving stream.");

        var authors = new List<ScopeAuthor>();
        try
        {
            for (var index = 0; index < 31; index++)
            {
                if (index % 4 == 0) authors.Add(new ScopeAuthor(rig, host, Sandbox.Id));
                That(authors[^1].Target(recipient.Id).ShowStinger(AStinger("Counted announcement " + index)).Succeeded,
                    "A shared toast leaves room for 31 addressed presentation events.");
            }

            var final = AStinger("Independent event");
            That(authors[^1].Target(recipient.Id).ShowStinger(final).ErrorCode == ModErrorCode.RateLimited &&
                authors[^1].Target(second.Id).ShowStinger(final).Succeeded,
                "After a toast and 31 stingers the first stream refuses the next counted event, and the other stream admits it.");
            var opened = recipient.Ui.CreateSurface(new UiSurfaceRequest("separate-window", "Separate window", "",
                UiSurfaceKind.Window, 320, 240, new UiText("Content", UiTextStyle.Title)));
            That(opened.TryGetValue(out var handle), "The targeted surface has its own send budget. " + opened.ErrorMessage);
            using var window = handle!;
            var delivered = rig.Run(() => guest.Ui.RemoteStingers.Count == 31 &&
                guest.Ui.Surfaces.ContainsKey("Separate window") && otherGuest.Ui.RemoteStingers.Count == 1);
            That(delivered && guest.Players.Rejected == rejected,
                $"All accepted events and the separate window arrive. Stingers {guest.Ui.RemoteStingers.Count}/31, " +
                $"other stream {otherGuest.Ui.RemoteStingers.Count}/1, window {guest.Ui.Surfaces.ContainsKey("Separate window")}, " +
                $"extra refusals {guest.Players.Rejected - rejected}.");
        }
        finally
        {
            foreach (var author in authors) author.Dispose();
        }
    }
}
