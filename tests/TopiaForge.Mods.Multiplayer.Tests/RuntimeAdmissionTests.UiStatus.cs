using TopiaForge.ModManager;
using TopiaForge.Mods.Internal;
using TopiaForge.TestSupport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    private static void StatusUsesItsAdmittedContractOnBothRoutes()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(165, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var old = rig.AddPeer("Foundation receiver", Sandbox);
        old.UseRuntime(new UncheckedRuntimeHost(old.Runtime, InternalUiPresentationContract.FoundationVersion));
        var peer = rig.AddPeer("Status receiver", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false);
        guest.Ui.RecordControlsAtBoundary = true;
        rig.StartHosting(new InternalHostSettings("Mixed status", 6));
        rig.JoinAndWait(old);
        rig.JoinAndWait(peer);
        var oldId = old.Control.SessionSnapshot.LocalParticipantId!.Value;
        var newId = peer.Control.SessionSnapshot.LocalParticipantId!.Value;
        That(rig.Run(() => (host.Players.Channel?.GetGeneration(oldId) ?? 0) != 0 &&
            (host.Players.Channel?.GetGeneration(newId) ?? 0) != 0), "Both peers have their own authenticated player route.");
        That(host.Directory!.TryGet(oldId, out var oldTarget), "The v2 recipient is addressable.");
        That(host.Directory.TryGet(newId, out var target), "The v3 recipient is addressable.");

        var baseProgress = new UiProgress(0.25f, "Compatible", UiTone.Primary, "progress");
        using var older = (RemoteUiSurface)oldTarget!.Ui.CreateSurface(
            new UiSurfaceRequest("older", "Ordinary progress", "", content: baseProgress)).Value!;
        var original = new UiText("Ready", UiTextStyle.Label);
        using var shared = (MirroredHudPresenter)host.Shared(new UiSurfaceRequest("status-hud", "Status HUD", "", UiSurfaceKind.Hud,
            420, 180, original)).Value!;
        That(rig.Run(() => guest.Ui.Surfaces.ContainsKey("Status HUD")), "A v2 HUD is admitted with both recipients present.");
        var originalRemote = guest.Ui.Surfaces["Status HUD"];
        var originalEncoded = shared.Encoded;

        var timer = UiCountdown.WithFormat(host.Ui.ClockTime + 120d, "Wave", UiCountdownFormat.MinutesSeconds, new UiThresholds(10d, 5d), id: "wave");
        var progress = UiProgress.WithThresholds(0.75f, "Pressure", new UiThresholds(0.85d, 0.95d, UiThresholdDirection.AtOrAbove), id: "pressure");
        var stat = new UiStatBar("Integrity", 87, 100, new UiThresholds(35d, 15d), UiStatFormat.ValueOfMaximum, id: "integrity");
        var olderRevision = older.Revision;
        var olderEnvelope = older.Envelope;
        var olderDirty = older.Dirty;
        foreach (var status in new UiNode[] { timer, progress, stat })
        {
            var update = older.SetContent(new UiColumn(new UiCard(status)));
            var creation = oldTarget.Ui.CreateSurface(new UiSurfaceRequest("refused", "Refused", "", content: status));
            That(update.ErrorCode == ModErrorCode.Unavailable && update.ErrorMessage == UiPresentationTheme.RequiredControlsRuntime &&
                creation.ErrorCode == ModErrorCode.Unavailable && creation.ErrorMessage == UiPresentationTheme.RequiredControlsRuntime,
                "Every status tag refuses creation and a nested replacement for its v2 recipient.");
        }
        var broadcast = shared.SetContent(timer);
        That(broadcast.ErrorCode == ModErrorCode.Unavailable && broadcast.ErrorMessage == UiPresentationTheme.RequiredControlsRuntime,
            "A v2 peer blocks a shared status upgrade for the whole receiving cohort.");
        That(older.Dirty == olderDirty && ReferenceEquals(shared.PresentationContent, original) && originalEncoded.HasValue &&
            ReferenceEquals(shared.Encoded?.Bytes, originalEncoded.Value.Bytes) &&
            ReferenceEquals(shared.Encoded?.Envelope, originalEncoded.Value.Envelope),
            "Refused updates preserve the exact local presentation, pending sends and retained publication.");
        rig.Run(() => false, maximumMicros: 300_000);
        That(timer.ClockEpoch == null && older.Revision == olderRevision && ReferenceEquals(older.Envelope, olderEnvelope) &&
            ReferenceEquals(older.Content, baseProgress) && ReferenceEquals(guest.Ui.Surfaces["Status HUD"], originalRemote) &&
            originalRemote.ReceivedContent is UiText { Text: "Ready" },
            "Rejected version upgrades preserve the authored clock, revision, envelope and the v3 recipient's prior HUD.");

        var tree = new UiColumn(new UiCard(timer), progress, stat);
        using var allowed = (RemoteUiSurface)target!.Ui.CreateSurface(
            new UiSurfaceRequest("current", "Status panel", "", content: tree)).Value!;
        That(allowed.Token == older.Token + 1, "Refused attempts spend no targeted surface token.");
        That(rig.Run(() => guest.Ui.Surfaces.ContainsKey("Status panel")),
            "The same v3 tags reach the separately admitted v3 player while the v2 peer remains connected.");
        var before = guest.Ui.Surfaces["Status panel"];
        var received = (UiColumn)before.ReceivedContent!;
        That(before.Theme.Envelope?.OwnerPackageId == Sandbox.Id && before.Theme.Package?.Sha256 == peer.Control.UiPresentationPackageDigest(Sandbox.Id) &&
            ((UiCard)received.Children[0]).Child is UiCountdown receivedTimer && receivedTimer.EndsAt == timer.EndsAt &&
            receivedTimer.ClockEpoch == guest.Players.UiClockEpoch && receivedTimer.Format == UiCountdownFormat.MinutesSeconds &&
            received.Children[1] is UiProgress { Thresholds.Critical: 0.95d } &&
            received.Children[2] is UiStatBar { Value: 87, Maximum: 100, Format: UiStatFormat.ValueOfMaximum },
            "The authenticated recipient receives the original deadline, fractional thresholds and exact stat fields.");
        var bytes = AllocationProbe.Measure("settled targeted K2 status", () =>
        {
            if (!allowed.SetContent(tree).Succeeded) throw new InvalidOperationException("Reapplying settled status must succeed.");
        }, frames: 4096, warmups: 128);
        That(bytes == 0 && allowed.Revision == 1, "Reapplying settled targeted status performs no work and allocates zero bytes.");

        old.Control.Leave();
        That(rig.Run(() => !rig.Host.Control.SessionSnapshot.TryGetParticipant(oldId, out var leaving) || !leaving!.IsConnected),
            "The older peer voluntarily leaves the mixed cohort.");
        That(shared.SetContent(timer).Succeeded && rig.Run(() => originalRemote.ReceivedContent is UiCountdown { Format: UiCountdownFormat.MinutesSeconds }),
            "A shared status then reaches the compatible cohort on its retained route.");
        var lateOld = rig.AddPeer("Later foundation receiver", Sandbox);
        lateOld.UseRuntime(new UncheckedRuntimeHost(lateOld.Runtime, InternalUiPresentationContract.FoundationVersion));
        That(lateOld.Control.Join(lateOld.Target(), new InternalJoinSettings()).Succeeded &&
            rig.Run(() => lateOld.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended) &&
            lateOld.Control.GetStatus().ContentProblems.Contains(UiPresentationTheme.RequiredControlsRuntime) &&
            guest.Ui.Surfaces.Count == 2, "A retained shared status rejects a later v2 admission and preserves the compatible guest.");

        var epoch = timer.ClockEpoch;
        var identity = timer.ClockIdentity;
        That(rig.Host.Control.ReplaceSession().Succeeded, "The provider replaces the real retained-status session.");
        That(rig.Run(() => guest.Players.UiClockEpoch != epoch &&
            guest.Ui.Surfaces.TryGetValue("Status panel", out var restored) &&
            restored.ReceivedContent is UiColumn restoredPanel &&
            ((UiCard)restoredPanel.Children[0]).Child is UiText { Text: "Wave 0:00", Tone: UiTone.Neutral } &&
            guest.Ui.Surfaces.TryGetValue("Status HUD", out var restoredHud) &&
            restoredHud.ReceivedContent is UiText { Text: "Wave 0:00", Tone: UiTone.Neutral }),
            "Both authenticated routes display a formatted zero in its base tone after the clock changes.");
        That(ReferenceEquals(identity, timer.ClockIdentity) && timer.ClockEpoch == epoch && allowed.SetContent(tree).Succeeded &&
            allowed.SetContent(new UiColumn(timer)).ErrorCode == ModErrorCode.InvalidArgument,
            "Retained reapplication keeps the clock identity and new admission refuses the expired node.");
        Console.WriteLine("K2 status delivery passed for v2 refusal, v3 targeted/shared delivery and epoch replacement. Settled frame bytes allocated: " + bytes + ".");
    }
}
