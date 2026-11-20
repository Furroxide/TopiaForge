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

        private bool TryPlaceHudNotice(UiLayoutExtent size, bool preferCurrent, out UiCaptureRect panel)
        {
            var safe = HudSafeArea();
            var occupied = new List<UiLayoutRect>();
            foreach (var peer in service.Surfaces)
            {
                if (ReferenceEquals(peer, surface) || !peer.IsVisible || peer.Request.Kind != UiSurfaceKind.Hud) continue;
                occupied.Add(HudBounds(peer));
            }
            var (x, y, px, py) = HudDockOrigin(surface, safe);
            var current = HudBounds(surface);
            if (preferCurrent && current.HasArea && occupied.TrueForAll(peer => !peer.Intersects(current)))
            {
                panel = CaptureRect(current);
                return true;
            }
            var preferred = UiHudViewport.AtPivot(x, y, px, py, size);
            if (UiHudNoticePlacement.TryPlace(safe, preferred, occupied, UiHudViewport.SlotGap * layoutScale, out var chosen))
            {
                panel = CaptureRect(chosen);
                return true;
            }
            panel = default;
            return false;
        }

        private void PaintHudLayoutNotice()
        {
            var kit = UiThemePackParser.Default;
            var pair = kit.SelectScheme(useHudScheme, preferences.HighContrast).GetPair(UiPaintRole.Surface);
            var padding = 12f * layoutScale;
            if (!TryMeasureHudLayoutNotice(out var panel))
            {
                nodes.Add(new UiCapturedNode("/fallback", null, UiHudOverflowNotice.CaptureKind, default,
                    new UiCaptureNodeContent(string.Empty, surface.HudOverflowText!, 0f, 0f, UiCaptureNodeFlags.LayoutDiagnostic),
                    default, kit.Id, usedFallback: true, isRendered: false, clipped: false, occluded: false));
                return;
            }

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

        private bool TryMeasureHudLayoutNotice(out UiCaptureRect panel)
        {
            var kit = UiThemePackParser.Default;
            var padding = 12f * layoutScale;
            var current = HudBounds(surface);
            var width = Math.Min(UiHudLayoutNotice.PreferredWidth * layoutScale, HudSafeArea().Width);
            var height = HeightAt(width);
            panel = default;
            return double.IsFinite(height) &&
                TryPlaceHudNotice(new UiLayoutExtent(width, height), HeightAt(current.Width) <= current.Height, out panel);

            double HeightAt(double outerWidth)
            {
                var innerWidth = (float)(outerWidth - 2f * padding);
                if (innerWidth <= 0f) return double.PositiveInfinity;
                var title = MeasureText(surface.Request.Title, UiTextStyle.Heading, innerWidth, descriptor: kit);
                var message = MeasureText(UiHudLayoutNotice.Text, UiTextStyle.Body, innerWidth, descriptor: kit);
                return Math.Max(title.Width, message.Width) > innerWidth ? double.PositiveInfinity : 2f * padding + title.Height + gap + message.Height;
            }
        }

        internal static bool FitsHudContent(FakeUiService service, FakeUiSurface surface, out bool noticeFits)
        {
            var probe = new FakeUiTreeCapture(service, surface, ReferenceViewport, UiCaptureControlState.Rest, null);
            try { probe.VerifyComposition(surface.Content!); }
            catch (UiLayoutException)
            {
                noticeFits = probe.TryMeasureHudLayoutNotice(out _);
                return false;
            }
            noticeFits = false;
            return true;
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
