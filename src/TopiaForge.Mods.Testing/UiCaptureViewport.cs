using System;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Defines the screen dimensions for a deterministic UI capture.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiCaptureViewport
    {
        /// <summary>Creates a viewport from 320 to 7680 pixels wide and 240 to 4320 pixels high.</summary>
        public UiCaptureViewport(int width, int height)
        {
            if (width < 320 || width > 7680) throw new ArgumentOutOfRangeException(nameof(width));
            if (height < 240 || height > 4320) throw new ArgumentOutOfRangeException(nameof(height));
            Width = width;
            Height = height;
        }

        internal int Width { get; }
        internal int Height { get; }

        // CanvasScaler gives equal weight to log(width / 1920) and log(height / 1080).
        internal float ScreenScale => (float)Math.Sqrt((Width / 1920d) * (Height / 1080d));
    }

    internal readonly struct UiCaptureRect
    {
        internal UiCaptureRect(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        internal float X { get; }
        internal float Y { get; }
        internal float Width { get; }
        internal float Height { get; }
        internal float Right => X + Width;
        internal float Bottom => Y + Height;

        internal bool Overlaps(UiCaptureRect other) =>
            Width > 0 && Height > 0 && other.Width > 0 && other.Height > 0 &&
            X < other.Right && Y < other.Bottom && Right > other.X && Bottom > other.Y;

        internal bool Contains(UiCaptureRect other)
        {
            const float layoutTolerance = 0.001f;
            return other.X >= X - layoutTolerance && other.Y >= Y - layoutTolerance &&
                other.Right <= Right + layoutTolerance && other.Bottom <= Bottom + layoutTolerance;
        }

        internal UiCaptureRect Intersect(UiCaptureRect other)
        {
            var left = Math.Max(X, other.X);
            var top = Math.Max(Y, other.Y);
            return new UiCaptureRect(left, top,
                Math.Max(0, Math.Min(Right, other.Right) - left), Math.Max(0, Math.Min(Bottom, other.Bottom) - top));
        }

        internal UiCaptureRect Inset(float left, float top, float right, float bottom) =>
            new UiCaptureRect(X + left, Y + top, Math.Max(0, Width - left - right), Math.Max(0, Height - top - bottom));
    }
}
