using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>Compiles the production scoreboard view and overlay; doubles only engine and widget dispatch.</summary>
    internal static partial class ScoreboardViewContractTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            var kit = "src/TopiaForge.Mods.UnityUi/";
            var paths = new[]
            {
                "src/TopiaForge.ModManager/Overlay/Multiplayer/ScoreboardView.cs",
                "src/TopiaForge.ModManager/Overlay/ScoreboardOverlay.cs",
                "src/TopiaForge.ModManager/Multiplayer/Screens/IScoreboardScreen.cs",
                "src/TopiaForge.ModManager/Multiplayer/Sessions/ScoreboardHold.cs",
                kit + "Runtime/TopiaForgeFocus.cs", kit + "Core/TopiaForgeDismissOrder.cs",
                kit + "Core/TopiaForgeInputClaim.cs", kit + "Core/TopiaForgeInputGate.cs", kit + "Core/TopiaForgeNavigationDriver.cs",
            };
            string Method(string file, string name) => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)))
                .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(x => x.Identifier.ValueText == name).ToString();
            var methods = "using UnityEngine; using UnityEngine.EventSystems; namespace TopiaForge.Mods.UnityUi { public static partial class TopiaForgeInputOwnership { "
                + Method(kit + "Runtime/TopiaForgeInputOwnership.cs", "HoldsSelection") + " } public partial class Widget { "
                + Method(kit + "Widgets/TopiaForgeWidget.cs", "SetSiblingIndex") + " } }";
            var sources = paths.Select(path => File.ReadAllText(Path.Combine(root, path)))
                .Concat(new[] { Contracts, OverlayContracts, Probe, methods });
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var compilation = CSharpCompilation.Create("ScoreboardViewProbe" + Guid.NewGuid().ToString("N"),
                sources.Select(source => CSharpSyntaxTree.ParseText(source)), trusted.Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            That(result.Success, "scoreboard views compile: " + string.Join("; ", result.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));
            var assembly = Assembly.Load(bytes.ToArray());
            try { assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException exception) { throw exception.InnerException!; }
            Console.WriteLine("ScoreboardViewContractTests passed: 64-column paging, row reuse, literal labels, selection retirement, overlay input/lifetime and 10,000 resting frames.");
        }

        private const string Probe = """
            using System;
            using System.Linq;
            using TopiaForge.ModManager;
            using TopiaForge.ModManager.Sessions;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine;
            using UnityEngine.EventSystems;
            public static class Probe
            {
                public static void Run()
                {
                    var errors = new System.Collections.Generic.List<string>();
                    foreach (var test in new Action[] { PagingAndRows, RetiredHeaderReleasesInput, OverlayLifetime })
                    { try { test(); } catch (Exception error) { errors.Add(error.Message); } }
                    if (errors.Count > 0) throw new Exception(string.Join("; ", errors));
                }
                private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
                private static void Reset()
                {
                    Widget.All.Clear(); TopiaForgeDismissStack.Order.Clear();
                    EventSystem.current = new EventSystem(); Application.isFocused = true;
                    TopiaForgeInputOwnership.Typing = false; TopiaForgeGameTextFocus.Current.Typing = false;
                }
                private static TopiaForgeButton Button(string text, int index = 0) => Widget.All.OfType<TopiaForgeButton>().Where(x => x.Text == text).ElementAt(index);
                private static void PagingAndRows()
                {
                    Reset(); var screen = new ScoreboardPresenter();
                    for (var c = 0; c < 64; c++) screen.Titles.Add("<b>Column " + c + "</b>");
                    for (var p = 0; p < 32; p++)
                    {
                        var row = new ScoreboardRow("<b>Sam</b>", false, false) { CanHear = true, CanModerate = true };
                        for (var c = 0; c < 64; c++) row.Values[c] = p + ":" + c;
                        screen.Players.Add(row);
                    }
                    var root = new TopiaForgeContainer(); var view = new ScoreboardView(root, screen);
                    Check(Widget.All.OfType<TopiaForgeLabel>().Where(x => x.Text.Contains("<b>")).All(x => x.Plain), "hostile names and titles use literal labels");
                    for (var page = 0; page < 16; page++)
                    {
                        view.Tick();
                        var sorts = Widget.All.OfType<TopiaForgeButton>().Where(x => x.Text == "SORT").ToArray();
                        for (var i = 0; i < 4; i++) { sorts[i].Click(); Check(screen.LastColumn == page * 4 + i, "every column sort targets its current page"); }
                        Check(Widget.All.OfType<TopiaForgeLabel>().Any(x => x.Text == "31:" + (page * 4 + 3)), "the last row exposes all 64 values");
                        if (page < 15) Button("NEXT").Click();
                    }
                    Check(!Button("NEXT").Enabled, "last page is bounded");
                    var count = Widget.All.Count; var chosen = screen.Players[0]; var pendingClick = Button("KICK"); pendingClick.Focus();
                    var dragging = Widget.All.OfType<TopiaForgeSlider>().First();
                    screen.Players.Reverse(); view.Tick();
                    Check(EventSystem.current.currentSelectedGameObject == null, "re-sort clears selected row action");
                    pendingClick.Click(); Check(ReferenceEquals(screen.LastRow, chosen), "a pointer press spanning re-sort must not kick a different player");
                    dragging.Change(20); Check(ReferenceEquals(screen.LastRow, chosen), "a slider drag spanning re-sort must not change a different player's gain");
                    screen.Players.RemoveAt(0); view.Tick(); Check(Widget.All.Count == count, "removing a row creates no replacement widgets");
                    for (var i = 0; i < 200; i++) view.Tick();
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var i = 0; i < 10000; i++) view.Tick();
                    Check(GC.GetAllocatedBytesForCurrentThread() == before, "production view dispatch allocates zero at rest");
                }
                private static void RetiredHeaderReleasesInput()
                {
                    Reset(); var screen = new ScoreboardPresenter();
                    screen.Titles.AddRange(new[] { "One", "Two", "Three", "Four" });
                    var root = new TopiaForgeContainer(); var view = new ScoreboardView(root, screen);
                    TopiaForgeDismissStack.Order.Push(new Surface(), TopiaForgeInputClaim.NavigationWhileSelected, root.Go);
                    Button("SORT", 3).Focus();
                    screen.Titles.RemoveRange(1, 3); view.Tick();
                    Check(EventSystem.current.currentSelectedGameObject == null, "retiring a selected score column releases its invisible control");
                    Check((TopiaForgeDismissStack.Order.Claims() & TopiaForgeInputClaim.Navigation) == 0, "hidden score header cannot keep gameplay navigation");
                }
                private static void OverlayLifetime()
                {
                    Reset(); var plugin = new TopiaForgeModManagerPlugin(); var sessions = new MultiplayerSessionsPresenter();
                    using var overlay = new ScoreboardOverlay(plugin, sessions, () => plugin.Voice);
                    void Frame(bool held, bool pressed = false) { plugin.Gameplay.Inputs.Key.IsHeld = held; plugin.Gameplay.Inputs.Key.WasPressed = pressed; overlay.Tick(); }
                    void Open() { Frame(false); Frame(true, true); Check(TopiaForgeCursorLease.Active && TopiaForgeDismissStack.Top() == overlay, "fresh Tab owns cursor and Back"); }
                    Open(); Frame(true); Check(TopiaForgeCursorLease.Active, "held Tab stays open");
                    var gate = new TopiaForgeInputGate();
                    Check(gate.Take(1, TopiaForgeInputDevice.Keyboard, 60, TopiaForgeInputRole.Back, 1, 0, 0, 0, TopiaForgeDismissStack.Order.Claims(), 0), "Escape belongs to scoreboard");
                    TopiaForgeDismissStack.Order.TakeOnBack(true, true)!.Dismiss(); Frame(true);
                    Check(!TopiaForgeCursorLease.Active && EventSystem.current.currentSelectedGameObject == null, "Escape clears cursor and selection");
                    Check(gate.Take(1, TopiaForgeInputDevice.Keyboard, 60, TopiaForgeInputRole.Back, 1, 0, 0, 0, TopiaForgeInputClaim.None, 1), "held Escape cannot leak to gameplay");
                    Open(); Frame(false); Check(!TopiaForgeCursorLease.Active, "release closes");
                    foreach (var block in new Action<bool>[] { value => TopiaForgeInputOwnership.Typing = value, value => TopiaForgeGameTextFocus.Current.Typing = value, value => plugin.Voice.PushToTalkSuspended = value })
                    { Open(); block(true); Frame(true); Check(!TopiaForgeCursorLease.Active, "typing or binding capture closes"); block(false); Frame(true); Check(!TopiaForgeCursorLease.Active, "held Tab never reopens after capture"); }
                    Open(); var modal = new Surface(); TopiaForgeDismissStack.Order.Push(modal, TopiaForgeInputClaim.Keyboard, null); Frame(true);
                    Check(!TopiaForgeCursorLease.Active, "another surface closes scoreboard"); TopiaForgeDismissStack.Order.Remove(modal); Frame(true); Check(!TopiaForgeCursorLease.Active, "modal ending never reopens");
                    Open(); UnityEngine.SceneManagement.SceneManager.Handle++; Frame(true); Check(!TopiaForgeCursorLease.Active, "scene transition closes");
                    Frame(true); Check(!TopiaForgeCursorLease.Active, "scene transition requires release");
                    Open(); Application.isFocused = false; Frame(true);
                    Check(!TopiaForgeCursorLease.Active && EventSystem.current.currentSelectedGameObject == null, "focus loss closes scoreboard even before key reset");
                    Frame(false); // A device reset while unfocused is not a physical release in gameplay.
                    Application.isFocused = true; Frame(true, true); Check(!TopiaForgeCursorLease.Active, "returning focus never inherits held Tab after device reset");
                    Open(); UiHost.Last!.Lose(); Check(!TopiaForgeCursorLease.Active, "layer loss releases cursor");
                    Open(); overlay.Dispose(); Check(!TopiaForgeCursorLease.Active && plugin.Gameplay.Inputs.Key.Disposed, "dispose releases input registration and cursor");
                }
                private sealed class Surface : ITopiaForgeDismissable
                { public TopiaForgeLayerBand Band => TopiaForgeLayerBand.Modal; public void Dismiss() { } }
            }
            """;
    }
}
