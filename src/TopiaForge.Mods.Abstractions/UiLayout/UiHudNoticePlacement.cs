using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>Places a readable replacement panel inside the safe HUD viewport.</summary>
    internal static class UiHudNoticePlacement
    {
        internal static bool TryPlace(UiLayoutRect viewport, UiLayoutRect preferred, IReadOnlyList<UiLayoutRect> occupied,
            double peerGap, out UiLayoutRect result)
        {
            result = default;
            if (!preferred.HasArea || preferred.Width > viewport.Width || preferred.Height > viewport.Height) return false;
            if (viewport.Contains(preferred) && Clear(preferred, occupied))
            {
                result = preferred;
                return true;
            }

            var size = new UiLayoutExtent(preferred.Width, preferred.Height);
            var midX = viewport.X + viewport.Width * 0.5d;
            var midY = viewport.Y + viewport.Height * 0.5d;
            var prefersRight = preferred.X + preferred.Width * 0.5d > midX;
            var prefersBottom = preferred.Y + preferred.Height * 0.5d > midY;
            var top = new UiLayoutRect(viewport.X, viewport.Y, viewport.Width, viewport.Height * 0.5d);
            var bottom = new UiLayoutRect(viewport.X, midY, viewport.Width, viewport.Height * 0.5d);
            var left = new UiLayoutRect(viewport.X, viewport.Y, viewport.Width * 0.5d, viewport.Height);
            var right = new UiLayoutRect(midX, viewport.Y, viewport.Width * 0.5d, viewport.Height);

            return Scan(prefersRight ? right : left, size, occupied, peerGap, vertical: true, farEdge: prefersRight, out result) ||
                Scan(prefersRight ? left : right, size, occupied, peerGap, vertical: true, farEdge: !prefersRight, out result) ||
                Scan(prefersBottom ? bottom : top, size, occupied, peerGap, vertical: false, farEdge: prefersBottom, out result) ||
                Scan(prefersBottom ? top : bottom, size, occupied, peerGap, vertical: false, farEdge: !prefersBottom, out result) ||
                ScanOpenColumns(viewport, size, occupied, peerGap, out result) ||
                ScanOpenColumns(viewport, size, occupied, 0d, out result);
        }

        private static bool ScanOpenColumns(UiLayoutRect viewport, UiLayoutExtent size, IReadOnlyList<UiLayoutRect> occupied,
            double gap, out UiLayoutRect result)
        {
            // A rectangle can slide left until its edge reaches the viewport or a blocking rectangle.
            if (Scan(viewport, size, occupied, gap, vertical: true, farEdge: false, out result)) return true;
            foreach (var obstacle in occupied)
            {
                var x = obstacle.Right + gap;
                if (x <= viewport.X || x + size.Width > viewport.Right) continue;
                var column = new UiLayoutRect(x, viewport.Y, size.Width, viewport.Height);
                if (Scan(column, size, occupied, gap, vertical: true, farEdge: false, out result)) return true;
            }
            return false;
        }

        private static bool Scan(UiLayoutRect edge, UiLayoutExtent size, IReadOnlyList<UiLayoutRect> occupied,
            double peerGap, bool vertical, bool farEdge, out UiLayoutRect result)
        {
            result = default;
            if (size.Width > edge.Width || size.Height > edge.Height) return false;
            var fixedStart = vertical ? farEdge ? edge.Right - size.Width : edge.X
                : farEdge ? edge.Bottom - size.Height : edge.Y;
            var coordinate = vertical ? edge.Y : edge.X;
            var end = vertical ? edge.Bottom - size.Height : edge.Right - size.Width;
            while (coordinate <= end)
            {
                var candidate = vertical ? new UiLayoutRect(fixedStart, coordinate, size.Width, size.Height)
                    : new UiLayoutRect(coordinate, fixedStart, size.Width, size.Height);
                var next = coordinate;
                foreach (var obstacle in occupied)
                {
                    if (candidate.Intersects(obstacle))
                        next = Math.Max(next, (vertical ? obstacle.Bottom : obstacle.Right) + peerGap);
                }
                if (next == coordinate)
                {
                    result = candidate;
                    return true;
                }
                coordinate = next;
            }
            return false;
        }

        private static bool Clear(UiLayoutRect candidate, IReadOnlyList<UiLayoutRect> occupied)
        {
            foreach (var obstacle in occupied)
                if (candidate.Intersects(obstacle)) return false;
            return true;
        }
    }
}
