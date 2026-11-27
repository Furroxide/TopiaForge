using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Per-owner root of the kit: creates band-allocated canvas layers, resolves and
    /// caches the two scheme themes (re-resolving when the global theme version moves),
    /// walks its widgets on theme change (no rebuilds — focus/scroll/selection
    /// survive), and tears everything down on Dispose.
    /// </summary>
    public sealed partial class UiHost : IDisposable
    {
        private readonly List<object> hotkeyRegistrations = new List<object>();
        private readonly List<GameObject> layerRoots = new List<GameObject>();
        private readonly List<CanvasScaler> scalers = new List<CanvasScaler>();
        private readonly List<ITopiaForgeThemeAware> themeAware = new List<ITopiaForgeThemeAware>();
        private readonly List<TopiaForgeWidget> widgets = new List<TopiaForgeWidget>();
        private readonly List<TopiaForgeWindow> windows = new List<TopiaForgeWindow>();
        private readonly List<TopiaForgeFullscreenTool> fullscreenTools = new List<TopiaForgeFullscreenTool>();
        private readonly List<TopiaForgeOverlay> overlays = new List<TopiaForgeOverlay>();
        private readonly List<TopiaForgeModalInstance> modalInstances = new List<TopiaForgeModalInstance>();
        private TopiaForgeModals? modals;
        private TopiaForgeResolvedTheme? paperTheme;
        private TopiaForgeResolvedTheme? hudTheme;
        private TopiaForgeRgba? accent;
        private bool disposed;
        internal bool PlainText { get; set; }

        internal UiHost(TopiaForgeUiOptions options)
        {
            OwnerId = options.OwnerId;
            accent = options.Accent;
            accessibilityProfile = options.AccessibilityProfile ?? TopiaForgeAccessibilityProfile.Default;
            StateStore = string.IsNullOrEmpty(options.DataDirectory)
                ? (ITopiaForgeStateStore)new TopiaForgeMemoryStateStore()
                : new TopiaForgeFileStateStore(options.DataDirectory!);
            TopiaForgeTheme.Changed += OnThemeChanged;
        }

        /// <summary>Gets owner id.</summary>
        public string OwnerId { get; }

        /// <summary>Gets state store.</summary>
        public ITopiaForgeStateStore StateStore { get; }

        /// <summary>Resolved theme for a scheme, cached per global and host theme version.</summary>
        public TopiaForgeResolvedTheme Theme(TopiaForgeScheme scheme)
        {
            ThrowIfDisposed();
            if (scheme == TopiaForgeScheme.Paper)
            {
                if (paperTheme == null || paperTheme.ThemeVersion != themeRevision)
                {
                    paperTheme = new TopiaForgeResolvedTheme(
                        TopiaForgeScheme.Paper,
                        accent,
                        EffectiveHighContrast,
                        themeRevision);
                }

                return paperTheme;
            }

            if (hudTheme == null || hudTheme.ThemeVersion != themeRevision)
            {
                hudTheme = new TopiaForgeResolvedTheme(
                    TopiaForgeScheme.Hud,
                    accent,
                    EffectiveHighContrast,
                    themeRevision);
            }

            return hudTheme;
        }

        internal TopiaForgeResolvedTheme ThemeFor(TopiaForgeWidget widget) =>
            widget.ThemeSlot?.ResolveTheme(widget.Scheme, EffectiveHighContrast, themeRevision) ?? Theme(widget.Scheme);

        internal void SetSurfaceTheme(GameObject canvas, UiThemeDescriptor? descriptor,
            Func<UiTextStyle, TMPro.TMP_FontAsset?>? registeredFonts = null)
        {
            ThrowIfDisposed();
            var slot = canvas.GetComponent<TopiaForgeLayerSlot>();
            if (slot != null && slot.SelectTheme(descriptor, registeredFonts)) WalkThemeAware(slot);
        }

        /// <summary>Sets this host's accent override and re-tints its live widgets.</summary>
        public void SetAccent(TopiaForgeRgba? value)
        {
            ThrowIfDisposed();
            if (Nullable.Equals(accent, value))
            {
                return;
            }

            accent = value;
            RefreshResolvedTheme(reapplyScalers: false);
        }

        /// <summary>
        /// Dark-scheme gameplay overlay layer with Scaled/World roots, floater pools,
        /// and a banner. Raycasting starts off; enable it during gameplay modals.
        /// </summary>
        public TopiaForgeHudLayer HudLayer(string name, bool persistent = false)
        {
            var canvasRoot = Layer(name, TopiaForgeLayerBand.Hud, TopiaForgeScheme.Hud, interactive: false, persistent);
            return new TopiaForgeHudLayer(this, canvasRoot);
        }

        /// <summary>Shows a process-wide toast notification.</summary>
        public void Toast(string text, TopiaForgeTone tone = TopiaForgeTone.Neutral)
        {
            ThrowIfDisposed();
            TopiaForgeToasts.Show(text, tone);
        }

        /// <summary>
        /// Creates a draggable brand window (hidden until Show()). Height 0 = grows
        /// with content. Rect persists per owner+id in the state store.
        /// </summary>
        public TopiaForgeWindow Window(string id, string title, float width = 460f, float height = 0f, TopiaForgeScheme scheme = TopiaForgeScheme.Paper, bool persistent = false)
        {
            var layer = Layer("window:" + id, TopiaForgeLayerBand.Window, scheme, interactive: true, persistent);
            var window = new TopiaForgeWindow(this, layer, id, title, width, height);
            windows.Add(window);
            return window;
        }

        /// <summary>
        /// Creates an immersive tool that fills the screen's safe area, traps UI focus,
        /// leases the cursor while shown, and closes through the shared Escape stack.
        /// </summary>
        public TopiaForgeFullscreenTool FullscreenTool(
            string id,
            string title,
            TopiaForgeScheme scheme = TopiaForgeScheme.Paper,
            bool persistent = false)
        {
            var layer = Layer("fullscreen:" + id, TopiaForgeLayerBand.Window, scheme, interactive: true, persistent);
            var tool = new TopiaForgeFullscreenTool(this, layer, title);
            fullscreenTools.Add(tool);
            return tool;
        }

        /// <summary>Modal dialog presets (Confirm/Destructive/Custom).</summary>
        public TopiaForgeModals Modal
        {
            get
            {
                ThrowIfDisposed();
                return modals ??= new TopiaForgeModals(this);
            }
        }

        /// <summary>Registers a global hotkey owned by this host (unregistered on Dispose).</summary>
        public object Hotkey(TopiaForgeKey key, Action action)
        {
            ThrowIfDisposed();
            var registration = TopiaForgeHotkeys.Register(OwnerId, key, action);
            hotkeyRegistrations.Add(registration);
            return registration;
        }

        /// <summary>Creates a canvas layer in a sorting band and wraps it as a container.</summary>
        public TopiaForgeContainer Layer(string name, TopiaForgeLayerBand band, TopiaForgeScheme scheme, bool interactive, bool persistent = false)
        {
            ThrowIfDisposed();
            ReportInitOnce();
            return TrackLayer(TopiaForgeLayers.CreateCanvas(OwnerId + ":" + name, band, interactive, persistent), scheme);
        }

        internal TopiaForgeContainer KitLayer(string name, TopiaForgeLayerBand band, TopiaForgeScheme scheme,
            bool interactive, bool persistent)
        {
            ThrowIfDisposed();
            ReportInitOnce();
            return TrackLayer(TopiaForgeLayers.CreateKitCanvas(OwnerId + ":" + name, band, interactive, persistent), scheme);
        }

        internal TopiaForgeContainer TrustLayer(bool persistent, bool overlay = false)
        {
            ThrowIfDisposed();
            ReportInitOnce();
            return TrackLayer(TopiaForgeLayers.CreateTrustCanvas(OwnerId + ":trust", persistent, overlay), TopiaForgeScheme.Paper);
        }

        private TopiaForgeContainer TrackLayer(GameObject root, TopiaForgeScheme scheme)
        {
            if (PlainText) root.AddComponent<TopiaForgePlainTextRoot>();
            TopiaForgeLayers.WatchLoss(root, HandleLayerLost);
            layerRoots.Add(root);
            var scaler = root.GetComponent<CanvasScaler>();
            TopiaForgeLayers.ApplyScaler(scaler, EffectiveUiScale);
            scalers.Add(scaler);
            return new TopiaForgeContainer(this, scheme, root);
        }

        internal void RegisterThemeAware(ITopiaForgeThemeAware widget)
        {
            themeAware.Add(widget);
        }

        internal void UnregisterThemeAware(ITopiaForgeThemeAware widget)
        {
            themeAware.Remove(widget);
        }

        internal void RegisterWidget(TopiaForgeWidget widget)
        {
            widgets.Add(widget);
        }

        internal void RegisterModal(TopiaForgeModalInstance modal)
        {
            modalInstances.Add(modal);
        }

        internal void UnregisterModal(TopiaForgeModalInstance modal)
        {
            modalInstances.Remove(modal);
        }

        /// <summary>
        /// Destroys every child widget under a container and unregisters theme/tween state first.
        /// Use this when rebuilding dynamic pages instead of destroying Unity children directly.
        /// </summary>
        public void Clear(TopiaForgeContainer container)
        {
            if (container == null || container.Go == null)
            {
                return;
            }

            for (var index = container.Go.transform.childCount - 1; index >= 0; index--)
            {
                DestroySubtree(container.Go.transform.GetChild(index).gameObject);
            }
        }

        internal void DestroyWidget(TopiaForgeWidget widget)
        {
            if (widget != null && widget.Go != null)
            {
                var root = widget is TopiaForgeWindow window
                    ? window.CanvasRoot
                    : widget is TopiaForgeFullscreenTool fullscreen
                        ? fullscreen.CanvasRoot
                        : widget is TopiaForgeOverlay overlay ? overlay.CanvasRoot : widget.Go;
                DestroySubtree(root);
            }
        }

        internal void DestroyLayer(GameObject root)
        {
            DestroySubtree(root);
        }

        /// <summary>Releases the resources owned by this instance.</summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            TopiaForgeTheme.Changed -= OnThemeChanged;
            AccessibilityProfileChanged = null;
            foreach (var registration in hotkeyRegistrations) TopiaForgeHotkeys.Unregister(registration);
            hotkeyRegistrations.Clear();
            var failures = new List<Exception>();
            foreach (var modal in modalInstances.ToArray()) AttemptCleanup(modal.Teardown, failures);
            modalInstances.Clear();
            foreach (var tool in fullscreenTools.ToArray()) AttemptCleanup(tool.Teardown, failures);
            fullscreenTools.Clear();
            foreach (var overlay in overlays.ToArray()) AttemptCleanup(overlay.Teardown, failures);
            overlays.Clear();
            foreach (var window in windows.ToArray()) AttemptCleanup(window.Teardown, failures);
            windows.Clear();
            foreach (var widget in widgets.ToArray()) AttemptCleanup(() => TopiaForgeTween.Cancel(widget), failures);
            widgets.Clear();
            themeAware.Clear();
            foreach (var root in layerRoots.ToArray())
            {
                AttemptCleanup(() => TopiaForgeLayers.Release(root), failures);
                if (root != null) AttemptCleanup(() => UnityEngine.Object.Destroy(root), failures);
            }
            layerRoots.Clear();
            scalers.Clear();
            AttemptCleanup(() => TopiaForgeUi.OnHostDisposed(this), failures);
            if (failures.Count > 0) throw new AggregateException("UI host cleanup failed.", failures);
        }

        private static void AttemptCleanup(Action cleanup, List<Exception> failures)
        {
            try { cleanup(); }
            catch (Exception exception) { failures.Add(exception); }
        }

        private void OnThemeChanged()
        {
            RefreshResolvedTheme(reapplyScalers: true);
        }

        private void WalkThemeAware(TopiaForgeLayerSlot? layer = null)
        {
            for (var index = themeAware.Count - 1; index >= 0; index--)
            {
                var aware = themeAware[index];
                if (aware is TopiaForgeWidget deadWidget && deadWidget.Go == null)
                {
                    themeAware.RemoveAt(index);
                    widgets.Remove(deadWidget);
                    continue;
                }

                var widget = aware as TopiaForgeWidget;
                if (layer != null && !ReferenceEquals(widget?.ThemeSlot, layer)) continue;
                try
                {
                    aware.ApplyTheme(widget == null ? Theme(TopiaForgeScheme.Paper) : ThemeFor(widget));
                }
                catch (Exception exception)
                {
                    TopiaForgeLog.Warn("UiHost '" + OwnerId + "' could not refresh a widget theme: " + exception.Message);
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("UiHost '" + OwnerId + "'");
            }
        }
    }
}
