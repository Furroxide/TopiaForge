using System;
using System.Collections.Generic;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Places every mod's HUD panels, process-wide. Each panel docks at its corner or edge, and panels that share one stack
    /// a small gap apart (<see cref="TopiaForgeHudDock"/>), so HUDs from different mods never overlap. Only shown panels take
    /// space. Positions are written only when the stack changes: a panel shown or hidden, added or removed, or a UI scale
    /// change. Main thread only.
    /// </summary>
    internal sealed class UnityUiHudPlacement : IDisposable
    {
        private readonly TopiaForgeHudDock dock = new TopiaForgeHudDock();
        private readonly List<Panel> panels = new List<Panel>();
        private int appliedVersion = int.MinValue;
        private bool disposed;

        public UnityUiHudPlacement()
        {
            TopiaForgeTheme.Changed += Refresh;
        }

        /// <summary>Adds a hidden panel, docked at its corner, after every panel already placed. Returns its handle.</summary>
        public int Add(UiHost host, RectTransform rect, TopiaForgeCorner corner, float height)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (rect == null) throw new ArgumentNullException(nameof(rect));
            TopiaForgeAnchors.Dock(rect, corner, TopiaForgeTokens.SafeMargin);
            var handle = dock.Add(corner, height, host.EffectiveUiScale);
            panels.Add(new Panel(handle, host, rect));
            return handle;
        }

        /// <summary>Shows or hides a panel in its stack; the panels after it move to close up or make room.</summary>
        public void SetShown(int handle, bool shown)
        {
            dock.SetShown(handle, shown);
            Apply();
        }

        /// <summary>Removes a panel from its stack.</summary>
        public void Remove(int handle)
        {
            for (var index = panels.Count - 1; index >= 0; index--)
            {
                if (panels[index].Handle == handle)
                {
                    panels.RemoveAt(index);
                }
            }

            dock.Remove(handle);
            Apply();
        }

        /// <summary>Re-reads every panel host's UI scale, after a global theme change or a mod's own accessibility change.</summary>
        public void Refresh()
        {
            if (disposed)
            {
                return;
            }

            foreach (var panel in panels)
            {
                dock.SetScale(panel.Handle, panel.Host.EffectiveUiScale);
            }

            Apply();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            TopiaForgeTheme.Changed -= Refresh;
            panels.Clear();
        }

        private void Apply()
        {
            if (disposed || dock.Version == appliedVersion)
            {
                return;
            }

            appliedVersion = dock.Version;
            foreach (var panel in panels)
            {
                if (panel.Rect == null || !dock.TryGetPosition(panel.Handle, out var x, out var y))
                {
                    continue;
                }

                var position = new Vector2(x, y);
                if (panel.Rect.anchoredPosition != position)
                {
                    panel.Rect.anchoredPosition = position;
                }
            }
        }

        private sealed class Panel
        {
            internal Panel(int handle, UiHost host, RectTransform rect)
            {
                Handle = handle;
                Host = host;
                Rect = rect;
            }

            internal int Handle { get; }

            internal UiHost Host { get; }

            internal RectTransform Rect { get; }
        }
    }
}
