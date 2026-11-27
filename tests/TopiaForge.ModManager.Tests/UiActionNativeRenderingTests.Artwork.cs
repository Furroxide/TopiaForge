namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiActionNativeRenderingTests
    {
        private const string ArtAndBindingScenarios = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using System.Text;
            using TMPro;
            using TopiaForge.ModManager.Core;
            using TopiaForge.Mods;
            using TopiaForge.Mods.Testing;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.UI;

            namespace TopiaForge.ModManager
            {
                internal sealed partial class OwnerUiService
                {
                    private sealed partial class UnityUiLayoutRenderer
                    {
                        private const string ArtTheme = "{\"schema\":1,\"id\":\"fixture-art\",\"images\":["
                            + "{\"id\":\"mark\",\"bundlePath\":\"ui/marks.bundle\",\"assetName\":\"Mark\",\"licensePath\":\"ui/LICENSE\",\"pixelWidth\":32,\"pixelHeight\":32,\"isMask\":true},"
                            + "{\"id\":\"photo\",\"bundlePath\":\"ui/marks.bundle\",\"assetName\":\"Photo\",\"licensePath\":\"ui/LICENSE\",\"pixelWidth\":48,\"pixelHeight\":24,\"isMask\":false}]}";

                        private static void ArtworkAndRebind()
                        {
                            var parsed = UiThemePackParser.Parse(Encoding.UTF8.GetBytes(ArtTheme));
                            Check(parsed.Succeeded, parsed.ErrorMessage);
                            var pack = parsed.Value!;
                            var catalogue = new AdmittedUiCatalogue(null, new Dictionary<string, UiThemeDescriptor> { [pack.Id] = pack });
                            var sprite = new Sprite("Verified mark sprite");
                            var resources = new UiThemeAcquisitions { Sprite = sprite };
                            using var life = new FakeModLifetime();
                            var input = Input(life);
                            var mask = new UiActionSlot("activate", "Relay", image: new UiImageRef(pack.Id, "mark"), unavailableReason: "Signal\nholding")
                                .WithWireImageDigest(pack.Digest.Span);
                            using var admitted = UiSurfaceArtwork.Stage(mask, remote: true, null, catalogue, resources, _ => { });
                            var view = new SlotProbe(mask, input, new Clock(), admitted);
                            var art = view.Graphic("Action art");
                            Check(resources.Demands == 1 && art.enabled && ReferenceEquals(art.sprite, sprite) && art.preserveAspect &&
                                art.color == view.Label.color && art.color != Color.white && !view.Graphic("Action image placeholder").enabled,
                                "The exact admitted descriptor routes its mask to the native slot and tints it with content ink.");

                            var original = view.Measured;
                            Check(input.Rebind(new[] { InputBinding.Key("KeypadMultiply") }).Succeeded && view.PullBinding(),
                                "A valid long viewer key marks the existing slot layout dirty.");
                            try { view.Reapply(); }
                            catch (ArgumentException error)
                            {
                                throw new InvalidOperationException("A valid KeypadMultiply rebind must keep the admitted art slot visible.", error);
                            }
                            var key = view.Host.Go.Part("Key cap");
                            var keyLabel = key.Part("Label").GetComponent<TMP_Text>();
                            Check(view.Measured.Equals(original) && keyLabel.Text == "KeypadMultiply" &&
                                key.Rect.Right <= art.rectTransform.X && key.Rect.Bottom <= view.Label.rectTransform.Y &&
                                view.Status.rectTransform.Bottom <= original.Height && ReferenceEquals(art.sprite, sprite) && art.enabled,
                                "The long rebind keeps the full key, artwork and authored text readable inside the same slot footprint.");

                            var otherHash = pack.Digest.ToArray();
                            otherHash[0] ^= 0x80;
                            var unresolved = mask.WithWireImageDigest(otherHash);
                            using var wrong = UiSurfaceArtwork.Stage(unresolved, remote: true, null, catalogue, resources, _ => { });
                            var fallback = new SlotProbe(unresolved, input, new Clock(), wrong);
                            Check(resources.Demands == 1 && !fallback.Graphic("Action art").enabled &&
                                fallback.Graphic("Action image placeholder").enabled && fallback.Label.Text == "Relay" &&
                                fallback.Status.Text == "Signal\nholding" && fallback.Measured.Equals(original),
                                "A mismatched descriptor digest does not draw or acquire its sprite and preserves the slot label and frame.");

                            var untinted = new UiActionSlot("activate", "Relay", image: new UiImageRef(pack.Id, "photo"), unavailableReason: "Later")
                                .WithWireImageDigest(pack.Digest.Span);
                            using var originalArt = UiSurfaceArtwork.Stage(untinted, remote: true, null, catalogue, resources, _ => { });
                            var fullColor = new SlotProbe(untinted, input, new Clock(), originalArt);
                            Check(fullColor.Graphic("Action art").color == Color.white,
                                "Verified full-color slot art preserves the sprite colors.");
                        }

                        private static void NoManagedAllocations()
                        {
                            using var life = new FakeModLifetime();
                            var input = Input(life);
                            var clock = new Clock { Now = 2.5d };
                            var timer = new UiCooldown(10d, 10d);
                            var active = new SlotProbe(new UiActionSlot("activate", "Relay", cooldown: timer), input, clock);
                            var charges = new SlotProbe(new UiActionSlot("activate", "Relay", charges: new UiCharges(1, 4, timer)), input, clock);
                            var pips = new PipProbe(UiPips.WithRefill(4, 1, "Cells", timer), clock);
                            var activeWidth = active.Sweep.Rect.Width;
                            var charge = Mark(charges, "Charge fill", 1);
                            var pip = pips.Host.Go.Part("Pip fill clip", 1);
                            for (var index = 0; index < 4096; index++) Step();

                            var writes = active.Sweep.Rect.Writes + charge.rectTransform.Writes + pip.Rect.Writes;
                            var digits = active.Status.Writes + charges.Status.Writes + pips.Label.Writes;
                            var before = GC.GetAllocatedBytesForCurrentThread();
                            for (var index = 0; index < 10_000; index++) Step();
                            var stable = GC.GetAllocatedBytesForCurrentThread() - before;
                            Check(stable == 0 && writes == active.Sweep.Rect.Writes + charge.rectTransform.Writes + pip.Rect.Writes &&
                                digits == active.Status.Writes + charges.Status.Writes + pips.Label.Writes,
                                "Settled slot and pip frames allocate zero bytes and write no geometry or readouts.");

                            clock.Now = 6.5d;
                            Step();
                            Check(active.Sweep.Rect.Width > activeWidth && active.Status.Text == "4 s" && pips.Label.Text == "Cells: 1 / 4 (65% refilling)",
                                "The animated allocation sample also exercises actual frame changes.");
                            var sweepWrites = active.Sweep.Rect.Writes;
                            var chargeWrites = charge.rectTransform.Writes;
                            var pipWrites = pip.Rect.Writes;
                            before = GC.GetAllocatedBytesForCurrentThread();
                            for (var index = 0; index < 10_000; index++)
                            {
                                clock.Now = (index & 1) == 0 ? 2.5d : 6.5d;
                                Step();
                            }
                            var animated = GC.GetAllocatedBytesForCurrentThread() - before;
                            Check(animated == 0 && active.Sweep.Rect.Writes == sweepWrites + 10_000 &&
                                charge.rectTransform.Writes == chargeWrites + 10_000 && pip.Rect.Writes == pipWrites + 10_000,
                                "Changing cooldown, charge and pip render samples allocate zero bytes after warm-up.");
                            Console.WriteLine("10,000 settled native frames: " + stable + " bytes; 10,000 changing native frames: " + animated + " bytes.");

                            void Step() { active.Tick(); charges.Tick(); pips.Tick(); }
                        }

                        private static void FakeRebindKeepsStatusInView()
                        {
                            using var context = new FakeModContext();
                            var action = context.Input.RegisterAction(new InputActionDefinition("activate", "Activate",
                                new[] { InputBinding.Key("E") })).Value!;
                            var slot = new UiActionSlot("activate", "Relay", glyph: UiGlyph.Check, unavailableReason: "Signal\nholding");
                            var request = new UiSurfaceRequest("commands", "Commands", "", UiSurfaceKind.Window, 540f, 360f, slot);
                            var creation = context.Ui.CreateSurface(request);
                            Check(creation.Succeeded, creation.ErrorMessage);
                            using var surface = (FakeUiSurface)creation.Value!;
                            var viewport = new UiCaptureViewport(1920, 1080);
                            var before = surface.Capture(viewport);
                            Check(!before.Nodes.Single(item => item.Kind == "ActionStatus").Clipped &&
                                action.Rebind(new[] { InputBinding.Key("KeypadMultiply") }).Succeeded,
                                "The fake admits the short-key fixture and the valid long viewer binding.");
                            var after = surface.Capture(viewport);
                            var status = after.Nodes.Single(item => item.Kind == "ActionStatus");
                            Check(status.Text == "Signal\nholding" && status.IsRendered && !status.Clipped &&
                                after.Nodes.Single(item => item.Kind == "ActionKey").KeyLabel == "KeypadMultiply" &&
                                after.Nodes.Any(item => item.Kind == "ActionGlyph") && after.CheckAccessibility().Count == 0,
                                "The headless capture preserves the long key, art and two-line status after rebinding.");
                        }

                        private static IInputAction Input(FakeModLifetime life)
                        {
                            var input = new FakeInputService(life);
                            var action = input.RegisterAction(new InputActionDefinition("activate", "Activate", new[] { InputBinding.Key("E") }));
                            return action.Value!;
                        }
                        private static int MarkCount(SlotProbe view, string name) =>
                            view.Host.Go.Part("Action charges").Parts(name).Count(item => item.GetComponent<Image>().enabled);
                        private static TopiaForgeRoundedImage Mark(SlotProbe view, string name, int index) =>
                            view.Host.Go.Part("Action charges").Part(name, index).GetComponent<TopiaForgeRoundedImage>();
                        private static bool Near(double first, double second) => Math.Abs(first - second) < .001d;
                        private static void Check(bool condition, string message)
                        {
                            if (!condition) throw new InvalidOperationException(message);
                        }

                        private sealed class Clock
                        {
                            internal double Now { get; set; }
                            internal string Epoch { get; set; } = "first";
                            internal UiCallbackGate Callbacks() => new UiCallbackGate { Clock = () => Now, ClockEpoch = () => Epoch };
                        }
                        private sealed class SlotProbe
                        {
                            private readonly UnityUiLayoutRenderer renderer;
                            private readonly NativeActionSlot slot;
                            private readonly UiSurfaceLayoutFrame frame;
                            private readonly TopiaForgeResolvedTheme native;
                            internal SlotProbe(UiActionSlot source, IInputAction action, Clock clock, UiSurfaceArtwork? assets = null, bool reduced = false)
                            {
                                native = new TopiaForgeResolvedTheme { Descriptor = Descriptor };
                                Host = new TopiaForgeContainer(native, 1f, reduced);
                                renderer = new UnityUiLayoutRenderer { input = new UiSurfaceInputContext(action, "fixture"), artwork = assets };
                                var view = new NativeNode(Host);
                                renderer.CreateActionSlot(view, source, clock.Callbacks());
                                slot = view.ActionSlot!;
                                frame = Host.Go.GetComponent<UiSurfaceLayoutFrame>();
                                Measured = slot.Measure(Descriptor, Descriptor, native.HighContrast).Size;
                                UiClockBinding.Bind(source, clock.Epoch);
                                slot.Apply(UiPaintRole.Surface, Hud, native, Descriptor, Descriptor);
                            }
                            internal TopiaForgeContainer Host { get; }
                            internal UiLayoutExtent Measured { get; private set; }
                            internal Image Fill => Graphic("Action fill");
                            internal TopiaForgeRoundedImage Outline => Host.Go.Part("Action boundary").GetComponent<TopiaForgeRoundedImage>();
                            internal GameObject Sweep => Host.Go.Part("Action sweep mask");
                            internal TMP_Text Status => Host.Go.Part("Action status").GetComponent<TMP_Text>();
                            internal TMP_Text Label => Host.Go.Part("Action label").GetComponent<TMP_Text>();
                            internal Image StatusCue => Graphic("Action status cue");
                            internal Image Graphic(string name) => Host.Go.Part(name).GetComponent<Image>();
                            internal bool PullBinding() => renderer.RefreshKeyGlyphs();
                            internal void Reapply()
                            {
                                Measured = slot.Measure(Descriptor, Descriptor, native.HighContrast).Size;
                                slot.Apply(UiPaintRole.Surface, Hud, native, Descriptor, Descriptor);
                            }
                            internal void Tick() => frame.TickTest();
                        }
                        private sealed class PipProbe
                        {
                            private readonly UiSurfaceLayoutFrame frame;
                            internal PipProbe(UiPips source, Clock clock, bool reduced = false)
                            {
                                var native = new TopiaForgeResolvedTheme { Descriptor = Descriptor };
                                Host = new TopiaForgeContainer(native, 1f, reduced);
                                var view = new NativeArtLeaf(Host, source, null, clock.Callbacks());
                                var size = view.Measure(UiLayoutConstraints.Loose(420, 160), Descriptor, Descriptor, native.HighContrast,
                                    protectImageLabels: true).Size;
                                UiClockBinding.Bind(source, clock.Epoch);
                                view.Apply(new UiLayoutRect(0, 0, size.Width, size.Height), UiPaintRole.Surface, Hud, native, Descriptor, Descriptor);
                                frame = Host.Go.GetComponent<UiSurfaceLayoutFrame>();
                            }
                            internal TopiaForgeContainer Host { get; }
                            internal TMP_Text Label => Host.Go.Part("Label").GetComponent<TMP_Text>();
                            internal void Tick() => frame.TickTest();
                        }
                    }
                }
            }
            """;
    }
}
