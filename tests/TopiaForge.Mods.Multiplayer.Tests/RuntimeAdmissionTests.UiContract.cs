extern alias provider;

using TopiaForge.ModManager;
using TopiaForge.Mods.Internal;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using ContentOfferCodec = provider::TopiaForge.Multiplayer.ContentOfferCodec;
using RuntimeContent = provider::TopiaForge.Multiplayer.RuntimeContent;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    internal static void UiCapabilityNegotiatesAndBlocksLegacyOnlyWhenRequired()
    {
        using var rig = new ControlRig(158, Sandbox);
        var host = rig.Host.Runtime;
        host.ValidateSharedUiHosting = () => OperationResult<bool>.Failure(ModErrorCode.Unavailable, UiPresentationTheme.RequiredRuntime);
        var deferred = rig.Host.Control.StartHosting(new InternalHostSettings("Deferred UI", 6));
        That(!deferred.Succeeded && rig.Host.Control.Role == InternalSessionRole.Solo && rig.Host.Factory.Networks.Count == 0,
            "The existing solo session stays intact when retained UI prevents promotion.");
        var ready = true;
        host.ValidateSharedUiHosting = () => ready ? UiPresentationTheme.Allowed : UiPresentationTheme.UnsupportedPeer;
        That(rig.Host.Control.StartHosting(new InternalHostSettings("Preparing UI", 6)).Succeeded, "The host initially qualifies for preparation.");
        ready = false;
        That(rig.Run(() => rig.Host.Control.GetStatus().HostPhase == InternalHostPhase.Failed) &&
            rig.Host.Control.Role == InternalSessionRole.Solo && rig.Host.Factory.Networks.Count == 0,
            "Retained UI is checked again after identity preparation, before the endpoint opens.");
        host.ValidateSharedUiHosting = null;
        rig.StartHosting(new InternalHostSettings("UI compatibility", 6));

        var legacy = rig.AddPeer("legacy", Sandbox);
        legacy.UseRuntime(new UncheckedRuntimeHost(legacy.Runtime));
        rig.JoinAndWait(legacy);
        var legacyId = legacy.Control.SessionSnapshot.LocalParticipantId!.Value;
        That(rig.Host.Control.UiContractVersionOf(legacyId) == InternalUiPresentationContract.LegacyVersion,
            "A legacy offer admits the peer to an ordinary session without assigning a capability it never advertised.");
        That(rig.Host.Control.UiPresentationPackageDigest(Sandbox.Id) == legacy.Control.UiPresentationPackageDigest(Sandbox.Id),
            "Both runtime roles name the signed session package digest.");

        var foundation = rig.AddPeer("foundation", Sandbox);
        rig.JoinAndWait(foundation);
        var foundationId = foundation.Control.SessionSnapshot.LocalParticipantId!.Value;
        That(rig.Host.Control.UiContractVersionOf(foundationId) == InternalUiPresentationContract.FoundationVersion,
            "The exact authenticated offer capability survives admission and is keyed to the receiver.");

        host.RequiresFoundationUi = () => true;
        var refused = rig.AddPeer("old receiver", Sandbox);
        refused.UseRuntime(new UncheckedRuntimeHost(refused.Runtime));
        That(refused.Control.Join(refused.Target(), new InternalJoinSettings()).Succeeded, "The legacy player can ask to join.");
        That(rig.Run(() => refused.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended), "The host refuses before admitting it.");
        var status = refused.Control.GetStatus();
        That(status.ContentProblems.Contains(UiPresentationTheme.RequiredRuntime) &&
            rig.Host.Control.SessionSnapshot.TryGetParticipant(legacyId, out var keptLegacy) && keptLegacy!.IsConnected &&
            rig.Host.Control.SessionSnapshot.TryGetParticipant(foundationId, out var keptFoundation) && keptFoundation!.IsConnected,
            "A joining legacy peer cannot replace the existing session or disconnect any admitted player.");

        var offered = new RuntimeContent("2545", "0.2.0") { Runtime = foundation.Runtime };
        var descriptor = rig.Host.Provider.Live!.Host!.Descriptor!.Descriptor;
        That(ContentOfferCodec.TryDecode(offered.ClientContent(), out var oldOffer) && oldOffer!.UiContractVersion == 1 &&
            ContentOfferCodec.TryDecode(offered.ClientContent(descriptor), out var newOffer) && newOffer!.UiContractVersion == 2,
            "Legacy and extended offers remain distinct and decode to their declared versions.");
        FoundationEnvelopeReachesBothAuthenticatedRoutes();
        TargetedUpgradeSurvivesGuestScopeTeardown();
        FailedTargetedUpgradeKeepsLegacyState();
        ArtUsesItsOwnPackOnBothRoutes();
        ArtUsesTheCompleteRetainedFrameBudget();
    }
}
