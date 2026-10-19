using System;
using System.IO;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Window clamp conventions. A window shown in the frame its canvas is built finds the canvas still
    /// 0x0, and clamping against that collapsed the UI Gallery to nothing, so Show defers the clamp until
    /// the canvas has a size. That wiring is Unity code no test here can run, so its shape is pinned: the
    /// deferral, and what Show feeds TopiaForgeWindowMath.TryPlace, which the math tests cover.
    /// </summary>
    internal static partial class UiKitSourceConventionTests
    {
        private static void WindowClampWaitsForAMeasuredCanvas()
        {
            var widgets = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.Mods.UnityUi", "Widgets");
            var window = File.ReadAllText(Path.Combine(widgets, "TopiaForgeWindow.cs"));
            var runtime = File.ReadAllText(Path.Combine(widgets, "TopiaForgeWindowRuntime.cs"));

            RequireMatch(
                window,
                @"Go\.SetActive\(true\);(?:\s|//[^\n]*)*deferredClamp\.enabled = !TryClampToScreen\(\);",
                "TopiaForgeWindow.Show must clamp at once, and arm the deferred clamp when the canvas has no size yet.");
            RequireMatch(
                window,
                @"return tornDown \|\| !open \|\| closing \|\| TryClampToScreen\(\);",
                "FinishDeferredClamp must stop once the window closes or is torn down, and retry until the clamp lands.");
            RequireMatch(
                window,
                @"if \(!TopiaForgeWindowMath\.TryPlace\(",
                "TryClampToScreen must place through TopiaForgeWindowMath.TryPlace, which refuses an unmeasured canvas.");
            RequireMatch(
                runtime,
                @"if \(Window == null \|\| Window\.FinishDeferredClamp\(\)\)\s*\{\s*enabled = false;",
                "TopiaForgeWindowDeferredClamp must retry the pending clamp each frame and turn itself off once it lands.");

            RequireShape(
                window,
                "deferredClamp = Go.AddComponent<TopiaForgeWindowDeferredClamp>(); deferredClamp.Window = this; deferredClamp.enabled = false;",
                "The window must bind its deferred clamp to itself, or the clamp turns itself off without running, and start it disarmed.");
            RequireShape(
                window,
                "canvasRect = layerRoot.Rect; designedSize = new Vector2(width, height);",
                "The window must measure its own canvas root and keep the size it was designed at.");
            RequireShape(
                window,
                "private bool TryClampToScreen() { var (rect, canvasSize) = CanvasSpaceRect(); "
                    + "if (!TopiaForgeWindowMath.TryPlace(rect, designedSize.x, designedSize.y, autoHeight, canvasSize.x, canvasSize.y, out var placed)) "
                    + "{ return false; } ApplyCanvasSpaceRect(placed, canvasSize); return true; }",
                "TryClampToScreen must cap from the designed size, keep a content-sized window's own size, and report a refused "
                    + "placement as still pending, so the deferred clamp is armed.");
            RequireShape(
                window,
                "private (TopiaForgeRect Rect, Vector2 CanvasSize) CanvasSpaceRect() { var canvasSize = canvasRect.rect.size; "
                    + "var size = Rect.rect.size; var position = Rect.anchoredPosition; "
                    + "var x = position.x + (canvasSize.x / 2f) - (size.x / 2f); var y = position.y + (canvasSize.y / 2f) - (size.y / 2f); "
                    + "return (new TopiaForgeRect(x, y, size.x, size.y), canvasSize); }",
                "CanvasSpaceRect must read the size of the window's own canvas root, with nothing standing in for it.");
            if (window.Contains("ReferenceWidth", StringComparison.Ordinal) || window.Contains("ReferenceHeight", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A window must never clamp against the reference resolution: an unmeasured canvas waits for its real size.");
            }

            RequireShape(
                window,
                "|| !float.IsFinite(x) || !float.IsFinite(y)) { return; } Rect.anchoredPosition = new Vector2(x, y); }",
                "RestoreRect must restore only a finite position, so a corrupt state entry cannot lose the window.");
        }

        private static void RequireMatch(string source, string pattern, string message)
        {
            // A timeout bounds the match: a pattern that backtracks badly fails this test instead of hanging the run.
            if (!Regex.IsMatch(source, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10)))
            {
                throw new InvalidOperationException(message);
            }
        }

        // Compares code with every run of whitespace collapsed to one space, so a pinned shape survives
        // re-indentation and line breaks but not a changed token.
        private static void RequireShape(string source, string shape, string message)
        {
            if (!CollapseWhitespace(source).Contains(CollapseWhitespace(shape), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(message);
            }
        }

        private static string CollapseWhitespace(string text)
        {
            return Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant);
        }
    }
}
