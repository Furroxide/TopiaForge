using System;
using System.Collections.Generic;
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
        private readonly List<GameObject> layerRoots = new List<GameObject>();
        private readonly List<CanvasScaler> scalers = new List<CanvasScaler>();
        private readonly List<ITopiaForgeThemeAware> themeAware = new List<ITopiaForgeThemeAware>();
        private readonly List<TopiaForgeWidget> widgets = new List<TopiaForgeWidget>();
        private readonly List<TopiaForgeWindow> windows = new List<TopiaForgeWindow>();
        private readonly List<TopiaForgeModalInstance> modalInstances = new List<TopiaForgeModalInstance>();
        private TopiaForgeModals? modals;
        private TopiaForgeResolvedTheme? paperTheme;
        private TopiaForgeResolvedTheme? hudTheme;
        private TopiaForgeRgba? accent;
        private bool disposed;
        private bool initReported;

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

        public string OwnerId { get; }

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
            return TopiaForgeHotkeys.Register(OwnerId, key, action);
        }

        /// <summary>Creates a canvas layer in a sorting band and wraps it as a container.</summary>
        public TopiaForgeContainer Layer(string name, TopiaForgeLayerBand band, TopiaForgeScheme scheme, bool interactive, bool persistent = false)
        {
            ThrowIfDisposed();
            ReportInitOnce();
            var root = TopiaForgeLayers.CreateCanvas(OwnerId + ":" + name, band, interactive, persistent);
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
                DestroySubtree(widget is TopiaForgeWindow window ? window.CanvasRoot : widget.Go);
            }
        }

        internal void DestroyLayer(GameObject root)
        {
            DestroySubtree(root);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            TopiaForgeTheme.Changed -= OnThemeChanged;
            AccessibilityProfileChanged = null;
            TopiaForgeHotkeys.UnregisterOwner(OwnerId);
            while (modalInstances.Count > 0)
            {
                modalInstances[modalInstances.Count - 1].Teardown();
            }

            for (var index = windows.Count - 1; index >= 0; index--)
            {
                windows[index].Teardown();
            }

            windows.Clear();
            for (var index = widgets.Count - 1; index >= 0; index--)
            {
                TopiaForgeTween.Cancel(widgets[index]);
            }

            widgets.Clear();
            themeAware.Clear();
            foreach (var root in layerRoots)
            {
                if (root != null)
                {
                    TopiaForgeLayers.Release(root);
                    UnityEngine.Object.Destroy(root);
                }
            }

            layerRoots.Clear();
            scalers.Clear();
            TopiaForgeUi.OnHostDisposed(this);
        }

        private void DestroySubtree(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            for (var index = modalInstances.Count - 1; index >= 0; index--)
            {
                var modalRoot = modalInstances[index].CanvasRoot;
                if (modalRoot == root || modalRoot != null && modalRoot.transform.IsChildOf(root.transform))
                {
                    modalInstances[index].Teardown();
                }
            }

            for (var index = windows.Count - 1; index >= 0; index--)
            {
                var window = windows[index];
                if (window.Go == root || window.Go != null && window.Go.transform.IsChildOf(root.transform))
                {
                    window.Teardown();
                    windows.RemoveAt(index);
                }
            }

            for (var index = widgets.Count - 1; index >= 0; index--)
            {
                var widget = widgets[index];
                if (widget.Go != root && (widget.Go == null || !widget.Go.transform.IsChildOf(root.transform)))
                {
                    continue;
                }

                TopiaForgeTween.Cancel(widget);
                if (widget is ITopiaForgeThemeAware aware)
                {
                    themeAware.Remove(aware);
                }

                widgets.RemoveAt(index);
            }

            var layerIndex = layerRoots.IndexOf(root);
            if (layerIndex >= 0)
            {
                TopiaForgeLayers.Release(root);
                layerRoots.RemoveAt(layerIndex);
                scalers.RemoveAt(layerIndex);
            }

            UnityEngine.Object.Destroy(root);
        }

        private void OnThemeChanged()
        {
            RefreshResolvedTheme(reapplyScalers: true);
        }

        private void WalkThemeAware()
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

                var scheme = aware is TopiaForgeWidget widget ? widget.Scheme : TopiaForgeScheme.Paper;
                try
                {
                    aware.ApplyTheme(Theme(scheme));
                }
                catch (Exception exception)
                {
                    TopiaForgeLog.Warn("UiHost '" + OwnerId + "' could not refresh a widget theme: " + exception.Message);
                }
            }
        }

        private void ReportInitOnce()
        {
            if (initReported)
            {
                return;
            }

            initReported = true;
            TopiaForgeLog.Info(
                "UiHost '" + OwnerId + "' initialized (fonts: " + TopiaForgeFonts.ResolvedTier +
                ", input: " + (TopiaForgeInput.LegacyAvailable ? "legacy/both" : "input-system") +
                ", ui-scale: " + EffectiveUiScale.ToString("0.##") +
                ", high-contrast: " + (EffectiveHighContrast ? "on" : "off") + ").");
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
