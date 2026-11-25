using TopiaForge.ModManager;
using TopiaForge.Mods.Internal;
using TopiaForge.TestSupport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    private static void ActionsUseTheirAdmittedRoutes()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(166, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var old = rig.AddPeer("Foundation receiver", Sandbox);
        old.UseRuntime(new UncheckedRuntimeHost(old.Runtime, InternalUiPresentationContract.FoundationVersion));
        var peer = rig.AddPeer("Action receiver", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false);
        guest.Ui.RecordControlsAtBoundary = true;
        rig.StartHosting(new InternalHostSettings("Mixed action bar", 6));
        rig.JoinAndWait(old);
        rig.JoinAndWait(peer);
        var oldId = old.Control.SessionSnapshot.LocalParticipantId!.Value;
        var newId = peer.Control.SessionSnapshot.LocalParticipantId!.Value;
        That(rig.Run(() => (host.Players.Channel?.GetGeneration(oldId) ?? 0) != 0 &&
            (host.Players.Channel?.GetGeneration(newId) ?? 0) != 0) &&
            host.Directory!.TryGet(oldId, out _) && host.Directory.TryGet(newId, out _),
            "Both recipients have authenticated action presentation routes.");
        host.Directory!.TryGet(oldId, out var foundationTarget);
        host.Directory.TryGet(newId, out var actionTarget);

        var action = new InputActionDefinition("confirm", "Confirm", new[] { InputBinding.Key("Q") });
        host.Ui.RegisterActionFixture(action);
        guest.Ui.InputDefinitions.Add("other-package", new InputActionDefinition("other-package", "Other", new[] { InputBinding.Key("A") }));
        var deadline = new UiCooldown(host.Players.UiClockTime + 120d, 10);
        var glyph = new UiKeyGlyph("confirm", "key");
        var slot = new UiActionSlot("confirm", "Confirm", cooldown: deadline, charges: new UiCharges(2, 3, deadline), id: "slot");
        var pips = UiPips.WithRefill(4, 3, "Cells", deadline, id: "pips");
        var bar = new UiColumn(glyph, slot, pips);
        var compatible = new UiText("Ready", UiTextStyle.Label);
        using var older = (RemoteUiSurface)foundationTarget!.Ui.CreateSurface(
            new UiSurfaceRequest("older", "Foundation HUD", "", content: compatible)).Value!;
        using var shared = (MirroredHudPresenter)host.Shared(
            new UiSurfaceRequest("shared-action", "Shared action", "", UiSurfaceKind.Hud, 420, 340, compatible)).Value!;
        That(rig.Run(() => guest.Ui.Surfaces.ContainsKey("Shared action")), "The compatible HUD is already retained.");
        var sharedReceipt = guest.Ui.Surfaces["Shared action"];
        var original = shared.Encoded;
        var revision = older.Revision;
        var envelope = older.Envelope;
        foreach (var candidate in new UiNode[] { glyph, slot, pips })
        {
            var refused = older.SetContent(new UiColumn(candidate));
            That(refused.ErrorCode == ModErrorCode.Unavailable && refused.ErrorMessage == UiPresentationTheme.RequiredControlsRuntime &&
                foundationTarget.Ui.CreateSurface(new UiSurfaceRequest("refused", "Refused", "", content: candidate)).ErrorCode == ModErrorCode.Unavailable,
                "Each action tag requires an authenticated v3 recipient for create and update.");
        }
        That(shared.SetContent(bar).ErrorCode == ModErrorCode.Unavailable &&
            ReferenceEquals(shared.Encoded?.Bytes, original?.Bytes) && ReferenceEquals(shared.Encoded?.Envelope, original?.Envelope) &&
            sharedReceipt.Theme.Envelope?.Generation == original?.Envelope?.Generation &&
            ReferenceEquals(shared.PresentationContent, compatible) && ReferenceEquals(older.Content, compatible) &&
            older.Revision == revision && ReferenceEquals(older.Envelope, envelope) && deadline.ClockIdentity == null,
            "One v2 guest blocks a mirrored v3 candidate without spending the existing frame, generation, revision or clock.");

        var unknown = actionTarget!.Ui.CreateSurface(new UiSurfaceRequest("missing", "Unknown", "",
            content: new UiKeyGlyph("other-package", "bad-key")));
        That(unknown.ErrorCode == ModErrorCode.InvalidArgument && unknown.ErrorMessage.Contains("bad-key", StringComparison.Ordinal),
            "Destination registrations cannot supply missing defaults to the originating package.");
        using var sent = (RemoteUiSurface)actionTarget.Ui.CreateSurface(
            new UiSurfaceRequest("targeted-action", "Targeted action", "", content: bar)).Value!;
        That(sent.Token == older.Token + 1 && rig.Run(() => guest.Ui.Surfaces.ContainsKey("Targeted action")),
            "A v3 target receives the authored bar in the mixed cohort, and refused requests spend no token.");
        var targetedReceipt = guest.Ui.Surfaces["Targeted action"];
        That(targetedReceipt.Theme.Envelope?.OwnerPackageId == Sandbox.Id &&
            targetedReceipt.Theme.Package?.Sha256 == peer.Control.UiPresentationPackageDigest(Sandbox.Id) &&
            Delivered(targetedReceipt.ReceivedContent, guest.Players.UiClockEpoch) && deadline.ClockEpoch == host.Players.UiClockEpoch,
            "Both action leaves receive the signed owner's exact defaults and both projected deadline types use the admitted epoch.");
        var inactiveBytes = AllocationProbe.Measure("settled targeted K2 actions", () =>
        {
            if (!sent.SetContent(bar).Succeeded) throw new InvalidOperationException("Reapplying retained K2 actions must succeed.");
        }, frames: 4096, warmups: 128);
        That(inactiveBytes == 0 && sent.Revision == 1, "Reapplying an unchanged action bar allocates zero bytes and keeps its revision.");

        old.Control.Leave();
        That(rig.Run(() => !rig.Host.Control.SessionSnapshot.TryGetParticipant(oldId, out var leaving) || !leaving!.IsConnected),
            "The v2 guest leaves the mixed cohort.");
        var sharedUpgrade = shared.SetContent(bar);
        That(sharedUpgrade.Succeeded && rig.Run(() => Delivered(sharedReceipt.ReceivedContent, guest.Players.UiClockEpoch)) &&
            sharedReceipt.Theme.Envelope?.Generation == original?.Envelope?.Generation &&
            ReferenceEquals(sharedReceipt, guest.Ui.Surfaces["Shared action"]),
            "The shared v3 upgrade delivers the exact owner hints on the existing presentation lease. " + sharedUpgrade.ErrorMessage);
        using var newHud = host.Shared(new UiSurfaceRequest("direct-action", "New action HUD", "", UiSurfaceKind.Hud, 420, 340, bar)).Value!;
        That(newHud != null && rig.Run(() => guest.Ui.Surfaces.TryGetValue("New action HUD", out var direct) &&
            Delivered(direct.ReceivedContent, guest.Players.UiClockEpoch)),
            "A new mirrored action HUD projects the same owned defaults before its first publication.");
        var later = rig.AddPeer("Later foundation receiver", Sandbox);
        later.UseRuntime(new UncheckedRuntimeHost(later.Runtime, InternalUiPresentationContract.FoundationVersion));
        That(later.Control.Join(later.Target(), new InternalJoinSettings()).Succeeded &&
            rig.Run(() => later.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended) &&
            later.Control.GetStatus().ContentProblems.Contains(UiPresentationTheme.RequiredControlsRuntime),
            "The retained action HUD blocks an incompatible late join.");
        var oldEpoch = deadline.ClockEpoch;
        var oldIdentity = deadline.ClockIdentity;
        That(rig.Host.Control.ReplaceSession().Succeeded, "The provider replaces the real action presentation session.");
        That(rig.Run(() => guest.Players.UiClockEpoch != oldEpoch &&
            guest.Ui.Surfaces.TryGetValue("Targeted action", out var restoredTarget) && Arrived(restoredTarget.ReceivedContent) &&
            guest.Ui.Surfaces.TryGetValue("Shared action", out var restoredShared) && Arrived(restoredShared.ReceivedContent)),
            "The new authenticated streams receive completed charge and pip surrogates.");
        That(ReferenceEquals(deadline.ClockIdentity, oldIdentity) && deadline.ClockEpoch == oldEpoch &&
            sent.SetContent(bar).Succeeded && sent.SetContent(new UiColumn(glyph, slot, pips)).ErrorCode == ModErrorCode.InvalidArgument,
            "Reapplying the retained tree keeps its old identity, while a newly authored tree cannot reuse the ended clock.");
        Console.WriteLine("K2 action delivery passed for v2 refusal, v3 targeted/shared delivery, lease retention and clock replacement.");
    }

    private static bool Delivered(UiNode? content, string epoch) =>
        content is UiColumn { Children.Count: 3 } column &&
        column.Children[0] is UiKeyGlyph { WireDefaults.Count: 1 } glyph && glyph.WireDefaults[0].Control == "Q" &&
        column.Children[1] is UiActionSlot { WireDefaults.Count: 1 } slot && slot.WireDefaults[0].Control == "Q" &&
        slot.Cooldown?.ClockEpoch == epoch && slot.Charges?.Refill?.ClockEpoch == epoch &&
        column.Children[2] is UiPips pips && pips.Refill?.ClockEpoch == epoch;

    private static bool Arrived(UiNode? content) => content is UiColumn { Children.Count: 3 } column &&
        column.Children[1] is UiActionSlot { Cooldown: null, Charges.Available: 3, Charges.Refill: null } &&
        column.Children[2] is UiPips { Filled: 4, Refill: null, Fraction: 0f };
}
