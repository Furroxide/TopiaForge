using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitCaptureTests
    {
        private static readonly UiCaptureViewport Viewport = new UiCaptureViewport(1920, 1080);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CreateThemeScope")]
        private static extern FakeUiService CreateThemeScope(FakeUiService service, FakeModLifetime childLifetime);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CaptureForFixture")]
        private static extern UiTreeCapture CaptureForFixture(FakeUiSurface surface, UiCaptureViewport viewport,
            string state, float scrollOffset, bool? hudScheme);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ThemeForWire")]
        private static extern UiThemeDescriptor? ThemeForWire(FakeUiService service, UiThemeRef? theme);

        internal static void Run()
        {
            GalleryNativeCaptureOnlyAdmitsAuthoredFixtures();
            NativeCaptureDetectsFixedClippingAndMissingHits();
            CatalogueIsAtomicAndUsesExactBytes();
            CascadeRefreshKeepsControlState();
            CardAndBoxThemeSwitchesAreAtomic();
            InheritedThemeSwitchPreflightsNamedStyles();
            SiblingPreloadsAndDrainAreIndependent();
            AssetsCommitTogetherAndEnrichTheDescriptor();
            PaintAndTypeRemainReadable();
            VerifyButtonCues();
            AuthoredBackplatesAndControlTargetsAreChecked();
            NativeControlsUseTheirResolvedRoles();
            GraphPortsHaveIndependentTargetsAndPaint();
            GraphConnectionsAreLocalPooledAndLabelled();
            GraphConnectionLabelsRevealBothEndpoints();
            GraphConnectionsPaintIndependentCues();
            GraphConnectionsLauncherFitsTheLabel();
            GraphConnectionsYieldToAnUnreachableLayout();
            NativeGraphConnectionsUseTheActualControls();
            ListSelectionRevealsPaddedRows();
            VirtualListRowsFitBeforeScrolling();
            VirtualListBadgesCaptureTheirResolvedPaint();
            NativeVirtualListUsesPooledTextMetrics();
            TargetedBudgetsIncludeFramingWithoutEarlyClockBinding();
            UnchangedReapplicationAllocatesNothing();
            RunArtCaptureTests();
            VerifyGoldenManifest();
            VerifyArtGoldenManifest();
        }

        private static void InheritedThemeSwitchPreflightsNamedStyles()
        {
            using var parentLife = new FakeModLifetime();
            var parent = new FakeUiService(parentLife);
            Check(parent.AddThemeFixture("""{"schema":1,"id":"badges","styles":[{"id":"status","target":"badge","radius":"card"}]}""").Succeeded &&
                parent.AddThemeFixture("""{"schema":1,"id":"boxes","styles":[{"id":"status","target":"box","radius":"small"}]}""").Succeeded,
                "The two package styles are independently valid.");
            var badges = new UiThemeRef("badges");
            var boxes = new UiThemeRef("boxes");
            Check(parent.Themes.UseTheme(badges).Succeeded, "The original package style is selected.");
            using var childLife = new FakeModLifetime();
            var child = CreateThemeScope(parent, childLife);
            var badge = new UiBadge("Stocked", style: new UiStyleRef("status"), id: "status-mark");
            using var inherited = Make(child, new UiSurfaceRequest("inherited", "Status", "", UiSurfaceKind.Window, 420, 300, badge));
            using var overridden = Make(parent, new UiSurfaceRequest("overridden", "Status", "", UiSurfaceKind.Window, 420, 300, badge)
                .WithTheme(badges));
            parent.CompletePendingThemeRegistrations();

            Check(parent.Themes.UseTheme(boxes).ErrorCode == ModErrorCode.InvalidArgument && Equals(parent.Themes.SelectedTheme, badges) &&
                inherited.Capture(Viewport).Nodes.Any(node => node.Id == "status-mark" && node.StyleId == "status" && node.IsRendered),
                "A parent's incompatible target leaves its inheriting child's current content and paint live.");
            Check(child.Themes.UseTheme(badges).Succeeded && parent.Themes.UseTheme(boxes).Succeeded,
                "An explicitly selected child and an explicit surface override shield their authored references.");
            Check(child.Themes.UseTheme(null).ErrorCode == ModErrorCode.InvalidArgument && Equals(child.Themes.SelectedTheme, badges) &&
                overridden.Capture(Viewport).Nodes.Any(node => node.StyleId == "status" && node.IsRendered),
                "Clearing a child selection also preflights the prospective inherited theme without disrupting a surface override.");
        }

        private static void CatalogueIsAtomicAndUsesExactBytes()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var definition = Definition("field", "#235548");
            var original = Encoding.UTF8.GetBytes(definition);
            var sha256 = Convert.ToHexString(SHA256.HashData(original));
            var catalogue = "{\"id\":\"example.test-mod\",\"ui\":{\"schema\":1,\"default\":\"field\",\"themes\":["
                + "{\"id\":\"field\",\"definition\":\"ui/themes/field.json\",\"sha256\":\"" + sha256 + "\"},"
                + "{\"id\":\"other\",\"definition\":\"ui/themes/other.json\"}]}}";
            var missing = ui.AddThemeCatalogue(catalogue, path => path.EndsWith("field.json", StringComparison.Ordinal)
                ? definition : throw new IOException("the original exception must not leak"));
            Check(missing.ErrorCode == ModErrorCode.Io && !missing.ErrorMessage.Contains("must not leak", StringComparison.Ordinal),
                "A failed catalogue file read reports only its logical entry.");
            Check(ui.Themes.UseTheme(new UiThemeRef("field")).ErrorCode == ModErrorCode.NotFound,
                "The earlier successful catalogue entry was not partially committed.");

            var wrongBytes = ui.AddThemeCatalogue(catalogue, path => path.EndsWith("field.json", StringComparison.Ordinal)
                ? definition + "\n" : Definition("other", "#57394C"));
            Check(wrongBytes.ErrorCode == ModErrorCode.InvalidArgument, "The catalogue hash covers exact UTF-8, including line endings.");
            Check(ui.AddThemeCatalogue(catalogue, path => path.EndsWith("field.json", StringComparison.Ordinal)
                ? definition : Definition("other", "#57394C")).Succeeded, "Both validated catalogue entries can be installed together.");
            Check(ui.Themes.SelectedTheme == null, "The manifest default is inherited.");

            using var surface = Make(ui, Request("field-view"));
            var pending = ui.Capture(surface.Id, Viewport).Nodes.Single(row => row.Id == "sample-text");
            Check(pending.UsedFallback && pending.IsRendered, "The admitted surface immediately shows readable fallback content.");
            ui.CompletePendingThemeRegistrations();
            var ready = ui.Capture(surface.Id, Viewport).Nodes.Single(row => row.Id == "sample-text");
            Check(ready.ThemeId == "field" && ready.InkColor == "#235548" && !ready.UsedFallback && ready.Path == pending.Path,
                "A declaration in the manifest becomes the default without a theme call or a new content id.");
        }

        private static void CascadeRefreshKeepsControlState()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            Check(ui.AddThemeFixture(Definition("parent", "#235548")).Succeeded, "Declare the parent theme.");
            Check(ui.AddThemeFixture(Definition("override", "#57394C")).Succeeded, "Declare the surface override.");
            using var childLifetime = new FakeModLifetime();
            var child = CreateThemeScope(ui, childLifetime);
            Check(ui.Themes.UseTheme(new UiThemeRef("parent")).Succeeded, "The parent selects its owned pack.");
            var content = new UiToggle("feature", "Feature", false, _ => { });
            using var inherited = Make(child, new UiSurfaceRequest("inherited", "Settings", "", content: content));
            using var explicitSurface = Make(child, Request("chosen").WithTheme(new UiThemeRef("override")));
            Check(inherited.ChangeToggle("feature", true).Succeeded, "The player can change a child surface control.");
            ui.CompletePendingThemeRegistrations();
            Check(child.Themes.SelectedTheme == null && inherited.Capture(Viewport).Nodes[0].ThemeId == "parent",
                "The unselected child inherits the nearest scope choice.");
            Check(explicitSurface.Capture(Viewport).Nodes[0].ThemeId == "override",
                "The surface's own selection shields it from its ancestor.");
            Check(child.Themes.UseTheme(new UiThemeRef("unknown")).ErrorCode == ModErrorCode.NotFound,
                "An unknown theme fails without changing the child selection.");
            Check(child.ApplyAccessibility(new UiAccessibilityPreferences(uiScale: 2f)).Succeeded &&
                inherited.SetContent(content).Succeeded && inherited.TryGetToggleValue("feature", out var value) && value,
                "Reapplying the same tree after a theme and accessibility change preserves live control values.");
            Check(ui.Themes.UseTheme(null).Succeeded && inherited.Capture(Viewport).Nodes[0].ThemeId == UiThemePackParser.Default.Id,
                "Clearing the parent choice leaves the unselected child on the kit default.");
            Check(explicitSurface.Capture(Viewport).Nodes[0].ThemeId == "override", "The explicit surface override remains active.");
        }

        private static void SiblingPreloadsAndDrainAreIndependent()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var reference = new UiThemeRef("field");
            Check(ui.AddThemeFixture(Definition("field", "#235548")).Succeeded, "Declare the shared pack.");
            using var firstLife = new FakeModLifetime();
            using var secondLife = new FakeModLifetime();
            var first = CreateThemeScope(ui, firstLife);
            var second = CreateThemeScope(ui, secondLife);
            var stopped = first.Themes.PreloadAsync(reference);
            var sibling = second.Themes.PreloadAsync(reference);
            using var repeatCancellation = new CancellationTokenSource();
            var duplicate = second.Themes.PreloadAsync(reference, repeatCancellation.Token);
            repeatCancellation.Cancel();
            Check(duplicate.IsCompletedSuccessfully && duplicate.Result.ErrorCode == ModErrorCode.Cancelled && !sibling.IsCompleted,
                "Cancelling a later caller in one scope preserves that scope's earlier pending demand.");
            Check(!stopped.IsCompleted && !sibling.IsCompleted, "Siblings wait for one explicitly advanced acquisition.");
            firstLife.Dispose();
            Check(stopped.IsCompletedSuccessfully && stopped.Result.ErrorCode == ModErrorCode.Cancelled && !sibling.IsCompleted,
                "Stopping the initiating scope cancels only its own preload.");
            ui.CompletePendingThemeRegistrations();
            Check(sibling.IsCompletedSuccessfully && sibling.Result.Succeeded, "The other scope completes its shared load.");
            secondLife.Dispose();

            using var cancellation = new CancellationTokenSource();
            var cancelled = ui.Themes.PreloadAsync(reference, cancellation.Token);
            cancellation.Cancel();
            Check(cancelled.IsCompletedSuccessfully && cancelled.Result.ErrorCode == ModErrorCode.Cancelled,
                "A cancelled last demand ends the waiter independently of load retirement.");
            var afterCancellation = ui.Themes.PreloadAsync(reference);
            ui.CompletePendingThemeRegistrations();
            Check(!afterCancellation.IsCompleted, "A later preload cannot join the cancelled attempt while it drains.");
            ui.CompletePendingThemeRegistrations();
            Check(afterCancellation.IsCompletedSuccessfully && afterCancellation.Result.Succeeded,
                "The new acquisition completes on the next fake advancement.");

        }

        private static void AssetsCommitTogetherAndEnrichTheDescriptor()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            const string bundle = "ui/assets.bundle";
            const string hash = "1111111111111111111111111111111111111111111111111111111111111111";
            const string image = "{\"id\":\"marker\",\"bundlePath\":\"ui/assets.bundle\",\"assetName\":\"Marker\",\"licensePath\":\"licenses/art.txt\"}";
            const string font = "{\"id\":\"signal\",\"bundlePath\":\"ui/assets.bundle\",\"assetName\":\"Signal\",\"licensePath\":\"licenses/font.txt\"}";
            const string loadedJson = "{\"schema\":1,\"id\":\"field\",\"images\":[" + image + "]}";
            const string brokenJson = "{\"schema\":1,\"id\":\"broken\",\"images\":[" + image + "],\"fonts\":[" + font + "]}";
            Check(ui.AddThemeFixture(loadedJson).Succeeded && ui.AddThemeFixture(brokenJson).Succeeded,
                "Definitions can be staged before fake resource metadata.");
            ui.AddAssetFixture(new FakeUiAssetMetadata(bundle, "Marker", hash, 24, 36, 864, isMask: true));
            ui.AddAssetFixture(new FakeUiAssetMetadata(bundle, "Signal", hash, 128, 128, 16384,
                ascender: 0.9f, descender: -0.4f, outcome: FakeUiAssetOutcome.LoadFailure));

            var field = new UiThemeRef("field");
            var broken = new UiThemeRef("broken");
            var loaded = ui.Themes.PreloadAsync(field);
            var failed = ui.Themes.PreloadAsync(broken);
            ui.CompletePendingThemeRegistrations();
            using var fallback = Make(ui, Request("broken-asset").WithTheme(broken));
            var marker = ThemeForWire(ui, field)!.Images.Single();
            Check(loaded.IsCompletedSuccessfully && loaded.Result.Succeeded && marker.PixelWidth == 24 && marker.PixelHeight == 36
                && marker.IsMask == true, "Fake receipts populate source dimensions and mask eligibility.");
            Check(failed.IsCompletedSuccessfully && failed.Result.ErrorCode == ModErrorCode.External && fallback.Capture(Viewport).Nodes[0].UsedFallback,
                "Failure in the last asset never commits the earlier asset or publishes the failed pack.");
        }

        private static void PaintAndTypeRemainReadable()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            Check(ui.AddThemeFixture("{\"schema\":1,\"id\":\"field\",\"typography\":{\"caption\":{\"case\":\"upper\"}},"
                + "\"shape\":{\"small\":6,\"medium\":10,\"control\":12,\"card\":26}}").Succeeded,
                "An authored case transform remains plain text.");
            Check(ui.Themes.UseTheme(new UiThemeRef("field")).Succeeded, "Select the configured typography.");
            const string caption = "Rack <b>";
            var tree = new UiColumn(new UiText(caption, UiTextStyle.Caption, id: "caption"),
                new UiButton("danger", "Close route", () => { }, UiButtonStyle.Danger));
            using var surface = Make(ui, new UiSurfaceRequest("paint", "Paint", "", width: 580, height: 320, content: tree));
            ui.CompletePendingThemeRegistrations();
            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(uiScale: 0.75f, reducedMotion: true)).Succeeded,
                "Reduction is applied without shrinking readable text.");
            var hover = CaptureForFixture(surface, Viewport, "Hovered", 0f, null);
            var press = CaptureForFixture(surface, Viewport, "Pressed", 0f, null);
            var hovered = hover.Nodes.Single(node => node.Id == "danger");
            var pressed = press.Nodes.Single(node => node.Id == "danger");
            Check(hovered.FillColor == "#BC3A48" && pressed.FillColor == "#B03744" && hovered.X == pressed.X && hovered.Y == pressed.Y,
                "Paper danger keeps a distinct press tint with reduced motion and no movement.");
            Check(hovered.CornerRadiusPixels == 9f && hovered.ShadowOffsetXPixels == -2.25f && hovered.ShadowOffsetYPixels == 3f,
                "The capture includes the authored radius and resolved hard-shadow offsets at the viewer's UI scale.");
            var smaller = hover.Nodes.Single(node => node.Id == "caption");
            Check(smaller.TextHeightPixels == 18 && smaller.SemanticLabel == caption && smaller.Text == "RACK <B>",
                "The lower scale boundary preserves an 18-pixel caption and its original literal accessible text.");
            Check(ui.ApplyAccessibility(new UiAccessibilityPreferences(highContrast: true, uiScale: 2f)).Succeeded,
                "Apply high contrast at double scale.");
            var high = CaptureForFixture(surface, Viewport, "Focused", 0f, null);
            var captionAtTwo = high.Nodes.Single(node => node.Id == "caption");
            Check(captionAtTwo.TextHeightPixels == 36 && captionAtTwo.ContrastRatio >= 7 && high.CheckAccessibility().Count == 0,
                "Double scale doubles the measured floor and keeps high-contrast labels readable.");
            Check(high.Nodes.Any(row => row.Kind == "FocusRing" && row.BorderWidthPixels == 6f),
                "A focused control has a three-unit ring scaled to six pixels.");
            Check(ui.ShowToast("Missing supplies", UiTone.Primary).ErrorCode == ModErrorCode.InvalidArgument,
                "The legacy toast rejects an enhanced tone.");
        }

        private static void TargetedBudgetsIncludeFramingWithoutEarlyClockBinding()
        {
            using var players = new FakePlayerDirectory();
            var recipient = players.Add(new ParticipantId("field-guest"), "Sam");
            var viewer = players.State(recipient.Id).Ui;
            var body = new string('é', 4096);
            using var retained = Make(recipient.Ui.CreateSurface(new UiSurfaceRequest("remote", "Title", body,
                content: new UiText("Keep me", id: "previous"))));
            var longLine = new string('é', 4096);
            var clock = new UiCountdown(8, "Timer", UiTone.Primary, "new-clock");
            var tree = new UiColumn(new UiText(longLine), new UiText(longLine), new UiText(longLine), clock);
            var rejected = retained.SetContent(tree);
            Check(rejected.ErrorCode == ModErrorCode.RateLimited && clock.ClockEpoch == null,
                "A frame over budget once its body and headers count cannot admit or bind a proposed countdown.");
            Check(retained.TryFindNode("previous", out _) && viewer.Surfaces.Count == 1,
                "The previous targeted composition and its handle survive the refusal.");
        }

        private static void AuthoredBackplatesAndControlTargetsAreChecked()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            const string definition = """
                {"schema":1,"id":"opposing-inks","paper":{"colors":{
                    "surface":{"fill":"#767676","on":"#000000"},
                    "raised":{"fill":"#040404","on":"#FFFFFF"},
                    "sunken":{"fill":"#10141B","on":"#FFFFFF"},
                    "selected":{"fill":"#151923","on":"#FFFFFF"}}}}
                """;
            Check(ui.AddThemeFixture(definition).Succeeded, "Each authored paint pair has a readable on-color.");
            var content = new UiColumn(
                new UiButton("secondary", "Check route", () => { }, UiButtonStyle.Secondary),
                new UiTextInput("field-value", "Call sign", "North", _ => { }),
                new UiToggle("disabled-switch", "Signal locked", true, _ => { }, enabled: false));
            using var surface = Make(ui, new UiSurfaceRequest("opposing-colors", "Route", "", width: 520, content: content)
                .WithTheme(new UiThemeRef("opposing-inks")));
            ui.CompletePendingThemeRegistrations();
            var pressed = CaptureForFixture(surface, Viewport, "Pressed", 0f, null);
            Check(pressed.Nodes.Single(row => row.Kind == "SurfaceTitle") is { FillColor: "#040404", InkColor: "#FFFFFF" },
                "Window title ink follows the Raised backdrop even when Surface has the opposite ink.");
            Check(pressed.Nodes.Single(row => row.Id == "secondary") is { ContrastRatio: >= 4.5f, InkColor: "#000000" },
                "The outline button's actual surface pair constrains its press tint.");
            Check(pressed.Nodes.Single(row => row.Kind == "FieldValue") is { ContrastRatio: >= 4.5f, InkColor: "#FFFFFF" },
                "The filled-in field maintains readable text over its actual Sunken fill.");
            Check(pressed.Nodes.Single(row => row.Kind == "ToggleTrack").FillColor == "#10141B",
                "An on switch draws a Sunken track once disabled.");
            Check(pressed.Nodes.Count(row => row.Kind == "HitTarget" && row.Width >= 40f && row.Height >= 40f) == 3,
                "Every reserved interactive rectangle clears the 40-unit minimum.");

            var narrow = new UiSlider("narrow", "Range", 0f, 10f, 4f, _ => { });
            var tooSmall = ui.CreateSurface(new UiSurfaceRequest("narrow-track", "R", "", width: 130, content: narrow));
            Check(tooSmall.ErrorCode == ModErrorCode.InvalidArgument,
                "A control whose active region is too narrow fails layout admission.");
        }

        private static void UnchangedReapplicationAllocatesNothing()
        {
            using var lifetime = new FakeModLifetime();
            var ui = new FakeUiService(lifetime);
            var tree = new UiColumn(new UiText("Station ready"));
            using var surface = Make(ui, new UiSurfaceRequest("static", "Station", "", content: tree));
            var theme = ui.Themes;
            var surfaces = ui.Surfaces;
            for (var index = 0; index < 4096; index++) _ = surface.SetContent(tree);
            var consistent = true;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 4096; index++)
            {
                consistent &= ReferenceEquals(theme, ui.Themes) && ReferenceEquals(surfaces, ui.Surfaces)
                    && theme.SelectedTheme == null && surface.SetContent(tree).Succeeded;
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(consistent && allocated == 0, "4,096 warmed unchanged reads and reapplications allocated " + allocated + " bytes.");

            ui.FailNextContentUpdate(surface.Id);
            Check(surface.SetContent(tree).Succeeded && !surface.SetContent(new UiText("New tree")).Succeeded
                && ReferenceEquals(surface.Content, tree), "Reapplying the retained tree leaves the next real content failure armed.");
        }

        private static UiSurfaceRequest Request(string id) => new UiSurfaceRequest(id, "Field station", "",
            content: new UiText("Route confirmed", UiTextStyle.Body, UiTone.Primary, "sample-text"));

        private static string Definition(string id, string color) =>
            "{\"schema\":1,\"id\":\"" + id + "\",\"paper\":{\"inks\":{\"primary\":\"" + color + "\"}}}";

        private static FakeUiSurface Make(FakeUiService ui, UiSurfaceRequest request) => Make(ui.CreateSurface(request));

        private static FakeUiSurface Make(OperationResult<IUiSurface> result)
        {
            if (result.TryGetValue(out var surface) && surface is FakeUiSurface fake) return fake;
            throw new InvalidOperationException("Could not create the fake surface. " + result.ErrorMessage);
        }

        private static void Check(bool passed, string detail)
        {
            if (!passed) throw new InvalidOperationException(detail);
        }
    }
}
