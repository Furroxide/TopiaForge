using TopiaForge.ModManager;
using TopiaForge.Mods.Internal;
using TopiaForge.TestSupport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class RuntimeAdmissionTests
{
    private static void ControlsUseOnlyTheirAdmittedRecipient()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        using var rig = new ControlRig(164, Sandbox);
        using var host = new UiDeliveryEndpoint(rig.Host, () => rig.Network.Now, author: true);
        var old = rig.AddPeer("Foundation receiver", Sandbox);
        old.UseRuntime(new UncheckedRuntimeHost(old.Runtime, InternalUiPresentationContract.FoundationVersion));
        var peer = rig.AddPeer("Controls receiver", Sandbox);
        using var guest = new UiDeliveryEndpoint(peer, () => rig.Network.Now, author: false);
        guest.Ui.RecordControlsAtBoundary = true;
        rig.StartHosting(new InternalHostSettings("Mixed UI", 6));
        rig.JoinAndWait(old);
        rig.JoinAndWait(peer);
        var oldId = old.Control.SessionSnapshot.LocalParticipantId!.Value;
        var id = peer.Control.SessionSnapshot.LocalParticipantId!.Value;
        That(rig.Host.Control.UiContractVersionOf(id) == InternalUiPresentationContract.ControlsVersion &&
            rig.Host.Control.UiContractVersionOf(oldId) == InternalUiPresentationContract.FoundationVersion &&
            InternalUiPresentationContract.ControlsVersion == UiWireCodec.ControlsContractVersion,
            "The host keeps independent admitted v2 and v3 capabilities.");
        That(rig.Run(() => (host.Players.Channel?.GetGeneration(id) ?? 0) != 0 &&
            (host.Players.Channel?.GetGeneration(oldId) ?? 0) != 0), "Both destinations have authenticated player streams.");
        That(host.Directory!.TryGet(oldId, out var oldTarget), "The v2 destination is targetable.");
        That(host.Directory.TryGet(id, out var newTarget), "The v3 destination is targetable.");

        var action = new InputActionDefinition("open", "Open settings", new[] { InputBinding.Key("F"), InputBinding.GamepadButton(InputGamepadButton.South) });
        host.Ui.InputDefinitions.Add(action.Name, action);
        var calls = new List<string>();
        var timer = new UiCountdown(100d, "Details", UiTone.Primary, "clock");
        var content = new UiTabs("tabs", new[]
        {
            new UiTab("play", "Play", new UiTooltip(new UiCheckbox("check", "Read", false, value => calls.Add(value ? "checked" : "cleared")), "Help")),
            new UiTab("more", "More", new UiColumn(timer, new UiKeyBindingField("key", "open"), new UiSlider("steps", "Scale",
                0f, 1f, 0.5f, value => calls.Add("step=" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)), 4, UiValueFormat.Percent()),
                UiTextInput.WithDecoration("text", "Input", "", calls.Add, new UiInputDecoration("Enter", "Try again"), 4))),
        }, "play", calls.Add, UiTabsStyle.Rail);
        var oldText = new UiText("Compatible", UiTextStyle.Label);
        using var foundation = (RemoteUiSurface)oldTarget!.Ui.CreateSurface(
            new UiSurfaceRequest("baseline", "Foundation", "", content: oldText)).Value!;
        var oldRevision = foundation.Revision;
        var oldEnvelope = foundation.Envelope;
        var refused = foundation.SetContent(content);
        var refusedCreate = oldTarget.Ui.CreateSurface(new UiSurfaceRequest("controls", "Must refuse", "", content: content));
        That(refused.ErrorCode == ModErrorCode.Unavailable && refused.ErrorMessage == UiPresentationTheme.RequiredControlsRuntime &&
            refusedCreate.ErrorCode == ModErrorCode.Unavailable && ReferenceEquals(foundation.Content, oldText) &&
            foundation.Revision == oldRevision && ReferenceEquals(foundation.Envelope, oldEnvelope) && timer.ClockEpoch == null,
            "The v2 target refuses both creation and replacement before the frame or candidate clock can commit.");

        var unknown = newTarget!.Ui.CreateSurface(new UiSurfaceRequest("missing", "Unknown action", "",
            content: new UiKeyBindingField("key", "other-package-action")));
        That(unknown.ErrorCode == ModErrorCode.InvalidArgument && guest.Ui.Surfaces.Count == 0,
            "The target cannot borrow defaults for an action outside the originating package.");
        using var sent = (RemoteUiSurface)newTarget.Ui.CreateSurface(new UiSurfaceRequest("controls", "Controls K2", "", content: content)).Value!;
        That(sent.Token == foundation.Token + 1, "Refused attempts spend no surface token.");
        That(rig.Run(() => guest.Ui.Surfaces.ContainsKey("Controls K2")),
            "A v3 window reaches the admitted v3 guest in the same cohort (" + guest.Ui.LastCreateFailure + ").");
        var receipt = guest.Ui.Surfaces["Controls K2"];
        var tabs = (UiTabs)receipt.ReceivedContent!;
        var first = (UiTooltip)tabs.Tabs[0].Child;
        var hidden = (UiColumn)tabs.Tabs[1].Child;
        var key = (UiKeyBindingField)hidden.Children[1];
        That(receipt.Theme.Envelope?.OwnerPackageId == Sandbox.Id &&
            key.WireDefaults.Select(binding => binding.Control).SequenceEqual(new[] { "F", "South" }) &&
            timer.ClockEpoch == host.Players.UiClockEpoch && ((UiCountdown)hidden.Children[0]).ClockEpoch == guest.Players.UiClockEpoch,
            "The receiver gets authenticated owning-package default hints and timed hidden controls.");

        tabs.Selected!("other");
        ((UiSlider)hidden.Children[2]).Changed(0.3f);
        ((UiTextInput)hidden.Children[3]).Changed("length");
        ((UiCheckbox)first.Child).Changed(true);
        tabs.Selected!("more");
        ((UiSlider)hidden.Children[2]).Changed(0.75f);
        ((UiTextInput)hidden.Children[3]).Changed("OK");
        That(rig.Run(() => calls.Count >= 4) && calls.SequenceEqual(new[] { "checked", "more", "step=0.75", "OK" }),
            "The original owner receives valid control replies and rejects invalid tab, step and edit values.");
        var measured = AllocationProbe.Measure("settled targeted K2 content", () =>
        {
            if (!sent.SetContent(content).Succeeded) throw new InvalidOperationException("Retained K2 content must succeed.");
        }, frames: 10_000, warmups: 256);
        That(measured == 0 && sent.Revision == 1 && foundation.IsVisible,
            "A settled v3 tree allocates nothing or changes revision and the v2 surface stays retained.");
        Console.WriteLine("K2 mixed v2/v3 delivery, transactional refusal, bound defaults, callbacks and zero idle bytes passed.");
    }
}
