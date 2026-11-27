using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiActionNativeRenderingTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            const string manager = "src/TopiaForge.ModManager/";
            var files = new[]
            {
                manager + "UnityUiLayoutRenderer.ActionSlots.cs",
                manager + "UnityUiLayoutRenderer.ActionSlots.Paint.cs",
                manager + "UnityUiLayoutRenderer.KeyGlyphs.cs",
                manager + "UnityUiLayoutRenderer.PipRefill.cs",
                manager + "UnityUiLayoutRenderer.Art.cs",
                manager + "UnityUiSurface.Artwork.cs",
                manager + "UiNativeActionBinding.cs",
                "src/TopiaForge.Mods.UnityUi/Widgets/TopiaForgeActionKeyCap.cs",
            };
            var framePath = Path.Combine(root, manager + "UnityUiSurface.Layout.cs");
            var frame = CSharpSyntaxTree.ParseText(File.ReadAllText(framePath)).GetRoot().DescendantNodes()
                .OfType<ClassDeclarationSyntax>().Single(item => item.Identifier.Text == "UiSurfaceLayoutFrame");
            var layoutPath = Path.Combine(root, manager + "UnityUiLayoutRenderer.cs");
            var sharedPaint = CSharpSyntaxTree.ParseText(File.ReadAllText(layoutPath)).GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>().Where(item => item.Identifier.Text is "Place" or "AddGraphic" or "ColorOf");
            var sources = files.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, path)), path: path))
                .Append(CSharpSyntaxTree.ParseText("using System; using UnityEngine; namespace TopiaForge.ModManager { " + frame + " }",
                    path: framePath))
                .Append(CSharpSyntaxTree.ParseText(ActionBoundary.Replace("/* layout paint */", string.Join("\n", sharedPaint)),
                    path: "NativeActionBoundary.cs"))
                .Append(CSharpSyntaxTree.ParseText(ActionScenarios, path: "NativeActionScenarios.cs"))
                .Append(CSharpSyntaxTree.ParseText(ArtAndBindingScenarios, path: "NativeActionArtwork.cs"));
            var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var references = platform.Append(typeof(UiNode).Assembly.Location).Append(typeof(AdmittedUiCatalogue).Assembly.Location)
                .Append(typeof(FakeUiService).Assembly.Location)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal) &&
                    Path.GetFileName(path) != "TopiaForge.ModManager.Tests.dll")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager.Tests", sources, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var assembly = new MemoryStream();
            var result = compilation.Emit(assembly);
            if (!result.Success)
                throw new InvalidOperationException("The production action renderer must compile at the Unity boundary. " +
                    string.Join("\n", result.Diagnostics.Where(item => item.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            var probe = Assembly.Load(assembly.ToArray()).GetType("TopiaForge.ModManager.OwnerUiService")!.GetMethod("ProbeActions")!;
            try { probe.Invoke(null, null); }
            catch (TargetInvocationException failure) { throw failure.InnerException!; }
            Console.WriteLine("Native action renderer passed for state, paint, retained epochs, key bindings, motion and allocations.");
        }

        private const string ActionScenarios = """
            using System;
            using System.Linq;
            using TMPro;
            using TopiaForge.Mods;
            using TopiaForge.Mods.Testing;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.UI;

            namespace TopiaForge.ModManager
            {
                internal sealed partial class OwnerUiService
                {
                    public static void ProbeActions() => UnityUiLayoutRenderer.ProbeActions();

                    private sealed partial class UnityUiLayoutRenderer
                    {
                        private static readonly UiThemeDescriptor Descriptor = UiThemePackParser.Default;
                        private static readonly UiThemeScheme Hud = Descriptor.SelectScheme(hud: true, highContrast: false);
                        private static readonly Color Sunken = ColorOf(Hud.GetPair(UiPaintRole.Sunken).Fill);
                        private static readonly Color Surface = ColorOf(Hud.GetPair(UiPaintRole.Surface).Fill);
                        private static readonly Color Raised = ColorOf(Hud.GetPair(UiPaintRole.Raised).Fill);

                        internal static void ProbeActions()
                        {
                            CooldownAndPrecedence();
                            ChargesAndPipsUseOneDeadline();
                            ReducedMotionUsesWholeSeconds();
                            RejectThreeLinesBeforeAdmittingAClock();
                            ArtworkAndRebind();
                            FakeRebindKeepsStatusInView();
                            NoManagedAllocations();
                        }

                        private static void CooldownAndPrecedence()
                        {
                            using var life = new FakeModLifetime();
                            var input = Input(life);
                            var clock = new Clock();
                            var deadline = new UiCooldown(10d, 10d);
                            var slot = new UiActionSlot("activate", "Relay point", detail: "Spare", emphasis: true,
                                cooldown: deadline, charges: new UiCharges(1, 4));
                            var view = new SlotProbe(slot, input, clock);
                            Check(view.Status.Text == "10 s" && view.Fill.color == Raised && view.Outline.Stroke == 3f &&
                                !view.Sweep.Active && view.StatusCue.enabled == false && MarkCount(view, "Charge fill") == 0,
                                "A running cooldown wins over charges and detail with a raised emphasis ring.");
                            clock.Now = 6.25d;
                            view.Tick();
                            var backing = view.Graphic("Action label backing");
                            var keyPlate = view.Host.Go.Part("Key cap").Part("Key cap fill").GetComponent<Image>();
                            Check(view.Status.Text == "4 s" && view.Sweep.Active && Near(view.Sweep.Rect.Width, 80d) &&
                                view.Sweep.Part("Action sweep").GetComponent<Image>().color == Sunken &&
                                view.Sweep.Part("Action sweep").GetComponent<Image>().rectTransform.Width == 128d,
                                "Cooldown readout rounds up and clips the complete rounded sweep to elapsed width.");
                            Check(backing.enabled && backing.color == Raised && backing.color.a == 1f &&
                                view.Graphic("Action status backing").color == Raised && keyPlate.color == Sunken && keyPlate.color.a == 1f &&
                                view.Label.color == ColorOf(Hud.GetPair(UiPaintRole.Raised).On) &&
                                view.Sweep.Rect.Width > backing.rectTransform.X &&
                                view.Sweep.Rect.Width < backing.rectTransform.Right,
                                "Opaque key and label paint protect readable ink from both sides of the moving sweep.");
                            Check(view.Host.Go.AllGraphics().All(item => !item.raycastTarget),
                                "A native slot is presentation only and has no hit graphics.");
                            clock.Now = 10d;
                            view.Tick();
                            Check(!view.Sweep.Active && view.Status.Text == "1 / 4" && MarkCount(view, "Charge fill") == 1,
                                "At expiry the native status changes from cooldown to the authored charges.");

                            var lockedSource = new UiActionSlot("activate", "Relay", detail: "Spare", emphasis: true,
                                cooldown: deadline, charges: new UiCharges(1, 4), unavailableReason: "Later");
                            clock.Now = 4;
                            var locked = new SlotProbe(lockedSource, input, clock);
                            Check(locked.Status.Text == "Later" && locked.StatusCue.enabled && locked.StatusCue.sprite?.Id == "Lock" &&
                                locked.Fill.color == Sunken && locked.Label.color == ColorOf(Hud.ResolveForeground(UiTone.Muted, UiPaintRole.Sunken)) &&
                                locked.Label.color.a == 1f && !locked.Sweep.Active && MarkCount(locked, "Charge boundary") == 0 &&
                                !locked.Graphic("Action shadow").enabled && locked.Outline.Stroke != 3f,
                                "Unavailable uses an unfaded muted Sunken treatment with Lock and suppresses all timers and charges.");
                            var ready = new SlotProbe(new UiActionSlot("activate", "Relay", detail: "Spare", cooldown: deadline), input, clock);
                            clock.Now = 10d;
                            ready.Tick();
                            Check(ready.Status.Text == "Spare" && ready.Fill.color == Surface && !ready.Sweep.Active,
                                "An expired cooldown reveals ready detail without a replacement tree.");
                        }

                        private static void ChargesAndPipsUseOneDeadline()
                        {
                            using var life = new FakeModLifetime();
                            var input = Input(life);
                            var clock = new Clock { Now = 2d };
                            var shared = new UiCooldown(10d, 10d);
                            var slot = new UiActionSlot("activate", "Supply", charges: new UiCharges(1, 4, shared));
                            var pips = UiPips.WithRefill(4, 1, "Cells", shared);
                            UiClockBinding.Bind(new UiColumn(slot, pips), clock.Epoch);
                            var identity = shared.ClockIdentity;
                            var action = new SlotProbe(slot, input, clock);
                            var track = new PipProbe(pips, clock);
                            var mark = Mark(action, "Charge fill", 1);
                            var dot = Mark(action, "Charge boundary", 1);
                            var clip = track.Host.Go.Part("Pip fill clip", 1);
                            var pip = clip.Part("Pip fill").GetComponent<TopiaForgeRoundedImage>();
                            Check(action.Status.Text == "1 / 4" && mark.enabled && dot.Radius > 0f && Near(mark.rectTransform.Width, dot.rectTransform.Width * .2d) &&
                                track.Label.Text == "Cells: 1 / 4 (20% refilling)" && clip.GetComponent<RectMask2D>() != null &&
                                clip.Rect.Width < pip.rectTransform.Width && Near(clip.Rect.Width, pip.rectTransform.Width * .2d),
                                "Refilling charges and pips paint only the next mark with the shared deadline, using a real pip mask.");
                            var writes = track.Label.Writes;
                            var initial = clip.Rect.Width;
                            clock.Now = 2.25d;
                            action.Tick();
                            track.Tick();
                            Check(mark.rectTransform.Width > dot.rectTransform.Width * .2d && clip.Rect.Width > initial && track.Label.Writes == writes,
                                "Pip fill follows active frames while the percentage text stays stable inside the elapsed second.");
                            clock.Now = 3d;
                            action.Tick();
                            track.Tick();
                            Check(track.Label.Text == "Cells: 1 / 4 (30% refilling)",
                                "The real refilling-pip readout updates at the next whole second.");
                            clock.Now = 10d;
                            action.Tick();
                            track.Tick();
                            clock.Now = 14d;
                            action.Tick();
                            track.Tick();
                            Check(action.Status.Text == "2 / 4" && track.Label.Text == "Cells: 2 / 4" &&
                                MarkCount(action, "Charge fill") == 2 && track.Host.Go.Parts("Pip fill clip").Count(child => child.Active) == 2 &&
                                slot.Charges!.Available == 1 && pips.Filled == 1 && ReferenceEquals(shared.ClockIdentity, identity),
                                "Expiry paints exactly one arriving charge and pip without mutating the authored counts or epoch binding.");

                            var old = new UiCooldown(100d, 100d);
                            var oldSlot = new UiActionSlot("activate", "Supply", cooldown: old, charges: new UiCharges(1, 4, old));
                            var oldPips = UiPips.WithRefill(4, 1, "Cells", old);
                            clock.Now = 20d;
                            var retained = new SlotProbe(oldSlot, input, clock);
                            var retainedTrack = new PipProbe(oldPips, clock);
                            var bound = old.ClockIdentity;
                            clock.Epoch = "next";
                            clock.Now = 0;
                            retained.Tick();
                            retainedTrack.Tick();
                            Check(retained.Status.Text == "2 / 4" && retainedTrack.Label.Text == "Cells: 2 / 4" &&
                                !retained.Sweep.Active && ReferenceEquals(old.ClockIdentity, bound),
                                "A retained clock-epoch change clears the old cooldown and shows one arriving refill.");
                            try { UiClockBinding.Check(new UiColumn(oldSlot, oldPips), clock.Epoch); }
                            catch (ArgumentException) { return; }
                            throw new InvalidOperationException("An old bound deadline cannot enter a newly admitted tree.");
                        }

                        private static void ReducedMotionUsesWholeSeconds()
                        {
                            using var life = new FakeModLifetime();
                            var input = Input(life);
                            var clock = new Clock { Now = 2.1d };
                            var deadline = new UiCooldown(10d, 10d);
                            var running = new SlotProbe(new UiActionSlot("activate", "Relay", cooldown: deadline), input, clock, reduced: true);
                            var charges = new SlotProbe(new UiActionSlot("activate", "Relay", charges: new UiCharges(1, 10, deadline)),
                                input, clock, reduced: true);
                            var pips = new PipProbe(UiPips.WithRefill(4, 1, "Cells", deadline), clock, reduced: true);
                            var segment = Mark(charges, "Charge fill", 1);
                            Check(Mark(charges, "Charge boundary", 1).Radius == 0f, "Above nine charges render as segments.");
                            var clip = pips.Host.Go.Part("Pip fill clip", 1);
                            var sweep = running.Sweep.Rect.Width;
                            var painted = segment.rectTransform.Width;
                            var pip = clip.Rect.Width;
                            clock.Now = 2.8;
                            running.Tick();
                            charges.Tick();
                            pips.Tick();
                            Check(running.Sweep.Rect.Width == sweep && segment.rectTransform.Width == painted && clip.Rect.Width == pip &&
                                running.Status.Text == "8 s", "Reduced motion holds all three native fills across subsecond frame changes.");
                            clock.Now = 3d;
                            running.Tick();
                            charges.Tick();
                            pips.Tick();
                            Check(running.Sweep.Rect.Width > sweep && segment.rectTransform.Width > painted && clip.Rect.Width > pip &&
                                running.Status.Text == "7 s", "Reduced motion continues current sweep, charge and pip paint at whole seconds.");
                        }

                        private static void RejectThreeLinesBeforeAdmittingAClock()
                        {
                            using var life = new FakeModLifetime();
                            var clock = new Clock();
                            var waiting = new UiCooldown(10d, 10d);
                            var tooTall = new UiActionSlot("activate", "One\nTwo\nThree", cooldown: waiting);
                            try { _ = new SlotProbe(tooTall, Input(life), clock); }
                            catch (ArgumentException error) when (error.Message.Contains("more than two readable lines", StringComparison.Ordinal))
                            {
                                Check(waiting.ClockIdentity == null, "A non-fitting native label does not bind its pending clock.");
                                return;
                            }
                            throw new InvalidOperationException("The action renderer must reject a three-line authored label before painting.");
                        }
                    }
                }
            }
            """;
    }
}
