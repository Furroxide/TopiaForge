using System;
using Segment = TopiaForge.Mods.UnityUi.TopiaForgeIconRaster.Segment;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Rasterizes safe semantic glyphs into the existing UI sprite atlas.</summary>
    internal static partial class TopiaForgeGlyphRaster
    {
        internal const int Count = (int)UiGlyph.Play + 1;
        internal const int TileSize = 24;

        internal static TopiaForgeIcon? SharedIcon(UiGlyph glyph) => glyph switch
        {
            UiGlyph.Check => TopiaForgeIcon.Check,
            UiGlyph.Cross => TopiaForgeIcon.Cross,
            UiGlyph.Down => TopiaForgeIcon.ChevronDown,
            UiGlyph.Next => TopiaForgeIcon.ChevronRight,
            UiGlyph.Search => TopiaForgeIcon.Magnifier,
            UiGlyph.Warning => TopiaForgeIcon.Warning,
            UiGlyph.Microphone => TopiaForgeIcon.Microphone,
            UiGlyph.MicrophoneOff => TopiaForgeIcon.MicrophoneOff,
            _ => null
        };

        internal static float Coverage(UiGlyph glyph, float pixelX, float pixelY, int size)
        {
            if (SharedIcon(glyph) is TopiaForgeIcon icon)
                return TopiaForgeIconRaster.Coverage(icon, pixelX, pixelY, size);

            // The board uses a 24-unit view box, with its origin above the drawing.
            const float strokeRadius = 1.2f;
            var unitsPerPixel = (float)TileSize / size;
            var x = pixelX * unitsPerPixel;
            var y = TileSize - (pixelY * unitsPerPixel);
            var distance = ShapeFor(glyph).Distance(x, y);
            return TopiaForgeIconRaster.Clamp01(0.5f + ((strokeRadius - distance) / unitsPerPixel));
        }

        private static Shape ShapeFor(UiGlyph glyph) => glyph switch
        {
            UiGlyph.Info => Info,
            UiGlyph.Use => Use,
            UiGlyph.Talk => Talk,
            UiGlyph.Vote => Vote,
            UiGlyph.Timer => Timer,
            UiGlyph.Lock => Lock,
            UiGlyph.Map => Map,
            UiGlyph.Notes => Notes,
            UiGlyph.Eye => Eye,
            UiGlyph.Bell => Bell,
            UiGlyph.Flag => Flag,
            UiGlyph.Star => Star,
            UiGlyph.Health => Health,
            UiGlyph.Shield => Shield,
            UiGlyph.Energy => Energy,
            UiGlyph.Settings => Settings,
            UiGlyph.Player => Player,
            UiGlyph.Players => Players,
            UiGlyph.Host => Host,
            UiGlyph.Signal => Signal,
            UiGlyph.Volume => Volume,
            UiGlyph.Leave => Leave,
            UiGlyph.Play => Play,
            _ => throw new ArgumentOutOfRangeException(nameof(glyph))
        };

        private sealed class Shape
        {
            private readonly Segment[] segments;
            private readonly CircleStroke? ring;

            internal Shape(Segment[] segments, CircleStroke? ring)
            {
                this.segments = segments;
                this.ring = ring;
            }

            internal float Distance(float x, float y)
            {
                var nearest = ring is CircleStroke circle ? circle.Distance(x, y) : float.MaxValue;
                for (var index = 0; index < segments.Length; index++)
                    nearest = Math.Min(nearest, TopiaForgeIconRaster.DistanceToSegment(x, y, segments[index]));
                return nearest;
            }
        }

        private readonly struct CircleStroke
        {
            private readonly float x;
            private readonly float y;
            private readonly float radius;

            internal CircleStroke(float x, float y, float radius)
            {
                this.x = x;
                this.y = y;
                this.radius = radius;
            }

            internal float Distance(float pixelX, float pixelY) =>
                Math.Abs(TopiaForgeIconRaster.Length(pixelX - x, pixelY - y) - radius);
        }

        private static Shape Lines(params Segment[][] paths) => new Shape(JoinPaths(paths), null);

        private static Shape Ring(float x, float y, float radius, params Segment[][] paths) =>
            new Shape(JoinPaths(paths), new CircleStroke(x, y, radius));

        private static Segment[] JoinPaths(params Segment[][] paths)
        {
            var count = 0;
            foreach (var path in paths) count += path.Length;
            var segments = new Segment[count];
            var offset = 0;
            foreach (var path in paths)
            {
                path.CopyTo(segments, offset);
                offset += path.Length;
            }
            return segments;
        }

        private static Segment[] Path(params float[] coordinates)
        {
            var segments = new Segment[(coordinates.Length / 2) - 1];
            for (var index = 0; index < segments.Length; index++)
            {
                var start = index * 2;
                segments[index] = new Segment(coordinates[start], coordinates[start + 1], coordinates[start + 2], coordinates[start + 3]);
            }
            return segments;
        }

        private static Segment[] Arc(float centerX, float centerY, float radius, float start, float end)
        {
            var count = (int)Math.Ceiling(Math.Abs(end - start) / 22.5f);
            var segments = new Segment[count];
            var previous = AtAngle(start);
            for (var index = 0; index < count; index++)
            {
                var next = AtAngle(start + ((end - start) * (index + 1) / count));
                segments[index] = new Segment(previous.X, previous.Y, next.X, next.Y);
                previous = next;
            }
            return segments;

            (float X, float Y) AtAngle(float degrees)
            {
                var radians = degrees * (Math.PI / 180);
                return (centerX + (radius * (float)Math.Cos(radians)), centerY + (radius * (float)Math.Sin(radians)));
            }
        }

        private static Segment[] Curve(float x0, float y0, float x1, float y1,
            float x2, float y2, float x3, float y3)
        {
            var segments = new Segment[6];
            var previousX = x0;
            var previousY = y0;
            for (var index = 0; index < segments.Length; index++)
            {
                var t = (index + 1f) / segments.Length;
                var s = 1f - t;
                var a = s * s * s;
                var b = 3f * s * s * t;
                var c = 3f * s * t * t;
                var d = t * t * t;
                var x = (a * x0) + (b * x1) + (c * x2) + (d * x3);
                var y = (a * y0) + (b * y1) + (c * y2) + (d * y3);
                segments[index] = new Segment(previousX, previousY, x, y);
                previousX = x;
                previousY = y;
            }
            return segments;
        }

        private static Segment[] RoundedBox(float left, float top, float right, float bottom, float radius) =>
            JoinPaths(
                Path(left + radius, top, right - radius, top),
                Arc(right - radius, top + radius, radius, -90f, 0f),
                Path(right, top + radius, right, bottom - radius),
                Arc(right - radius, bottom - radius, radius, 0f, 90f),
                Path(right - radius, bottom, left + radius, bottom),
                Arc(left + radius, bottom - radius, radius, 90f, 180f),
                Path(left, bottom - radius, left, top + radius),
                Arc(left + radius, top + radius, radius, 180f, 270f));
    }
}
