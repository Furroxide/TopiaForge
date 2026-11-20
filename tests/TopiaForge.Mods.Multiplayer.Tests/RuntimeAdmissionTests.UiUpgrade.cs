using TopiaForge.ModManager;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    private static void TargetedUpgradeSurvivesGuestScopeTeardown()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(160, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var peer = rig.AddPeer("Scoped recipient", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false);
        using var guestAuthor = new FakeModLifetime();
        var authorUi = BindGuestAuthorUi(guest, guestAuthor);
        var target = JoinTargetedUi(rig, host, peer);

        var legacy = new UiText("Legacy revision");
        using var targeted = (RemoteUiSurface)target.Ui.CreateSurface(new UiSurfaceRequest(
            "legacy-window", "Legacy window", "Original body", content: legacy)).Value!;
        That(rig.Run(() => authorUi.Surfaces.ContainsKey("Legacy window")), "A legacy window uses the guest author scope.");
        var previous = authorUi.Surfaces["Legacy window"];
        var token = targeted.Token;
        var revision = targeted.Revision;

        That(targeted.SetContent(new UiText("Foundation revision", UiTextStyle.Banner)).Succeeded, "The host can send a K1 upgrade.");
        That(rig.Run(() => ObservedWindow()?.Theme.Envelope != null), "The guest reads the upgraded revision before its author scope ends.");
        var upgraded = ObservedWindow()!;
        guestAuthor.Dispose();
        That(upgraded.IsVisible && guest.Ui.Surfaces.TryGetValue("Legacy window", out var installed) && ReferenceEquals(upgraded, installed)
            && upgraded.Id == previous.Id && targeted.Token == token && !previous.IsVisible,
            "The same token survives guest author teardown with its upgraded canvas in the process receiver.");

        var rejected = guest.Players.Rejected;
        SendTargetedRevision(host, target.Id, token, revision, targeted.Request, legacy, null);
        That(rig.Run(() => guest.Players.Rejected > rejected) && upgraded.Surface.Content is UiText { Text: "Foundation revision" },
            "An older revision cannot replace the rehomed canvas after the guest context ends.");
        That(targeted.IsVisible, "Retiring the old canvas cannot dismiss the upgraded window.");

        var called = 0;
        var next = new UiColumn(new UiText("New revision", UiTextStyle.Banner), new UiButton("ready", "Ready", () => called++));
        That(targeted.SetContent(next).Succeeded && rig.Run(() => upgraded.Surface.Content is UiColumn),
            "The same token accepts a subsequent revision without a guest author context.");
        That(upgraded.Surface.ActivateButton("ready").Succeeded && rig.Run(() => called == 1) &&
            ReferenceEquals(upgraded, guest.Ui.Surfaces["Legacy window"]),
            "The subsequent revision routes the current callback through the surviving canvas.");

        ThemeSurfaceProbe? ObservedWindow() => guest.Ui.Surfaces.GetValueOrDefault("Legacy window")
            ?? authorUi.Surfaces.GetValueOrDefault("Legacy window");
    }

    private static void FailedTargetedUpgradeKeepsLegacyState()
    {
        using var rig = new ControlRig(161, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var peer = rig.AddPeer("Rollback recipient", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false);
        using var guestAuthor = new FakeModLifetime();
        var authorUi = BindGuestAuthorUi(guest, guestAuthor);
        var target = JoinTargetedUi(rig, host, peer);

        var legacyCalls = 0;
        var legacy = new UiButton("legacy", "Legacy action", () => legacyCalls++);
        using var targeted = (RemoteUiSurface)target.Ui.CreateSurface(new UiSurfaceRequest(
            "rollback", "Rollback window", "Original body", content: legacy)).Value!;
        That(rig.Run(() => authorUi.Surfaces.ContainsKey("Rollback window")), "The original legacy window is visible.");
        var previous = authorUi.Surfaces["Rollback window"];
        var admitted = previous.Surface.Content;

        That(host.Players.TryOutgoingUiDigest(Sandbox.Id, requireSignedRoster: true, out var digest),
            "The host can resolve its admitted package digest.");
        That(UiPresentationTheme.TryNextTargetGeneration(0, out var generation), "The first targeted generation is available.");
        var candidate = UiPresentationTheme.Envelope(Sandbox.Id, digest!, generation, null);
        var next = new UiText("Foundation revision", UiTextStyle.Banner);
        guest.Ui.FailNextRemoteShow = true;
        var rejected = guest.Players.Rejected;
        SendTargetedRevision(host, target.Id, targeted.Token, targeted.Revision + 1, targeted.Request, next, candidate);
        That(rig.Run(() => guest.Players.Rejected > rejected), "A replacement canvas that fails during visibility preparation is refused.");
        That(ReferenceEquals(admitted, previous.Surface.Content) && previous.Surface.Body == "Original body" && previous.IsVisible &&
            guest.Ui.Surfaces.Count == 0 && previous.Surface.ActivateButton("legacy").Succeeded && rig.Run(() => legacyCalls == 1),
            "A failed transfer disposes its staged canvas and leaves the legacy tree, body and callback intact.");

        var predecessor = UiPresentationTheme.Envelope(Sandbox.Id, digest!, generation - 1, null);
        var probeRequest = new UiSurfaceRequest("watermark", "Watermark probe", "");
        SendTargetedRevision(host, target.Id, targeted.Token + 1, 1, probeRequest, next, predecessor);
        That(rig.Run(() => guest.Ui.Surfaces.ContainsKey("Watermark probe")),
            "A refused replacement cannot advance the route's high-water mark past another presentation.");
        That(targeted.SetContent(next).Succeeded && rig.Run(() => guest.Ui.Surfaces.ContainsKey("Rollback window")) && !previous.IsVisible,
            "The same staged generation can be accepted once the receiving canvas is available.");
        Console.WriteLine("K1 targeted upgrade: guest-scope teardown, refused migration, stale revisions and callback continuity passed.");
    }

    private static ThemeProbe BindGuestAuthorUi(UiDeliveryEndpoint guest, FakeModLifetime lifetime)
    {
        var ui = new ThemeProbe(lifetime, author: false);
        guest.Players.Bind(Sandbox.Id, true, new CapturedModLogger(), lifetime, new FakeLocalPlayerService(lifetime), ui, new ModInputActivity());
        return ui;
    }

    private static IPlayer JoinTargetedUi(ControlRig rig, UiDeliveryEndpoint host, ControlPeer peer)
    {
        rig.StartHosting(new InternalHostSettings("Targeted UI", 4));
        rig.JoinAndWait(peer);
        var recipient = peer.Session.Snapshot.LocalParticipantId!.Value;
        That(rig.Run(() => (host.Players.Channel?.GetGeneration(recipient) ?? 0) != 0), "The recipient has an authenticated Players stream.");
        That(host.Directory!.TryGet(recipient, out var target), "The host can address the joined player.");
        return target!;
    }

    private static void SendTargetedRevision(UiDeliveryEndpoint host, ParticipantId recipient, ulong token, ulong revision,
        UiSurfaceRequest request, UiNode content, UiWireEnvelope? envelope)
    {
        var writer = new WorldWireWriter(MultiplayerPlayers.MaximumMessageBytes);
        writer.Begin(10);
        writer.WriteString(Sandbox.Id);
        writer.WriteString(recipient.Value);
        writer.WriteVarUInt(token);
        writer.WriteVarUInt(revision);
        UiWireCodec.WriteTargetedSurfaceBody(writer, request, "Offered body", true, content, host.Players.UiClockEpoch,
            retained: false, envelope: envelope);
        var sent = host.Players.Channel!.Send(writer.Segment, InternalSystemTarget.Only(recipient));
        That(sent.TryGetValue(out var recipients) && recipients == 1, "The host queues the recorded revision to one recipient.");
    }
}
