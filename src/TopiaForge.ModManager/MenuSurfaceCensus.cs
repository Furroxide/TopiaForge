using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// One UI surface observed in the main-menu scene, reduced to plain data so the reporting below is
    /// testable without a running player. Purely diagnostic: nothing about mounting the manager's menu
    /// buttons depends on finding a surface here.
    /// </summary>
    internal readonly struct MenuSurfaceCandidate
    {
        /// <summary>A uGUI <c>Canvas</c>.</summary>
        public const string UguiCanvasKind = "ugui-canvas";

        /// <summary>A UI Toolkit runtime panel (a <c>UIDocument</c>).</summary>
        public const string UiToolkitPanelKind = "uitoolkit-panel";

        public MenuSurfaceCandidate(string kind, string name, int sortingOrder, int interactiveCount)
        {
            Kind = kind ?? string.Empty;
            Name = name ?? string.Empty;
            SortingOrder = sortingOrder;
            InteractiveCount = interactiveCount;
        }

        public string Kind { get; }

        public string Name { get; }

        public int SortingOrder { get; }

        /// <summary>Interactable controls found under the surface, when they can be counted cheaply.</summary>
        public int InteractiveCount { get; }
    }

    /// <summary>
    /// Reporting for the manager's main-menu entry point.
    /// <para>
    /// The manager used to hunt for one of the game's own canvases to parent its GAMEMODES and
    /// TOPIAFORGE buttons onto: first a component named <c>LevelSelectController</c>, then any canvas
    /// holding buttons with a legacy <c>UnityEngine.UI.Text</c> child. Robotopia build 2409 satisfies
    /// neither. Its main menu is UI Toolkit — <c>StartMenuApp</c> on ClockworkLabs Rish — and the menu
    /// scene contains no <c>Canvas</c>, no <c>Button</c> and no uGUI text at all. The lookup returned
    /// null on every frame, silently, so no button was ever created and TopiaForge looked uninstalled.
    /// </para>
    /// <para>
    /// The fix is to stop depending on the game's UI: the buttons now mount on the kit's own
    /// band-allocated canvas, which sits above the game's UI whatever framework the game uses. What
    /// remains here is the census that makes the surrounding conditions visible in <c>manager.log</c>,
    /// so the next time an assumption about the menu stops holding it is stated rather than silent.
    /// </para>
    /// </summary>
    internal static class MenuSurfaceCensus
    {
        /// <summary>
        /// Attempts before a failure to mount is escalated to a warning. At the injector's one-second
        /// retry this is roughly ten seconds, long enough that a menu still building is not reported
        /// as broken.
        /// </summary>
        public const int WarnAfterAttempts = 10;

        /// <summary>
        /// The kit names every canvas it creates <c>&lt;ownerId&gt;:&lt;layer&gt;</c>, and every TopiaForge owner id
        /// begins with this prefix. Matching the prefix keeps our own menu bar, toasts, modals and mod
        /// HUDs out of the census; the previous exact-match on a single overlay name did not.
        /// </summary>
        public const string OwnerIdPrefix = "io.github.furroxide.topiaforge";

        public static bool IsTopiaForgeOwned(string? surfaceName)
        {
            return !string.IsNullOrEmpty(surfaceName)
                && surfaceName!.StartsWith(OwnerIdPrefix, StringComparison.Ordinal);
        }

        /// <summary>True once a mount has failed for long enough to be worth reporting, and only once.</summary>
        public static bool ShouldWarn(int attempts, bool mounted, bool alreadyWarned)
        {
            return !mounted && !alreadyWarned && attempts >= WarnAfterAttempts;
        }

        /// <summary>
        /// One line describing where the manager's buttons went and what the game's menu looked like
        /// while it happened. Read together, "mounted" plus the surface list is enough to tell a
        /// working build from a broken one without attaching a debugger.
        /// </summary>
        public static string Describe(
            string sceneName,
            bool mounted,
            int attempts,
            int canvasSortingOrder,
            IReadOnlyList<MenuSurfaceCandidate>? surfaces)
        {
            var builder = new StringBuilder();
            builder.Append("Menu entry point ");
            builder.Append(mounted ? "mounted" : "NOT mounted");
            builder.Append(" in scene '").Append(sceneName).Append('\'');
            if (mounted)
            {
                builder.Append(" on its own canvas at sorting order ")
                    .Append(canvasSortingOrder.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(" after ").Append(attempts.ToString(CultureInfo.InvariantCulture)).Append(" attempt(s)");
            }

            builder.Append(". Game menu surfaces: ");
            AppendSurfaces(builder, surfaces);
            builder.Append('.');
            return builder.ToString();
        }

        private static void AppendSurfaces(StringBuilder builder, IReadOnlyList<MenuSurfaceCandidate>? surfaces)
        {
            var ugui = 0;
            var uiToolkit = 0;
            if (surfaces != null)
            {
                foreach (var surface in surfaces)
                {
                    if (string.Equals(surface.Kind, MenuSurfaceCandidate.UguiCanvasKind, StringComparison.Ordinal))
                    {
                        ugui++;
                    }
                    else if (string.Equals(surface.Kind, MenuSurfaceCandidate.UiToolkitPanelKind, StringComparison.Ordinal))
                    {
                        uiToolkit++;
                    }
                }
            }

            builder.Append(MenuSurfaceCandidate.UguiCanvasKind).Append('=').Append(ugui.ToString(CultureInfo.InvariantCulture));
            builder.Append(' ').Append(MenuSurfaceCandidate.UiToolkitPanelKind).Append('=')
                .Append(uiToolkit.ToString(CultureInfo.InvariantCulture));

            if (surfaces == null || surfaces.Count == 0)
            {
                return;
            }

            builder.Append(" [");
            for (var index = 0; index < surfaces.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append("; ");
                }

                var surface = surfaces[index];
                builder.Append(surface.Name)
                    .Append(" (").Append(surface.Kind)
                    .Append(", order ").Append(surface.SortingOrder.ToString(CultureInfo.InvariantCulture))
                    .Append(", ").Append(surface.InteractiveCount.ToString(CultureInfo.InvariantCulture))
                    .Append(" interactive)");
            }

            builder.Append(']');
        }
    }
}
