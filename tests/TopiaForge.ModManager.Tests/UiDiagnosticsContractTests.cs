using System;
using System.Globalization;
using System.IO;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Offline contract for the explicit UI diagnostics additions: the Unity-free formatting and naming rules
    /// (bounded value text, #rrggbb colour text, "$scroll-n", "$close" and "toast-n" tags), the measured widget
    /// model, and the source-level wiring that produces those measurements inside the Unity assembly.
    /// </summary>
    internal static class UiDiagnosticsContractTests
    {
        public static void Run()
        {
            ColourTextIsLowercaseHexOfRoundedChannels();
            ColourTextValidation();
            BackgroundAlphaThreshold();
            TextBounding();
            TagNaming();
            InvariantSliderText();
            WidgetModelCarriesMeasuredFields();
            ProducersAreWiredInSource();
            Console.WriteLine("UiDiagnosticsContractTests passed.");
        }

        private static void ColourTextIsLowercaseHexOfRoundedChannels()
        {
            var paper = TopiaForgePalette.Paper;
            Assert(TopiaForgeUiDiagnosticFormat.Hex(paper.R, paper.G, paper.B) == "#f5f1e8", "palette paper formats as #f5f1e8");
            var ink = TopiaForgePalette.Ink;
            Assert(TopiaForgeUiDiagnosticFormat.Hex(ink.R, ink.G, ink.B) == "#2d3748", "palette ink formats as #2d3748");
            Assert(TopiaForgeUiDiagnosticFormat.Hex(0f, 0f, 0f) == "#000000", "black");
            Assert(TopiaForgeUiDiagnosticFormat.Hex(1f, 1f, 1f) == "#ffffff", "white");
            Assert(TopiaForgeUiDiagnosticFormat.Hex(-1f, 2f, float.NaN) == "#00ff00", "channels clamp and NaN reads as zero");
            Assert(TopiaForgeUiDiagnosticFormat.Hex(0.5f, 0.5f, 0.5f) == "#808080", "midpoint rounds away from zero to 0x80");
            Assert(TopiaForgeUiDiagnosticFormat.Hex(0.001f, 0.001f, 0.001f) == "#000000", "tiny values round down");
            for (var value = 0; value < 256; value++)
            {
                var channel = value / 255f;
                var expected = "#" + value.ToString("x2", CultureInfo.InvariantCulture) + "0000";
                Assert(TopiaForgeUiDiagnosticFormat.Hex(channel, 0f, 0f) == expected, "every byte round-trips through a float channel: " + value);
            }
        }

        private static void ColourTextValidation()
        {
            Assert(TopiaForgeUiDiagnosticFormat.IsColorText(string.Empty), "empty colour text is the documented absence");
            Assert(TopiaForgeUiDiagnosticFormat.IsColorText("#f5f1e8"), "lowercase #rrggbb");
            Assert(!TopiaForgeUiDiagnosticFormat.IsColorText("#F5F1E8"), "uppercase is not the documented form");
            Assert(!TopiaForgeUiDiagnosticFormat.IsColorText("f5f1e8"), "a missing hash is rejected");
            Assert(!TopiaForgeUiDiagnosticFormat.IsColorText("#f5f1e"), "short values are rejected");
            Assert(!TopiaForgeUiDiagnosticFormat.IsColorText("#f5f1e8ff"), "alpha is never encoded");
            Assert(!TopiaForgeUiDiagnosticFormat.IsColorText("#g5f1e8"), "non-hex digits are rejected");
            Assert(!TopiaForgeUiDiagnosticFormat.IsColorText(null), "null is not colour text");
            Assert(TopiaForgeUiDiagnosticFormat.IsColorText(TopiaForgeUiDiagnosticFormat.Hex(0.2f, 0.7f, 0.9f)), "formatter output validates");
        }

        private static void BackgroundAlphaThreshold()
        {
            Assert(TopiaForgeUiDiagnosticFormat.IsOpaqueBackground(1f), "opaque counts");
            Assert(TopiaForgeUiDiagnosticFormat.IsOpaqueBackground(0.5f), "the 0.5 threshold is inclusive");
            Assert(!TopiaForgeUiDiagnosticFormat.IsOpaqueBackground(0.49f), "below the threshold is skipped");
            Assert(!TopiaForgeUiDiagnosticFormat.IsOpaqueBackground(0f), "clear images are skipped");
            Assert(!TopiaForgeUiDiagnosticFormat.IsOpaqueBackground(float.NaN), "NaN alpha is never a background");
        }

        private static void TextBounding()
        {
            Assert(TopiaForgeUiDiagnosticFormat.MaxTextLength == 256, "the documented bound is 256 characters");
            Assert(TopiaForgeUiDiagnosticFormat.Bound(null) == string.Empty, "null bounds to empty");
            Assert(TopiaForgeUiDiagnosticFormat.Bound(string.Empty) == string.Empty, "empty stays empty");
            var exact = new string('x', 256);
            Assert(ReferenceEquals(TopiaForgeUiDiagnosticFormat.Bound(exact), exact), "a value at the bound is returned unchanged");
            var bounded = TopiaForgeUiDiagnosticFormat.Bound(new string('y', 257) + "tail");
            Assert(bounded.Length == 256 && bounded == new string('y', 256), "longer values truncate to the first 256 characters");
        }

        private static void TagNaming()
        {
            Assert(TopiaForgeUiDiagnosticFormat.ToastSurface == "$toast", "toast surface id");
            Assert(TopiaForgeUiDiagnosticFormat.CloseTag == "$close", "close chrome node id");
            Assert(TopiaForgeUiDiagnosticFormat.ScrollKind == "scroll" && TopiaForgeUiDiagnosticFormat.ToastKind == "toast", "reserved kinds");
            Assert(TopiaForgeUiDiagnosticFormat.ScrollTag(0) == "$scroll-0", "scroll containers count from 0");
            Assert(TopiaForgeUiDiagnosticFormat.ScrollTag(2) == "$scroll-2", "render-order index is decimal");
            Assert(TopiaForgeUiDiagnosticFormat.ScrollTag(1234) == "$scroll-1234", "no padding or grouping");
            Assert(Throws<ArgumentOutOfRangeException>(() => TopiaForgeUiDiagnosticFormat.ScrollTag(-1)), "negative scroll index is rejected");
            Assert(TopiaForgeUiDiagnosticFormat.ToastNode(1) == "toast-1", "toast sequences start at 1");
            Assert(TopiaForgeUiDiagnosticFormat.ToastNode(long.MaxValue) == "toast-" + long.MaxValue.ToString(CultureInfo.InvariantCulture), "sequence is a 64-bit counter");
            Assert(Throws<ArgumentOutOfRangeException>(() => TopiaForgeUiDiagnosticFormat.ToastNode(0)), "sequence zero is never presented");
        }

        private static void InvariantSliderText()
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert(TopiaForgeUiDiagnosticFormat.InvariantNumber(1.25f) == "1.25", "slider text ignores the current culture");
                Assert(TopiaForgeUiDiagnosticFormat.InvariantNumber(-0.5f) == "-0.5", "negative values keep the invariant separator");
                Assert(TopiaForgeUiDiagnosticFormat.InvariantNumber(3f) == "3", "integral values carry no separator");
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        private static void WidgetModelCarriesMeasuredFields()
        {
            var longText = new string('t', 300);
            var widget = new TopiaForgeUiDiagnosticWidget("surface", "roster-list/owned-robot:1", "list-item", longText, "Body",
                1f, 2f, 3f, 4f, true, true, false, false, true, true, 1.25f, 0f,
                selected: true, value: new string('v', 300), foreground: "#2d3748", background: "#ffe8d1");
            Assert(widget.Selected, "selected round-trips");
            Assert(widget.Value.Length == 256 && widget.Value == new string('v', 256), "value is bounded to 256 characters");
            Assert(widget.Text.Length == 256, "text is bounded to 256 characters");
            Assert(widget.Foreground == "#2d3748" && widget.Background == "#ffe8d1", "colour text round-trips");

            var legacy = new TopiaForgeUiDiagnosticWidget("surface", "hide-workbench", "button", "Hide", "",
                0f, 0f, 10f, 10f, true, true, false, false, false, false, 1f, 1f);
            Assert(!legacy.Selected && legacy.Value == string.Empty && legacy.Foreground == string.Empty && legacy.Background == string.Empty,
                "widgets without measurements report false and empty strings, never null");

            var snapshot = new TopiaForgeUiDiagnosticSnapshot("owner", new[] { widget, legacy }, 1, 2, 3, 4, 5, 6, 1920, 1080);
            Assert(snapshot.Widgets.Count == 2 && snapshot.Widgets[0].Selected && !snapshot.Widgets[1].Selected, "snapshot exposes measured rows");
        }

        private static void ProducersAreWiredInSource()
        {
            var root = Program.FindRepoRoot();
            var kit = Path.Combine(root, "src", "TopiaForge.Mods.UnityUi");
            var diagnostics = File.ReadAllText(Path.Combine(kit, "Diagnostics", "TopiaForgeUiDiagnostics.cs"));
            RequireSource(diagnostics, "widget is TopiaForgeListRow row && row.Selected", "selected measures the rendered list-row state");
            RequireSource(diagnostics, "case TopiaForgeDropdown dropdown: return dropdown.SelectedCaption;", "dropdown value is the caption of the current option");
            RequireSource(diagnostics, "case TopiaForgeToggle toggle: return toggle.Value ? \"true\" : \"false\";", "toggle value is true/false");
            RequireSource(diagnostics, "case TopiaForgeSlider slider: return TopiaForgeUiDiagnosticFormat.InvariantNumber(slider.Value);", "slider value is invariant text");
            RequireSource(diagnostics, "case TopiaForgeInputField input: return input.Text;", "input value is the current text");
            RequireSource(diagnostics, "GetComponentsInChildren<TMP_Text>(true)", "foreground walks the widget subtree for TMP_Text");
            RequireSource(diagnostics, "TopiaForgeUiDiagnosticFormat.IsOpaqueBackground(image.color.a)", "background applies the alpha threshold");
            RequireSource(diagnostics, "current = current.parent", "background falls back to the nearest ancestor Image");

            var toast = File.ReadAllText(Path.Combine(kit, "Widgets", "TopiaForgeToast.cs"));
            RequireSource(toast, "public const string DiagnosticsOwnerId = \"io.github.furroxide.topiaforge.ui.toasts\";", "toast host owner id is published");
            RequireSource(toast, "OwnerId = TopiaForgeToasts.DiagnosticsOwnerId", "the toast host is created under the published owner id");
            RequireSource(toast, "TopiaForgeUiDiagnosticFormat.ToastNode(sequence)", "toast nodes use the process-monotonic sequence");
            RequireSource(toast, "TopiaForgeUiDiagnosticFormat.ToastSurface,", "toasts are tagged on the $toast surface");
            RequireSource(toast, "pending.Text, pending.Tone.ToString()", "toast text is the message and style is the tone name");

            var manager = Path.Combine(root, "src", "TopiaForge.ModManager");
            var renderer = File.ReadAllText(Path.Combine(manager, "UnityUiCompositionRenderer.cs"));
            RequireSource(renderer, "pass.NextScrollTag(), TopiaForgeUiDiagnosticFormat.ScrollKind", "renderer scroll containers are tagged $scroll-n in render order");
            RequireSource(renderer, "TopiaForgeUiDiagnosticFormat.ScrollTag(scrollContainers++)", "the scroll counter starts at 0 per render pass");
            var surface = File.ReadAllText(Path.Combine(manager, "UnityUiSurface.cs"));
            RequireSource(surface, "new UiRenderPass(Id)", "every SetContent starts a fresh render pass");
            RequireSource(surface, "TopiaForgeUiDiagnostics.TagWidget(closeButton, id, TopiaForgeUiDiagnosticFormat.CloseTag, \"button\")", "the chrome close button is tagged $close on its surface");
            foreach (var chrome in new[] { "TopiaForgeWindow.cs", "TopiaForgeFullscreenTool.cs" })
            {
                RequireSource(File.ReadAllText(Path.Combine(kit, "Widgets", chrome)),
                    "CloseButton = titleBar.IconButton(TopiaForgeIcon.Cross, Close, TopiaForgeButtonStyle.Ghost)",
                    chrome + " exposes its title-bar close button for tagging");
            }

            var workbench = File.ReadAllText(Path.Combine(root, "mods", "TopiaForge.Sandbox", "CreatorTools", "CreatorWorkbench.Ui.cs"));
            RequireSource(workbench, "new UiButton(\"undo-last\", \"Undo\", () => Execute(Undo), UiButtonStyle.Ghost, history.Count > 0)",
                "undo-last calls the existing Undo() and is enabled only while history is non-empty");
        }

        private static void RequireSource(string source, string expected, string message)
        {
            if (!source.Contains(expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("UI diagnostics contract: " + message + " (missing '" + expected + "').");
            }
        }

        private static bool Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return true; }
            return false;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("UI diagnostics contract: " + message);
        }
    }
}
