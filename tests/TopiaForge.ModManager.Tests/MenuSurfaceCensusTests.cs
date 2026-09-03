using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Tests
{
    // Exercises the reporting behind the manager's main-menu entry point (MenuSurfaceCensus is compiled
    // into this assembly via <Compile Include>; it is deliberately Unity-free).
    internal static class MenuSurfaceCensusTests
    {
        public static void Run()
        {
            TestRobotopiaMenuHasNoUguiCanvas();
            TestDescribeNamesEverySurface();
            TestDescribeSurvivesAnEmptyCensus();
            TestTopiaForgeOwnedSurfacesAreRecognised();
            TestWarningEscalatesOnceAtTheThreshold();
            Console.WriteLine("All menu surface census tests passed.");
        }

        /// <summary>
        /// The shape that broke the old injector. Robotopia build 2409's menu scene contains one UI Toolkit
        /// panel and no uGUI canvas at all, so any entry point that needed to find a game canvas could never
        /// mount. The census has to state that plainly — "ugui-canvas=0" beside a mounted entry point is the
        /// line that would have made the original bug self-evident on the first run.
        /// </summary>
        private static void TestRobotopiaMenuHasNoUguiCanvas()
        {
            var surfaces = new List<MenuSurfaceCandidate>
            {
                new MenuSurfaceCandidate(MenuSurfaceCandidate.UiToolkitPanelKind, "RishRoot", 0, 0),
            };

            var line = MenuSurfaceCensus.Describe("TestCityStartMenu", mounted: true, attempts: 1, 30000, surfaces);

            Assert(line.Contains("ugui-canvas=0"), "a menu with no uGUI canvas must report zero of them");
            Assert(line.Contains("uitoolkit-panel=1"), "the UI Toolkit panel must be counted");
            Assert(line.Contains("mounted"), "a successful mount must be stated");
            Assert(line.Contains("30000"), "the mounted sorting order must be reported");
            Assert(!line.Contains("NOT mounted"), "a successful mount must not read as a failure");
        }

        private static void TestDescribeNamesEverySurface()
        {
            var surfaces = new List<MenuSurfaceCandidate>
            {
                new MenuSurfaceCandidate(MenuSurfaceCandidate.UguiCanvasKind, "HudCanvas", 10, 4),
                new MenuSurfaceCandidate(MenuSurfaceCandidate.UiToolkitPanelKind, "RishRoot", 0, 5),
            };

            var line = MenuSurfaceCensus.Describe("TestCityStartMenu", mounted: false, attempts: 10, 0, surfaces);

            Assert(line.Contains("NOT mounted"), "a failed mount must be stated");
            Assert(line.Contains("10 attempt(s)"), "the attempt count must be reported on failure");
            Assert(line.Contains("HudCanvas") && line.Contains("RishRoot"), "every surface must be named");
            Assert(line.Contains("ugui-canvas=1") && line.Contains("uitoolkit-panel=1"), "both kinds must be counted");
        }

        private static void TestDescribeSurvivesAnEmptyCensus()
        {
            var empty = MenuSurfaceCensus.Describe("TestCityStartMenu", mounted: false, attempts: 3, 0, new List<MenuSurfaceCandidate>());
            Assert(empty.Contains("ugui-canvas=0") && empty.Contains("uitoolkit-panel=0"),
                "an empty census still reports both counts");

            var missing = MenuSurfaceCensus.Describe("TestCityStartMenu", mounted: false, attempts: 3, 0, null);
            Assert(missing.Contains("ugui-canvas=0"), "a null census must not throw and must still report");
        }

        private static void TestTopiaForgeOwnedSurfacesAreRecognised()
        {
            Assert(MenuSurfaceCensus.IsTopiaForgeOwned("io.github.furroxide.topiaforge.modmanager.menu:menu-bar"),
                "the kit names its canvases <ownerId>:<layer>, so our own menu bar is ours");
            Assert(MenuSurfaceCensus.IsTopiaForgeOwned("io.github.furroxide.topiaforge.zombies:hud"),
                "a mod's own kit canvas is ours too, not a game menu surface");
            Assert(!MenuSurfaceCensus.IsTopiaForgeOwned("RishRoot"), "a game surface is not ours");
            Assert(!MenuSurfaceCensus.IsTopiaForgeOwned(null), "an unnamed surface is not ours");
        }

        private static void TestWarningEscalatesOnceAtTheThreshold()
        {
            Assert(!MenuSurfaceCensus.ShouldWarn(1, mounted: false, alreadyWarned: false),
                "a single failed attempt is a menu still building, not a broken build");
            Assert(!MenuSurfaceCensus.ShouldWarn(MenuSurfaceCensus.WarnAfterAttempts - 1, false, false),
                "the threshold must not fire early");
            Assert(MenuSurfaceCensus.ShouldWarn(MenuSurfaceCensus.WarnAfterAttempts, false, false),
                "the threshold must fire once it is reached");
            Assert(!MenuSurfaceCensus.ShouldWarn(MenuSurfaceCensus.WarnAfterAttempts + 50, false, true),
                "a reported failure must not be reported again every retry");
            Assert(!MenuSurfaceCensus.ShouldWarn(MenuSurfaceCensus.WarnAfterAttempts, mounted: true, alreadyWarned: false),
                "a mounted entry point is never a failure");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
