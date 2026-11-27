using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class MenuLoadPresentationTests
    {
        private static void ActualOverlayDisposal()
        {
            string Method(string path, string name) => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(Program.FindRepoRoot(), path))).GetRoot()
                .DescendantNodes().OfType<MethodDeclarationSyntax>().Single(item => item.Identifier.ValueText == name).ToFullString();
            var source = DisposalBoundary.Replace("/* actual dispose */", Method("src/TopiaForge.ModManager/Overlay/ManagerOverlay.cs", "Dispose"))
                .Replace("/* actual hidden */", Method("src/TopiaForge.ModManager/Overlay/GamemodesTab.cs", "OnHidden"));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("Unity", StringComparison.Ordinal))
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("MenuDisposalProbe", new[] { CSharpSyntaxTree.ParseText(source) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success) throw new InvalidOperationException("Menu disposal extraction compile failure (not a behavioral red):\n"
                + string.Join("\n", emitted.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.ModManager.DisposalProbe", true)!;
            ((Action)probe.GetMethod("Run")!.CreateDelegate(typeof(Action)))();
        }

        private const string DisposalBoundary = """
using System;
namespace TopiaForge.ModManager
{
    internal interface IManagerTab { }
    internal sealed class FakeBinding { internal int Resets; internal void Reset() { Resets++; } }
    internal sealed class GamemodesTab : IManagerTab
    {
        private object? plugin = new object(), menuNotice = new object(), menuButton = new object(), currentBuild = new object();
        private readonly FakeBinding menuBinding = new FakeBinding();
        internal bool Cleared => plugin == null && menuNotice == null && menuButton == null && currentBuild == null && menuBinding.Resets == 1;
        /* actual hidden */
    }
    internal sealed class OtherTab : IManagerTab { internal int Hidden; public void OnHidden() { Hidden++; } }
    internal sealed class Guard
    {
        internal int Calls;
        internal void Run(string stage, Action action) { Calls++; action(); }
    }
    internal sealed class Counter
    {
        internal int Disposed, Closed, Hidden, Stopped, Released;
        internal void Dispose() { Disposed++; }
        internal void Close() { Closed++; }
        internal void OnHidden() { Hidden++; }
        internal void Stop() { Stopped++; }
        internal void Release() { Released++; }
    }
    internal sealed class FakeHost
    {
        private readonly GamemodesTab menu;
        internal int Disposals; internal bool ClearedBeforeHost;
        internal FakeHost(GamemodesTab menu) { this.menu = menu; }
        internal void Dispose() { ClearedBeforeHost = menu.Cleared; Disposals++; }
    }
    internal static class MultiplayerOverlayFrame
    { internal const string VoiceTeardownStage = "voice", TeardownStage = "teardown"; }
    internal static class TopiaForgeDismissStack
    { internal static int Removed; internal static void Remove(object value) { Removed++; } }
    internal sealed class OverlayProbe
    {
        internal readonly GamemodesTab Menu = new GamemodesTab();
        internal readonly OtherTab Other = new OtherTab();
        private readonly IManagerTab[] tabs;
        internal readonly Guard multiplayerGuard = new Guard();
        internal readonly Counter nametags = new Counter(), multiplayerUi = new Counter(), inSession = new Counter(), multiplayer = new Counter(), cursorLease = new Counter();
        private FakeHost? host;
        private object? root = new object();
        internal readonly FakeHost SavedHost;
        internal int VoiceStopped;
        internal OverlayProbe() { tabs = new IManagerTab[] { Menu, Other }; host = SavedHost = new FakeHost(Menu); }
        private void StopVoiceChat() { VoiceStopped++; }
        internal bool Closed => host == null && root == null;
        /* actual dispose */
    }
    public static class DisposalProbe
    {
        public static void Run()
        {
            var overlay = new OverlayProbe();
            overlay.Dispose();
            Check(overlay.Menu.Cleared && overlay.SavedHost.ClearedBeforeHost,
                "actual overlay disposal retires menu view before UI host disposal");
            Check(overlay.Other.Hidden == 0 && overlay.VoiceStopped == 1 && overlay.multiplayerGuard.Calls == 2
                && overlay.nametags.Disposed == 1 && overlay.multiplayerUi.Closed == 1 && overlay.inSession.Disposed == 1
                && overlay.multiplayer.Hidden == 1 && overlay.multiplayer.Stopped == 1 && overlay.cursorLease.Released == 1
                && overlay.SavedHost.Disposals == 1 && TopiaForgeDismissStack.Removed == 1 && overlay.Closed,
                "menu disposal hook preserves all original teardown calls without broadcasting new hidden notifications");
        }
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Menu presentation: " + message); }
    }
}
""";
    }
}
