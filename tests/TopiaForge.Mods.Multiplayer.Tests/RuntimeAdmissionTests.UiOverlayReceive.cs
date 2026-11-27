using TopiaForge.ModManager;
using TopiaForge.Mods.Internal;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    private static void OverlayReceiverRefusalsAreAtomic()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(169, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var peer = rig.AddPeer("Receiving overlay test", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false);
        rig.StartHosting(new InternalHostSettings("Overlay receiver checks", 4));
        rig.JoinAndWait(peer);
        var player = OverlayTarget(rig, host, peer);
        using var targeted = (RemoteUiSurface)player.Ui.CreateSurface(Overlay("Targeted", new UiText("Original target"))).Value!;
        using var shared = (MirroredHudPresenter)host.Shared(Overlay("Shared", new UiText("Original shared"))).Value!;
        using var hud = (MirroredHudPresenter)host.Shared(SizedHud("HUD", new UiText("Original HUD"))).Value!;
        That(rig.Run(() => guest.Ui.Surfaces.Count == 3), "Both authenticated overlay paths and the old HUD are established.");
        var targetState = guest.Ui.Surfaces["Targeted"];
        var sharedState = guest.Ui.Surfaces["Shared"];
        var hudState = guest.Ui.Surfaces["HUD"];
        var targetEnvelope = targetState.Theme.Envelope!;
        var sharedEnvelope = sharedState.Theme.Envelope!;
        var wrongDigest = new UiWireEnvelope(targetEnvelope.OwnerPackageId, new byte[32],
            targetEnvelope.Generation, targetEnvelope.ThemeId, targetEnvelope.DescriptorDigest.Span);
        var channel = host.Players.Channel!;
        var recipient = InternalSystemTarget.Only(player.Id);

        void RejectTarget(UiSurfaceRequest request, UiWireEnvelope envelope)
        {
            var revision = targeted.Revision + 1;
            var frame = new WorldWireWriter(MultiplayerPlayers.MaximumMessageBytes);
            frame.Begin(10); frame.WriteString(Sandbox.Id); frame.WriteString(player.Id.Value);
            frame.WriteVarUInt(targeted.Token); frame.WriteVarUInt(revision);
            UiWireCodec.WriteTargetedSurfaceBody(frame, request, request.Body, true, request.Content,
                guest.Players.UiClockEpoch, retained: false, envelope);
            var rejected = guest.Players.Rejected;
            That(channel.Send(frame.Segment, recipient).Succeeded && rig.Run(() => guest.Players.Rejected > rejected) &&
                targetState.ReceivedContent is UiText { Text: "Original target" } &&
                ReferenceEquals(targetState.Theme.Envelope, targetEnvelope),
                "An invalid targeted candidate preserves the admitted content, request and envelope.");
        }

        RejectTarget(Overlay("Targeted", new UiText("Wrong package")), wrongDigest);
        RejectTarget(new UiSurfaceRequest("Targeted", "Targeted", "Changed", UiSurfaceKind.Window, 320, 240,
            new UiText("Wrong kind")), targetEnvelope);
        RejectTarget(Overlay("Targeted", new UiButton("upgrade", "Upgrade", () => { })), targetEnvelope);
        That(targeted.SetContent(new UiText("New target")).Succeeded &&
            rig.Run(() => targetState.ReceivedContent is UiText { Text: "New target" }),
            "The next genuine targeted revision is still accepted.");

        var candidateHud = WholeSharedRecord(hud.Payload!, UiSurfaceKind.Hud, new UiText("Should stay unpublished"), null);
        var malformed = WholeSharedRecord(shared.Payload!, UiSurfaceKind.Overlay, new UiText("Invalid dismissal flag"), sharedEnvelope);
        malformed[^1] = 2;
        RejectBaseline(malformed);
        var nonDismissible = WholeSharedRecord(shared.Payload!, UiSurfaceKind.Overlay, new UiText("Impossible button"), sharedEnvelope);
        nonDismissible[^1] = 0;
        RejectBaseline(nonDismissible);
        RejectBaseline(WholeSharedRecord(shared.Payload!, UiSurfaceKind.Overlay,
            new UiButton("remote", "Remote control", () => { }), sharedEnvelope));
        RejectBaseline(WholeSharedRecord(shared.Payload!, UiSurfaceKind.Hud, new UiText("Wrong kind"), sharedEnvelope));
        That(shared.SetContent(new UiText("New shared")).Succeeded && hud.SetContent(new UiText("New HUD")).Succeeded &&
            rig.Run(() => sharedState.ReceivedContent is UiText { Text: "New shared" } &&
                hudState.Surface.Content is UiText { Text: "New HUD" }),
            "Invalid baselines did not advance the accepted version or overwrite a valid earlier entry.");

        void RejectBaseline(byte[] bad)
        {
            var frame = new WorldWireWriter(MultiplayerPlayers.MaximumMessageBytes);
            frame.Begin(30); frame.WriteString(""); frame.WriteVarUInt(ulong.MaxValue); frame.WriteVarUInt(2);
            frame.WriteBytes(candidateHud); frame.WriteBytes(bad); frame.WriteVarUInt(0);
            var rejected = guest.Players.Rejected;
            That(channel.Send(frame.Segment, recipient).Succeeded && rig.Run(() => guest.Players.Rejected > rejected) &&
                sharedState.ReceivedContent is UiText { Text: "Original shared" } &&
                hudState.Surface.Content is UiText { Text: "Original HUD" } &&
                ReferenceEquals(sharedState.Theme.Envelope, sharedEnvelope),
                "One invalid shared overlay rejects the whole candidate baseline without changing its HUD or overlay.");
        }
    }

    private static byte[] WholeSharedRecord(byte[] previous, UiSurfaceKind kind, UiNode tree, UiWireEnvelope? envelope)
    {
        var read = new WorldWireReader();
        read.Reset(previous);
        That(read.TryReadHeader(out _), "The existing shared record has its own surface-kind header.");
        var token = UiNodeWireCodec.Id(read);
        var title = UiNodeWireCodec.Text(read);
        var request = new UiSurfaceRequest(title, title, "Body", kind, 320, 240, tree);
        var frame = new WorldWireWriter(MultiplayerPresentation.MaximumBytes);
        frame.Begin((byte)kind); frame.WriteVarUInt(token);
        frame.WriteString(request.Title); frame.WriteString(request.Body);
        frame.WriteSingle(request.Width); frame.WriteSingle(request.Height);
        frame.WriteByte((byte)request.HudAnchor); frame.WriteByte((byte)request.HudVisibility); frame.WriteByte(1);
        UiWireCodec.WriteSurfaceComposition(frame, request, tree, "", retained: false, envelope);
        return MultiplayerPresentation.Copy(frame);
    }
}
