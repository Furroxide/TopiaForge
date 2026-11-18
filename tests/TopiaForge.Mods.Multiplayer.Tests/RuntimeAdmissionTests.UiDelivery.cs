using System.Text;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    private static void FoundationEnvelopeReachesBothAuthenticatedRoutes()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(159, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var peer = rig.AddPeer("UI receiver", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false);
        rig.StartHosting(new InternalHostSettings("Themed UI", 4));
        rig.JoinAndWait(peer);
        var targetId = peer.Session.Snapshot.LocalParticipantId!.Value;
        That(rig.Run(() => (host.Players.Channel?.GetGeneration(targetId) ?? 0) != 0), "The recipient has an authenticated Players stream.");
        That(host.Directory!.TryGet(targetId, out var target), "The host can address the joined player.");

        var earlyLegacy = rig.AddPeer("Legacy UI receiver", Sandbox);
        earlyLegacy.UseRuntime(new UncheckedRuntimeHost(earlyLegacy.Runtime));
        rig.JoinAndWait(earlyLegacy);
        var earlyId = earlyLegacy.Control.SessionSnapshot.LocalParticipantId!.Value;
        using (var hiddenLegacyHud = host.Shared("Hidden legacy HUD", UiTextStyle.Body))
        {
            hiddenLegacyHud.Hide();
            var refusedSelection = host.Ui.Themes.UseTheme(new UiThemeRef("first"));
            That(refusedSelection.ErrorCode == ModErrorCode.Unavailable &&
                refusedSelection.ErrorMessage == UiPresentationTheme.RequiredRuntime && host.Ui.Themes.SelectedTheme == null,
                "A hidden retained HUD prevents a scope-wide upgrade while a legacy peer is admitted.");
        }

        earlyLegacy.Control.Leave();
        That(rig.Run(() => !rig.Host.Control.SessionSnapshot.TryGetParticipant(earlyId, out var leaving) || !leaving!.IsConnected),
            "The legacy player leaves on its own before the scope upgrades.");
        That(host.Ui.Themes.UseTheme(new UiThemeRef("first")).Succeeded, "The author selects its installed theme.");

        var shared = host.Shared("Shared K1");
        var other = host.Shared("Second K1");
        var callbacks = 0;
        var contents = new UiColumn(new UiText("Target K1", UiTextStyle.Banner), new UiButton("ready", "Ready", () => callbacks++));
        using var targeted = target!.Ui.CreateSurface(new UiSurfaceRequest("targeted", "Targeted K1", "Ready", content: contents)).Value!;
        That(rig.Run(() => guest.Ui.Surfaces.Count == 3), "Shared HUDs and the targeted window reach a guest without a mod context.");
        var sharedReceipt = guest.Ui.Surfaces["Shared K1"];
        var otherReceipt = guest.Ui.Surfaces["Second K1"];
        var targetedReceipt = guest.Ui.Surfaces["Targeted K1"];
        That(sharedReceipt.Theme.Envelope!.Generation == otherReceipt.Theme.Envelope!.Generation,
            "Both shared surfaces use the same active presentation context for their author scope.");
        var oldShared = sharedReceipt.Theme.Envelope.Generation;
        var oldTargeted = targetedReceipt.Theme.Envelope!.Generation;
        foreach (var receipt in guest.Ui.Surfaces.Values)
        {
            That(receipt.Theme.Envelope!.OwnerPackageId == Sandbox.Id && receipt.Theme.Envelope.ThemeId == "first" &&
                receipt.Theme.Package?.Sha256 == peer.Control.UiPresentationPackageDigest(Sandbox.Id),
                "The process receiver accepts only the admitted exact local package identity.");
        }
        var valid = targetedReceipt.Theme.Envelope!;
        var otherArchive = new UiWireEnvelope(valid.OwnerPackageId, new byte[32], valid.Generation, valid.ThemeId, valid.DescriptorDigest.Span);
        That(!guest.Players.AuthenticateUiTheme(otherArchive, null, out _) &&
            !guest.Players.AuthenticateUiTheme(valid, "io.example.other", out _),
            "A different package digest or a different targeted owner cannot borrow the admitted theme identity.");

        That(targetedReceipt.Surface.ActivateButton("ready").Succeeded && rig.Run(() => callbacks == 1),
            "The envelope route returns the original targeted callback without running recipient gameplay code.");
        _ = targeted.SetContent(contents);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++) _ = targeted.SetContent(contents);
        var after = GC.GetAllocatedBytesForCurrentThread();
        That(after == before, "No-op retained writes consume zero managed bytes on the targeted K1 route.");

        That(host.Ui.Themes.UseTheme(new UiThemeRef("second")).Succeeded, "Changing the scope selection stages all retained routes.");
        That(rig.Run(() => guest.Ui.Surfaces.Values.All(surface => surface.Theme.Envelope?.ThemeId == "second")),
            "The same remote surfaces receive the new descriptor selection.");
        That(ReferenceEquals(sharedReceipt, guest.Ui.Surfaces["Shared K1"]) &&
            ReferenceEquals(targetedReceipt, guest.Ui.Surfaces["Targeted K1"]) &&
            sharedReceipt.Theme.Envelope!.Generation == oldShared && targetedReceipt.Theme.Envelope!.Generation == oldTargeted,
            "Theme replacement preserves both surface and presentation-context identity.");

        var legacy = rig.AddPeer("Old UI receiver", Sandbox);
        legacy.UseRuntime(new UncheckedRuntimeHost(legacy.Runtime));
        That(legacy.Control.Join(legacy.Target(), new InternalJoinSettings()).Succeeded &&
            rig.Run(() => legacy.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended) &&
            legacy.Control.GetStatus().ContentProblems.Contains(UiPresentationTheme.RequiredRuntime),
            "An actual retained K1 HUD prevents a legacy join without clearing the existing guest's UI.");
        That(guest.Ui.Surfaces.Count == 3, "The existing guest keeps all its surfaces after an incompatible join.");

        shared.Dispose(); other.Dispose();
        That(rig.Run(() => guest.Ui.Surfaces.Count == 1), "A complete baseline retires the last surface in the shared generation.");
        using var reopened = host.Shared("New K1");
        That(rig.Run(() => guest.Ui.Surfaces.ContainsKey("New K1")) &&
            guest.Ui.Surfaces["New K1"].Theme.Envelope!.Generation > oldShared,
            "The next shared presentation of the same author scope takes a fresh generation.");
        Console.WriteLine("K1 UI: signed-roster delivery, capability refusal, retained contexts and targeted callbacks passed.");
    }

    private sealed class UiDeliveryEndpoint : IDisposable
    {
        private readonly FakeModLifetime lifetime = new();
        private readonly RuntimeReplication? origin;
        internal readonly MultiplayerPlayers Players;
        internal readonly PlayerDirectory? Directory;
        internal readonly ThemeProbe Ui;

        internal UiDeliveryEndpoint(ControlPeer peer, Func<long> clock, bool author)
        {
            var runtime = peer.Composition;
            runtime.Disconnect();
            var replication = new MultiplayerReplication(runtime);
            var presence = MultiplayerPresence.Install(runtime, replication, new FakePresenceBackend { RecordTrails = false }, clock);
            Players = MultiplayerPlayers.Install(runtime, presence);
            replication.Players = Players;
            Ui = new ThemeProbe(lifetime, author);
            var audio = new FakeAudioService(lifetime);
            Players.Presentation = new MultiplayerPresentation(Players, () => new PresentationSink(Ui, audio));
            That(runtime.Connect(), "The provider attaches to the real Players runtime.");
            if (!author) return;
            Ui.PresentationThemeScope!.PrepareSelection = Players.PrepareThemeSelection;
            var profile = new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer);
            origin = replication.Bind(Sandbox.Id, profile, lifetime);
            var context = Players.Bind(Sandbox.Id, true, new CapturedModLogger(), lifetime,
                new FakeLocalPlayerService(lifetime), Ui, new ModInputActivity());
            Directory = new PlayerDirectory(() => peer.Session.Snapshot, () => 0, context);
            context.Directory = Directory;
        }

        internal IUiSurface Shared(string title, UiTextStyle style = UiTextStyle.Label) => Players.Presentation!.CreateHud(origin!, Ui,
            new UiSurfaceRequest(title, title, "Status", UiSurfaceKind.Hud, 320, 160, new UiText(title, style))).Value!;

        public void Dispose() { Directory?.Dispose(); lifetime.Dispose(); }
    }

    private sealed class ThemeProbe : IUiService, IUiClockReceiver, IUiPresentationThemeSource, IRemoteThemedUiReceiver
    {
        private readonly FakeUiService ui;
        internal readonly Dictionary<string, ThemeSurfaceProbe> Surfaces = new(StringComparer.Ordinal);

        internal ThemeProbe(FakeModLifetime lifetime, bool author)
        {
            ui = new FakeUiService(lifetime);
            if (!author) return;
            var declared = new Dictionary<string, UiThemeDescriptor>(StringComparer.Ordinal);
            foreach (var id in new[] { "first", "second" })
                declared.Add(id, UiThemePackParser.Parse(Encoding.UTF8.GetBytes("{\"schema\":1,\"id\":\"" + id + "\"}")).Value!);
            PresentationThemeScope = new UiThemeScope(lifetime, new AdmittedUiCatalogue(null, declared));
        }

        public IUiThemeService Themes => PresentationThemeScope!;
        public UiThemeScope? PresentationThemeScope { get; }
        public UiAccessibilityPreferences Accessibility => ui.Accessibility;
        public double ClockTime => ui.ClockTime;
        public void FollowClock(Func<double> clock, Func<string> epoch) => ((IUiClockReceiver)ui).FollowClock(clock, epoch);
        public OperationResult<bool> DescribePresentationTheme(UiSurfaceRequest request, out UiThemeDescriptor? theme) =>
            PresentationThemeScope!.TryDescribe(request.Theme, out theme) ? UiPresentationTheme.Allowed : UiPresentationTheme.UnknownTheme;
        public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences) => ui.ApplyAccessibility(preferences);
        public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) => ui.CreateSurface(request);
        public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) => ui.ShowModal(request, completed);
        public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral) => ui.ShowToast(message, tone);

        public OperationResult<IUiSurface> RemoteSurface(UiSurfaceRequest request, UiRemoteThemeSelection theme)
        {
            var created = ui.CreateSurface(request);
            if (!created.TryGetValue(out var surface)) return created;
            var wrapper = new ThemeSurfaceProbe(this, (FakeUiSurface)surface, theme);
            Surfaces.Add(request.Title, wrapper);
            return OperationResult<IUiSurface>.Success(wrapper);
        }
    }

    private sealed class ThemeSurfaceProbe : IUiSurface, IRemoteThemedSurface
    {
        private readonly ThemeProbe owner;
        internal readonly FakeUiSurface Surface;
        internal UiRemoteThemeSelection Theme;

        internal ThemeSurfaceProbe(ThemeProbe owner, FakeUiSurface surface, UiRemoteThemeSelection theme)
        { this.owner = owner; Surface = surface; Theme = theme; }
        public string Id => Surface.Id;
        public bool IsVisible => Surface.IsVisible;
        public void Show() => Surface.Show();
        public void Hide() => Surface.Hide();
        public void SetBody(string value) => Surface.SetBody(value);
        public OperationResult<bool> SetContent(UiNode content) => Surface.SetContent(content);
        public OperationResult<bool> SetRemotePresentation(UiNode? content, UiRemoteThemeSelection theme)
        {
            var result = Surface.SetContent(content ?? new UiColumn());
            if (result.Succeeded) Theme = theme;
            return result;
        }

        public void Dispose() { owner.Surfaces.Remove(Surface.Request.Title); Surface.Dispose(); }
    }
}
