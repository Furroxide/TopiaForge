using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitTrustIsolationTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            VerifySystemRoutes(root);
            var kit = Path.Combine(root, "src", "TopiaForge.Mods.UnityUi");
            var files = new[]
            {
                "UiHost.cs", "UiHost.Accessibility.cs", "UiHost.Lifetime.cs", "TopiaForgeUiOptions.cs", "Runtime/TopiaForgeLayerSlot.cs",
                "Runtime/TopiaForgeLayers.cs", "Runtime/TopiaForgeDismissStack.cs", "Layout/TopiaForgeModalColumn.cs",
                "Widgets/TopiaForgeModal.cs", "Theme/TopiaForgeTheme.cs", "Theme/TopiaForgeResolvedTheme.cs",
                "Theme/TopiaForgeResolvedTheme.Packs.cs",
            };
            var trees = files.Select(relative => Parse(Path.Combine(kit, relative)))
                .Concat(Directory.EnumerateFiles(Path.Combine(kit, "Core"), "*.cs").Select(Parse))
                .Append(CSharpSyntaxTree.ParseText(Boundaries, path: "TrustKitBoundaries.cs"))
                .Append(CSharpSyntaxTree.ParseText(Scenario, path: "TrustKitScenario.cs"));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal) &&
                    Path.GetFileName(path) != "TopiaForge.Mods.UnityUi.dll")
                .Select(path => MetadataReference.CreateFromFile(path));
            // Use the shipped host, theme resolver and modal against a canvas boundary with no Unity player.
            var compilation = CSharpCompilation.Create("TopiaForge.Mods.UnityUi", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emit = compilation.Emit(bytes);
            if (!emit.Success)
                throw new InvalidOperationException(string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var probe = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.Mods.UnityUi.TrustKitScenario", throwOnError: true)!;
            var run = (Action<byte[]>)probe.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.CreateDelegate(typeof(Action<byte[]>));
            run(File.ReadAllBytes(Path.Combine(root, "mods", "TopiaForge.UiGallery", "ui", "themes", "survival.json")));
            Console.WriteLine("K1 trusted modal ordering, palette isolation, live accessibility refresh and dismissal passed against canvas doubles.");
            Console.WriteLine("K1 modal scroll-body cap preserves measured title and actions across viewport changes.");
        }

        private static void VerifySystemRoutes(string root)
        {
            const string manager = "src/TopiaForge.ModManager/";
            AssertKitModal(manager + "UnityUiService.cs", "ShowModal");
            AssertKitModal(manager + "Overlay/Multiplayer/ConnectingOverlay.cs", "Open");
            if (!Calls(FindMethod(manager + "Overlay/Multiplayer/MultiplayerVoiceView.cs", "AskConsent"), "MultiplayerPromptModals.Consent").Any())
                throw new InvalidOperationException("Voice consent must use the first-party consent factory.");
            var prompt = FindMethod(manager + "Overlay/Multiplayer/MultiplayerSessionsUi.cs", "Show");
            AssertKitModal(manager + "Overlay/Multiplayer/MultiplayerSessionsUi.cs", "Show");
            const string modalFactory = manager + "Overlay/Multiplayer/MultiplayerPromptModals.cs";
            var destructive = FindMethod(modalFactory, "Destructive", loader: true);
            if (!Calls(prompt, "MultiplayerPromptModals.Destructive").Any() ||
                !Calls(destructive, "host.Modal.Destructive").Any(call => call.ArgumentList.Arguments.Any(argument =>
                argument.NameColon?.Name.Identifier.ValueText == "trust" &&
                argument.Expression.ToString() == "prompt.Kind == ScreenPromptKind.TrustIdentity")))
                throw new InvalidOperationException("Only a server identity decision moves a destructive prompt into the trust band.");
            if (!Calls(prompt, "MultiplayerPromptModals.Consent").Any(call => call.Parent is ConditionalExpressionSyntax choice &&
                choice.WhenTrue == call && choice.Condition.ToString() == "prompt.Kind is ScreenPromptKind.Disclosure or ScreenPromptKind.DirectConnection"))
                throw new InvalidOperationException("Address disclosure and direct-connection consent must use the kit-owned trust band.");
            if (!Calls(FindMethod(modalFactory, "Consent", loader: true), "host.Modal.TrustCustom").Any() ||
                !Calls(FindMethod(modalFactory, "Consent"), "host.Modal.Custom").Any() ||
                Calls(FindMethod(modalFactory, "Destructive"), "host.Modal.Destructive").Any(call =>
                    call.ArgumentList.Arguments.Any(argument => argument.NameColon?.Name.Identifier.ValueText == "trust")))
                throw new InvalidOperationException("Only the loader build can open trusted modals from the shared views.");

            void AssertKitModal(string relative, string methodName)
            {
                var method = FindMethod(relative, methodName);
                if (!Calls(method, "host.Modal.Custom").Any(call => call.ArgumentList.Arguments.Count >= 2 &&
                        call.ArgumentList.Arguments[1].Expression.ToString() == "TopiaForgeScheme.Paper"))
                    throw new InvalidOperationException(relative + " must route its modal through kit-owned Paper.");
            }

            MethodDeclarationSyntax FindMethod(string relative, string name, bool loader = false)
            {
                var path = Path.Combine(root, relative);
                var options = loader ? CSharpParseOptions.Default.WithPreprocessorSymbols("TOPIAFORGE_LOADER_TRUST_DIALOGS") : null;
                return CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path).GetRoot()
                    .DescendantNodes().OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == name);
            }
        }

        private static SyntaxTree Parse(string path) => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path);

        private static System.Collections.Generic.IEnumerable<InvocationExpressionSyntax> Calls(MethodDeclarationSyntax method, string name) =>
            method.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => call.Expression.ToString() == name);

        private const string Scenario = """
            using System;
            using TopiaForge.Mods;
            using UnityEngine;
            using UnityEngine.UI;

            namespace TopiaForge.Mods.UnityUi
            {
                public static class TrustKitScenario
                {
                    public static void Run(byte[] packageDefinition)
                    {
                        var definition = UiThemePackParser.Parse(packageDefinition).Value!;
                        using var host = new UiHost(new TopiaForgeUiOptions { OwnerId = "manager" });
                        using var owner = new UiHost(new TopiaForgeUiOptions { OwnerId = "package" });
                        var mirror = host.Layer("mirror", TopiaForgeLayerBand.Hud, TopiaForgeScheme.Paper, interactive: false);
                        var modWindow = owner.Layer("tool", TopiaForgeLayerBand.Window, TopiaForgeScheme.Paper, interactive: true);
                        owner.SetSurfaceTheme(modWindow.Go, definition);
                        var kit = host.Theme(TopiaForgeScheme.Paper);
                        Require(owner.ThemeFor(modWindow).Surface != kit.Surface, "The adjacent authored pack has visibly different paint.");

                        var answered = 0;
                        var cancelled = 0;
                        var trust = host.Modal.Destructive("Trust this server?", "Check the server identity.", "TRUST",
                            () => answered++, () => cancelled++, "CANCEL", plainText: true, persistent: true, trust: true);
                        var confirmation = owner.Modal.Custom("Confirm", TopiaForgeScheme.Paper, 520f, persistent: true);
                        confirmation.Show();
                        var toast = host.Layer("system toast", TopiaForgeLayerBand.Toast, TopiaForgeScheme.Hud, interactive: false);
                        var debug = owner.Layer("diagnostics", TopiaForgeLayerBand.Debug, TopiaForgeScheme.Hud, interactive: false);
                        var trustOrder = Order(trust.CanvasRoot);
                        Require(Order(confirmation.CanvasRoot) < Order(toast.Go) &&
                            Order(toast.Go) < Order(debug.Go) && Order(debug.Go) < trustOrder,
                            "First-party trust draws over the system toast and ordinary mod modal bands.");
                        Require(ReferenceEquals(TopiaForgeDismissStack.Top(), trust) &&
                            ReferenceEquals(TopiaForgeDismissStack.FocusRoot, trust.CanvasRoot),
                            "The later ordinary modal cannot take Back or navigation from the trust prompt.");
                        Require(!Enum.IsDefined(typeof(TopiaForgeLayerBand), TopiaForgeLayerBands.TrustBand),
                            "The public layer enum does not include the private trust band.");
                        RejectBand(() => TopiaForgeLayers.Allocate(TopiaForgeLayerBands.TrustBand, "package"));
                        RejectBand(() => owner.Layer("package", TopiaForgeLayerBands.TrustBand, TopiaForgeScheme.Paper, interactive: true));
                        Require(Chrome(trust) == Expected(kit) && Chrome(confirmation) == Expected(kit),
                            "A system trust dialog and a mod's existing confirmation use the kit Paper palette.");
                        host.SetSurfaceTheme(mirror.Go, definition);
                        Require(host.ThemeFor(mirror).Surface != kit.Surface &&
                            Chrome(trust) == Expected(kit) && ReferenceEquals(host.ThemeFor(trust.Content).Descriptor, UiThemePackParser.Default),
                            "Applying an admitted theme to another surface in the same host leaves trust chrome and content kit-owned.");

                        owner.SetAccessibilityProfile(new TopiaForgeAccessibilityProfile(highContrast: true));
                        Require(Chrome(trust) == Expected(kit) && Chrome(confirmation) == Expected(owner.Theme(TopiaForgeScheme.Paper)),
                            "A host-local contrast change repaints its open confirmation and leaves the system trust palette alone.");
                        TopiaForgeTheme.HighContrast = true;
                        TopiaForgeTheme.UiScale = 2f;
                        TopiaForgeTheme.ReducedMotion = true;
                        var contrast = host.Theme(TopiaForgeScheme.Paper);
                        Require(contrast.HighContrast && contrast.Backdrop != kit.Backdrop,
                            "The kit distinguishes standard and high-contrast trust chrome.");
                        Require(Chrome(trust) == Expected(contrast) && ReferenceEquals(host.ThemeFor(trust.Content).Descriptor, UiThemePackParser.Default) &&
                            trust.CanvasRoot.GetComponent<CanvasScaler>()!.referenceResolution ==
                                new Vector2(TopiaForgeTokens.ReferenceWidth / 2f, TopiaForgeTokens.ReferenceHeight / 2f) && host.EffectiveReducedMotion,
                            "An open system trust modal follows local contrast, scale and reduced motion in place.");
                        Require(trust.CanvasRoot.activeSelf && ReferenceEquals(TopiaForgeDismissStack.Top(), trust) && answered == 0 && cancelled == 0,
                            "A palette refresh preserves the open dialog and its dismissal ownership.");
                        trust.Close();
                        trust.Close();
                        Require(ReferenceEquals(TopiaForgeDismissStack.Top(), confirmation) && answered == 0 && cancelled == 1,
                            "Closing after a theme refresh cancels the trust action exactly once.");
                        var consent = host.Modal.TrustCustom("Share connection details?", 560f, persistent: true);
                        consent.Show();
                        Require(Order(consent.CanvasRoot) == trustOrder && ReferenceEquals(TopiaForgeDismissStack.Top(), consent),
                            "A consent prompt reuses the released trust slot and takes navigation.");
                        consent.Close();
                        confirmation.Close();
                        TopiaForgeTheme.HighContrast = false;
                        TopiaForgeTheme.UiScale = 1f;
                        TopiaForgeTheme.ReducedMotion = false;
                        CheckViewportCap(host);
                    }

                    private static void CheckViewportCap(UiHost host)
                    {
                        var modal = host.Modal.Custom(string.Empty, TopiaForgeScheme.Paper, 680f, persistent: true);
                        const float title = 45f;
                        const float actions = 40f;
                        const float preferredBody = 456f;
                        modal.Content.CreateChildGameObject("Title").AddComponent<LayoutElement>().preferredHeight = title;
                        var scroll = modal.Content.CreateChildGameObject("Scroll");
                        scroll.AddComponent<ScrollRect>();
                        var requested = scroll.AddComponent<LayoutElement>();
                        requested.minHeight = preferredBody;
                        requested.preferredHeight = preferredBody;
                        modal.Content.CreateChildGameObject("Actions").AddComponent<LayoutElement>().preferredHeight = actions;
                        var canvas = (RectTransform)modal.CanvasRoot.transform;
                        canvas.rect = new Rect(1920f, 1080f);
                        modal.Show();
                        var layout = modal.Content.Go.GetComponent<TopiaForgeModalColumn>()!;
                        layout.CalculateLayoutInputHorizontal();
                        layout.CalculateLayoutInputVertical();
                        var cap = scroll.GetComponent<TopiaForgeModalScrollLimit>()!;
                        Require(cap.preferredHeight == preferredBody, "The modal gives the fixed scroll body its authored height when it fits.");
                        canvas.rect = new Rect(960f, 540f);
                        layout.ViewportChanged();
                        Require(layout.NeedsLayout, "A viewport resize dirties the existing modal layout.");
                        layout.CalculateLayoutInputHorizontal();
                        layout.CalculateLayoutInputVertical();
                        var overhead = title + actions + 2f * (int)TopiaForgeGap.Xl + 2f * (int)TopiaForgeGap.Md +
                            2f * TopiaForgeTokens.SafeMargin;
                        Require(cap.preferredHeight == 540f - overhead && cap.minHeight == cap.preferredHeight &&
                            requested.preferredHeight == preferredBody && modal.CanvasRoot.activeSelf,
                            "The resized modal caps its retained scroll body while preserving the title and full action row.");
                        canvas.rect = new Rect(1920f, 1080f);
                        layout.ViewportChanged();
                        layout.CalculateLayoutInputHorizontal();
                        layout.CalculateLayoutInputVertical();
                        Require(cap.preferredHeight == preferredBody, "The retained modal restores the authored body height as space returns.");
                        modal.Close();
                    }

                    private static (Color, Color, Color) Chrome(TopiaForgeModalInstance modal)
                    {
                        var panel = modal.Content.Go.transform;
                        return (modal.CanvasRoot.GetComponent<Image>()!.color, panel.Find("Fill").gameObject.GetComponent<Image>()!.color,
                            panel.Find("Ring").gameObject.GetComponent<Image>()!.color);
                    }

                    private static (Color, Color, Color) Expected(TopiaForgeResolvedTheme theme) =>
                        (theme.Backdrop, theme.Surface, theme.OutlineStrong);

                    private static int Order(GameObject canvas) => canvas.GetComponent<Canvas>()!.sortingOrder;

                    private static void RejectBand(Action create)
                    {
                        try { create(); }
                        catch (ArgumentOutOfRangeException) { return; }
                        throw new InvalidOperationException("Public allocation cannot claim the reserved trust band.");
                    }

                    private static void Require(bool condition, string message)
                    {
                        if (!condition) throw new InvalidOperationException(message);
                    }
                }
            }
            """;
    }
}
