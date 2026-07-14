using System;
using System.Collections.Generic;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery
{
    /// <summary>
    /// The gallery shell: one window per scheme (Paper and HUD render side by side via
    /// a scheme switch), page tabs, and host-scoped accessibility toggles that exercise
    /// the live theme-refresh path without changing another mod's UI.
    /// </summary>
    internal sealed class GalleryWindow : IDisposable
    {
        private readonly UiHost ui;
        private readonly TopiaForgeWindow paperWindow;
        private readonly TopiaForgeWindow hudWindow;
        private readonly List<TopiaForgeToggle> highContrastControls = new List<TopiaForgeToggle>();
        private readonly List<TopiaForgeToggle> reducedMotionControls = new List<TopiaForgeToggle>();
        private readonly List<TopiaForgeSlider> uiScaleControls = new List<TopiaForgeSlider>();
        private TopiaForgeWindow active;
        private bool disposed;

        public GalleryWindow(UiHost host)
        {
            ui = host;
            paperWindow = Build(TopiaForgeScheme.Paper);
            hudWindow = Build(TopiaForgeScheme.Hud);
            active = paperWindow;
            ui.AccessibilityProfileChanged += SyncAccessibilityControls;
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

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            ui.AccessibilityProfileChanged -= SyncAccessibilityControls;
            Pages.HudPage.Reset();
            Pages.OverlaysPage.Reset();
            Pages.ShopPage.Reset();
        }

        private TopiaForgeWindow Build(TopiaForgeScheme scheme)
        {
            var window = ui.Window(
                "gallery-" + scheme.ToString().ToLowerInvariant(),
                "TOPIAFORGE GALLERY — " + scheme.ToString().ToUpperInvariant(),
                width: 760f,
                height: 640f,
                scheme: scheme);

            var column = window.Content;

            // Host controls: scheme swap + accessibility toggles (live theme refresh).
            var controls = column.Row(TopiaForgeGap.Sm);
            controls.Button("SWAP SCHEME", () =>
            {
                var next = active == paperWindow ? hudWindow : paperWindow;
                active.Close();
                active = next;
                active.Show();
            }, TopiaForgeButtonStyle.Outline);
            highContrastControls.Add(controls.Toggle(
                "High contrast", ui.AccessibilityProfile.HighContrast, SetHighContrast));
            reducedMotionControls.Add(controls.Toggle(
                "Reduced motion", ui.AccessibilityProfile.ReducedMotion, SetReducedMotion));
            uiScaleControls.Add(controls.Slider(
                "UI scale", 0.75f, 1.5f, ui.AccessibilityProfile.UiScale, SetUiScale));

            var tabs = column.Tabs("WIDGETS", "STATES", "LISTS", "SHOP", "OVERLAYS", "HUD", "MOTION");
            var pageHost = column.Scroll(TopiaForgeGap.Md, TopiaForgeGap.Sm);
            pageHost.Flex(1f, 1f);

            var pages = new System.Action<TopiaForgeContainer>[]
            {
                Pages.WidgetsPage.Build,
                Pages.StatesPage.Build,
                Pages.ListsPage.Build,
                Pages.ShopPage.Build,
                Pages.OverlaysPage.Build,
                Pages.HudPage.Build,
                Pages.MotionPage.Build,
            };

            void ShowPage(int index)
            {
                ui.Clear(pageHost.Content);
                pages[index](pageHost.Content);
                pageHost.ScrollToTop();
            }

            tabs.OnSelected(ShowPage);
            ShowPage(0);
            return window;
        }

        private void SyncAccessibilityControls()
        {
            for (var index = 0; index < highContrastControls.Count; index++)
            {
                highContrastControls[index].SetValue(ui.AccessibilityProfile.HighContrast);
            }

            for (var index = 0; index < reducedMotionControls.Count; index++)
            {
                reducedMotionControls[index].SetValue(ui.AccessibilityProfile.ReducedMotion);
            }

            for (var index = 0; index < uiScaleControls.Count; index++)
            {
                uiScaleControls[index].SetValue(ui.AccessibilityProfile.UiScale);
            }
        }

        private void SetHighContrast(bool value)
        {
            var current = ui.AccessibilityProfile;
            ui.SetAccessibilityProfile(new TopiaForgeAccessibilityProfile(
                value,
                current.UiScale,
                current.ReducedMotion,
                current.MotionIntensity));
        }

        private void SetReducedMotion(bool value)
        {
            var current = ui.AccessibilityProfile;
            ui.SetAccessibilityProfile(new TopiaForgeAccessibilityProfile(
                current.HighContrast,
                current.UiScale,
                value,
                current.MotionIntensity));
        }

        private void SetUiScale(float value)
        {
            var current = ui.AccessibilityProfile;
            ui.SetAccessibilityProfile(new TopiaForgeAccessibilityProfile(
                current.HighContrast,
                value,
                current.ReducedMotion,
                current.MotionIntensity));
        }
    }
}
