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
    /// <summary>The production views and focus helper over small UI contracts; actual Unity rendering stays an in-game gate.</summary>
    internal static partial class ChatViewContractTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            var files = new[]
            {
                "src/TopiaForge.ModManager/Overlay/Multiplayer/ChatOverlay.cs",
                "src/TopiaForge.ModManager/Overlay/Multiplayer/ConnectionIndicator.cs",
                "src/TopiaForge.ModManager/Multiplayer/Screens/IInSessionScreen.cs",
                "src/TopiaForge.Mods.UnityUi/Runtime/TopiaForgeFocus.cs"
            };
            var kit = "src/TopiaForge.Mods.UnityUi/";
            var inputFiles = new[] { "Core/TopiaForgeInputClaim.cs", "Core/TopiaForgeInputGate.cs", "Core/TopiaForgeDismissOrder.cs",
                "Core/TopiaForgeNavigationDriver.cs", "Core/TopiaForgeTypingStart.cs", "Runtime/TopiaForgeTextInput.cs" };
            var methods = "namespace TopiaForge.Mods.UnityUi { public partial class Widget { "
                + Method(root, kit + "Widgets/TopiaForgeWidget.cs", "PushDismissable")
                + " } public partial class TopiaForgeInputField { "
                + Method(root, kit + "Widgets/TopiaForgeInputField.cs", "Focus") + " } }";
            var contracts = UiKitInputOwnershipTests.TextInputContract.Replace("internal static bool AnyKeyHeld;",
                "internal static bool AnyKeyHeld => InputProbe.AnyKeyHeld;", StringComparison.Ordinal);
            var trees = files.Concat(inputFiles.Select(file => kit + file))
                .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file))))
                .Concat(new[] { contracts, Contracts, methods, Probe, InputProbe }.Select(source => CSharpSyntaxTree.ParseText(source)));
            var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
            var compilation = CSharpCompilation.Create("ChatViewProbe" + Guid.NewGuid().ToString("N"), trees,
                trusted.Select(path => MetadataReference.CreateFromFile(path)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var result = compilation.Emit(bytes);
            That(result.Success, "production chat views compile against the UI contract: " + string.Join("; ", result.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)));
            var assembly = Assembly.Load(bytes.ToArray());
            try { assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException exception) { throw exception.InnerException!; }
            try { assembly.GetType("InputProbe")!.GetMethod("Run")!.Invoke(null, null); }
            catch (TargetInvocationException exception) { throw exception.InnerException!; }
            Console.WriteLine("ChatViewContractTests passed: 6 input scenarios, close click, leave, scene hide, destruction, selection isolation, and 10,000 idle view frames (0 bytes).");
        }

        // Compile these production methods verbatim too: a hand-written Focus double hid the held-key integration bug.
        private static string Method(string root, string file, string name) => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)))
            .GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == name).ToString();

        private const string Probe = """
            using System;
            using TopiaForge.ModManager.Sessions;
            using TopiaForge.Mods.UnityUi;
            using UnityEngine.EventSystems;
            public static class Probe
            {
                public static void Run()
                {
                    EventSystem.current = new EventSystem();
                    var root = new TopiaForgeContainer();
                    var screen = new Screen();
                    var view = new ChatOverlay(root, screen);
                    var indicator = new ConnectionIndicator(root, screen);
                    screen.Open(); view.Tick(true);
                    TopiaForgeInputField.Last!.Advance(); view.Tick(true);
                    Check(screen.IsTyping && TopiaForgeInputField.Last!.IsFocused, "opening focuses a kit field");
                    Check(TopiaForgeDismissStack.PushedRoot != root.Go && TopiaForgeDismissStack.PushedRoot?.transform.parent == root.Go.transform
                        && TopiaForgeInputField.Last.Go.transform.IsChildOf(TopiaForgeDismissStack.PushedRoot.transform)
                        && TopiaForgeChatLog.Last!.Go.transform.parent == TopiaForgeDismissStack.PushedRoot.transform
                        && !TopiaForgeLabel.Last!.Go.transform.IsChildOf(TopiaForgeDismissStack.PushedRoot.transform),
                        "chat claims its own log and composer subtree, never the parent or connection HUD");
                    TopiaForgeContainer.Click("CLOSE");
                    Check(!screen.IsTyping && EventSystem.current.currentSelectedGameObject == null && !TopiaForgeCursorLease.Active && TopiaForgeDismissStack.Count == 0,
                        "closing by click clears the selected button, cursor and typing");
                    screen.Open(); view.Tick(true); screen.IsAvailable = false; view.Tick(true);
                    Check(!TopiaForgeInputField.Last!.IsFocused && EventSystem.current.currentSelectedGameObject == null && !screen.IsTyping, "leaving releases field focus and typing");
                    screen.IsAvailable = true; screen.Open(); view.Tick(true); view.Tick(false);
                    Check(!screen.IsOpen && !TopiaForgeInputField.Last!.IsFocused && !screen.IsTyping, "scene hiding ends typing");
                    var elsewhere = new UnityEngine.GameObject(); EventSystem.current.SetSelectedGameObject(elsewhere); view.Close();
                    Check(EventSystem.current.currentSelectedGameObject == elsewhere, "closing never clears another surface's selection");
                    screen.Open(); view.Tick(true); view.Lost();
                    Check(!screen.IsTyping && !TopiaForgeCursorLease.Active && TopiaForgeDismissStack.Count == 0, "canvas destruction releases ownership");
                    screen.Open(); view.Tick(true);
                    TopiaForgeInputField.Last!.Advance(); view.Tick(true);
                    Check(screen.IsTyping, "the idle probe keeps the composer focused");
                    for (var index = 0; index < 200; index++) { view.Tick(true); indicator.Tick(true); }
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    for (var index = 0; index < 10000; index++) { view.Tick(true); indicator.Tick(true); }
                    Check(GC.GetAllocatedBytesForCurrentThread() == before, "idle production views allocate zero bytes");
                    view.Dispose();
                    Check(!screen.IsTyping && !TopiaForgeCursorLease.Active, "disposal ends typing");
                }
                private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
                internal sealed class Screen : IInSessionScreen
                {
                    public bool IsAvailable { get; set; } = true;
                    public bool IsOpen { get; private set; }
                    public bool IsTyping { get; private set; }
                    public int Revision => 1;
                    public bool IsReconnecting => false;
                    public string ConnectionText => "Connected";
                    public void Open() => IsOpen = true;
                    public void Close() { IsOpen = false; IsTyping = false; }
                    public void SetTyping(bool value) => IsTyping = IsOpen && value;
                    public bool Submit(string text) { Close(); return true; }
                    public bool TryTakeLine(out ChatLine line) { line = default; return false; }
                }
            }
            """;
    }
}
