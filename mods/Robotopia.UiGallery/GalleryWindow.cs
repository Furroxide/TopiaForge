using Robotopia.Mods.UnityUi;

namespace Robotopia.UiGallery
{
    /// <summary>
    /// The gallery shell: one window per scheme (Paper and HUD render side by side via
    /// a scheme switch), page tabs, and global accessibility toggles that exercise the
    /// live theme-refresh path.
    /// </summary>
    internal sealed class GalleryWindow
    {
        private readonly UiHost ui;
        private readonly QwWindow paperWindow;
        private readonly QwWindow hudWindow;
        private QwWindow active;

        public GalleryWindow(UiHost host)
        {
            ui = host;
            paperWindow = Build(QwScheme.Paper);
            hudWindow = Build(QwScheme.Hud);
            active = paperWindow;
        }

        public void Toggle()
        {
            if (active.IsOpen)
            {
                active.Close();
            }
            else
            {
                active.Show();
            }
        }

        private QwWindow Build(QwScheme scheme)
        {
            var window = ui.Window(
                "gallery-" + scheme.ToString().ToLowerInvariant(),
                "QW GALLERY — " + scheme.ToString().ToUpperInvariant(),
                width: 760f,
                height: 640f,
                scheme: scheme);

            var column = window.Content;

            // Global controls: scheme swap + accessibility toggles (live theme refresh).
            var controls = column.Row(QwGap.Sm);
            controls.Button("SWAP SCHEME", () =>
            {
                var next = active == paperWindow ? hudWindow : paperWindow;
                active.Close();
                active = next;
                active.Show();
            }, QwButtonStyle.Outline);
            controls.Toggle("High contrast", QwTheme.HighContrast, v => QwTheme.HighContrast = v);
            controls.Toggle("Reduced motion", QwTheme.ReducedMotion, v => QwTheme.ReducedMotion = v);
            controls.Slider("UI scale", 0.75f, 1.5f, QwTheme.UiScale, v => QwTheme.UiScale = v);

            var tabs = column.Tabs("WIDGETS", "LISTS", "OVERLAYS", "HUD", "MOTION");
            var pageHost = column.Scroll(QwGap.Md, QwGap.Sm);
            pageHost.Flex(1f, 1f);

            var pages = new System.Action<QwContainer>[]
            {
                Pages.WidgetsPage.Build,
                Pages.ListsPage.Build,
                Pages.OverlaysPage.Build,
                Pages.HudPage.Build,
                Pages.MotionPage.Build,
            };

            void ShowPage(int index)
            {
                foreach (UnityEngine.Transform child in pageHost.Content.Go.transform)
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }

                pages[index](pageHost.Content);
                pageHost.ScrollToTop();
            }

            tabs.OnSelected(ShowPage);
            ShowPage(0);
            return window;
        }
    }
}
