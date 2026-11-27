using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiStingerNativeTests
    {
        internal static void Run()
        {
            var root = Program.FindRepoRoot();
            var kit = "src/TopiaForge.Mods.UnityUi/";
            var files = new[]
            {
                "Core/ITopiaForgeDismissable.cs", "Core/TopiaForgeDismissOrder.cs", "Core/TopiaForgeEasing.cs",
                "Core/TopiaForgeInputClaim.cs", "Core/TopiaForgeInputGate.cs", "Core/TopiaForgeLayerBands.cs",
                "Core/TopiaForgeLayerLedger.cs", "Core/TopiaForgeNavigationDriver.cs", "Core/TopiaForgeTokens.cs",
                "Motion/TopiaForgeMotionPresets.cs", "Motion/TopiaForgeTween.cs", "Runtime/TopiaForgeCursor.cs",
                "Runtime/TopiaForgeDismissStack.cs", "Runtime/TopiaForgeFocus.cs", "Runtime/TopiaForgeLayers.cs",
                "Runtime/TopiaForgeOverlayStack.cs", "Runtime/TopiaForgeStingerAnnouncements.cs",
                "Text/TopiaForgeTypography.cs", "Text/TopiaForgeTypographyMetrics.cs",
                "Widgets/TopiaForgeOverlay.cs", "Widgets/TopiaForgeStinger.cs",
            };
            var trees = files.Select(file => kit + file).Concat(new[]
            {
                "src/TopiaForge.Mods.Abstractions/UiStingerQueue.cs",
                "src/TopiaForge.ModManager/UnityUiStingers.cs",
                "src/TopiaForge.ModManager/UnityUiService.Stingers.cs",
                "src/TopiaForge.ModManager/Multiplayer/Presentation/UiStingerDelivery.cs",
                "tests/Shared/AllocationProbe.cs",
            }).Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, file)), path: file))
                .Append(CSharpSyntaxTree.ParseText(UiOverlayNativeTests.Boundaries.Replace("label.ApplyFont();",
                    "label.ApplyStingerTestTypeface();", StringComparison.Ordinal), path: "UnityBoundaries.cs"))
                .Append(CSharpSyntaxTree.ParseText(Boundaries, path: "StingerScopeBoundary.cs"))
                .Append(CSharpSyntaxTree.ParseText(LabelTypeface(root, kit), path: "StingerLabelTypeface.cs"))
                .Append(CSharpSyntaxTree.ParseText(Scenarios, path: "StingerScenarios.cs"))
                .Append(CSharpSyntaxTree.ParseText(FontScenarios, path: "StingerFontScenarios.cs"));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => !Path.GetFileName(path).StartsWith("UnityEngine", StringComparison.Ordinal) &&
                    Path.GetFileName(path) != "TopiaForge.Mods.UnityUi.dll")
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("TopiaForge.Mods.UnityUi", trees, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release,
                    nullableContextOptions: NullableContextOptions.Enable));
            using var bytes = new MemoryStream();
            var emit = compilation.Emit(bytes);
            if (!emit.Success)
                throw new InvalidOperationException(string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            var method = Assembly.Load(bytes.ToArray()).GetType("TopiaForge.ModManager.StingerProbe", true)!
                .GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic)!;
            ((Action)method.CreateDelegate(typeof(Action)))();
        }

        private static string LabelTypeface(string root, string kit)
        {
            var label = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, kit, "Widgets/TopiaForgeLabel.cs"))).GetRoot();
            var field = label.DescendantNodes().OfType<FieldDeclarationSyntax>()
                .Single(item => item.Declaration.Variables.Any(value => value.Identifier.Text == "frozenTypeface"));
            var methods = label.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(item => item.Identifier.Text is "FreezeTypeface" or "ApplyTypeface" or "WriteText");
            return $$"""
                using TMPro;
                using TopiaForge.Mods;
                namespace TopiaForge.Mods.UnityUi
                {
                    public sealed partial class TopiaForgeLabel
                    {
                        private TMP_Text text => Go.GetComponent<TMP_Text>();
                        private TopiaForgeTextStyle style => text.Style;
                        private TopiaForgeResolvedTheme Theme => ResolvedTheme;
                        private string? initialText;
                        private string lastText = "";
                        private bool uppercase;
                        internal void ApplyStingerTestTypeface()
                        {
                            if (initialText == null) lastText = initialText = text.text;
                            ApplyTypeface(Theme);
                        }

                        {{field}}
                        {{string.Join("\n", methods)}}
                    }
                }
                """;
        }

        private const string Scenarios = """"
            using System;
            using System.Linq;
            using TMPro;
            using TopiaForge.Mods;
            using TopiaForge.Mods.Testing;
            using TopiaForge.Mods.UnityUi;
            using TopiaForge.TestSupport;
            using UnityEngine;
            using UnityEngine.UI;

            namespace TopiaForge.ModManager
            {
                internal static partial class StingerProbe
                {
                    internal static void Run()
                    {
                        TwoLinesAndSafePaint();
                        NamedStyleAndThemeFingerprint();
                        QueueAndFade();
                        ObserveVisibleAndPromoted();
                        ThemeLeases();
                        FontFitUsesAuthoredInputs();
                        PendingAndKitOnlyEventsKeepTheirFaces();
                        OwnerStopAndLayerLoss();
                        RemoteScopesAndPreflight();
                        SettledTick();
                        Console.WriteLine("UiStingerNativeTests passed 8 production view, queue and owner scenarios.");
                    }

                    private static void TwoLinesAndSafePaint()
                    {
                        var host = new UiHost { EffectiveUiScale = 2f, EffectiveReducedMotion = true };
                        using var scope = new UnityUiStingers(host, new TestLog());
                        var lower = TopiaForgeLayers.CreateKitCanvas("Overlay", TopiaForgeLayerPriority.Overlay, false, true);
                        var upper = TopiaForgeLayers.CreateCanvas("Trust", TopiaForgeLayerBand.Modal, false, true);
                        var longTitle = new UiStingerRequest(new string('W', 40), glyph: UiGlyph.Star);
                        var longSubtitle = new UiStingerRequest("Title", new string('W', 80));
                        var thirdTitleLine = new UiStingerRequest("One\nTwo\nThree");
                        var thirdSubtitleLine = new UiStingerRequest("Title", "One\nTwo\nThree");
                        foreach (var request in new[] { longTitle, longSubtitle, thirdTitleLine, thirdSubtitleLine })
                            Check(Offer(scope, request).ErrorCode == ModErrorCode.InvalidArgument,
                                "titles and subtitles are rejected if their natural paint needs a third line");
                        Check(TopiaForgeLayers.HeldSlots == 2, "rejected measures release the candidate and its empty canvas");
                        var accepted = new UiStingerRequest("<b>Wave\nClear</b>", "Requisitions\nopen", UiGlyph.Star);
                        Check(Offer(scope, accepted).Succeeded, "both supported fields admit exactly two lines");
                        var layer = host.LastLayer!;
                        var view = Active(layer);
                        var title = view.Find("Title").GetComponent<TMP_Text>();
                        var subtitle = view.Find("Subtitle").GetComponent<TMP_Text>();
                        var glyph = view.Find("Glyph").GetComponent<Image>();
                        var fill = view.Find("Surface").GetComponent<TopiaForgeRoundedImage>();
                        var descriptor = UiThemePackParser.Default;
                        var pair = descriptor.SelectScheme(true, false).GetPair(accepted.Role);
                        Check(view.Rect.sizeDelta.x <= 900 && Near(view.Rect.anchorMin.x, 0.5f) &&
                            Near(view.Rect.anchorMin.y, 0.70f) && title.Style == TopiaForgeTextStyle.Banner &&
                            subtitle.Style == TopiaForgeTextStyle.Title && title.text == accepted.Title && !title.richText &&
                            glyph.sprite != null && glyph.rectTransform.anchoredPosition.x < title.rectTransform.anchoredPosition.x &&
                            title.color == ToColor(pair.On) && subtitle.color == ToColor(pair.On) && fill.color == ToColor(pair.Fill),
                            "the rendered plate is safely centred at 30%, with literal Banner, Title, glyph and its role pair");
                        var lowestPaint = view.Rect.anchorMin.y * 540f - 1.02f * (view.Rect.sizeDelta.y / 2f + 8f);
                        Check(lowestPaint > 270f, "two full lines in both roles and the shadow clear the crosshair throughout entrance");
                        var surface = layer.GetComponent<Canvas>();
                        Check(lower.GetComponent<Canvas>().sortingOrder < surface.sortingOrder &&
                            surface.sortingOrder < upper.GetComponent<Canvas>().sortingOrder &&
                            !layer.GetComponent<GraphicRaycaster>().enabled && TopiaForgeDismissStack.Count == 0,
                            "the stinger is click-through above overlays and below trust with no Back");
                        Check(TopiaForgeStingerAnnouncements.Latest == accepted.Title + ". " + accepted.Subtitle,
                            "the visible announcement is recorded once as plain title and subtitle");
                        scope.Dispose();
                        TopiaForgeLayers.Release(upper);
                        TopiaForgeLayers.Release(lower);
                        TopiaForgeStingerAnnouncements.Reset();
                    }

                    private static void NamedStyleAndThemeFingerprint()
                    {
                        var none = Parse("""{"schema":1,"id":"announcements"}""");
                        var explicitDefault = Parse("""{"schema":1,"id":"announcements","components":{"stinger":{"radius":"card","border":"strong","shadow":"card"}}}""");
                        Check(none.Digest.Span.SequenceEqual(explicitDefault.Digest.Span) &&
                            explicitDefault.MinimumWireVersion == UiWireCodec.ControlsContractVersion,
                            "explicit stinger defaults preserve the effective digest and declare the v3 feature");
                        var descriptor = Parse("""
                            {"schema":1,"id":"announcements","components":{"stinger":{"radius":"medium","border":"thin","shadow":"none"}},
                             "styles":[{"id":"valid","target":"stinger","radius":"small","border":"thin","shadow":"small","fill":"primary"},
                             {"id":"wrong-role","target":"stinger","radius":"small","fill":"warning"},
                             {"id":"wrong-target","target":"badge","radius":"small"}]}
                            """);
                        Check(!none.Digest.Span.SequenceEqual(descriptor.Digest.Span), "component and named style change the canonical digest");
                        var host = new UiHost { EffectiveReducedMotion = true };
                        using var scope = new UnityUiStingers(host, new TestLog());
                        foreach (var name in new[] { "wrong-role", "wrong-target", "absent" })
                            Check(Offer(scope, new UiStingerRequest("Wave clear", style: new UiStyleRef(name)), descriptor).ErrorCode ==
                                ModErrorCode.InvalidArgument, "named styles must target stinger and preserve its role");
                        Check(host.LastLayer == null, "wrong styling allocates no layer");
                        var good = new UiStingerRequest("Wave clear", style: new UiStyleRef("valid"));
                        Check(Offer(scope, good, descriptor).Succeeded, "the matching stinger style admits");
                        var view = Active(host.LastLayer!);
                        Check(view.Find("Boundary").GetComponent<TopiaForgeRoundedImage>().Border == 1 &&
                            view.Find("Shadow").GetComponent<TopiaForgeRoundedImage>().enabled, "the named style paints its border and shadow");
                        TopiaForgeStingerAnnouncements.Reset();
                    }

                    private static void QueueAndFade()
                    {
                        var host = new UiHost();
                        using var scope = new UnityUiStingers(host, new TestLog());
                        Time.unscaledTimeAsDouble = 0;
                        var first = new UiStingerRequest("One", durationSeconds: 1);
                        Check(Offer(scope, first).Succeeded, "active offer succeeds");
                        var layer = host.LastLayer!;
                        var active = Active(layer);
                        Check(Near(active.Rect.localScale.x, 0.92f), "the enter presentation starts at 0.92 scale");
                        TopiaForgeTween.Tick(TopiaForgeTokens.DurationBase / 2);
                        Check(active.Rect.localScale.x > 1f, "the half-enter overshoots with OutBack");
                        TopiaForgeTween.Tick(TopiaForgeTokens.DurationBase / 2);
                        var second = new UiStingerRequest("Two", durationSeconds: 1);
                        Check(Offer(scope, second).Succeeded && Offer(scope, new UiStingerRequest("Three")).Succeeded &&
                            Offer(scope, new UiStingerRequest("Four")).Succeeded,
                            "one visible request has room for exactly three waiting requests");
                        Check(Offer(scope, new UiStingerRequest("Five")).ErrorCode == ModErrorCode.RateLimited,
                            "a fourth waiting announcement returns RateLimited");
                        Time.unscaledTimeAsDouble = 0.6;
                        Check(scope.Show(new UiStingerRequest("One", durationSeconds: 6), null, null, null, false, out var newActive).Succeeded &&
                            scope.Show(second, null, null, null, false, out var newWaiting).Succeeded && !newActive && !newWaiting &&
                            ReferenceEquals(Active(layer), active),
                            "equal active and queued announcements coalesce without an extra start");
                        Time.unscaledTimeAsDouble = 1;
                        scope.Tick();
                        TopiaForgeTween.Tick(TopiaForgeTokens.DurationSlow / 2);
                        Check(ReferenceEquals(Active(layer), active) && active.GetComponent<CanvasGroup>().alpha is > 0f and < 1f,
                            "only the old panel is painted during its slow exit fade");
                        Time.unscaledTimeAsDouble = 1.3;
                        TopiaForgeTween.Tick(TopiaForgeTokens.DurationSlow / 2);
                        Check(!active.activeSelf && Title(Active(layer)) == "Two" && TopiaForgeStingerAnnouncements.Count == 2,
                            "the next panel and announcement begin only after the old panel is retired");
                        Time.unscaledTimeAsDouble = 2.1;
                        scope.Tick();
                        Check(Active(layer).GetComponent<CanvasGroup>().alpha == 1f,
                            "the next panel starts a complete duration after the prior exit");
                        Time.unscaledTimeAsDouble = 2.31;
                        scope.Tick();
                        host.EffectiveReducedMotion = true;
                        TopiaForgeTween.Tick(0);
                        Check(Title(Active(layer)) == "Three" && Near(Active(layer).Rect.localScale.x, 1f) &&
                            TopiaForgeTween.ActiveCount == 0, "live reduced motion settles the fade and suppresses the next entrance");
                        TopiaForgeStingerAnnouncements.Reset();
                    }

                    private static void ObserveVisibleAndPromoted()
                    {
                        var host = new UiHost { EffectiveReducedMotion = true };
                        var active = new UiStingerRequest("Active", durationSeconds: 1);
                        var waiting = new UiStingerRequest("Waiting", durationSeconds: 1);
                        var captures = new System.Collections.Generic.List<(string Title, double ShownAt)>();
                        Time.unscaledTimeAsDouble = 50;
                        Check(UnityUiStingers.ValidateForDelivery(host, active, null, null, out _).Succeeded &&
                            TopiaForgeLayers.HeldSlots == 0, "stinger preflight retires its measurement view");
                        using var scope = new UnityUiStingers(host, new TestLog());
                        void Observe(TopiaForgeStinger view, double shownAt)
                        {
                            var title = Title(view.Go);
                            captures.Add((view.Go.activeInHierarchy ? title : "Hidden " + title, shownAt));
                        }
                        Check(scope.Show(active, null, null, null, false, out var started, Observe).Succeeded && started &&
                            scope.Show(waiting, null, null, null, false, out var queued, Observe).Succeeded && queued &&
                            captures.SequenceEqual(new[] { ("Active", 50d) }),
                            "only an actual active stinger is observed after presentation");
                        Time.unscaledTimeAsDouble = 51;
                        scope.Tick();
                        Check(captures.SequenceEqual(new[] { ("Active", 50d), ("Waiting", 51d) }),
                            "a queued stinger is observed exactly once when promoted at its new clock origin");
                        TopiaForgeStingerAnnouncements.Reset();
                    }

                    private static void ThemeLeases()
                    {
                        var descriptor = Parse("""
                            {"schema":1,"id":"selected-theme","fonts":[{"id":"brand","bundlePath":"ui/fonts.bundle","assetName":"Brand","licensePath":"ui/LICENSE.txt"}],
                             "typography":{"banner":{"font":"brand"}}}
                            """);
                        var reference = new TMP_FontAsset { Advance = 19 };
                        var provider = new UiThemeScope(descriptor) { Ready = new UiThemeRegistration(reference) };
                        var host = new UiHost { EffectiveReducedMotion = true };
                        using var scope = new UnityUiStingers(host, new TestLog());
                        Time.unscaledTimeAsDouble = 0;
                        Check(scope.Show(new UiStingerRequest("One", durationSeconds: 1), descriptor, provider, null, false, out _).Succeeded &&
                            scope.Show(new UiStingerRequest("Two", durationSeconds: 1), descriptor, provider, null, false, out _).Succeeded,
                            "active and queued requested fonts are acquired");
                        var layer = host.LastLayer!;
                        Check(provider.Leases == 2 && ReferenceEquals(Active(layer).Find("Title").GetComponent<TMP_Text>().font, reference),
                            "the measured stage uses an admitted ready font and retains it for the queue");
                        provider.Ready = new UiThemeRegistration(new TMP_FontAsset { Advance = 130 });
                        var tooWide = new UiStingerRequest(new string('x', 30));
                        Check(scope.Show(tooWide, descriptor, provider, null, false, out _).ErrorCode == ModErrorCode.InvalidArgument &&
                            provider.Leases == 2, "a wide registered font must also satisfy the two-line limit and a failure returns its lease");
                        Time.unscaledTimeAsDouble = 1;
                        scope.Tick();
                        Check(Title(Active(layer)) == "Two" && provider.Leases == 1 &&
                            ReferenceEquals(Active(layer).Find("Title").GetComponent<TMP_Text>().font, reference),
                            "promotion keeps the originally accepted font rather than the current selection");
                        scope.Dispose();
                        Check(provider.Leases == 0 && TopiaForgeLayers.HeldSlots == 0, "scope retirement drops the remaining registration");
                        provider.Ready = null;
                        using var pendingScope = new UnityUiStingers(host, new TestLog());
                        var pending = new UiStingerRequest("Waiting for fonts");
                        Check(pendingScope.Show(pending, descriptor, provider, null, false, out _).Succeeded && provider.Leases == 1,
                            "a pending pack takes one lease and paints the measured kit fallback");
                        var pendingText = Active(host.LastLayer!).Find("Title").GetComponent<TMP_Text>();
                        var fallbackFont = pendingText.font;
                        provider.Ready = new UiThemeRegistration(reference);
                        Check(pendingScope.Show(pending, descriptor, provider, null, false, out var newEntry).Succeeded && !newEntry &&
                            ReferenceEquals(pendingText.font, fallbackFont),
                            "a late native registration does not change a visible stinger's admitted typography");
                        pendingScope.Dispose();
                        Check(provider.Leases == 0, "the pending attempt is released with the announced event");
                        TopiaForgeStingerAnnouncements.Reset();
                    }

                    private static void OwnerStopAndLayerLoss()
                    {
                        var host = new UiHost();
                        using var lifetime = new FakeModLifetime();
                        var owner = new OwnerUiService(host, lifetime);
                        Check(owner.ShowStinger(new UiStingerRequest("First"), out var accepted).Succeeded && accepted &&
                            owner.ShowStinger(new UiStingerRequest("Second")).Succeeded, "the SDK owner admits local announcements");
                        host.Lose(host.LastLayer!);
                        Check(TopiaForgeLayers.HeldSlots == 0 && TopiaForgeTween.ActiveCount == 0 &&
                            owner.ShowStinger(new UiStingerRequest("Recovered")).Succeeded,
                            "destroying the native canvas drains its scope and permits a new local announcement");
                        var remote = new UiStingerRequest("Fallback", style: new UiStyleRef("remote-style"));
                        Check(owner.ShowRemoteStinger("package-a", 11, remote, null, null, hasDeclaredTheme: true, UiStingerFontFit.KitOnly).Succeeded &&
                            owner.ShowRemoteStinger("package-a", 11, remote, null, null, hasDeclaredTheme: false, UiStingerFontFit.KitOnly).ErrorCode == ModErrorCode.InvalidArgument,
                            "the authenticated receiver can fall back for an unavailable declared theme, while a missing local style is refused");
                        lifetime.Dispose();
                        Check(!host.LastLayer!.activeSelf && TopiaForgeLayers.HeldSlots == 0 && TopiaForgeTween.ActiveCount == 0 &&
                            owner.ShowStinger(new UiStingerRequest("Stopped")).ErrorCode == ModErrorCode.Cancelled,
                            "scope stop clears all queued work and its native paint");
                        TopiaForgeStingerAnnouncements.Reset();
                    }

                    private static void RemoteScopesAndPreflight()
                    {
                        var host = new UiHost { EffectiveReducedMotion = true };
                        using var lifetime = new FakeModLifetime();
                        var owner = new OwnerUiService(host, lifetime);
                        IUiStingerOrigin origin = owner;
                        IRemoteUiStingerReceiver guest = owner;
                        Screen.safeArea = new Rect(0, 0, 20, 24);
                        Check(origin.ValidateStingerForDelivery(new UiStingerRequest("Valid"), null, out _).Succeeded &&
                            origin.ValidateStingerForDelivery(new UiStingerRequest("One\nTwo\nThree"), null, out _).ErrorCode ==
                                ModErrorCode.InvalidArgument &&
                            TopiaForgeLayers.HeldSlots == 0 && TopiaForgeStingerAnnouncements.Count == 0,
                            "targeted preflight measures canonical fit independently of the local viewport and leaves no paint");
                        Screen.safeArea = new Rect(0, 0, 1920, 1080);
                        var local = new UiStingerRequest("Local");
                        Check(origin.ShowStinger(local, out var queued).Succeeded && queued,
                            "the ordinary local queue stays available after preflight");
                        for (var index = 1; index <= 4; index++)
                            Check(guest.ShowRemoteStinger("package-a", 11, new UiStingerRequest("Entry " + index),
                                null, null, false, UiStingerFontFit.KitOnly).Succeeded, "the first authenticated scope has one active and three queued");
                        var separate = new UiStingerRequest("Entry 1");
                        Check(guest.ShowRemoteStinger("package-a", 11, new UiStingerRequest("Overflow"), null, null, false, UiStingerFontFit.KitOnly).ErrorCode ==
                            ModErrorCode.RateLimited &&
                            guest.ShowRemoteStinger("package-a", 12, separate, null, null, false, UiStingerFontFit.KitOnly).Succeeded &&
                            guest.ShowRemoteStinger("package-b", 11, separate, null, null, false, UiStingerFontFit.KitOnly).Succeeded &&
                            TopiaForgeLayers.HeldSlots == 4,
                            "sibling tokens and unrelated packages have independent storage and coalescing");
                        Check(!guest.RetireRemoteStingers("package-b", 12) &&
                            guest.RetireRemoteStingers("package-a", 11) &&
                            !guest.RetireRemoteStingers("package-a", 11) && TopiaForgeLayers.HeldSlots == 3,
                            "only the exact authenticated scope retires its active view and waiting queue");
                        var announcements = TopiaForgeStingerAnnouncements.Count;
                        Check(guest.ShowRemoteStinger("package-a", 12, separate, null, null, false, UiStingerFontFit.KitOnly).Succeeded &&
                            guest.ShowRemoteStinger("package-b", 11, separate, null, null, false, UiStingerFontFit.KitOnly).Succeeded &&
                            TopiaForgeStingerAnnouncements.Count == announcements && TopiaForgeLayers.HeldSlots == 3,
                            "the same-package sibling and same-token other package retain their active queues");
                        guest.ResetRemoteStingers();
                        Check(TopiaForgeLayers.HeldSlots == 1 && origin.ShowStinger(local, out var kept).Succeeded && !kept,
                            "reset retires every remote queue and preserves the active local announcement");
                        TopiaForgeStingerAnnouncements.Reset();
                    }

                    private static void SettledTick()
                    {
                        var host = new UiHost { EffectiveReducedMotion = true };
                        using var scope = new UnityUiStingers(host, new TestLog());
                        var request = new UiStingerRequest("Settled");
                        Check(Offer(scope, request).Succeeded, "settled fixture appears");
                        var measures = TMP_Text.LayoutRequests;
                        var allocated = AllocationProbe.Measure("settled native stinger", () =>
                        {
                            scope.Tick();
                            Check(scope.Show(request, null, null, null, false, out var added).Succeeded && !added, "stinger stays coalesced");
                        }, frames: 10000, warmups: 32);
                        Check(allocated == 0 && measures == TMP_Text.LayoutRequests &&
                            TopiaForgeStingerAnnouncements.Count == 1, "settled and coalesced calls allocate zero bytes and do no layout");
                        TopiaForgeStingerAnnouncements.Reset();
                    }

                    private static UiThemeDescriptor Parse(string source)
                    {
                        var result = UiThemePackParser.Parse(System.Text.Encoding.UTF8.GetBytes(source));
                        Check(result.Succeeded, result.ErrorMessage);
                        return result.Value!;
                    }

                    private static OperationResult<bool> Offer(UnityUiStingers scope, UiStingerRequest request, UiThemeDescriptor? theme = null) =>
                        scope.Show(request, theme, null, null, false, out _);
                    private static GameObject Active(GameObject layer) => layer.Rect.Children.Single(view => view.activeInHierarchy);
                    private static string Title(GameObject view) => view.Find("Title").GetComponent<TMP_Text>().text;
                    private static Color ToColor(UiThemeColor value) => new Color32(value.R, value.G, value.B, 255);
                    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.01;
                    private static void Check(bool condition, string message)
                    {
                        if (!condition) throw new InvalidOperationException(message);
                    }
                }
            }
            """";
    }
}
