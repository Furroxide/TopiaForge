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
    public sealed class UiHost : IDisposable
    {
        private readonly List<GameObject> layerRoots = new List<GameObject>();
        private readonly List<CanvasScaler> scalers = new List<CanvasScaler>();
        private readonly List<IQwThemeAware> themeAware = new List<IQwThemeAware>();
        private readonly List<QwWindow> windows = new List<QwWindow>();
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
            StateStore = string.IsNullOrEmpty(options.DataDirectory)
                ? (IQwStateStore)new QwMemoryStateStore()
                : new QwFileStateStore(options.DataDirectory!);
            QwTheme.Changed += OnThemeChanged;
        }

        public string OwnerId { get; }

        public IQwStateStore StateStore { get; }

        /// <summary>Resolved theme for a scheme, cached per global theme version.</summary>
        public QwResolvedTheme Theme(QwScheme scheme)
        {
            if (scheme == QwScheme.Paper)
            {
                if (paperTheme == null || paperTheme.ThemeVersion != QwTheme.Version)
                {
                    paperTheme = new QwResolvedTheme(QwScheme.Paper, accent);
                }

                return paperTheme;
            }

            if (hudTheme == null || hudTheme.ThemeVersion != QwTheme.Version)
            {
                hudTheme = new QwResolvedTheme(QwScheme.Hud, accent);
            }

            return hudTheme;
        }

        /// <summary>Sets this host's accent override and re-tints its live widgets.</summary>
        public void SetAccent(QwRgba? value)
        {
            if (Nullable.Equals(accent, value))
            {
                return;
            }

            accent = value;
            paperTheme = null;
            hudTheme = null;
            WalkThemeAware();
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
        public QwModals Modal => modals ??= new QwModals(this);

        /// <summary>Registers a global hotkey owned by this host (unregistered on Dispose).</summary>
        public object Hotkey(QwKey key, Action action)
        {
            return QwHotkeys.Register(OwnerId, key, action);
        }

        /// <summary>Creates a canvas layer in a sorting band and wraps it as a container.</summary>
        public QwContainer Layer(string name, QwLayerBand band, QwScheme scheme, bool interactive, bool persistent = false)
        {
            ThrowIfDisposed();
            ReportInitOnce();
            var root = QwLayers.CreateCanvas(OwnerId + ":" + name, band, interactive, persistent);
            layerRoots.Add(root);
            scalers.Add(root.GetComponent<CanvasScaler>());
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

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            QwTheme.Changed -= OnThemeChanged;
            QwHotkeys.UnregisterOwner(OwnerId);
            foreach (var window in windows)
            {
                window.Teardown();
            }

            windows.Clear();
            themeAware.Clear();
            foreach (var root in layerRoots)
            {
                if (root != null)
                {
                    UnityEngine.Object.Destroy(root);
                }
            }

            layerRoots.Clear();
            scalers.Clear();
        }

        private void OnThemeChanged()
        {
            paperTheme = null;
            hudTheme = null;
            foreach (var scaler in scalers)
            {
                if (scaler != null)
                {
                    QwLayers.ApplyScaler(scaler);
                }
            }

            WalkThemeAware();
        }

        private void WalkThemeAware()
        {
            for (var index = 0; index < themeAware.Count; index++)
            {
                var aware = themeAware[index];
                var scheme = aware is QwWidget widget ? widget.Scheme : QwScheme.Paper;
                aware.ApplyTheme(Theme(scheme));
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
                ", ui-scale: " + QwTheme.UiScale.ToString("0.##") +
                ", high-contrast: " + (QwTheme.HighContrast ? "on" : "off") + ").");
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
