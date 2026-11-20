using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    internal sealed partial class FakeUiTreeCapture
    {
        private UiLayoutRect HudSafeArea() => UiHudViewport.SafeArea(
            new UiLayoutRect(0d, 0d, viewport.Width, viewport.Height), layoutScale);

        private static UiHudAnchor Anchor(UiSurfaceRequest request) => UiHudViewport.Anchor(request.HudAnchor, request.HudVisibility);

        private UiLayoutRect HudBounds(FakeUiSurface hud)
        {
            var safe = HudSafeArea();
            var (x, y, px, py) = HudDockOrigin(hud, safe);
            return UiHudViewport.Fit(safe, x, y, px, py,
                new UiLayoutExtent(hud.Request.Width * layoutScale, hud.Request.Height * layoutScale));
        }

        private (double X, double Y, double PivotX, double PivotY) HudDockOrigin(FakeUiSurface hud, UiLayoutRect safe)
        {
            var anchor = Anchor(hud.Request);
            var before = 0d;
            var after = 0d;
            var reached = false;
            foreach (var peer in service.Surfaces)
            {
                if (ReferenceEquals(peer, hud)) { reached = true; continue; }
                if (!peer.IsVisible || peer.Request.Kind != UiSurfaceKind.Hud || Anchor(peer.Request) != anchor) continue;
                var extent = peer.Request.Height * layoutScale + UiHudViewport.SlotGap * layoutScale;
                if (reached) after += extent;
                else before += extent;
            }

            var px = anchor switch
            {
                UiHudAnchor.TopLeft or UiHudAnchor.Left or UiHudAnchor.BottomLeft => 0d,
                UiHudAnchor.TopRight or UiHudAnchor.Right or UiHudAnchor.BottomRight => 1d,
                _ => 0.5d,
            };
            var x = safe.X + px * safe.Width;
            return anchor switch
            {
                UiHudAnchor.Top or UiHudAnchor.TopLeft or UiHudAnchor.TopRight => (x, safe.Y + before, px, 0d),
                UiHudAnchor.Bottom or UiHudAnchor.BottomLeft or UiHudAnchor.BottomRight => (x, safe.Bottom - before, px, 1d),
                _ => (x, (viewport.Height + before - after) * 0.5d, px, 0.5d),
            };
        }

        private static List<HudPlacement>? PlanHudNotices(FakeUiService service, UiCaptureViewport viewport)
        {
            List<HudPlacement>? placements = null;
            var surfaces = service.Surfaces;
            for (var index = 0; index < surfaces.Count; index++)
            {
                var peer = surfaces[index];
                if (peer.Request.Kind != UiSurfaceKind.Hud || !peer.IsVisible) continue;
                var probe = new FakeUiTreeCapture(service, peer, viewport, UiCaptureControlState.Rest, null);
                (placements ??= new List<HudPlacement>()).Add(new HudPlacement(probe, probe.HudBounds(peer)));
            }

            if (placements == null) return null;
            List<UiLayoutRect>? occupied = null;
            // Keep authored dock reservations for stacking. Solve paint in creation order so that each notice
            // avoids the rectangles already assigned to earlier peers and the authored slots of the other peers.
            for (var index = 0; index < placements.Count; index++)
            {
                var entry = placements[index];
                if (entry.Probe.FitsHudContent(entry.Authored)) continue;
                (occupied ??= new List<UiLayoutRect>(placements.Count - 1)).Clear();
                for (var peer = 0; peer < placements.Count; peer++)
                {
                    if (peer == index) continue;
                    var region = placements[peer].PaintedOrAuthored;
                    if (region.HasArea) occupied.Add(region);
                }
                var hasNotice = entry.Probe.TryMeasureHudLayoutNotice(entry.Authored, occupied, out var notice);
                placements[index] = new HudPlacement(entry.Probe, entry.Authored, unfit: true,
                    notice: hasNotice ? notice : null);
            }
            return placements;
        }

        internal static void RefreshHudNotices(FakeUiService service)
        {
            var placements = PlanHudNotices(service, ReferenceViewport);
            if (placements == null) return;
            foreach (var entry in placements)
                entry.Probe.surface.ApplyHudNoticeFit(entry.Unfit, entry.Notice.HasValue);
        }

        private UiLayoutRect? HudNoticeBounds()
        {
            var placements = PlanHudNotices(service, viewport);
            if (surface.IsVisible && placements != null)
            {
                foreach (var entry in placements)
                    if (ReferenceEquals(entry.Probe.surface, surface)) return entry.Notice;
            }

            // A hidden surface can still be inspected, but only visible peers reserve screen space.
            var occupied = new List<UiLayoutRect>();
            if (placements != null)
            {
                foreach (var entry in placements)
                    if (entry.PaintedOrAuthored.HasArea) occupied.Add(entry.PaintedOrAuthored);
            }
            return TryMeasureHudLayoutNotice(HudBounds(surface), occupied, out var panel) ? panel : null;
        }

        private readonly struct HudPlacement
        {
            internal HudPlacement(FakeUiTreeCapture probe, UiLayoutRect authored, bool unfit = false,
                UiLayoutRect? notice = null)
            {
                Probe = probe;
                Authored = authored;
                Unfit = unfit;
                Notice = notice;
            }

            internal FakeUiTreeCapture Probe { get; }
            internal UiLayoutRect Authored { get; }
            internal bool Unfit { get; }
            internal UiLayoutRect? Notice { get; }
            internal UiLayoutRect PaintedOrAuthored => Notice ?? Authored;
        }

        private void PaintHudLayoutNotice()
        {
            var kit = UiThemePackParser.Default;
            var pair = kit.SelectScheme(useHudScheme, preferences.HighContrast).GetPair(UiPaintRole.Surface);
            var padding = 12f * layoutScale;
            if (HudNoticeBounds() is not { } notice)
            {
                nodes.Add(new UiCapturedNode("/fallback", null, UiHudOverflowNotice.CaptureKind, default,
                    new UiCaptureNodeContent(string.Empty, surface.HudOverflowText!, 0f, 0f, UiCaptureNodeFlags.LayoutDiagnostic),
                    default, kit.Id, usedFallback: true, isRendered: false, clipped: false, occluded: false));
                return;
            }

            var panel = CaptureRect(notice);
            nodes.Insert(0, new UiCapturedNode("/", surface.Id, nameof(UiSurfaceKind.Hud), panel, EmptyContent,
                new UiCaptureNodePaint(null, pair.Fill), kit.Id, usedFallback: true, isRendered: surface.IsVisible,
                clipped: false, occluded: false));
            var inner = panel.Inset(padding, padding, padding, padding);
            var title = surface.Request.Title;
            var titleSize = MeasureText(title, UiTextStyle.Heading, inner.Width, descriptor: kit);
            PaintNoticeText("/title", "SurfaceTitle", title, UiTextStyle.Heading,
                new UiCaptureRect(inner.X, inner.Y, titleSize.Width, titleSize.Height), pair, fits: true, clipped: false);
            var text = MeasureText(UiHudLayoutNotice.Text, UiTextStyle.Body, inner.Width, descriptor: kit);
            PaintNoticeText("/fallback", "LayoutFallback", UiHudLayoutNotice.Text, UiTextStyle.Body,
                new UiCaptureRect(inner.X, inner.Y + titleSize.Height + gap, text.Width, text.Height), pair, fits: true, clipped: false);
        }

        private bool TryMeasureHudLayoutNotice(UiLayoutRect authored, IReadOnlyList<UiLayoutRect> occupied,
            out UiLayoutRect panel)
        {
            var kit = UiThemePackParser.Default;
            var padding = 12f * layoutScale;
            var safe = HudSafeArea();
            return UiHudNoticePlacement.TryPlaceMeasured(safe, authored, HudDockOrigin(surface, safe),
                UiHudLayoutNotice.PreferredWidth * layoutScale, occupied, UiHudViewport.SlotGap * layoutScale,
                HeightAt, out panel, out _);

            double HeightAt(double outerWidth)
            {
                var innerWidth = (float)(outerWidth - 2f * padding);
                if (innerWidth <= 0f) return double.PositiveInfinity;
                var title = MeasureText(surface.Request.Title, UiTextStyle.Heading, innerWidth, descriptor: kit);
                var message = MeasureText(UiHudLayoutNotice.Text, UiTextStyle.Body, innerWidth, descriptor: kit);
                return Math.Max(title.Width, message.Width) > innerWidth ? double.PositiveInfinity : 2f * padding + title.Height + gap + message.Height;
            }
        }

        private bool FitsHudContent(UiLayoutRect authored)
        {
            try
            {
                if (surface.Content is { } content) VerifyComposition(content);
                else MeasureBodyOnlyHud(CaptureRect(authored), out _, out _);
                return true;
            }
            catch (UiLayoutException)
            {
                return false;
            }
        }

        private void PaintBodyOnlyHud(UiCaptureRect panel, UiCaptureRect clip, UiThemePair surfacePair)
        {
            UiCaptureRect title;
            UiCaptureRect body;
            try { MeasureBodyOnlyHud(panel, out title, out body); }
            catch (UiLayoutException failure)
            {
                PaintLayoutFailure(panel, clip, failure);
                return;
            }

            Add("/", surface.Id, nameof(UiSurfaceKind.Hud), panel, clip, EmptyContent,
                new UiCaptureNodePaint(null, surfacePair.Fill));
            Text("/title", null, "SurfaceTitle", title, clip, surface.Request.Title, UiTextStyle.Heading,
                UiTone.Neutral, UiCaptureNodeFlags.RequiresLabel);
            if (surface.Body.Length != 0)
                Text("/body", null, "SurfaceBody", body, clip, surface.Body, UiTextStyle.Body, UiTone.Neutral,
                    UiCaptureNodeFlags.None);
        }

        private void MeasureBodyOnlyHud(UiCaptureRect panel, out UiCaptureRect title, out UiCaptureRect body)
        {
            var padding = 12f * layoutScale;
            var inner = panel.Inset(padding, padding, padding, padding);
            if (inner.Width <= 0f || inner.Height <= 0f)
                throw new UiLayoutException(UiLayoutFailure.InvalidLayout, "/0", "The HUD has no usable physical content area.");
            title = new UiCaptureRect(inner.X, inner.Y, inner.Width, TextSize(UiTextStyle.Heading));
            body = default;
            var bottom = title.Bottom;
            if (surface.Body.Length != 0)
            {
                var measured = MeasureText(surface.Body, UiTextStyle.Body, inner.Width);
                if (measured.Width > inner.Width + 0.001d)
                    throw new UiLayoutException(UiLayoutFailure.ClippedContent, "/body", "The HUD body exceeds its physical viewport.");
                body = new UiCaptureRect(inner.X, title.Bottom + gap, measured.Width, measured.Height);
                bottom = body.Bottom;
            }
            if (bottom > inner.Bottom + 0.001d)
                throw new UiLayoutException(UiLayoutFailure.ClippedContent, "/0", "The HUD chrome exceeds its physical viewport.");
        }

        private void PaintNoticeText(string path, string kind, string text, UiTextStyle role, UiCaptureRect bounds,
            UiThemePair pair, bool fits, bool clipped)
        {
            var kit = UiThemePackParser.Default;
            var typography = kit.GetText(role);
            var shown = typography.IsUppercase ? text.ToUpperInvariant() : text;
            var expected = typography.ReferenceSize * readableScale;
            nodes.Add(new UiCapturedNode(path, null, kind, bounds,
                new UiCaptureNodeContent(shown, text, expected, expected, UiCaptureNodeFlags.RequiresLabel),
                new UiCaptureNodePaint(pair.On, pair.Fill), kit.Id, usedFallback: true,
                isRendered: surface.IsVisible && fits, clipped, occluded: false));
        }

        private static UiCaptureRect CaptureRect(UiLayoutRect rect) =>
            new UiCaptureRect((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height);
    }
}
