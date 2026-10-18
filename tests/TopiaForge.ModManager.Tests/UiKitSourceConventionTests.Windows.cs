using System;
using System.IO;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Window clamp conventions. A window shown in the frame its canvas is built finds the canvas still
    /// 0x0, and clamping against that collapsed the UI Gallery to nothing, so Show defers the clamp until
    /// the canvas has a size. That wiring is Unity code no test here can run, so its shape is pinned.
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
        }

        private static void RequireMatch(string source, string pattern, string message)
        {
            if (!Regex.IsMatch(source, pattern, RegexOptions.CultureInvariant))
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
