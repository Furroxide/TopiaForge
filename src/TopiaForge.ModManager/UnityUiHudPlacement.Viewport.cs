using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.ModManager
{
    internal sealed partial class UnityUiHudPlacement
    {
        private readonly List<UiLayoutRect> occupied = new List<UiLayoutRect>();
        private readonly Vector3[] corners = new Vector3[4];
        private RectTransform? protectedNoticeArea;
        private int occupancyVersion;

        internal int DockVersion => dock.Version;
        internal int Version => unchecked(DockVersion + occupancyVersion);

        internal void SetProtectedNoticeArea(RectTransform? area)
        {
            if (ReferenceEquals(protectedNoticeArea, area)) return;
            protectedNoticeArea = area;
            occupancyVersion++;
        }

        internal bool TryGetViewportSizes(int handle, out UiLayoutExtent size, out UiLayoutExtent safe)
        {
            size = default;
            safe = default;
            var panel = Find(handle);
            if (panel == null || !TryProjectDock(panel, out var viewport)) return false;
            size = viewport.ContentSize;
            safe = new UiLayoutExtent(viewport.Safe.Width / viewport.Scale, viewport.Safe.Height / viewport.Scale);
            return true;
        }

        internal void SetPanelPaintShown(int handle, bool shown)
        {
            var panel = Find(handle);
            if (panel == null || panel.Rect.gameObject.activeSelf == shown) return;
            panel.Rect.gameObject.SetActive(shown);
            occupancyVersion++;
        }

        internal void ApplyContentViewport(int handle)
        {
            var panel = Find(handle);
            if (panel == null || !TryProjectDock(panel, out var viewport)) return;
            RestoreDock(panel, viewport);
        }

        internal bool SetNoticeRoom(int handle, double preferredWidth, Func<double, double> heightAt)
        {
            var panel = Find(handle);
            if (panel == null || !TryProjectDock(panel, out var viewport)) return false;
            occupied.Clear();
            foreach (var peer in panels)
            {
                if (ReferenceEquals(panel, peer) || !dock.TryGetPosition(peer.Handle, out _, out _) ||
                    !peer.Rect.gameObject.activeInHierarchy) continue;
                if (!TryProject(peer.Rect, out var region))
                {
                    RestoreDock(panel, viewport);
                    return false;
                }
                if (region.HasArea) occupied.Add(region);
            }
            if (protectedNoticeArea != null && TryProject(protectedNoticeArea, out var blocked)) occupied.Add(blocked);

            var origin = (viewport.DockX, viewport.DockY, (double)panel.AuthoredPivot.x, 1d - panel.AuthoredPivot.y);
            var scale = viewport.Scale;
            if (!UiHudNoticePlacement.TryPlaceMeasured(viewport.Safe, viewport.Content, origin, preferredWidth * scale,
                occupied, UiHudViewport.SlotGap * scale, width => heightAt(width / scale) * scale,
                out var chosen, out var usesCurrent))
            {
                RestoreDock(panel, viewport);
                return false;
            }
            if (usesCurrent)
            {
                RestoreDock(panel, viewport);
                return true;
            }

            // Keep the authored height reserved in the dock. Only this panel moves to the alternative slot.
            var corner = new Vector2(0f, 1f);
            var rect = panel.Rect;
            rect.anchorMin = rect.anchorMax = rect.pivot = corner;
            rect.sizeDelta = new Vector2((float)(chosen.Width / scale), (float)(chosen.Height / scale));
            rect.anchoredPosition = new Vector2((float)((chosen.X - viewport.Canvas.X) / scale),
                (float)((viewport.Canvas.Y - chosen.Y) / scale));
            RecordNotice(panel, chosen);
            return true;
        }

        private Panel? Find(int handle)
        {
            foreach (var panel in panels)
                if (panel.Handle == handle && panel.Rect != null) return panel;
            return null;
        }

        private void RestoreDock(Panel panel, DockViewport viewport)
        {
            var rect = panel.Rect;
            var size = viewport.ContentSize;
            rect.anchorMin = rect.anchorMax = panel.AuthoredAnchor;
            rect.pivot = panel.AuthoredPivot;
            rect.sizeDelta = new Vector2((float)size.Width, (float)size.Height);
            rect.anchoredPosition = viewport.Position;
            RecordNotice(panel, null);
        }

        private void RecordNotice(Panel panel, UiLayoutRect? notice)
        {
            if (Nullable.Equals(panel.Notice, notice)) return;
            panel.Notice = notice;
            occupancyVersion++;
        }

        private bool TryProjectDock(Panel panel, out DockViewport result)
        {
            result = default;
            var canvas = panel.Rect.GetComponentInParent<Canvas>(includeInactive: true)?.rootCanvas;
            if (canvas == null || !dock.TryGetProspectivePosition(panel.Handle, out var x, out var y)) return false;
            var pixels = canvas.pixelRect;
            if (pixels.width <= 0f || pixels.height <= 0f || Screen.width <= 0 || Screen.height <= 0) return false;
            var bounds = new UiLayoutRect(pixels.xMin, Screen.height - pixels.yMax, pixels.width, pixels.height);
            var screenScale = UiHudViewport.ScreenScale(Screen.width, Screen.height);
            // The scaler applies the host's requested scale when Unity lays out the canvas. Use the same conversion
            // during admission, even when an inactive canvas has yet to process its first scaler update.
            var scale = screenScale * panel.Host.EffectiveUiScale;
            if (scale <= 0f) return false;
            var screen = new UiLayoutRect(0, 0, Screen.width, Screen.height);
            var safe = UiHudViewport.SafeArea(screen, scale).Intersect(bounds);
            var anchor = panel.AuthoredAnchor;
            var pivot = panel.AuthoredPivot;
            var dockX = bounds.X + anchor.x * bounds.Width + x * scale;
            var dockY = bounds.Y + (1d - anchor.y) * bounds.Height - y * scale;
            var content = UiHudViewport.Fit(safe, dockX, dockY, pivot.x, 1f - pivot.y,
                new UiLayoutExtent(panel.AuthoredSize.x * (double)scale, panel.AuthoredSize.y * (double)scale));
            result = new DockViewport(bounds, safe, content, new Vector2(x, y), scale, dockX, dockY);
            return true;
        }

        private bool TryProject(RectTransform rect, out UiLayoutRect bounds)
        {
            bounds = default;
            if (rect == null || rect.GetComponentInParent<Canvas>(includeInactive: true)?.rootCanvas is not { } canvas) return false;
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            rect.GetWorldCorners(corners);
            var left = float.PositiveInfinity;
            var bottom = float.PositiveInfinity;
            var right = float.NegativeInfinity;
            var top = float.NegativeInfinity;
            foreach (var corner in corners)
            {
                var point = RectTransformUtility.WorldToScreenPoint(camera, corner);
                left = Math.Min(left, point.x);
                bottom = Math.Min(bottom, point.y);
                right = Math.Max(right, point.x);
                top = Math.Max(top, point.y);
            }
            bounds = new UiLayoutRect(left, Screen.height - top, right - left, top - bottom);
            return double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
                double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);
        }

        private readonly struct DockViewport
        {
            internal DockViewport(UiLayoutRect canvas, UiLayoutRect safe, UiLayoutRect content, Vector2 position, float scale,
                double dockX, double dockY)
            {
                Canvas = canvas;
                Safe = safe;
                Content = content;
                Position = position;
                Scale = scale;
                DockX = dockX;
                DockY = dockY;
            }

            internal UiLayoutRect Canvas { get; }
            internal UiLayoutRect Safe { get; }
            internal UiLayoutRect Content { get; }
            internal Vector2 Position { get; }
            internal float Scale { get; }
            internal double DockX { get; }
            internal double DockY { get; }
            internal UiLayoutExtent ContentSize => new UiLayoutExtent(Content.Width / Scale, Content.Height / Scale);
        }
    }
}
