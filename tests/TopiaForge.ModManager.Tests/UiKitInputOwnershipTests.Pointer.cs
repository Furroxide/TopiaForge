using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitInputOwnershipTests
    {
        /// <summary>
        /// The game's pause menu turns its whole menu map off as it closes, the UI module's pointer actions with it, so no
        /// click reached a TopiaForge surface after a pause. While a surface shows the cursor, the kit turns an action the
        /// game left off back on, and turns it off again as the last such surface closes; what the game itself changes
        /// meanwhile stands, and where the game leaves the action on, nothing changes.
        /// </summary>
        private static void TheKitTurnsTheGamesPointerBackOnOnlyWhileASurfaceShowsTheCursor()
        {
            var point = new TopiaForgePointerHold();
            AssertEqual(point.Tick(wanted: true, present: true, enabled: true), TopiaForgePointerStep.None,
                "at the game's menus, where its pointer is on, a surface showing the cursor changes nothing");
            AssertEqual(point.Tick(false, true, true), TopiaForgePointerStep.None, "nor does its closing");

            AssertEqual(point.Tick(true, true, false), TopiaForgePointerStep.Enable, "after the pause menu, the kit turns the game's pointer back on");
            Assert(point.Held, "and holds it");
            AssertEqual(point.Tick(true, true, true), TopiaForgePointerStep.None, "once");
            AssertEqual(point.Tick(false, true, true), TopiaForgePointerStep.Disable, "the last surface closing turns it off again");
            Assert(!point.Held, "and lets it go");
            AssertEqual(point.Tick(false, true, false), TopiaForgePointerStep.None, "after which nothing more happens");
            AssertEqual(point.Tick(false, true, false), TopiaForgePointerStep.None, "a surface that does not show the cursor changes nothing");

            point.Tick(true, true, false);
            point.Superseded();
            AssertEqual(point.Tick(true, true, true), TopiaForgePointerStep.None, "the game's pause menu turning its map on meanwhile");
            AssertEqual(point.Tick(false, true, true), TopiaForgePointerStep.None, "stands as the surface closes: the menu keeps its pointer");

            point.Tick(true, true, false);
            point.Superseded();
            AssertEqual(point.Tick(true, true, false), TopiaForgePointerStep.Enable, "the game turning it off again under a surface is taken afresh");
            AssertEqual(point.Tick(false, true, true), TopiaForgePointerStep.Disable, "and given back as the surface closes");

            point.Tick(true, true, false);
            Assert(point.Release(enabled: true), "replaced or stopped while held, the action the kit turned on goes back off");
            Assert(!point.Release(enabled: true), "once");
            point.Tick(true, true, false);
            Assert(!point.Release(enabled: false), "and one already off stays as it is");
            AssertEqual(point.Tick(true, false, false), TopiaForgePointerStep.None, "a module with no such action needs nothing");
            Assert(!point.Held, "and holds nothing");
        }

        /// <summary>A kit scroll view moves its content just enough to show the control the keyboard selects.</summary>
        private static void AScrollViewShowsTheControlTheKeyboardSelects()
        {
            const float Margin = TopiaForgeScrollReveal.Margin;
            AssertEqual(TopiaForgeScrollReveal.Vertical(0f, 300f, 100f, 140f), 0f, "a control shown in full moves nothing");
            AssertEqual(TopiaForgeScrollReveal.Vertical(0f, 300f, -40f, 0f), 40f + Margin, "one below the view moves up into it, a margin inside");
            AssertEqual(TopiaForgeScrollReveal.Vertical(0f, 300f, -20f, 20f), 20f + Margin, "and so does one cut off at the bottom");
            AssertEqual(TopiaForgeScrollReveal.Vertical(0f, 300f, 320f, 360f), -60f - Margin, "one above it moves down into it");
            AssertEqual(TopiaForgeScrollReveal.Vertical(0f, 300f, 280f, 320f), -20f - Margin, "as one cut off at the top does");
            AssertEqual(TopiaForgeScrollReveal.Vertical(0f, 300f, -500f, -100f), 400f, "one taller than the view shows its top edge");
            AssertEqual(TopiaForgeScrollReveal.Vertical(0f, 100f, -10f, 86f), 12f, "one that nearly fills the view keeps what margin fits");
            AssertEqual(TopiaForgeScrollReveal.Horizontal(0f, 200f, 210f, 250f), -50f - Margin, "a control to the right moves left into the view");
            AssertEqual(TopiaForgeScrollReveal.Horizontal(0f, 100f, 50f, 300f), -50f, "one wider than the view shows its left edge");
            AssertEqual(TopiaForgeScrollReveal.Vertical(0f, 0f, -40f, 0f), 0f, "a view with no size moves nothing");
            AssertEqual(TopiaForgeScrollReveal.Vertical(0f, 300f, float.NaN, 0f), 0f, "nor does a control with no place");
        }
    }
}
