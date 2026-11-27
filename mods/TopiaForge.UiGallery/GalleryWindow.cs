using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using TopiaForge.UiGallery.Pages;

namespace TopiaForge.UiGallery
{
    /// <summary>
    /// The gallery shell: one window per scheme (Paper and HUD render side by side via
    /// a scheme switch), page tabs, and host-scoped accessibility toggles that exercise
    /// the live theme-refresh path without changing another mod's UI.
    /// </summary>
    internal sealed class GalleryWindow : IDisposable
    {
        private static readonly string[] PreviewPalettes = { "Kit", "Survival" };
        private readonly UiHost ui;
        private readonly IModContext context;
        private readonly ILocalPlayerService player;
        private readonly IUiService previewUi;
        private readonly IModLogger logger;
        private readonly TopiaForgeWindow paperWindow;
        private readonly TopiaForgeWindow hudWindow;
        private readonly List<TopiaForgeToggle> highContrastControls = new List<TopiaForgeToggle>();
        private readonly List<TopiaForgeToggle> reducedMotionControls = new List<TopiaForgeToggle>();
        private readonly List<TopiaForgeSlider> uiScaleControls = new List<TopiaForgeSlider>();
        private readonly List<TopiaForgeDropdown> paletteControls = new List<TopiaForgeDropdown>();
        private int previewPalette = 1;
        private TopiaForgeWindow active;
        private IUiSurface? preview;
        private (UiAccessibilityPreferences Accessibility, UiThemeRef? ClearedTheme)? priorPreviewSettings;
        private GalleryPresentation? presentation;
        private bool disposed;

        public GalleryWindow(UiHost host, IModContext context)
        {
            ui = host;
            this.context = context;
            player = context.LocalPlayer;
            previewUi = context.Ui;
            logger = context.Logger;
            paperWindow = Build(TopiaForgeScheme.Paper);
            hudWindow = Build(TopiaForgeScheme.Hud);
            active = paperWindow;
            ui.AccessibilityProfileChanged += SyncAccessibilityControls;
        }

        /// <summary>Drives the animated demos (synthetic voice, auto-walking stepper, orbiting tag).</summary>
        public void Tick(float deltaTime)
        {
            if (!disposed)
            {
                if (preview != null && !preview.IsVisible)
                {
                    ReturnToGallery();
                }

                if (presentation != null && !presentation.Tick())
                {
                    ReturnToGallery();
                }

                Pages.MultiplayerPage.Tick(deltaTime);
                Pages.SessionsPage.Tick(deltaTime);
            }
        }

        public void Toggle()
        {
            if (disposed) return;

            if (preview != null || presentation != null)
            {
                ReturnToGallery();
                return;
            }

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
            ClosePreview();
            Pages.HudPage.Reset();
            Pages.OverlaysPage.Reset();
            Pages.ShopPage.Reset();
            Pages.MultiplayerPage.Reset();
            Pages.SessionsPage.Reset();
        }

        private void BuildOverlays(TopiaForgeContainer page)
        {
            var demos = page.Row(TopiaForgeGap.Sm);
            demos.Button("Stingers", () => ShowPresentation(PresentationPage.Stingers), TopiaForgeButtonStyle.Outline);
            demos.Button("Overlay examples", () => ShowPresentation(PresentationPage.Overlays), TopiaForgeButtonStyle.Outline);
            demos.Button("Kit toasts", () => ShowPresentation(PresentationPage.Toasts), TopiaForgeButtonStyle.Outline);
            Pages.OverlaysPage.Build(page);
        }

        private void ShowPresentation(PresentationPage page)
        {
            if (disposed || preview != null || presentation != null) return;
            presentation = GalleryPresentation.Open(context, page, PreviewTheme, PreviewPreferences(ui.AccessibilityProfile));
            if (presentation != null) active.Close();
        }

        private static UiAccessibilityPreferences PreviewPreferences(TopiaForgeAccessibilityProfile profile) =>
            new UiAccessibilityPreferences(profile.HighContrast, profile.UiScale, profile.ReducedMotion, profile.MotionIntensity);

        private TopiaForgeWindow Build(TopiaForgeScheme scheme)
        {
            var window = ui.Window(
                "gallery-" + scheme.ToString().ToLowerInvariant(),
                "TOPIAFORGE GALLERY — " + scheme.ToString().ToUpperInvariant(),
                width: 860f,
                height: 640f,
                scheme: scheme,
                persistent: true);

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
                "UI scale", 0.75f, 2f, ui.AccessibilityProfile.UiScale, SetUiScale));

            var foundation = column.Row(TopiaForgeGap.Sm);
            var previewKind = scheme == TopiaForgeScheme.Paper ? UiSurfaceKind.Window : UiSurfaceKind.Hud;
            foundation.Button("Theme packs", () => ShowFoundation(FoundationPage.ThemePacks, previewKind));
            foundation.Button("Typography", () => ShowFoundation(FoundationPage.Typography, previewKind));
            if (previewKind == UiSurfaceKind.Window)
            {
                foundation.Button("Interaction states", () => ShowFoundation(FoundationPage.InteractionStates, previewKind));
            }
            var palettes = column.Row(TopiaForgeGap.Sm);
            palettes.Label("Preview theme", TopiaForgeTextStyle.Caption);
            paletteControls.Add(palettes.Dropdown(PreviewPalettes, previewPalette, SelectPreviewPalette));
            var layoutPages = column.Row(TopiaForgeGap.Sm);
            layoutPages.Button("Flex and padding", () => ShowFoundation(FoundationPage.FlexAndPadding, previewKind));
            layoutPages.Button("Wrap and grid", () => ShowFoundation(FoundationPage.WrapAndGrid, previewKind));
            layoutPages.Button("Stack and bounds", () => ShowFoundation(FoundationPage.StackAndBounds, previewKind));
            layoutPages.Button("Cards and dividers", () => ShowFoundation(FoundationPage.CardsAndDividers, previewKind));
            if (previewKind == UiSurfaceKind.Window)
                layoutPages.Button("Graph connections", () => ShowFoundation(FoundationPage.GraphConnections, previewKind));
            if (previewKind == UiSurfaceKind.Hud)
                layoutPages.Button("Layout fit", () => ShowSafePreview(FoundationLayoutPages.LayoutNotice()));

            var artPages = column.Row(TopiaForgeGap.Sm);
            artPages.Button("Icons", () => ShowFoundation(FoundationPage.Icons, previewKind));
            artPages.Button("Images", () => ShowFoundation(FoundationPage.Images, previewKind));
            artPages.Button("Badges and pips", () => ShowFoundation(FoundationPage.BadgesAndPips, previewKind));
            artPages.Button("Foundation integration", () => ShowFoundation(FoundationPage.FoundationIntegration, previewKind));
            artPages.Button("Trust and fallback", () => ShowFoundation(FoundationPage.TrustAndFallback, previewKind));

            if (previewKind == UiSurfaceKind.Window)
            {
                var kitControls = column.Row(TopiaForgeGap.Sm);
                kitControls.Button("Buttons", () => ShowControls(ControlGalleryPage.Buttons));
                kitControls.Button("Inputs", () => ShowControls(ControlGalleryPage.Fields));
                kitControls.Button("Selections", () => ShowControls(ControlGalleryPage.Selections));
                kitControls.Button("Bindings", () => ShowControls(ControlGalleryPage.Bindings));
                kitControls.Button("Help and tiles", () => ShowControls(ControlGalleryPage.HelpAndTiles));
            }

            var statusPages = column.Row(TopiaForgeGap.Sm);
            statusPages.Button("Status", () => ShowStatus(StatusGalleryPage.Status, previewKind));
            statusPages.Button("Countdowns", () => ShowStatus(StatusGalleryPage.Countdowns, previewKind));
            statusPages.Button("Key glyphs", () => ShowActions(ActionGalleryPage.KeyGlyphs, previewKind));
            statusPages.Button("Action slots", () => ShowActions(ActionGalleryPage.Slots, previewKind));

            var tabs = column.Tabs("WIDGETS", "STATES", "LISTS", "GRAPH", "SHOP", "OVERLAYS", "HUD", "MOTION", "ONLINE", "SESSIONS");
            var pageHost = column.Scroll(TopiaForgeGap.Md, TopiaForgeGap.Sm);
            pageHost.Flex(1f, 1f);

            var pages = new System.Action<TopiaForgeContainer>[]
            {
                Pages.WidgetsPage.Build,
                Pages.StatesPage.Build,
                Pages.ListsPage.Build,
                Pages.GraphPage.Build,
                Pages.ShopPage.Build,
                BuildOverlays,
                Pages.HudPage.Build,
                Pages.MotionPage.Build,
                page => Pages.MultiplayerPage.Build(page, player),
                page => Pages.SessionsPage.Build(page, ui),
            };

            void ShowPage(int index)
            {
                Pages.MultiplayerPage.Detach(pageHost.Content);
                Pages.SessionsPage.Detach(pageHost.Content);
                ui.Clear(pageHost.Content);
                pages[index](pageHost.Content);
                pageHost.ScrollToTop();
            }

            tabs.OnSelected(ShowPage);
            ShowPage(0);
            return window;
        }

        private void ShowFoundation(FoundationPage page, UiSurfaceKind kind) =>
            ShowSafePreview(FoundationPages.CreateRequest(page, kind, PreviewTheme, ReportInteraction));

        private void ShowControls(ControlGalleryPage page) =>
            ShowSafePreview(ControlGalleryPages.Create(page, report: ReportInteraction));

        private void ShowStatus(StatusGalleryPage page, UiSurfaceKind kind) =>
            ShowSafePreview(StatusGalleryPages.CreateRequest(page, kind, previewUi.ClockTime));

        private void ShowActions(ActionGalleryPage page, UiSurfaceKind kind) =>
            ShowSafePreview(ActionGalleryPages.CreateRequest(page, kind, previewUi.ClockTime));

        private void ShowSafePreview(UiSurfaceRequest request)
        {
            var profile = ui.AccessibilityProfile;
            if (preview != null || presentation != null) return;
            var theme = request.Theme ?? PreviewTheme;
            var previous = previewUi.Accessibility;
            var clearedTheme = theme == null ? previewUi.Themes.SelectedTheme : null;
            if (clearedTheme != null)
            {
                var cleared = previewUi.Themes.UseTheme(null);
                if (!cleared.Succeeded)
                {
                    logger.Warn("Gallery preview could not select Kit (" + cleared.ErrorCode + "): " + cleared.ErrorMessage);
                    return;
                }
            }

            priorPreviewSettings = (previous, clearedTheme);
            try
            {
                var applied = previewUi.ApplyAccessibility(PreviewPreferences(profile));
                if (!applied.Succeeded)
                {
                    logger.Warn("Gallery preview settings could not be applied (" + applied.ErrorCode + "): " + applied.ErrorMessage);
                    return;
                }

                var result = previewUi.CreateSurface(theme == null ? request : request.WithTheme(theme));
                if (!result.TryGetValue(out preview) || preview == null)
                {
                    logger.Warn("Gallery preview could not be opened (" + result.ErrorCode + "): " + result.ErrorMessage);
                    return;
                }

                active.Close();
            }
            finally
            {
                if (preview == null) RestorePreviewSettings();
            }
        }

        private void ReturnToGallery()
        {
            ClosePreview();
            active.Show();
        }

        private void ClosePreview()
        {
            try { preview?.Dispose(); }
            finally
            {
                preview = null;
                RestorePreviewSettings();
            }
            presentation?.Dispose();
            presentation = null;
        }

        private void RestorePreviewSettings()
        {
            var previous = priorPreviewSettings;
            priorPreviewSettings = null;
            if (!previous.HasValue || context.Lifetime.IsStopping) return;
            if (previous.Value.ClearedTheme != null)
            {
                var selected = previewUi.Themes.UseTheme(previous.Value.ClearedTheme);
                if (!selected.Succeeded)
                    logger.Warn("Gallery preview theme could not be restored (" + selected.ErrorCode + "): " + selected.ErrorMessage);
            }
            var restored = previewUi.ApplyAccessibility(previous.Value.Accessibility);
            if (!restored.Succeeded)
                logger.Warn("Gallery preview settings could not be restored (" + restored.ErrorCode + "): " + restored.ErrorMessage);
        }

        private void ReportInteraction(string message) => preview?.SetBody(message);

        private UiThemeRef? PreviewTheme => previewPalette == 1 ? FoundationPages.SurvivalTheme : null;

        private void SelectPreviewPalette(int index)
        {
            previewPalette = index;
            foreach (var control in paletteControls)
                control.SetValue(index);
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
