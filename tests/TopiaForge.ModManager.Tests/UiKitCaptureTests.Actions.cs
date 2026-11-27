using System;
using System.Linq;
using System.Text;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.TestSupport;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        internal static void RunActionCaptureTests()
        {
            ActionThemesResolveWithoutChangingOlderIdentities();
            ActionDevicesAndOwnershipAreViewerLocal();
            ActionClocksAndArtworkKeepTheirLease();
            SettledActionBindingLookupDoesNotAllocate();
            VerifyActionGoldenManifest();
            Console.WriteLine("K2 action captures passed for viewer bindings, clock expiry, artwork and accessibility.");
        }

        private static void ActionThemesResolveWithoutChangingOlderIdentities()
        {
            UiThemeDescriptor Parse(string json)
            {
                var result = UiThemePackParser.Parse(Encoding.UTF8.GetBytes(json));
                Check(result.Succeeded, result.ErrorMessage);
                return result.Value!;
            }
            var kit = UiThemePackParser.Default;
            Check(kit.MinimumWireVersion == 2 && kit.CanonicalComponentCount == 4 &&
                Convert.ToHexString(kit.Digest.Span) == "4602CE2902CF9C95898AEA57EDFD9E9293DD563941CBF191240C299393D89E3A" &&
                kit.Components[UiThemeTarget.KeyGlyph].Radius == UiThemeRadius.Small &&
                kit.Components[UiThemeTarget.ActionSlot].Shadow == UiThemeShadow.Small, "The added kit defaults retain the foundation fingerprint.");
            var controls = Parse("{\"schema\":1,\"id\":\"controls\",\"components\":{\"button\":{\"radius\":\"control\",\"border\":\"standard\",\"shadow\":\"small\"}}}");
            var status = Parse("{\"schema\":1,\"id\":\"status\",\"components\":{\"statBar\":{\"radius\":\"pill\",\"border\":\"thin\",\"shadow\":\"none\"}}}");
            Check(controls.CanonicalComponentCount == 8 && status.CanonicalComponentCount == 9 &&
                Convert.ToHexString(controls.Digest.Span) == "D7182366911388390A7CB8628EA5F0DDBD70C374292EADF7E87ED8CDA616C2ED" &&
                Convert.ToHexString(status.Digest.Span) == "CE4C3F3261F031C7AC8828AE9865EBE64BD447227E3A5D66F6B52E0A89E36C3A",
                "Earlier K2 controls and status descriptors keep their exact canonical component coverage.");
            const string initial = "{\"schema\":1,\"id\":\"actions\",\"components\":{\"keyGlyph\":{\"radius\":\"small\",\"border\":\"standard\",\"shadow\":\"none\"}}}";
            var glyphs = Parse(initial);
            var altered = Parse(initial.Replace("small", "medium", StringComparison.Ordinal));
            Check(glyphs.MinimumWireVersion == 3 && !glyphs.Digest.Span.SequenceEqual(altered.Digest.Span),
                "Authored key cap components require v3 and enter the admission digest.");
            const string styleJson = "{\"schema\":1,\"id\":\"actions\",\"styles\":[{\"id\":\"slot.edge\",\"target\":\"actionSlot\",\"radius\":\"medium\",\"fill\":null}]}";
            var style = Parse(styleJson);
            var slot = new UiActionSlot("confirm", "Confirm", style: new UiStyleRef("slot.edge"));
            var decoration = UiLayoutDecoration.Resolve(slot, style, style, new UiLayoutExtent(128, 112), false);
            Check(style.MinimumWireVersion == 3 && UiLayoutDecoration.AdmitsNamedStyles(slot, style) &&
                !UiLayoutDecoration.AdmitsNamedStyles(slot, kit) &&
                decoration is { StyleId: "slot.edge", Fill: UiPaintRole.Surface },
                "An authored slot style is v3, target-checked, and null fill keeps Surface.");
        }

        private static void ActionDevicesAndOwnershipAreViewerLocal()
        {
            using var host = ActionContext();
            using var guest = ActionContext();
            using var other = ActionContext("example.other");
            var clock = new StatusCaptureClock();
            clock.Connect(host.Ui);
            clock.Connect(guest.Ui);
            clock.Connect(other.Ui);
            InstallActionDefinitions(host);
            var authorBinding = host.Input.GetAction(ActionGalleryPages.Confirm);
            Check(authorBinding.Rebind(new[] { InputBinding.Key("F") }).Succeeded, "Change the sender's live binding without rewriting its definition.");
            var definition = new InputActionDefinition(ActionGalleryPages.Confirm, "Confirm",
                new[] { InputBinding.Key("Q"), InputBinding.GamepadButton(InputGamepadButton.East) });
            var local = guest.Input.RegisterAction(definition).Value!;
            Check(other.Input.RegisterAction(definition).Succeeded, "A different package registers the same ordinal name independently.");
            var request = ActionGalleryPages.CreateRequest(ActionGalleryPage.KeyGlyphs, UiSurfaceKind.Window, 0);
            using var original = Make(host.Ui, request);
            using var remote = OpenActionReceiver(guest.Ui, host, request, clock.Epoch);
            using var wrongOwner = OpenActionReceiver(other.Ui, host, request, clock.Epoch);
            var localCapture = original.Capture(Viewport);
            var hostKey = ActionNode(localCapture, "key-confirm");
            Check(ActionNode(localCapture, "key-spare").KeyLabel == "Unbound" &&
                ActionNode(remote.Capture(Viewport), "key-spare").KeyLabel == "U",
                "The Gallery shows a live cleared action, while an unregistered guest uses its nonempty authored default.");
            guest.Ui.LastInputDevice = UiInputDevice.Gamepad;
            Check(hostKey.KeyLabel == "F" && ActionNode(remote.Capture(Viewport), "key-confirm").KeyLabel == "B" &&
                ActionNode(wrongOwner.Capture(Viewport), "key-confirm").KeyLabel == "E",
                "The viewer chooses its own Gamepad binding. A different package sees only the owner's declared default.");
            guest.Ui.LastInputDevice = UiInputDevice.KeyboardMouse;
            Check(ActionNode(remote.Capture(Viewport), "key-confirm").KeyLabel == "Q", "Changing viewer device changes the same retained tree.");
            Check(local.Rebind(new[] { InputBinding.Key("J"), InputBinding.Key("T") }).Succeeded, "Set two viewer keyboard controls.");
            guest.Ui.LastInputDevice = UiInputDevice.Gamepad;
            Check(ActionNode(remote.Capture(Viewport), "key-confirm").KeyLabel == "J", "A viewer without that device falls back to its own first other-device binding.");
            Check(local.Rebind(Array.Empty<InputBinding>()).Succeeded, "The viewer can deliberately clear its live bindings.");
            var cleared = remote.Capture(Viewport);
            Check(ActionNode(cleared, "key-confirm").KeyLabel == "Unbound" &&
                cleared.Nodes.Any(node => node.Kind == "KeyCapCue" && node.Path.EndsWith("/cue:Info", StringComparison.Ordinal)),
                "A cleared viewer action stays registered and overrides the authenticated publisher hint.");
            Check(local.ResetBindings().Succeeded && ActionNode(remote.Capture(Viewport), "key-confirm").KeyLabel == "B",
                "Reset restores the viewer's originally registered bindings on the same retained tree.");
            local.Dispose();
            Check(ActionNode(remote.Capture(Viewport), "key-confirm").KeyLabel == "A", "When the viewer registration is gone the owned wire default becomes visible.");
            authorBinding.Dispose();
            var removed = original.Capture(Viewport);
            Check(ActionNode(removed, "key-confirm").KeyLabel == "Unbound" &&
                removed.Nodes.Any(node => node.Kind == "KeyCapCue" && node.Path.EndsWith("/cue:Info", StringComparison.Ordinal)),
                "A once-admitted local action shows Unbound and Info after disposal.");
            var unknown = host.Ui.CreateSurface(new UiSurfaceRequest("unknown", "Unknown", "", content: new UiKeyGlyph("ghost", "missing-key")));
            Check(unknown.ErrorCode == ModErrorCode.InvalidArgument && unknown.ErrorMessage.Contains("missing-key", StringComparison.Ordinal),
                "An unknown local action is refused with the authored node id.");
            var unregistered = host.Ui.CreateSurface(new UiSurfaceRequest("unknown-slot", "Unknown", "",
                content: new UiActionSlot("ghost", "Ghost", id: "missing-slot")));
            Check(unregistered.ErrorCode == ModErrorCode.InvalidArgument && unregistered.ErrorMessage.Contains("missing-slot", StringComparison.Ordinal),
                "An unregistered slot fails the same local owning-action admission.");
            var cap = UiActionBindingPresentation.Resolve(Array.Empty<InputBinding>(),
                new[] { new UiInputBindingHint(InputBindingKind.Key, "E") }, UiInputDevice.KeyboardMouse);
            Check(cap.Label == "Unbound" && cap.Cue == UiGlyph.Info, "A live empty snapshot cannot fall back to the host's hint.");
            var empty = new UiKeyGlyph("missing-here", "wire-empty", Array.AsReadOnly(Array.Empty<UiInputBindingHint>()));
            var emptyEnvelope = new UiWireEnvelope(StatusPublisher, new byte[32], 2, null, new byte[32]);
            using var emptyReceiver = OpenActionPresentation(other.Ui, new UiSurfaceRequest("empty-hints", "Empty", "", content: empty),
                empty, emptyEnvelope, clock.Epoch, retained: false);
            var emptyCapture = emptyReceiver.Capture(Viewport);
            Check(ActionNode(emptyCapture, "wire-empty").KeyLabel == "Unbound" &&
                emptyCapture.Nodes.Any(node => node.Kind == "KeyCapCue" && node.Path.EndsWith("/cue:Info", StringComparison.Ordinal)),
                "A received zero-count hints field displays Unbound with a visible Info mark.");
        }

        private static void ActionClocksAndArtworkKeepTheirLease()
        {
            using var author = ActionContext();
            using var viewer = ActionContext();
            var fixtures = new GalleryArtFixtures();
            fixtures.Install(author.Ui);
            fixtures.Install(viewer.Ui, digestOverride: new string('0', 64));
            InstallActionDefinitions(author);
            var clock = new StatusCaptureClock();
            clock.Time.AdvanceFrame(120f);
            clock.Connect(author.Ui);
            clock.Connect(viewer.Ui);
            var request = ActionGalleryPages.CreateRequest(ActionGalleryPage.Slots, UiSurfaceKind.Window, author.Ui.ClockTime);
            using var local = Make(author.Ui, request);
            author.Ui.CompletePendingThemeRegistrations();
            var initial = local.Capture(Viewport);
            Check(ActionNode(initial, "slot-cooldown") is { State: "cooldown", Fraction: 0.5f, RemainingSeconds: 6 } &&
                ActionNode(initial, "slot-charging") is { State: "charging", Fraction: 0.5f, RemainingSeconds: 8 } &&
                ActionNode(initial, "pips-refill").RemainingSeconds == 8 &&
                initial.Nodes.Any(node => node.Kind == "ActionImage" && !node.UsedFallback && node.InkColor != null),
                "A settled action bar captures pending deadlines and a verified tinted icon.");
            clock.Epoch = "next-action-world";
            using var remote = OpenActionReceiver(viewer.Ui, author, request, clock.Epoch, retained: true);
            viewer.Ui.CompletePendingThemeRegistrations();
            var retained = local.Capture(Viewport);
            var received = remote.Capture(Viewport);
            Check(ActionNode(retained, "slot-cooldown").State == "ready" && ActionNode(received, "slot-cooldown").State == "ready" &&
                ActionNode(retained, "slot-charging").SemanticLabel.EndsWith("3 of 3 charges", StringComparison.Ordinal) &&
                ActionNode(received, "slot-charging").SemanticLabel.EndsWith("3 of 3 charges", StringComparison.Ordinal) &&
                received.Nodes.Any(node => node.Kind == "PipsLabel" && node.Text == "Cells: 3 / 4"),
                "The retained bar expires in place at the same time when the epoch changes, and its surrogates agree.");
            Check(received.Nodes.Any(node => node.Kind == "ActionImage" && node.UsedFallback && node.InkColor == null) &&
                ActionNode(received, "slot-cooldown").SemanticLabel.StartsWith("Charge", StringComparison.Ordinal),
                "A changed icon pack digest loses art authority and keeps the slot label usable.");
            Check(ReferenceEquals(local.Content, request.Content) &&
                local.SetContent(request.Content!).Succeeded, "Retained capture keeps the authored action composition.");
            var tooLong = author.Ui.CreateSurface(new UiSurfaceRequest("three-lines", "Line limit", "",
                content: new UiActionSlot(ActionGalleryPages.Confirm, "One\nTwo\nThree", id: "too-long")));
            Check(tooLong.ErrorCode == ModErrorCode.InvalidArgument && tooLong.ErrorMessage.Contains("two readable lines", StringComparison.Ordinal),
                "Action labels requiring a third line are refused before admission.");
        }

        private static FakeModContext ActionContext(string id = StatusPublisher) =>
            new FakeModContext(new ModIdentity(id, "Action fixture", SemanticVersion.Parse("1.0.0")));

        private static void InstallActionDefinitions(FakeModContext fixture)
        {
            Check(ActionGalleryPages.RegisterActions(fixture.Input), "Register the Gallery actions and explicitly clear the spare action.");
        }

        private static UiCapturedNode ActionNode(UiTreeCapture tree, string id) => tree.Nodes.Single(node => node.Id == id);

        private static void SettledActionBindingLookupDoesNotAllocate()
        {
            var live = new[] { InputBinding.Key("Q"), InputBinding.GamepadButton(InputGamepadButton.West) };
            var defaults = new[] { new UiInputBindingHint(InputBindingKind.Key, "E") };
            var observed = 0;
            var bytes = AllocationProbe.Measure("settled key glyphs", () =>
            {
                observed += UiActionBindingPresentation.Resolve(live, defaults, UiInputDevice.Gamepad).Label.Length +
                    UiActionBindingPresentation.Resolve(null, defaults, UiInputDevice.KeyboardMouse).Label.Length;
            }, frames: 4096, warmups: 256);
            Check(bytes == 0 && observed > 0, "Settled live and hinted binding lookup allocates zero bytes.");
        }
    }
}
