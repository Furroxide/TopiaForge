using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.TestSupport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    internal static void RunOverlayWire()
    {
        MixedOverlayRoutesKeepTheirClassAndClock();
        OverlayScopesShareTheHudBudgets();
        OverlayReceiverRefusalsAreAtomic();
        Console.WriteLine("K2 overlay delivery passed for negotiated routes, fixed class, signed ownership, lifetime, visibility, callbacks and quotas.");
    }

    private static void MixedOverlayRoutesKeepTheirClassAndClock()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(167, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var old = rig.AddPeer("Foundation receiver", Sandbox);
        old.UseRuntime(new UncheckedRuntimeHost(old.Runtime, InternalUiPresentationContract.FoundationVersion));
        var peer = rig.AddPeer("Overlay receiver", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false);
        guest.Ui.RecordControlsAtBoundary = true;
        rig.StartHosting(new InternalHostSettings("Overlay compatibility", 6));
        rig.JoinAndWait(old);
        rig.JoinAndWait(peer);
        var oldTarget = OverlayTarget(rig, host, old);
        var target = OverlayTarget(rig, host, peer);
        That(rig.Host.Control.UiContractVersionOf(oldTarget.Id) == 2 && rig.Host.Control.UiContractVersionOf(target.Id) == 3,
            "The provider negotiated different UI versions on the two authenticated peer streams.");

        using var hud = host.Shared("Legacy baseline", UiTextStyle.Body);
        var countdown = UiCountdown.WithFormat(100d, "Wave", format: UiCountdownFormat.MinutesSeconds);
        var plain = new UiText("A notice with only legacy UI tags");
        var refusedShared = host.Shared(Overlay("Shared overlay", plain));
        var refusedTarget = oldTarget.Ui.CreateSurface(Overlay("Target overlay", plain));
        That(refusedShared.ErrorCode == ModErrorCode.Unavailable && refusedTarget.ErrorCode == ModErrorCode.Unavailable &&
            refusedShared.ErrorMessage == UiPresentationTheme.RequiredControlsRuntime &&
            refusedTarget.ErrorMessage == UiPresentationTheme.RequiredControlsRuntime &&
            host.Ui.LocalSurfaces.Count == 1 && host.Players.Presentation!.RequiredUiVersion == 1,
            "Kind 3 alone refuses v2 on the exact addressed and shared routes, before opening a canvas or raising retained capability.");
        That(host.Shared(Overlay("Unclosable", plain).WithDismissible(false)).ErrorCode == ModErrorCode.InvalidArgument &&
            target.Ui.CreateSurface(Overlay("Unclosable", plain).WithDismissible(false)).ErrorCode == ModErrorCode.InvalidArgument,
            "A non-dismissible overlay needs a reachable enabled button before either route can admit it.");

        var presses = 0;
        var button = new UiButton("continue", "Continue", () => presses++);
        using var local = host.Shared(Overlay("Local interactive overlay", button)).Value!;
        That(local != null && local.IsVisible && local.SetContent(plain).ErrorCode == ModErrorCode.InvalidArgument &&
            ReferenceEquals(host.Ui.LocalSurfaces.Single(surface => surface.Id == "Local interactive overlay").Content, button),
            "A host-only overlay opens alongside an older peer and retains its interactive tree after a refused replacement.");

        var tree = new UiColumn(countdown, button);
        using var aimed = (RemoteUiSurface)target.Ui.CreateSurface(Overlay("Target overlay", tree).WithDismissible(false)).Value!;
        using var aimedReadOnly = (RemoteUiSurface)target.Ui.CreateSurface(Overlay("Read-only target", plain)).Value!;
        That(rig.Run(() => guest.Ui.Surfaces.Count == 3) &&
            guest.Ui.Surfaces.TryGetValue("Target overlay", out _) && guest.Ui.Surfaces.TryGetValue("Read-only target", out _) &&
            !guest.Ui.Surfaces.ContainsKey("Local interactive overlay"),
            "The compatible recipient receives both targeted overlays and the legacy HUD. The host's interactive route stays local.");
        var aimedReceipt = guest.Ui.Surfaces["Target overlay"];
        var readOnlyReceipt = guest.Ui.Surfaces["Read-only target"];
        That(aimedReceipt.Request.Kind == UiSurfaceKind.Overlay && !aimedReceipt.Request.Dismissible &&
            readOnlyReceipt.Request.Kind == UiSurfaceKind.Overlay && readOnlyReceipt.Request.Dismissible &&
            aimedReceipt.Theme.Envelope?.OwnerPackageId == Sandbox.Id &&
            aimedReceipt.Theme.Package?.Sha256 == peer.Control.UiPresentationPackageDigest(Sandbox.Id),
            "The targeted receiver sees the immutable request and the exact admitted package even for legacy content.");
        var aimedRevision = aimed.Revision;
        That(aimed.SetContent(plain).ErrorCode == ModErrorCode.InvalidArgument &&
            aimed.SetContent(new UiButton("disabled", "Disabled", () => { }, enabled: false)).ErrorCode == ModErrorCode.InvalidArgument &&
            aimedReadOnly.SetContent(button).ErrorCode == ModErrorCode.InvalidArgument &&
            ReferenceEquals(aimed.Content, tree) && aimed.Revision == aimedRevision && ReferenceEquals(aimedReadOnly.Content, plain),
            "Targeted overlays refuse both content-class changes and loss of the non-dismissible button before touching their retained state.");

        var oldButton = (UiButton)((UiColumn)aimedReceipt.ReceivedContent!).Children[1];
        oldButton.Activated();
        That(rig.Run(() => presses == 1), "The real targeted callback runs on the author's context.");
        var rejected = host.Players.Rejected;
        SendOverlayReply(guest.Players.Channel!, aimed.Token, aimed.Revision, kind: 15);
        That(rig.Run(() => host.Players.Rejected > rejected) && aimed.IsVisible, "A non-dismissible overlay refuses a guest's Back reply.");
        rejected = host.Players.Rejected;
        SendOverlayReply(guest.Players.Channel!, aimedReadOnly.Token, aimedReadOnly.Revision, kind: 15);
        That(rig.Run(() => host.Players.Rejected > rejected) && aimedReadOnly.IsVisible, "A read-only overlay refuses a guest's Back reply.");
        rejected = host.Players.Rejected;
        That(old.Control.OpenSystemChannel(InternalSystemChannelKind.Players).TryGetValue(out var olderChannel),
            "The older peer has its own authenticated channel.");
        using (olderChannel) SendOverlayReply(olderChannel!, aimed.Token, aimed.Revision, kind: 14, control: "continue");
        That(rig.Run(() => host.Players.Rejected > rejected) && presses == 1, "A different authenticated peer has no targeted callback authority.");

        old.Control.Leave();
        That(rig.Run(() => !rig.Host.Control.SessionSnapshot.TryGetParticipant(oldTarget.Id, out var leaving) || !leaving!.IsConnected),
            "The older receiver leaves before the shared overlay is opened.");
        var readout = UiCountdown.WithFormat(100d, "Reveal", format: UiCountdownFormat.MinutesSeconds);
        using var mirrored = (MirroredHudPresenter)host.Shared(Overlay("Shared overlay", readout)).Value!;
        That(rig.Run(() => guest.Ui.Surfaces.ContainsKey("Shared overlay")), "A shared v3 overlay reaches the compatible guest.");
        var sharedReceipt = guest.Ui.Surfaces["Shared overlay"];
        That(sharedReceipt.Request.Kind == UiSurfaceKind.Overlay && sharedReceipt.Request.Dismissible &&
            sharedReceipt.Theme.Envelope?.OwnerPackageId == Sandbox.Id &&
            mirrored.SetContent(button).ErrorCode == ModErrorCode.InvalidArgument &&
            ReferenceEquals(mirrored.Content, readout),
            "The read-only mirrored route retains its authenticated kind, content and initial class.");
        mirrored.Hide();
        aimed.Hide();
        That(rig.Run(() => !sharedReceipt.IsVisible && !aimedReceipt.IsVisible), "The same remote overlays hide when their author hides them.");
        rejected = host.Players.Rejected;
        SendOverlayReply(guest.Players.Channel!, aimed.Token, aimed.Revision, kind: 14, control: "continue");
        That(rig.Run(() => host.Players.Rejected > rejected) && presses == 1, "Hidden targeted overlays cannot invoke a callback.");
        mirrored.Show();
        aimed.Show();
        That(rig.Run(() => sharedReceipt.IsVisible && aimedReceipt.IsVisible), "Explicit Show restores both retained overlays.");

        var late = rig.AddPeer("Later foundation receiver", Sandbox);
        late.UseRuntime(new UncheckedRuntimeHost(late.Runtime, InternalUiPresentationContract.FoundationVersion));
        mirrored.Hide();
        That(late.Control.Join(late.Target(), new InternalJoinSettings()).Succeeded &&
            rig.Run(() => late.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended) &&
            late.Control.GetStatus().ContentProblems.Contains(UiPresentationTheme.RequiredControlsRuntime),
            "Even a hidden shared overlay blocks an incompatible late admission.");
        mirrored.Show();

        var epoch = countdown.ClockEpoch;
        var identity = countdown.ClockIdentity;
        var resting = AllocationProbe.Measure("settled overlay delivery", () => _ = aimed.SetContent(tree),
            frames: 10_000, warmups: 256);
        var restingShared = AllocationProbe.Measure("settled mirrored overlay delivery", () =>
        {
            _ = mirrored.SetContent(readout);
            host.Players.Presentation!.Send();
        }, frames: 10_000, warmups: 256);
        That(resting == 0 && restingShared == 0 && aimed.Revision == aimedRevision,
            "Settled targeted and mirrored overlay writes allocate zero bytes.");
        That(rig.Host.Control.ReplaceSession().Succeeded, "The provider replaces the presentation epoch.");
        var replayed = rig.Run(() => guest.Players.UiClockEpoch != epoch &&
            guest.Ui.Surfaces.TryGetValue("Target overlay", out var targetState) &&
            targetState.Request.Kind == UiSurfaceKind.Overlay && !targetState.Request.Dismissible &&
            targetState.ReceivedContent is UiColumn replaced && replaced.Children[0] is UiText { Text: "Wave 0:00" } &&
            guest.Ui.Surfaces.TryGetValue("Shared overlay", out var sharedState) &&
            sharedState.Request.Kind == UiSurfaceKind.Overlay && sharedState.ReceivedContent is UiText { Text: "Reveal 0:00" });
        That(replayed, "Both authenticated route replays keep the overlay and deliver the expired timer's zero surrogate. " +
            $"Target {OverlayReadout("Target overlay")}, shared {OverlayReadout("Shared overlay")}, " +
            $"epoch changed {guest.Players.UiClockEpoch != epoch}, canvas refusal {guest.Ui.LastCreateFailure ?? "none"}.");

        string OverlayReadout(string title)
        {
            if (!guest.Ui.Surfaces.TryGetValue(title, out var received)) return "missing";
            var node = received.ReceivedContent is UiColumn group ? group.Children.FirstOrDefault() : received.ReceivedContent;
            return received.Request.Kind + "/" + received.Request.Dismissible + "/" + (node switch
            {
                UiText text => text.Text,
                _ => node?.GetType().Name ?? "empty",
            });
        }
        That(ReferenceEquals(identity, countdown.ClockIdentity) && countdown.ClockEpoch == epoch && aimed.SetContent(tree).Succeeded &&
            aimed.SetContent(new UiColumn(countdown, button)).ErrorCode == ModErrorCode.InvalidArgument,
            "A retained tree keeps its original clock while fresh admission refuses the expired deadline.");
        rejected = host.Players.Rejected;
        SendOverlayReply(guest.Players.Channel!, aimed.Token, aimedRevision, kind: 14, control: "continue");
        That(rig.Run(() => host.Players.Rejected > rejected) && presses == 1, "The old callback revision cannot target the new epoch.");
        ((UiButton)((UiColumn)guest.Ui.Surfaces["Target overlay"].ReceivedContent!).Children[1]).Activated();
        That(rig.Run(() => presses == 2), "The new epoch still owns the targeted button.");

        aimedReadOnly.Dispose();
        using var backable = (RemoteUiSurface)target.Ui.CreateSurface(Overlay("Back overlay", button)).Value!;
        var dismissals = 0;
        backable.Dismissed += () => dismissals++;
        That(rig.Run(() => guest.Ui.Surfaces.ContainsKey("Back overlay")), "Disposing a slot permits another targeted overlay.");
        guest.Ui.Surfaces["Back overlay"].BackAtBoundary();
        That(rig.Run(() => dismissals == 1 && !backable.IsVisible && !guest.Ui.Surfaces["Back overlay"].IsVisible),
            "A valid Back event hides the dismissible targeted overlay exactly once.");

        host.Dispose();
        That(rig.Run(() => guest.Ui.Surfaces.Count == 0), "Ending the author scope retires all its shared and targeted overlays.");
        Console.WriteLine("Authenticated overlay epoch and callbacks passed. Settled targeted and mirrored updates allocated 0 bytes each.");
    }

    private static void OverlayScopesShareTheHudBudgets()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(168, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var peer = rig.AddPeer("Overlay quota recipient", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false);
        rig.StartHosting(new InternalHostSettings("Overlay quotas", 4));
        rig.JoinAndWait(peer);
        var target = OverlayTarget(rig, host, peer);
        var ownId = rig.Host.Control.SessionSnapshot.LocalParticipantId!.Value;
        That(host.Directory!.TryGet(ownId, out var ownPlayer), "The host's own player route is available.");

        var label = new UiText("Notice");
        var parked = (MirroredHudPresenter)host.Shared(Overlay("Shared", label)).Value!;
        using var local = host.Shared(Overlay("Local", new UiButton("close", "Close", () => { }))).Value!;
        var remote = target.Ui.CreateSurface(Overlay("Targeted", label)).Value!;
        using var own = ownPlayer!.Ui.CreateSurface(Overlay("Own player", label)).Value!;
        parked.Hide();
        That(host.Shared(Overlay("Fifth", label)).ErrorCode == ModErrorCode.RateLimited &&
            target.Ui.CreateSurface(Overlay("Fifth targeted", label)).ErrorCode == ModErrorCode.RateLimited,
            "Local, mirrored and addressed handles count together once each, and hidden overlays still consume their slots.");
        remote.Dispose();
        using var replacement = target.Ui.CreateSurface(Overlay("Reclaimed", label)).Value!;
        That(replacement != null && rig.Run(() => guest.Ui.Surfaces.ContainsKey("Reclaimed")), "Disposal immediately frees an author slot.");

        using var siblingLifetime = new FakeModLifetime();
        var sibling = host.Scope(siblingLifetime, ModExecutionContext.GamemodeServer);
        var siblingUi = new ThemeProbe(siblingLifetime, author: true);
        using var other = (MirroredHudPresenter)host.Players.CreateContextSurface(sibling, siblingUi, Overlay("Sibling", label)).Value!;
        That(other != null && rig.Run(() => guest.Ui.Surfaces.ContainsKey("Sibling")), "A sibling scope of the same package has its own four slots.");
        var siblingOverlays = new List<IUiSurface>();
        for (var index = 1; index < 4; index++)
        {
            var siblingResult = host.Players.CreateContextSurface(sibling, siblingUi, Overlay("Sibling " + index, label));
            That(siblingResult.Succeeded, "Each sibling overlay owns its own valid slot.");
            siblingOverlays.Add(siblingResult.Value!);
        }
        That(rig.Run(() => guest.Ui.Surfaces.Values.Count(surface => surface.Request.Kind == UiSurfaceKind.Overlay) == 6),
            "A single receiving service can display more than four overlays from distinct author scopes.");
        foreach (var siblingOverlay in siblingOverlays) siblingOverlay.Dispose();
        var ordinaryHuds = new List<IUiSurface>();
        for (var index = 0; index < MultiplayerPresentation.MaximumSurfaces - 2; index++)
        {
            var id = "Ordinary " + index;
            var corner = (UiHudAnchor)((int)UiHudAnchor.TopLeft + index % 8);
            var request = new UiSurfaceRequest(id, id, "", UiSurfaceKind.Hud, 160, 96).WithHudAnchor(corner);
            var ordinary = host.Shared(request);
            That(ordinary.TryGetValue(out var ordinaryHud),
                $"The baseline admits ordinary HUD {index + 1} alongside both overlay records. {ordinary.ErrorMessage}");
            ordinaryHuds.Add(ordinaryHud!);
        }
        var extraHud = host.Shared(SizedHud("Extra HUD", label));
        var extraOverlay = host.Players.CreateContextSurface(sibling, siblingUi, Overlay("Extra overlay", label));
        That(extraHud.ErrorCode == ModErrorCode.RateLimited && extraOverlay.ErrorCode == ModErrorCode.RateLimited,
            $"HUDs and overlays share exactly one 32-surface baseline quota. HUD {extraHud.ErrorCode}, overlay {extraOverlay.ErrorCode}.");
        foreach (var hud in ordinaryHuds) hud.Dispose();
        other!.Dispose();

        var fillers = new List<MirroredHudPresenter>();
        for (var index = 0; index < 6; index++)
        {
            var corner = (UiHudAnchor)((int)UiHudAnchor.TopLeft + index);
            var filler = host.Shared(BudgetHud("Filler " + index, 4096).WithHudAnchor(corner));
            That(filler.Succeeded, "The byte-budget setup can publish each bounded filler HUD. " + filler.ErrorMessage);
            fillers.Add((MirroredHudPresenter)filler.Value!);
        }
        var occupied = parked.Bytes + fillers.Sum(surface => surface.Bytes);
        var counted = host.Shared(BudgetHud("Byte budget", 2000, label));
        That(counted.Succeeded, "The byte-budget setup can measure a partial frame. " + counted.ErrorMessage);
        var probe = (MirroredHudPresenter)counted.Value!;
        var tail = 2000 + MultiplayerPresentation.MaximumBytes - occupied - probe.Bytes;
        probe.Dispose();
        var admittedExact = host.Shared(BudgetHud("Byte budget", tail, label));
        That(admittedExact.Succeeded, "The remaining byte budget fits the final frame. " + admittedExact.ErrorMessage);
        using var exact = (MirroredHudPresenter)admittedExact.Value!;
        var admitted = exact.Content;
        var grown = new UiText("Notice!");
        That(exact.Bytes + occupied == MultiplayerPresentation.MaximumBytes && parked.Payload![1] == 3 && parked.Payload[^1] == 1 &&
            rig.Run(() => guest.Ui.Surfaces.ContainsKey("Byte budget")), "The actual nested kind and dismissal byte consume the shared 28 KiB budget.");
        That(exact.SetContent(grown).ErrorCode == ModErrorCode.RateLimited &&
            ReferenceEquals(exact.Content, admitted), "Exceeding the common HUD and overlay quota by one byte preserves the previous baseline.");
        parked.Dispose();
        That(exact.SetContent(grown).Succeeded, "Retiring the overlay's exact record frees bytes immediately.");
        foreach (var filler in fillers) filler.Dispose();
    }

    private static IPlayer OverlayTarget(ControlRig rig, UiDeliveryEndpoint host, ControlPeer peer)
    {
        var id = peer.Control.SessionSnapshot.LocalParticipantId!.Value;
        That(rig.Run(() => (host.Players.Channel?.GetGeneration(id) ?? 0) != 0), "The overlay peer has an authenticated Players stream.");
        That(host.Directory!.TryGet(id, out var player), "The connected overlay recipient can be addressed.");
        return player!;
    }

    private static UiSurfaceRequest Overlay(string id, UiNode content) =>
        new(id, id, "Body", UiSurfaceKind.Overlay, 320, 240, content);

    private static UiSurfaceRequest SizedHud(string id, UiNode content) =>
        new(id, id, "Body", UiSurfaceKind.Hud, 320, 240, content);

    private static UiSurfaceRequest BudgetHud(string id, int bodyLength, UiNode? content = null) =>
        new UiSurfaceRequest(id, id, new string('x', bodyLength), UiSurfaceKind.Hud, 1920, 1440, content)
            .WithHudAnchor(UiHudAnchor.BottomRight);

    private static void SendOverlayReply(IInternalSystemChannel channel, ulong token, ulong revision, byte kind, string control = "")
    {
        var writer = new WorldWireWriter(128);
        writer.Begin(kind); writer.WriteString(Sandbox.Id);
        writer.WriteVarUInt(token); writer.WriteVarUInt(revision);
        writer.WriteString(control); writer.WriteString("");
        That(channel.Send(writer.Segment, InternalSystemTarget.Host).Succeeded, "The authenticated guest reply reaches its channel.");
    }
}
