using System;
using System.Collections.Generic;
using Robotopia.Mods.UnityUi;

namespace Robotopia.UiGallery
{
    /// <summary>
    /// The gallery shell: one window per scheme (Paper and HUD render side by side via
    /// a scheme switch), page tabs, and host-scoped accessibility toggles that exercise
    /// the live theme-refresh path without changing another mod's UI.
    /// </summary>
    internal sealed class GalleryWindow : IDisposable
    {
        private readonly UiHost ui;
        private readonly QwWindow paperWindow;
        private readonly QwWindow hudWindow;
        private readonly List<QwToggle> highContrastControls = new List<QwToggle>();
        private readonly List<QwToggle> reducedMotionControls = new List<QwToggle>();
        private readonly List<QwSlider> uiScaleControls = new List<QwSlider>();
        private QwWindow active;
        private bool disposed;

        public GalleryWindow(UiHost host)
        {
            ui = host;
            paperWindow = Build(QwScheme.Paper);
            hudWindow = Build(QwScheme.Hud);
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

        private QwWindow Build(QwScheme scheme)
        {
            var window = ui.Window(
                "gallery-" + scheme.ToString().ToLowerInvariant(),
                "QW GALLERY — " + scheme.ToString().ToUpperInvariant(),
                width: 760f,
                height: 640f,
                scheme: scheme);

            var column = window.Content;

            // Host controls: scheme swap + accessibility toggles (live theme refresh).
            var controls = column.Row(QwGap.Sm);
            controls.Button("SWAP SCHEME", () =>
            {
                var next = active == paperWindow ? hudWindow : paperWindow;
                active.Close();
                active = next;
                active.Show();
            }, QwButtonStyle.Outline);
            highContrastControls.Add(controls.Toggle(
                "High contrast", ui.AccessibilityProfile.HighContrast, SetHighContrast));
            reducedMotionControls.Add(controls.Toggle(
                "Reduced motion", ui.AccessibilityProfile.ReducedMotion, SetReducedMotion));
            uiScaleControls.Add(controls.Slider(
                "UI scale", 0.75f, 1.5f, ui.AccessibilityProfile.UiScale, SetUiScale));

            var tabs = column.Tabs("WIDGETS", "STATES", "LISTS", "SHOP", "OVERLAYS", "HUD", "MOTION");
            var pageHost = column.Scroll(QwGap.Md, QwGap.Sm);
            pageHost.Flex(1f, 1f);

            var pages = new System.Action<QwContainer>[]
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
            ui.SetAccessibilityProfile(new QwAccessibilityProfile(
                value,
                current.UiScale,
                current.ReducedMotion,
                current.MotionIntensity));
        }

        private void SetReducedMotion(bool value)
        {
            var current = ui.AccessibilityProfile;
            ui.SetAccessibilityProfile(new QwAccessibilityProfile(
                current.HighContrast,
                current.UiScale,
                value,
                current.MotionIntensity));
        }

        private void SetUiScale(float value)
        {
            var current = ui.AccessibilityProfile;
            ui.SetAccessibilityProfile(new QwAccessibilityProfile(
                current.HighContrast,
                value,
                current.ReducedMotion,
                current.MotionIntensity));
        }
    }
}
