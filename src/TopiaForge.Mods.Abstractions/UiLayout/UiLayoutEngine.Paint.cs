using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    internal sealed partial class UiLayoutEngine
    {
        private readonly List<PaintRecord> paint = new List<PaintRecord>();

        private void Place(Slot root, UiLayoutRect clip, UiOuterScroll? outerScroll)
        {
            var clips = new PlacementClips(clip, clip, clip, null);
            if (outerScroll is { } outer)
            {
                var top = -outer.BeforeComposition;
                var height = Math.Max(outer.ApertureHeight, outer.BeforeComposition + root.Size.Height + outer.AfterComposition);
                var viewport = new UiLayoutRect(clip.X, top, clip.Width, outer.ApertureHeight);
                var canvas = new UiLayoutRect(clip.X, top, clip.Width, height);
                // Admission considers every legal offset. Renderers apply the current crop to the same layout.
                var projection = new ScrollProjection(null, viewport, height - outer.ApertureHeight);
                clips = new PlacementClips(clip, clip, canvas, projection);
            }
            Place(root, 0, 0, clips);
        }

        private void Place(Slot slot, double x, double y, PlacementClips clips)
        {
            var bounds = new UiLayoutRect(x, y, slot.Size.Width, slot.Size.Height);
            var placement = slot.Placement;
            placement.Bounds = bounds;
            placement.Clip = clips.Visible;
            placements.Add(placement);
            var protectedGroup = slot.PaintKind == UiLayoutPaintKind.Protected;
            if (protectedGroup && !clips.Content.Contains(bounds)) throw Clipped(slot);
            if (protectedGroup && clips.Scroll is { } scroll)
            {
                // Keep existing fixed-mask clipping of outer paint. The remaining focus envelope and full
                // hit target must fit in the scroll aperture at a legal offset.
                var required = bounds.WithOutsets(slot.FocusOutsets).Intersect(clips.Paint);
                if (!scroll.CanReveal(required, slot.ScrollLineHeight)) throw Clipped(slot);
            }

            if (slot.PaintKind != UiLayoutPaintKind.None)
            {
                var rectangle = slot.LocalPaintBounds?.Translate(x, y) ?? bounds;
                var normal = rectangle.WithOutsets(slot.PaintOutsets);
                var focused = rectangle.WithOutsets(new UiLayoutOutsets(
                    Math.Max(slot.PaintOutsets.Left, slot.FocusOutsets.Left),
                    Math.Max(slot.PaintOutsets.Top, slot.FocusOutsets.Top),
                    Math.Max(slot.PaintOutsets.Right, slot.FocusOutsets.Right),
                    Math.Max(slot.PaintOutsets.Bottom, slot.FocusOutsets.Bottom)));
                placement.PaintBounds = focused.Intersect(clips.Visible);
                paint.Add(new PaintRecord(placement, slot.PaintKind,
                    normal.Intersect(clips.Paint), focused.Intersect(clips.Paint), clips.Scroll));
            }

            var childClips = ClipChildren(slot, bounds, clips);
            foreach (var child in slot.Children)
            {
                Place(child, x + child.X, y + child.Y, childClips);
            }
        }

        private static PlacementClips ClipChildren(Slot slot, UiLayoutRect bounds, PlacementClips inherited)
        {
            var content = inherited.Content.Intersect(bounds);
            if (!slot.ClipsChildren) return new PlacementClips(content, inherited.Visible, inherited.Paint, inherited.Scroll);
            var visible = inherited.Visible.Intersect(bounds);
            if (slot.Node is UiScroll && bounds.HasArea && inherited.Content.Contains(bounds))
            {
                // Validate the whole vertical scroll canvas. Rows beyond the current crop can become visible together.
                var canvas = new UiLayoutRect(content.X, bounds.Y, content.Width,
                    Math.Max(bounds.Height, slot.Children[0].Size.Height));
                var scroll = new ScrollProjection(inherited.Scroll, inherited.Paint.Intersect(bounds), canvas.Height - bounds.Height);
                return new PlacementClips(canvas, visible, canvas, scroll);
            }

            return new PlacementClips(content, visible, inherited.Paint.Intersect(bounds), inherited.Scroll);
        }

        private void ValidatePaint()
        {
            var protectedGroups = new List<PaintRecord>();
            var backdrops = new List<PaintRecord>();
            foreach (var later in paint)
            {
                foreach (var earlier in protectedGroups)
                {
                    if (later.Intersects(earlier))
                        throw new UiLayoutException(UiLayoutFailure.OccludedContent, later.Placement.Path,
                            "This painter covers a protected group at " + earlier.Placement.Path + ".");
                }

                if (later.Kind == UiLayoutPaintKind.Protected)
                {
                    foreach (var backdrop in backdrops)
                    {
                        if (!later.Intersects(backdrop)) continue;
                        later.Placement.RequiresBackplate = true;
                        break;
                    }
                    protectedGroups.Add(later);
                }
                else if (later.Kind == UiLayoutPaintKind.Image || later.Placement.Node is UiBox)
                {
                    backdrops.Add(later);
                }
            }
        }

        private readonly struct PlacementClips
        {
            internal PlacementClips(UiLayoutRect content, UiLayoutRect visible, UiLayoutRect paint, ScrollProjection? scroll)
            {
                Content = content;
                Visible = visible;
                Paint = paint;
                Scroll = scroll;
            }

            internal UiLayoutRect Content { get; }
            internal UiLayoutRect Visible { get; }
            internal UiLayoutRect Paint { get; }
            internal ScrollProjection? Scroll { get; }
        }

        private readonly struct PaintRecord
        {
            internal PaintRecord(UiLayoutPlacement placement, UiLayoutPaintKind kind, UiLayoutRect normal, UiLayoutRect focused,
                ScrollProjection? scroll)
            {
                Placement = placement;
                Kind = kind;
                Normal = normal;
                Focused = focused;
                Scroll = scroll;
            }

            internal UiLayoutPlacement Placement { get; }
            internal UiLayoutPaintKind Kind { get; }
            private UiLayoutRect Normal { get; }
            private UiLayoutRect Focused { get; }
            private ScrollProjection? Scroll { get; }

            // Only one control has input focus. Check each possible ring against the other group's normal paint.
            internal bool Intersects(PaintRecord other)
            {
                var common = ScrollProjection.Common(Scroll, other.Scroll);
                var normal = ScrollProjection.Sweep(Normal, Scroll, common);
                var focused = ScrollProjection.Sweep(Focused, Scroll, common);
                return normal.Intersects(ScrollProjection.Sweep(other.Focused, other.Scroll, common)) ||
                    focused.Intersects(ScrollProjection.Sweep(other.Normal, other.Scroll, common));
            }
        }

        private sealed class ScrollProjection
        {
            private readonly ScrollProjection? parent;
            private readonly UiLayoutRect viewport;
            private readonly double maximumOffset;
            private readonly int depth;

            internal ScrollProjection(ScrollProjection? parent, UiLayoutRect viewport, double maximumOffset)
            {
                this.parent = parent;
                this.viewport = viewport;
                this.maximumOffset = maximumOffset;
                depth = (parent?.depth ?? 0) + 1;
            }

            internal bool CanReveal(UiLayoutRect rect, double? lineHeight)
            {
                var reveal = new UiVerticalReveal(rect.Y, rect.Height, lineHeight);
                for (ScrollProjection? scroll = this; scroll != null; scroll = scroll.parent)
                    if (!reveal.Project(scroll.viewport.Y, scroll.viewport.Bottom, -scroll.maximumOffset)) return false;
                return true;
            }

            // Movement shared by both painters preserves their relative positions.
            internal static ScrollProjection? Common(ScrollProjection? left, ScrollProjection? right)
            {
                while (!ReferenceEquals(left, right))
                {
                    if (left == null || right == null) return null;
                    if (left.depth >= right.depth) left = left.parent;
                    else right = right.parent;
                }
                return left;
            }

            internal static UiLayoutRect Sweep(UiLayoutRect rect, ScrollProjection? scroll, ScrollProjection? common)
            {
                while (rect.HasArea && scroll != null && !ReferenceEquals(scroll, common))
                {
                    // Clip in this scroll's parent canvas before applying any movement of that canvas.
                    rect = new UiLayoutRect(rect.X, rect.Y - scroll.maximumOffset, rect.Width, rect.Height + scroll.maximumOffset)
                        .Intersect(scroll.viewport);
                    scroll = scroll.parent;
                }
                return rect;
            }
        }
    }
}
