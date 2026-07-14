using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Robotopia.Mods.UnityUi
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
        private readonly List<IQwThemeAware> themeAware = new List<IQwThemeAware>();
        private readonly List<QwWidget> widgets = new List<QwWidget>();
        private readonly List<QwWindow> windows = new List<QwWindow>();
        private readonly List<QwModalInstance> modalInstances = new List<QwModalInstance>();
        private QwModals? modals;
        private QwResolvedTheme? paperTheme;
        private QwResolvedTheme? hudTheme;
        private QwRgba? accent;
        private bool disposed;
        private bool initReported;

        internal UiHost(QwUiOptions options)
        {
            OwnerId = options.OwnerId;
            accent = options.Accent;
            accessibilityProfile = options.AccessibilityProfile ?? QwAccessibilityProfile.Default;
            StateStore = string.IsNullOrEmpty(options.DataDirectory)
                ? (IQwStateStore)new QwMemoryStateStore()
                : new QwFileStateStore(options.DataDirectory!);
            QwTheme.Changed += OnThemeChanged;
        }

        public string OwnerId { get; }

        public IQwStateStore StateStore { get; }

        /// <summary>Resolved theme for a scheme, cached per global and host theme version.</summary>
        public QwResolvedTheme Theme(QwScheme scheme)
        {
            ThrowIfDisposed();
            if (scheme == QwScheme.Paper)
            {
                if (paperTheme == null || paperTheme.ThemeVersion != themeRevision)
                {
                    paperTheme = new QwResolvedTheme(
                        QwScheme.Paper,
                        accent,
                        EffectiveHighContrast,
                        themeRevision);
                }

                return paperTheme;
            }

            if (hudTheme == null || hudTheme.ThemeVersion != themeRevision)
            {
                hudTheme = new QwResolvedTheme(
                    QwScheme.Hud,
                    accent,
                    EffectiveHighContrast,
                    themeRevision);
            }

            return hudTheme;
        }

        /// <summary>Sets this host's accent override and re-tints its live widgets.</summary>
        public void SetAccent(QwRgba? value)
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
        public QwHudLayer HudLayer(string name, bool persistent = false)
        {
            var canvasRoot = Layer(name, QwLayerBand.Hud, QwScheme.Hud, interactive: false, persistent);
            return new QwHudLayer(this, canvasRoot);
        }

        /// <summary>Shows a process-wide toast notification.</summary>
        public void Toast(string text, QwTone tone = QwTone.Neutral)
        {
            ThrowIfDisposed();
            QwToasts.Show(text, tone);
        }

        /// <summary>
        /// Creates a draggable brand window (hidden until Show()). Height 0 = grows
        /// with content. Rect persists per owner+id in the state store.
        /// </summary>
        public QwWindow Window(string id, string title, float width = 460f, float height = 0f, QwScheme scheme = QwScheme.Paper, bool persistent = false)
        {
            var layer = Layer("window:" + id, QwLayerBand.Window, scheme, interactive: true, persistent);
            var window = new QwWindow(this, layer, id, title, width, height);
            windows.Add(window);
            return window;
        }

        /// <summary>Modal dialog presets (Confirm/Destructive/Custom).</summary>
        public QwModals Modal
        {
            get
            {
                ThrowIfDisposed();
                return modals ??= new QwModals(this);
            }
        }

        /// <summary>Registers a global hotkey owned by this host (unregistered on Dispose).</summary>
        public object Hotkey(QwKey key, Action action)
        {
            ThrowIfDisposed();
            return QwHotkeys.Register(OwnerId, key, action);
        }

        /// <summary>Creates a canvas layer in a sorting band and wraps it as a container.</summary>
        public QwContainer Layer(string name, QwLayerBand band, QwScheme scheme, bool interactive, bool persistent = false)
        {
            ThrowIfDisposed();
            ReportInitOnce();
            var root = QwLayers.CreateCanvas(OwnerId + ":" + name, band, interactive, persistent);
            layerRoots.Add(root);
            var scaler = root.GetComponent<CanvasScaler>();
            QwLayers.ApplyScaler(scaler, EffectiveUiScale);
            scalers.Add(scaler);
            return new QwContainer(this, scheme, root);
        }

        internal void RegisterThemeAware(IQwThemeAware widget)
        {
            themeAware.Add(widget);
        }

        internal void UnregisterThemeAware(IQwThemeAware widget)
        {
            themeAware.Remove(widget);
        }

        internal void RegisterWidget(QwWidget widget)
        {
            widgets.Add(widget);
        }

        internal void RegisterModal(QwModalInstance modal)
        {
            modalInstances.Add(modal);
        }

        internal void UnregisterModal(QwModalInstance modal)
        {
            modalInstances.Remove(modal);
        }

        /// <summary>
        /// Destroys every child widget under a container and unregisters theme/tween state first.
        /// Use this when rebuilding dynamic pages instead of destroying Unity children directly.
        /// </summary>
        public void Clear(QwContainer container)
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

        internal void DestroyWidget(QwWidget widget)
        {
            if (widget != null && widget.Go != null)
            {
                DestroySubtree(widget is QwWindow window ? window.CanvasRoot : widget.Go);
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
            QwTheme.Changed -= OnThemeChanged;
            AccessibilityProfileChanged = null;
            QwHotkeys.UnregisterOwner(OwnerId);
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
                QwTween.Cancel(widgets[index]);
            }

            widgets.Clear();
            themeAware.Clear();
            foreach (var root in layerRoots)
            {
                if (root != null)
                {
                    QwLayers.Release(root);
                    UnityEngine.Object.Destroy(root);
                }
            }

            layerRoots.Clear();
            scalers.Clear();
            QwUi.OnHostDisposed(this);
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

                QwTween.Cancel(widget);
                if (widget is IQwThemeAware aware)
                {
                    themeAware.Remove(aware);
                }

                widgets.RemoveAt(index);
            }

            var layerIndex = layerRoots.IndexOf(root);
            if (layerIndex >= 0)
            {
                QwLayers.Release(root);
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
                if (aware is QwWidget deadWidget && deadWidget.Go == null)
                {
                    themeAware.RemoveAt(index);
                    widgets.Remove(deadWidget);
                    continue;
                }

                var scheme = aware is QwWidget widget ? widget.Scheme : QwScheme.Paper;
                try
                {
                    aware.ApplyTheme(Theme(scheme));
                }
                catch (Exception exception)
                {
                    QwLog.Warn("UiHost '" + OwnerId + "' could not refresh a widget theme: " + exception.Message);
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
            QwLog.Info(
                "UiHost '" + OwnerId + "' initialized (fonts: " + QwFonts.ResolvedTier +
                ", input: " + (QwInput.LegacyAvailable ? "legacy/both" : "input-system") +
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
