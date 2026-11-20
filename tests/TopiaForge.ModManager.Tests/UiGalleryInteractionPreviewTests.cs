using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiGalleryInteractionPreviewTests
    {
        private const string Option = "TOPIAFORGE_UI_GALLERY_CAPTURE";

        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            var manager = Path.Combine(root, "src", "TopiaForge.ModManager");
            var gallery = Path.Combine(root, "mods", "TopiaForge.UiGallery");
            var files = new[]
            {
                "AssetNativeWorkTracker.cs", "OwnerModLifetime.cs", "UiGameplayGate.cs", "UiSurfacePresence.cs",
                "UiThemeScope.cs", "UnityMainThreadGuard.cs", "UnityUiService.cs", "UnityUiService.Themes.cs",
                "UnityUiService.Remote.cs", "UnityUiSurface.GalleryCapture.cs", "Multiplayer/Presentation/UiPresentationTheme.cs",
                "UiGalleryRecorder/UiGalleryCaptureFixture.cs", "UiGalleryRecorder/UiGallerySurvivalTheme.cs",
                "UiGalleryRecorder/UnityUiGalleryCaptureDriver.cs", "UiGalleryRecorder/UnityUiGalleryInteractionPreview.cs",
            }.Select(path => Path.Combine(manager, path))
                .Append(Path.Combine(gallery, "Pages", "FoundationPages.cs"))
                .Append(Path.Combine(gallery, "Pages", "FoundationLayoutPages.cs"))
                .Append(Path.Combine(gallery, "Pages", "FoundationGraphPages.cs"));
            var trees = files.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path))
                .Append(CSharpSyntaxTree.ParseText(Boundaries, path: "PreviewBoundaries.cs"))
                .Append(CSharpSyntaxTree.ParseText(Scenario, path: "PreviewScenario.cs")).ToArray();
            CheckAcceptanceBindings(root, trees.Single(tree => Path.GetFileName(tree.FilePath) == "UnityUiGalleryInteractionPreview.cs"));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal)
                    && Path.GetFileName(path) != "TopiaForge.Mods.UnityUi.dll")
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.ModManager", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
            var resource = new ResourceDescription("TopiaForge.UiGallery.survival.json",
                () => File.OpenRead(Path.Combine(gallery, "ui", "themes", "survival.json")), isPublic: false);
            using var image = new MemoryStream();
            var emitted = compilation.Emit(image, manifestResources: new[] { resource });
            if (!emitted.Success)
                throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var bytes = image.ToArray();
            var before = Environment.GetEnvironmentVariable(Option);
            try
            {
                RunProbe("0", false);
                RunProbe("1", true);
            }
            finally { Environment.SetEnvironmentVariable(Option, before); }

            var plugin = PluginSourceSyntax.Read();
            var awake = plugin.Statements("Awake");
            var capturesStartupOption = awake.First().DescendantTokens().Any(token => token.ValueText == "GalleryCaptureEnabled");
            var armsFromStartup = awake.Any(statement => PluginSourceSyntax.CallsOn(statement, "runtime", "StartGalleryInteractionPreview"));
            var retiresOnTeardown = plugin.Statements("BeginPluginTeardown")
                .Any(statement => PluginSourceSyntax.CallsOn(statement, "galleryInteractionPreview", "Dispose"));
            if (!capturesStartupOption || !armsFromStartup || !retiresOnTeardown)
                throw new InvalidOperationException("The loader owns the startup option and preview teardown.");
            Console.WriteLine("UiGalleryInteractionPreviewTests passed for startup, four palettes, capture and caller isolation.");

            void RunProbe(string startOption, bool enabled)
            {
                Environment.SetEnvironmentVariable(Option, startOption);
                var probe = Assembly.Load(bytes).GetType("TopiaForge.ModManager.GalleryInteractionPreviewProbe", throwOnError: true)!;
                probe.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.CreateDelegate<Action<bool>>()(enabled);
            }
        }

        private static void CheckAcceptanceBindings(string root, SyntaxTree preview)
        {
            var previewBinding = preview.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Single(call => call.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "Hotkey");
            var previewKey = ((MemberAccessExpressionSyntax)previewBinding.ArgumentList.Arguments[0].Expression).Name.Identifier.ValueText;
            var acceptance = new[] { "TopiaForge.SdkAcceptanceMod", "TopiaForge.MultiplayerAcceptanceMod" };
            var files = acceptance.SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "tests", project), "*.cs"));
            foreach (var file in files)
            {
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
                var keys = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Where(call => call.Expression is MemberAccessExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax { Identifier.ValueText: "InputBinding" },
                        Name.Identifier.ValueText: "Key",
                    })
                    .Select(call => call.ArgumentList.Arguments.FirstOrDefault()?.Expression)
                    .OfType<LiteralExpressionSyntax>();
                if (keys.Any(key => string.Equals(key.Token.ValueText, previewKey, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The Gallery preview key overlaps an acceptance binding in " + Path.GetFileName(file));
            }
        }
    }
}
