using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>Places a readable replacement panel inside the safe HUD viewport.</summary>
    internal static class UiHudNoticePlacement
    {
        internal static bool TryPlaceMeasured(UiLayoutRect viewport, UiLayoutRect current,
            (double X, double Y, double PivotX, double PivotY) dock, double preferredWidth,
            IReadOnlyList<UiLayoutRect> occupied, double peerGap, Func<double, double> heightAt,
            out UiLayoutRect result, out bool usesCurrent)
        {
            usesCurrent = current.HasArea && Clear(current, occupied) && heightAt(current.Width) <= current.Height;
            if (usesCurrent)
            {
                result = current;
                return true;
            }

            var width = Math.Min(preferredWidth, viewport.Width);
            var height = heightAt(width);
            var preferred = UiHudViewport.AtPivot(dock.X, dock.Y, dock.PivotX, dock.PivotY, new UiLayoutExtent(width, height));
            if (TryPlace(viewport, preferred, occupied, peerGap, out result)) return true;
            // A longer title may fit a wide opening with less vertical space.
            return TryPlaceOpenings(viewport, width, occupied, heightAt, out result) ||
                (width < viewport.Width && TryPlaceOpenings(viewport, viewport.Width, occupied, heightAt, out result));
        }

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

        private static bool TryPlaceOpenings(UiLayoutRect viewport, double maximumWidth, IReadOnlyList<UiLayoutRect> occupied,
            Func<double, double> heightAt, out UiLayoutRect result)
        {
            var best = default(UiLayoutRect);
            var barriers = new List<UiLayoutRect>(occupied.Count);
            var starts = new List<double>(occupied.Count + 1) { viewport.Y };
            foreach (var obstacle in occupied)
            {
                var barrier = viewport.Intersect(obstacle);
                if (!barrier.HasArea) continue;
                barriers.Add(barrier);
                if (barrier.Bottom < viewport.Bottom) starts.Add(barrier.Bottom);
            }
            barriers.Sort((left, right) => left.Y.CompareTo(right.Y));
            starts.Sort();

            // A free rectangle can slide up until it meets the viewport or a peer's bottom edge.
            // From each such top edge, scan down and subtract peers from the horizontal openings.
            var blocked = new List<(double Left, double Right)>();
            var heights = new Dictionary<double, double>();
            var previousStart = double.NegativeInfinity;
            foreach (var top in starts)
            {
                if (top == previousStart) continue;
                previousStart = top;
                blocked.Clear();
                var limit = top;
                foreach (var barrier in barriers)
                {
                    if (barrier.Bottom <= top) continue;
                    if (barrier.Y > limit)
                    {
                        CheckOpenings(top, barrier.Y);
                        limit = barrier.Y;
                    }
                    BlockSpan(blocked, barrier.X, barrier.Right);
                }
                CheckOpenings(top, viewport.Bottom);
                if (best.Width == maximumWidth) break;
            }
            result = best;
            return result.HasArea;

            void CheckOpenings(double top, double bottom)
            {
                var left = viewport.X;
                foreach (var span in blocked)
                {
                    CheckWidth(left, span.Left, top, bottom);
                    left = span.Right;
                }
                CheckWidth(left, viewport.Right, top, bottom);
            }

            void CheckWidth(double left, double right, double top, double bottom)
            {
                var width = Math.Min(maximumWidth, right - left);
                if (width <= best.Width) return;
                if (!heights.TryGetValue(width, out var height))
                {
                    height = heightAt(width);
                    heights.Add(width, height);
                }
                if (height > 0d && height <= bottom - top) best = new UiLayoutRect(left, top, width, height);
            }
        }

        private static void BlockSpan(List<(double Left, double Right)> blocked, double left, double right)
        {
            var first = 0;
            while (first < blocked.Count && blocked[first].Right < left) first++;
            var end = first;
            while (end < blocked.Count && blocked[end].Left <= right)
            {
                left = Math.Min(left, blocked[end].Left);
                right = Math.Max(right, blocked[end].Right);
                end++;
            }
            if (end == first)
                blocked.Insert(first, (left, right));
            else
            {
                blocked[first] = (left, right);
                if (end > first + 1) blocked.RemoveRange(first + 1, end - first - 1);
            }
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
