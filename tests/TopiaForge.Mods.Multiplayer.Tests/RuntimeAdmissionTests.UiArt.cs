using System.Text;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.TestSupport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    private static void ArtUsesItsOwnPackOnBothRoutes()
    {
        var catalogue = ArtCatalogue(out var imagePack, out var selected);
        using var rig = new ControlRig(162, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true, catalogue);
        var peer = rig.AddPeer("Art receiver", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false, catalogue);
        var target = JoinTargetedUi(rig, host, peer);
        That(host.Ui.Themes.UseTheme(new UiThemeRef(selected.Id)).Succeeded, "The author selects chrome from a different pack than the image.");

        var image = new UiImage(new UiImageRef(imagePack.Id, "card"), "Supply card", height: 40, id: "supply");
        var badge = new UiBadge("Secured", UiPaintRole.Success, style: new UiStyleRef("status"));
        var heading = new UiText("Supply status");
        var sharedTree = new UiCard(new UiColumn(image, badge), header: heading, selected: true);
        using var shared = (MirroredHudPresenter)host.Shared(new UiSurfaceRequest(
            "art-shared", "Shared artwork", "", UiSurfaceKind.Hud, 320, 180, sharedTree)).Value!;
        var presses = 0;
        var control = new UiButton("accept", "Accept", () => presses++);
        var timer = new UiCountdown(90, "Remaining", id: "timer");
        var authored = new UiSplitPane(new UiScroll(new UiCard(new UiPadding(image, default), header: heading)),
            new UiColumn(badge, control, timer));
        using var targeted = (RemoteUiSurface)target.Ui.CreateSurface(new UiSurfaceRequest(
            "art-target", "Targeted artwork", "Original body", width: 480, height: 400, content: authored)).Value!;
        var canvases = guest.Ui.Art!.Surfaces;
        That(rig.Run(() => canvases.Count == 2), "Shared and targeted receipts reach the guest without an author context.");
        var seenShared = canvases["Shared artwork"];
        var seenTarget = canvases["Targeted artwork"];
        That(ReferenceEquals(shared.Content, sharedTree) && ReferenceEquals(targeted.Content, authored) && !image.Width.HasValue &&
            ReferenceEquals(((UiCard)shared.PresentationContent!).Header, heading) &&
            ReferenceEquals(((UiSplitPane)targeted.PresentationContent!).Secondary, authored.Secondary) &&
            ReferenceEquals(FindNode<UiCountdown>(targeted.PresentationContent, "timer"), timer),
            "Projection preserves authored trees, wrapper choices and the existing countdown leaf.");
        That(new[] { seenShared, seenTarget }.All(surface =>
        {
            var delivered = FindNode<UiImage>(surface.Content, "supply");
            return delivered.Width == 96 && delivered.Height == 40 &&
                delivered.PackDescriptorDigest.Span.SequenceEqual(imagePack.Digest.Span) &&
                surface.Theme.Envelope!.DescriptorDigest.Span.SequenceEqual(selected.Digest.Span);
        }), "Both publishers use the verified null axis and the image pack's own digest independently of surface chrome.");
        FindNode<UiButton>(seenTarget.Content, "accept").Activated();
        That(rig.Run(() => presses == 1), "Projection preserves targeted callback identity.");

        var targetedBytes = AllocationProbe.Measure("targeted art", () => _ = targeted.SetContent(authored), 10_000, warmups: 10);
        var sharedBytes = AllocationProbe.Measure("shared art", () => _ = shared.SetContent(sharedTree), 10_000, warmups: 10);
        That(targetedBytes == 0 && sharedBytes == 0, "Settled image trees allocate zero bytes on unchanged shared and targeted writes.");

        var knownInvalid = new UiImage(image.Image, image.SemanticLabel, tint: UiTone.Success, id: image.Id);
        var undeclared = new UiImage(new UiImageRef(imagePack.Id, "missing"), "Undeclared");
        var large = new UiImage(new UiImageRef(imagePack.Id, "wide"), "Wide");
        That(shared.SetContent(knownInvalid).ErrorCode == ModErrorCode.InvalidArgument &&
            targeted.SetContent(knownInvalid).ErrorCode == ModErrorCode.InvalidArgument &&
            targeted.SetContent(undeclared).ErrorCode == ModErrorCode.NotFound &&
            targeted.SetContent(large).ErrorCode == ModErrorCode.InvalidArgument,
            "Both hosts reject tint on ordinary art, an undeclared asset, and an omitted oversized axis without publishing.");
        var style = new UiThemeRef("other");
        That(host.Ui.Themes.UseTheme(style).ErrorCode == ModErrorCode.InvalidArgument &&
            host.Ui.Themes.SelectedTheme!.Id == selected.Id && ReferenceEquals(targeted.Content, authored),
            "Changing the effective pack rejects a role-changing named style before touching any shared or targeted state.");

        RejectInvalidArtBaseline(rig, host, guest, target, shared, selected, seenShared);
        shared.Dispose();
        That(rig.Run(() => !canvases.ContainsKey("Shared artwork")), "The shared surface can retire before checking the other route.");
        That(host.Ui.Themes.UseTheme(style).ErrorCode == ModErrorCode.InvalidArgument &&
            host.Ui.Themes.SelectedTheme!.Id == selected.Id,
            "Prospective targeted selection rejects a style targeting a different semantic role even without a shared surface.");

        var validEnvelope = targeted.Envelope!;
        var oldRevision = targeted.Revision;
        var oldContent = seenTarget.Content;
        var rejected = guest.Players.Rejected;
        var invalidEnvelope = UiPresentationTheme.Envelope(Sandbox.Id, validEnvelope.PackageDigest.ToArray(), validEnvelope.Generation + 2, selected);
        SendTargetedRevision(host, target.Id, targeted.Token, oldRevision + 1, targeted.Request,
            knownInvalid.WithWireMetadata(96, 40, imagePack.Digest.Span), invalidEnvelope);
        That(rig.Run(() => guest.Players.Rejected > rejected) && ReferenceEquals(seenTarget.Content, oldContent) &&
            seenTarget.Body == "Original body" && seenTarget.Theme.Envelope?.ThemeId == validEnvelope.ThemeId,
            "An exact-descriptor image with an invalid tint does not change the accepted canvas.");

        var lowerEnvelope = UiPresentationTheme.Envelope(Sandbox.Id, validEnvelope.PackageDigest.ToArray(), validEnvelope.Generation + 1, selected);
        var unmatched = knownInvalid.WithWireMetadata(111, 37, new byte[32]);
        SendTargetedRevision(host, target.Id, targeted.Token, oldRevision + 1, targeted.Request, unmatched, lowerEnvelope);
        That(rig.Run(() => seenTarget.Theme.Envelope?.Generation == lowerEnvelope.Generation) &&
            seenTarget.Content is UiImage { Width: 111, Height: 37 } retained &&
            retained.PackDescriptorDigest.Span.SequenceEqual(unmatched.PackDescriptorDigest.Span),
            "Invalid admission keeps the watermark. A later unmatched descriptor retains the transmitted image box for fallback.");

        var absent = new UiImage(new UiImageRef("absent-pack", "card"), "Missing art")
            .WithWireMetadata(73, 19, imagePack.Digest.Span);
        SendTargetedRevision(host, target.Id, targeted.Token, oldRevision + 2, targeted.Request, absent, lowerEnvelope);
        That(rig.Run(() => seenTarget.Content is UiImage { Width: 73, Height: 19 } missing && missing.Image.PackId == "absent-pack") &&
            UiPresentationTheme.ViewerAdmits(absent, new UiRemoteThemeSelection(lowerEnvelope, null)),
            "An absent image pack or absent exact package leaves the transmitted dimensions available for fallback.");
    }

    private static void ArtUsesTheCompleteRetainedFrameBudget()
    {
        var catalogue = ArtCatalogue(out var imagePack, out var chrome);
        using var rig = new ControlRig(163, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true, catalogue);
        var peer = rig.AddPeer("Exact art receiver", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false, catalogue);
        var target = JoinTargetedUi(rig, host, peer);
        var scope = host.Ui.PresentationThemeScope!;
        That(scope.UseTheme(new UiThemeRef(chrome.Id)).Succeeded, "The host can select the small descriptor.");
        That(host.Players.TryOutgoingUiDigest(Sandbox.Id, requireSignedRoster: true, out var packageDigest),
            "The session admits the publishing package.");
        That(UiPresentationTheme.TryNextTargetGeneration(0, out var generation), "The first target generation is available.");
        var envelope = UiPresentationTheme.Envelope(Sandbox.Id, packageDigest!, generation, chrome);
        var request = new UiSurfaceRequest("exact-art", "Exact artwork", "", width: 1280, height: 720);
        var children = new UiNode[9];
        children[0] = new UiImage(new UiImageRef(imagePack.Id, "card"), "Card", id: "exact-image");
        for (var index = 1; index <= 7; index++) children[index] = new UiText(new string('a', 4096));
        children[8] = new UiText(new string('a', 256));
        That(UiPresentationImages.Prepare(new UiColumn(children), scope, chrome, out var projected).Succeeded, "The projected tree is measurable.");
        var measured = UiWireCodec.MeasureTargetedSurface(Sandbox.Id, target.Id.Value, 1, 1, request, "", true, projected,
            host.Players.UiClockEpoch, envelope: envelope);
        children[8] = new UiText(new string('a', 256 + MultiplayerPlayers.MaximumMessageBytes - measured));
        var exact = new UiColumn(children);
        var created = target.Ui.CreateSurface(new UiSurfaceRequest(request.Id, request.Title, request.Body, width: request.Width,
            height: request.Height, content: exact));
        That(created.Succeeded && rig.Run(() => guest.Ui.Art!.Surfaces.ContainsKey(request.Title)),
            "A projection that exactly fits the fully framed targeted limit publishes.");
        using var surface = (RemoteUiSurface)created.Value!;
        var originalProjection = surface.PresentationContent;
        var refused = false;
        try { surface.SetBody("!"); }
        catch (InvalidOperationException) { refused = true; }
        That(refused && surface.Body.Length == 0 && !surface.Dirty,
            "One added byte in a retained body fails before marking the window dirty.");
        var size = scope.UseTheme(new UiThemeRef("panel-long"));
        That(size.ErrorCode == ModErrorCode.RateLimited && scope.SelectedTheme!.Id == chrome.Id &&
            ReferenceEquals(originalProjection, surface.PresentationContent) && surface.Revision == 1 &&
            !surface.Dirty && surface.SetContent(exact).Succeeded,
            "Prospective selection measures the retained image and all frame fields before changing theme or revision.");
        Console.WriteLine("K1 art wire: both authenticated routes, atomic validation, retained fallback dimensions and exact framing passed.");
    }

    private static void RejectInvalidArtBaseline(ControlRig rig, UiDeliveryEndpoint host, UiDeliveryEndpoint guest, IPlayer target,
        MirroredHudPresenter shared, UiThemeDescriptor selected, ArtCanvas observed)
    {
        var envelope = shared.Encoded!.Value.Envelope!;
        var newTheme = UiPresentationTheme.Envelope(Sandbox.Id, envelope.PackageDigest.ToArray(), envelope.Generation + 1, selected);
        var bad = new UiPips(5, 3, "Charge", role: UiPaintRole.Danger, style: new UiStyleRef("charge"));
        var oldContent = observed.Content;
        var rejected = guest.Players.Rejected;
        var first = new WorldWireWriter(MultiplayerPresentation.MaximumBytes);
        WriteHud(first, 1, shared.Request.Title, new UiText("Partial change"), newTheme);
        var second = new WorldWireWriter(MultiplayerPresentation.MaximumBytes);
        WriteHud(second, 2, "Invalid status", bad, newTheme);
        var baseline = new WorldWireWriter(MultiplayerPlayers.MaximumMessageBytes);
        baseline.Begin(30);
        baseline.WriteString("");
        baseline.WriteVarUInt(10_000);
        baseline.WriteVarUInt(2);
        baseline.WriteBytes(first.Segment.AsSpan());
        baseline.WriteBytes(second.Segment.AsSpan());
        baseline.WriteVarUInt(0);
        That(host.Players.Channel!.Send(baseline.Segment, InternalSystemTarget.Only(target.Id)).Succeeded &&
            rig.Run(() => guest.Players.Rejected > rejected) && ReferenceEquals(oldContent, observed.Content) &&
            !guest.Ui.Art!.Surfaces.ContainsKey("Invalid status"),
            "A shared baseline with a wrong status role rejects every update before replacing any admitted HUD.");

        void WriteHud(WorldWireWriter writer, ulong token, string title, UiNode content, UiWireEnvelope presentation)
        {
            writer.Begin(0);
            writer.WriteVarUInt(token);
            writer.WriteString(title);
            writer.WriteString("Updated body");
            writer.WriteSingle(320);
            writer.WriteSingle(180);
            writer.WriteByte((byte)UiHudAnchor.BottomLeft);
            writer.WriteByte((byte)UiHudVisibility.Always);
            writer.WriteByte(1);
            UiWireCodec.Prepare(writer, content, host.Players.UiClockEpoch, retained: false, presentation);
        }
    }

    private static T FindNode<T>(UiNode? content, string id) where T : UiNode =>
        UiLayoutTree.TryFind(content, id, out var node, out _) && node is T found ? found
            : throw new InvalidOperationException("Expected a captured node at the declared id.");

    private static AdmittedUiCatalogue ArtCatalogue(out UiThemeDescriptor images, out UiThemeDescriptor selected)
    {
        images = Parse("""
            {"schema":1,"id":"markings","images":[
              {"id":"card","bundlePath":"ui/markings.bundle","assetName":"card","licensePath":"licenses/art.txt","pixelWidth":96,"pixelHeight":72,"isMask":false},
              {"id":"wide","bundlePath":"ui/markings.bundle","assetName":"wide","licensePath":"licenses/art.txt","pixelWidth":1500,"pixelHeight":72,"isMask":false}
            ]}
            """);
        selected = Panel("panel", "success");
        var incompatible = Panel("other", "danger");
        var larger = Panel("panel-long", "success");
        return new AdmittedUiCatalogue(null, new Dictionary<string, UiThemeDescriptor>
        {
            [images.Id] = images, [selected.Id] = selected, [incompatible.Id] = incompatible, [larger.Id] = larger,
        });

        static UiThemeDescriptor Panel(string id, string role) => Parse($$"""
            {"schema":1,"id":"{{id}}","styles":[
              {"id":"status","target":"badge","fill":"{{role}}","radius":"none"},
              {"id":"charge","target":"pips","fill":"{{role}}","radius":"none"}
            ]}
            """);

        static UiThemeDescriptor Parse(string json)
        {
            var result = UiThemePackParser.Parse(Encoding.UTF8.GetBytes(json), packaged: true, fileHash: _ => new byte[32]);
            return result.Value ?? throw new InvalidOperationException(result.ErrorMessage);
        }
    }

    // Records the real receiver handoff. The visual tests exercise layout and fallback painting separately.
    private sealed class ArtCanvasRecorder(FakeModLifetime lifetime)
    {
        internal readonly Dictionary<string, ArtCanvas> Surfaces = new(StringComparer.Ordinal);

        internal OperationResult<IUiSurface> Create(UiSurfaceRequest request, UiRemoteThemeSelection theme)
        {
            var canvas = new ArtCanvas(request, theme, () => Surfaces.Remove(request.Title));
            Surfaces.Add(request.Title, canvas);
            lifetime.Track(canvas);
            return OperationResult<IUiSurface>.Success(canvas);
        }
    }

    private sealed class ArtCanvas(UiSurfaceRequest request, UiRemoteThemeSelection theme, Action release) : IUiSurface, IRemoteThemedSurface
    {
        private bool disposed;
        public string Id => request.Id;
        public bool IsVisible { get; private set; } = true;
        internal string Body { get; private set; } = request.Body;
        internal UiNode? Content { get; private set; } = request.Content;
        internal UiRemoteThemeSelection Theme { get; private set; } = theme;
        public void Show() => IsVisible = !disposed;
        public void Hide() => IsVisible = false;
        public void SetBody(string body) => Body = body;
        public OperationResult<bool> SetContent(UiNode content) => SetRemotePresentation(content, Theme);
        public OperationResult<bool> SetRemotePresentation(UiNode? content, UiRemoteThemeSelection selected)
        {
            Content = content;
            Theme = selected;
            return UiPresentationTheme.Allowed;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            IsVisible = false;
            release();
        }
    }
}
