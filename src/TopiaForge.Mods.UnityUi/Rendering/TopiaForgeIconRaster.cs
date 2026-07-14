using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Procedural icons rasterized into the atlas (no icon-font dependency).</summary>
    public enum TopiaForgeIcon
    {
        Check,
        Cross,
        ChevronDown,
        ChevronRight,
        Magnifier,
        Grip,
        Warning,
    }

    /// <summary>
    /// Rasterizes the kit's icon set as anti-aliased stroke coverage. Icons are defined
    /// as line segments (plus discs) in unit space and sampled per pixel-center by
    /// TopiaForgeSprites when it fills the atlas.
    /// </summary>
    public static class TopiaForgeIconRaster
    {
        private readonly struct Segment
        {
            public readonly float X1;
            public readonly float Y1;
            public readonly float X2;
            public readonly float Y2;

            public Segment(float x1, float y1, float x2, float y2)
            {
                X1 = x1;
                Y1 = y1;
                X2 = x2;
                Y2 = y2;
            }
        }

        // Unit-space (0..1, origin bottom-left) stroke definitions.
        private static readonly Segment[] Check =
        {
            new Segment(0.16f, 0.52f, 0.40f, 0.26f),
            new Segment(0.40f, 0.26f, 0.84f, 0.72f),
        };

        private static readonly Segment[] Cross =
        {
            new Segment(0.22f, 0.22f, 0.78f, 0.78f),
            new Segment(0.22f, 0.78f, 0.78f, 0.22f),
        };

        private static readonly Segment[] ChevronDown =
        {
            new Segment(0.20f, 0.62f, 0.50f, 0.32f),
            new Segment(0.50f, 0.32f, 0.80f, 0.62f),
        };

        private static readonly Segment[] ChevronRight =
        {
            new Segment(0.38f, 0.80f, 0.68f, 0.50f),
            new Segment(0.68f, 0.50f, 0.38f, 0.20f),
        };

        private static readonly Segment[] MagnifierHandle =
        {
            new Segment(0.60f, 0.40f, 0.82f, 0.18f),
        };

        private static readonly Segment[] WarningTriangle =
        {
            new Segment(0.50f, 0.88f, 0.92f, 0.14f),
            new Segment(0.92f, 0.14f, 0.08f, 0.14f),
            new Segment(0.08f, 0.14f, 0.50f, 0.88f),
        };

        /// <summary>Coverage in [0, 1] for a pixel center in a size×size icon tile.</summary>
        public static float Coverage(TopiaForgeIcon icon, float px, float py, int size)
        {
            var x = px / size;
            var y = py / size;
            var stroke = 2.4f / size;

            switch (icon)
            {
                case TopiaForgeIcon.Check:
                    return Strokes(Check, x, y, stroke);
                case TopiaForgeIcon.Cross:
                    return Strokes(Cross, x, y, stroke);
                case TopiaForgeIcon.ChevronDown:
                    return Strokes(ChevronDown, x, y, stroke);
                case TopiaForgeIcon.ChevronRight:
                    return Strokes(ChevronRight, x, y, stroke);
                case TopiaForgeIcon.Magnifier:
                    {
                        var ring = RingDisc(x, y, 0.42f, 0.60f, 0.24f, stroke);
                        var handle = Strokes(MagnifierHandle, x, y, stroke * 1.2f);
                        return Math.Max(ring, handle);
                    }

                case TopiaForgeIcon.Grip:
                    {
                        var coverage = 0f;
                        for (var row = 0; row < 2; row++)
                        {
                            for (var column = 0; column < 3; column++)
                            {
                                var cx = 0.30f + (column * 0.20f);
                                var cy = 0.40f + (row * 0.20f);
                                coverage = Math.Max(coverage, Disc(x, y, cx, cy, 0.055f, stroke));
                            }
                        }

                        return coverage;
                    }

                case TopiaForgeIcon.Warning:
                    {
                        var triangle = Strokes(WarningTriangle, x, y, stroke);
                        var bang = Strokes(new[] { new Segment(0.50f, 0.66f, 0.50f, 0.40f) }, x, y, stroke * 1.1f);
                        var dot = Disc(x, y, 0.50f, 0.28f, 0.045f, stroke);
                        return Math.Max(triangle, Math.Max(bang, dot));
                    }

                default:
                    return 0f;
            }
        }

        private static float Strokes(Segment[] segments, float x, float y, float strokeRadius)
        {
            var best = float.MaxValue;
            for (var index = 0; index < segments.Length; index++)
            {
                best = Math.Min(best, DistanceToSegment(x, y, segments[index]));
            }

            // Convert unit-space distance to pixel-ish AA falloff via the stroke radius.
            return Clamp01((strokeRadius - best) / strokeRadius * 2f);
        }

        private static float Disc(float x, float y, float cx, float cy, float radius, float aa)
        {
            var dist = Length(x - cx, y - cy) - radius;
            return Clamp01((aa - dist) / aa);
        }

        private static float RingDisc(float x, float y, float cx, float cy, float radius, float stroke)
        {
            var dist = Math.Abs(Length(x - cx, y - cy) - radius) - (stroke * 0.5f);
            return Clamp01((stroke * 0.5f - dist) / (stroke * 0.5f) * 1.5f);
        }

        private static float DistanceToSegment(float px, float py, Segment segment)
        {
            var vx = segment.X2 - segment.X1;
            var vy = segment.Y2 - segment.Y1;
            var wx = px - segment.X1;
            var wy = py - segment.Y1;
            var lengthSquared = (vx * vx) + (vy * vy);
            var t = lengthSquared <= 0f ? 0f : Clamp01(((wx * vx) + (wy * vy)) / lengthSquared);
            return Length(wx - (t * vx), wy - (t * vy));
        }

        private static float Length(float x, float y)
        {
            return (float)Math.Sqrt((x * x) + (y * y));
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }
    }
}
