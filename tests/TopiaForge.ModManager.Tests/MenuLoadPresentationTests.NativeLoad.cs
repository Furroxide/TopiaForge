using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class MenuLoadPresentationTests
    {
        private static Type nativeAdapter = null!;
        private static Type replyAdapter = null!;
        private static void PrepareActualNativeLoad()
        {
            var source = File.ReadAllText(Path.Combine(Program.FindRepoRoot(), "src/TopiaForge.ModManager/ModRuntime.Sessions.cs"));
            var method = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(item => item.Identifier.ValueText == "LoadSessionMainMenuAsync").ToFullString();
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("Unity", StringComparison.Ordinal))
                .Select(path => MetadataReference.CreateFromFile(path));
            var replySource = File.ReadAllText(Path.Combine(Program.FindRepoRoot(), "src/TopiaForge.ModManager/TopiaForgeModManagerPlugin.Gamemodes.cs"));
            var reply = CSharpSyntaxTree.ParseText(replySource).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(item => item.Identifier.ValueText == "ReturnToMainMenu").ToFullString();
            var presentation = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(Program.FindRepoRoot(), "src/TopiaForge.ModManager/MenuLoadPresentation.cs"))).GetRoot();
            var messages = string.Join("\n", presentation.DescendantNodes().OfType<FieldDeclarationSyntax>()
                .Where(field => field.Modifiers.Any(token => token.IsKind(SyntaxKind.ConstKeyword))).Select(field => field.ToFullString()));
            messages += presentation.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(item => item.Identifier.ValueText == "TerminalText").ToFullString();
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager",
                new[] { CSharpSyntaxTree.ParseText(NativeBoundary.Replace("/* actual menu load */", method)),
                    CSharpSyntaxTree.ParseText(ReplyBoundary.Replace("/* actual reply */", reply).Replace("/* actual messages */", messages)) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            if (!emitted.Success) throw new InvalidOperationException("Menu load extraction compile failure (not a behavioral red):\n"
                + string.Join("\n", emitted.Diagnostics.Where(item => item.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)));
            var assembly = Assembly.Load(bytes.ToArray());
            nativeAdapter = assembly.GetType("TopiaForge.ModManager.MenuNativeProbe", true)!;
            replyAdapter = assembly.GetType("TopiaForge.ModManager.MenuReplyProbe", true)!;
        }

        private static Func<IInternalSceneTransitionService, CancellationToken, Task<OperationResult<bool>>> ActualNativeLoad(
            Func<SceneLoadRequest, IInternalNativeSceneCompletion, NativeSceneDispatchStatus> dispatch)
        {
            var instance = Activator.CreateInstance(nativeAdapter, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { dispatch }, null)!;
            return (Func<IInternalSceneTransitionService, CancellationToken, Task<OperationResult<bool>>>)nativeAdapter
                .GetMethod("Load", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(
                    typeof(Func<IInternalSceneTransitionService, CancellationToken, Task<OperationResult<bool>>>), instance);
        }

        private static Task<(bool Ok, string Message)> ActualReply(Task<OperationResult<bool>> result, bool ready = true)
        {
            var instance = Activator.CreateInstance(replyAdapter, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { result, ready }, null)!;
            return ((Func<Task<(bool Ok, string Message)>>)replyAdapter.GetMethod("ReturnToMainMenu")!
                .CreateDelegate(typeof(Func<Task<(bool Ok, string Message)>>), instance))();
        }

        private const string ReplyBoundary = """
using System;
using System.Threading.Tasks;
using TopiaForge.Mods;
namespace TopiaForge.ModManager
{
    internal static class MenuLoadPresentation { /* actual messages */ }
    internal sealed class MenuReplyProbe
    {
        private readonly GateStub launchDiscovery = new GateStub();
        private readonly RuntimeStub runtime;
        private readonly bool ready;
        internal MenuReplyProbe(Task<OperationResult<bool>> result, bool ready) { runtime = new RuntimeStub(result); this.ready = ready; }
        private void CancelPendingStartup() { }
        /* actual reply */
    }
    internal sealed class GateStub
    {
        internal Task<T> ExplicitAsync<T>(Func<Task<T>> action) => action();
    }
    internal sealed class RuntimeStub
    {
        internal readonly SessionStub Sessions;
        internal RuntimeStub(Task<OperationResult<bool>> result) { Sessions = new SessionStub(result); }
    }
    internal sealed class SessionStub
    {
        private readonly Task<OperationResult<bool>> result;
        internal SessionStub(Task<OperationResult<bool>> result) { this.result = result; }
        internal Task<OperationResult<bool>> ReturnToMainMenuAsync() => result;
    }
}
""";

        private const string NativeBoundary = """
using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
namespace TopiaForge.ModManager
{
    internal sealed class MenuNativeProbe
    {
        private readonly NativeHostStub? nativeHost;
        internal MenuNativeProbe(Func<SceneLoadRequest, IInternalNativeSceneCompletion, NativeSceneDispatchStatus> dispatch)
        { nativeHost = new NativeHostStub(dispatch); }
        internal Task<OperationResult<bool>> Load(IInternalSceneTransitionService transitions, CancellationToken token) =>
            LoadSessionMainMenuAsync(transitions, token);
        /* actual menu load */
    }
    internal sealed class NativeHostStub
    {
        internal readonly SceneStub Scenes;
        internal NativeHostStub(Func<SceneLoadRequest, IInternalNativeSceneCompletion, NativeSceneDispatchStatus> dispatch)
        { Scenes = new SceneStub(dispatch); }
    }
    internal sealed class SceneStub
    {
        private readonly Func<SceneLoadRequest, IInternalNativeSceneCompletion, NativeSceneDispatchStatus> dispatch;
        internal SceneStub(Func<SceneLoadRequest, IInternalNativeSceneCompletion, NativeSceneDispatchStatus> dispatch)
        { this.dispatch = dispatch; }
        internal NativeSceneDispatchStatus DispatchLoad(SceneLoadRequest request, IInternalNativeSceneCompletion completion) => dispatch(request, completion);
    }
}
""";
    }
}
