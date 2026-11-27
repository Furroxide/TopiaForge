using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.TestSupport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    private static void StingerReceivesExactOwnershipAndLeavesSurfaceGenerationsAlone()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(171, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var peer = rig.AddPeer("Authenticated stinger receiver", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false);
        That(host.Ui.Themes.UseTheme(new UiThemeRef("first")).Succeeded, "The sender selects its admitted theme.");
        That(host.Ui.PresentationThemeScope!.TryDescribe((UiThemeRef?)null, out var theme) && theme != null,
            "The sender has a selected descriptor.");
        var catalogue = new AdmittedUiCatalogue(null, new Dictionary<string, UiThemeDescriptor> { ["first"] = theme! });
        peer.Runtime.Inventory.Packages.Single(package => package.Manifest.Id == Sandbox.Id).Manifest.AdmittedUi = catalogue;
        rig.StartHosting(new InternalHostSettings("Authenticated stinger envelopes", 4));
        rig.JoinAndWait(peer);
        var player = OverlayTarget(rig, host, peer);

        var shared = (MirroredHudPresenter)host.Shared("Shared before");
        var addressed = (RemoteUiSurface)player.Ui.CreateSurface(new UiSurfaceRequest("before", "Target before", "",
            content: new UiText("Before", UiTextStyle.Label))).Value!;
        That(rig.Run(() => guest.Ui.Surfaces.Count == 2), "The old shared and targeted surface generations are active.");
        var sharedGeneration = shared.Encoded!.Value.Envelope!.Generation;
        var targetGeneration = addressed.Envelope!.Generation;
        shared.Dispose();
        addressed.Dispose();
        That(rig.Run(() => guest.Ui.Surfaces.Count == 0), "Both surface generations have retired before the stinger.");

        var invalidStyle = new UiStingerRequest("Wrong style", style: new UiStyleRef("missing"));
        var preflights = host.Ui.StingerPreflights;
        That(host.Stinger(invalidStyle).ErrorCode == ModErrorCode.InvalidArgument &&
            player.Ui.ShowStinger(invalidStyle).ErrorCode == ModErrorCode.InvalidArgument &&
            host.Ui.StingerPreflights == preflights, "Both sender routes check the named style before enqueue or preflight.");
        That(player.Ui.ShowStinger(AStinger("Exact theme")).Succeeded && rig.Run(() => guest.Ui.RemoteStingers.Count == 1),
            "A real targeted event names the admitted package and descriptor.");
        var accepted = guest.Ui.RemoteStingers[0];
        That(accepted.HasDeclaredTheme && ReferenceEquals(accepted.Theme, theme) && ReferenceEquals(accepted.Catalogue, catalogue),
            "The receiver adopts only the exact catalogue and descriptor from its own admitted package.");

        var digest = new byte[32];
        var signed = rig.Host.Control.UiPresentationPackageDigest(Sandbox.Id);
        That(signed != null && UiPresentationTheme.TryHexDigest(signed, digest), "The harness can name the signed package.");
        That(accepted.Envelope is { } capture && capture.OwnerPackageId == Sandbox.Id &&
            capture.PackageDigest.Span.SequenceEqual(digest) && capture.ThemeId == theme!.Id &&
            capture.DescriptorDigest.Span.SequenceEqual(theme.Digest.Span),
            "The native observer receives the exact verified stinger event envelope.");
        var valid = UiPresentationTheme.EventEnvelope(Sandbox.Id, digest, theme);
        var badPackage = new UiWireEventEnvelope(Sandbox.Id, new byte[32], theme!.Id, theme.Digest.Span);
        var unknown = new UiWireEventEnvelope("io.example.unknown", digest, null, new byte[32]);
        var destination = InternalSystemTarget.Only(player.Id);
        var channel = host.Players.Channel!;

        void Reject(string audience, UiWireEventEnvelope envelope, UiStingerRequest request)
        {
            var before = guest.Players.Rejected;
            var frame = TestStingerFrame(audience, envelope, request);
            That(channel.Send(frame.Segment, destination).Succeeded && rig.Run(() => guest.Players.Rejected > before) &&
                guest.Ui.RemoteStingers.Count == 1, "An invalid event cannot create or update a receiver queue.");
        }

        Reject("wrong-player", valid, AStinger("Wrong audience"));
        Reject(player.Id.Value, badPackage, AStinger("Wrong package digest"));
        Reject(string.Empty, unknown, AStinger("Unadmitted owner"));
        Reject(player.Id.Value, valid, invalidStyle);

        var rejected = host.Players.Rejected;
        var guestEvent = TestStingerFrame("", valid, AStinger("A guest cannot publish"));
        That(guest.Players.Channel!.Send(guestEvent.Segment, InternalSystemTarget.Host).Succeeded &&
            rig.Run(() => host.Players.Rejected > rejected) && host.Ui.RemoteStingers.Count == 0,
            "An authenticated guest cannot use the host's announcement route.");

        var descriptorDigest = new byte[32];
        Array.Fill(descriptorDigest, (byte)0x65);
        var missingDescriptor = new UiWireEventEnvelope(Sandbox.Id, digest, theme.Id, descriptorDigest);
        var fallback = TestStingerFrame("", missingDescriptor,
            new UiStingerRequest("Default style", style: new UiStyleRef("origin-style")));
        That(channel.Send(fallback.Segment, destination).Succeeded && rig.Run(() => guest.Ui.RemoteStingers.Count == 2) &&
            guest.Ui.RemoteStingers[1].HasDeclaredTheme && guest.Ui.RemoteStingers[1].Theme == null &&
            ReferenceEquals(guest.Ui.RemoteStingers[1].Catalogue, catalogue),
            "An authenticated but unavailable descriptor falls back within the same package context.");

        using var afterShared = (MirroredHudPresenter)host.Shared("Shared after");
        using var afterTarget = (RemoteUiSurface)player.Ui.CreateSurface(new UiSurfaceRequest("after", "Target after", "",
            content: new UiText("After", UiTextStyle.Label))).Value!;
        That(afterShared.Encoded!.Value.Envelope!.Generation == sharedGeneration + 1 &&
            afterTarget.Envelope!.Generation == targetGeneration + 1 &&
            rig.Run(() => guest.Ui.Surfaces.Count == 2),
            "Valid, refused and fallback stingers leave both retained surface generations and admission ledgers untouched.");
    }

    private static void StingerUsesTheExistingPlayerUiEventBudget()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(172, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var peer = rig.AddPeer("Event budget recipient", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false);
        rig.StartHosting(new InternalHostSettings("Stinger event budget", 4));
        rig.JoinAndWait(peer);
        var id = OverlayTarget(rig, host, peer).Id;
        var scopes = new List<FakeModLifetime>();
        try
        {
            var eventCount = 0;
            for (var index = 0; index < 9; index++)
            {
                var scope = new FakeModLifetime();
                scopes.Add(scope);
                var ui = new ThemeProbe(scope, author: true);
                var context = host.Players.Bind(Sandbox.Id, true, new CapturedModLogger(), scope,
                    new FakeLocalPlayerService(scope), ui, new ModInputActivity());
                using var directory = new PlayerDirectory(() => rig.Host.Control.SessionSnapshot, () => 0, context);
                context.Directory = directory;
                That(directory.TryGet(id, out var target), "Every author scope addresses the same current recipient.");
                // Fractional lengths give the admission ledger a nonintegral expiry boundary.
                for (var count = 0; count < (index == 8 ? 1 : 4); count++)
                {
                    var result = target!.Ui.ShowStinger(AStinger("Stinger " + eventCount, 1.1d));
                    That(eventCount < 32 ? result.Succeeded : result.ErrorCode == ModErrorCode.RateLimited &&
                        result.ErrorMessage.Contains("presentation events are too frequent", StringComparison.Ordinal),
                        "Independent scopes share the existing 32-message outgoing event burst.");
                    eventCount++;
                }
            }
        }
        finally
        {
            foreach (var scope in scopes) scope.Dispose();
        }

        var now = rig.Network.Now;
        That(rig.Run(() => rig.Network.Now >= now + 2_000_000, frameMicros: 20_000), "The per-player burst refills.");
        That(host.Directory!.TryGet(id, out var player), "The original author scope still addresses the recipient.");
        for (var count = 0; count < 4; count++)
            That(player!.Ui.ShowStinger(AStinger("Timed stinger " + count, 1.1d)).Succeeded,
                "The original scope can fill all four positions with fractional durations.");
        That(player!.Ui.ShowStinger(AStinger("Still waiting", 1.1d)).ErrorCode == ModErrorCode.RateLimited,
            "A full queue refuses another entry before any duration expires.");
        now = rig.Network.Now;
        That(rig.Run(() => rig.Network.Now >= now + 4_500_000, frameMicros: 20_000) &&
            player!.Ui.ShowStinger(AStinger("After timed queue", 1.1d)).Succeeded,
            "Elapsed fractional durations retire the whole queue and free a new position.");
    }

    private static WorldWireWriter TestStingerFrame(string audience, UiWireEventEnvelope envelope, UiStingerRequest request, ulong scopeToken = 257)
    {
        var writer = new WorldWireWriter(2304);
        writer.Begin(UiStingerDelivery.EventKind); writer.WriteString(""); writer.WriteString(audience);
        UiWireCodec.WriteStinger(writer, envelope, scopeToken, UiStingerFontFit.KitOnly, request);
        return writer;
    }
}
