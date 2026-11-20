using System;

namespace TopiaForge.Mods
{
    internal readonly struct UiLayoutExtent
    {
        internal UiLayoutExtent(double width, double height) { Width = width; Height = height; }
        internal double Width { get; }
        internal double Height { get; }
    }

    internal readonly struct UiLayoutRect
    {
        // Equal cell widths and later translated edges can differ by floating-point roundoff.
        private const double EdgeRounding = 0.000000001d;

        internal UiLayoutRect(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        internal double X { get; }
        internal double Y { get; }
        internal double Width { get; }
        internal double Height { get; }
        internal double Right => X + Width;
        internal double Bottom => Y + Height;
        internal bool HasArea => Width > 0 && Height > 0;

        internal UiLayoutRect Translate(double x, double y) => new UiLayoutRect(X + x, Y + y, Width, Height);

        internal UiLayoutRect WithOutsets(UiLayoutOutsets outsets) => new UiLayoutRect(X - outsets.Left, Y - outsets.Top,
            Width + outsets.Left + outsets.Right, Height + outsets.Top + outsets.Bottom);

        internal bool Contains(UiLayoutRect other) =>
            other.X >= X - EdgeRounding && other.Right <= Right + EdgeRounding &&
            other.Y >= Y - EdgeRounding && other.Bottom <= Bottom + EdgeRounding;

        internal bool Intersects(UiLayoutRect other) => HasArea && other.HasArea &&
            Math.Min(Right, other.Right) - Math.Max(X, other.X) > EdgeRounding &&
            Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y) > EdgeRounding;

        internal UiLayoutRect Intersect(UiLayoutRect other)
        {
            var left = Math.Max(X, other.X);
            var top = Math.Max(Y, other.Y);
            return new UiLayoutRect(left, top,
                Math.Max(0, Math.Min(Right, other.Right) - left), Math.Max(0, Math.Min(Bottom, other.Bottom) - top));
        }
    }

    internal readonly struct UiLayoutOutsets
    {
        internal UiLayoutOutsets(double left, double top, double right, double bottom)
        {
            Left = RequireExtent(left, nameof(left));
            Top = RequireExtent(top, nameof(top));
            Right = RequireExtent(right, nameof(right));
            Bottom = RequireExtent(bottom, nameof(bottom));
        }

        internal double Left { get; }
        internal double Top { get; }
        internal double Right { get; }
        internal double Bottom { get; }

        private static double RequireExtent(double value, string name) => double.IsFinite(value) && value >= 0
            ? value : throw new ArgumentOutOfRangeException(name);
    }

    internal readonly struct UiLayoutConstraints
    {
        internal UiLayoutConstraints(double minWidth, double maxWidth, double minHeight, double maxHeight)
        {
            if (!ValidAxis(minWidth, maxWidth)) throw new ArgumentOutOfRangeException(nameof(maxWidth));
            if (!ValidAxis(minHeight, maxHeight)) throw new ArgumentOutOfRangeException(nameof(maxHeight));
            MinWidth = minWidth;
            MaxWidth = maxWidth;
            MinHeight = minHeight;
            MaxHeight = maxHeight;
        }

        internal double MinWidth { get; }
        internal double MaxWidth { get; }
        internal double MinHeight { get; }
        internal double MaxHeight { get; }

        internal static UiLayoutConstraints Loose(double maxWidth, double maxHeight) => new UiLayoutConstraints(0, maxWidth, 0, maxHeight);

        internal static UiLayoutConstraints Tight(double width, double height) => new UiLayoutConstraints(width, width, height, height);

        internal UiLayoutExtent Fit(UiLayoutExtent extent) =>
            new UiLayoutExtent(Math.Clamp(extent.Width, MinWidth, MaxWidth), Math.Clamp(extent.Height, MinHeight, MaxHeight));

        internal UiLayoutConstraints Inset(double horizontal, double vertical) => new UiLayoutConstraints(
            Math.Max(0, MinWidth - horizontal), Math.Max(0, MaxWidth - horizontal),
            Math.Max(0, MinHeight - vertical), Math.Max(0, MaxHeight - vertical));

        private static bool ValidAxis(double minimum, double maximum) => double.IsFinite(minimum) && minimum >= 0 && maximum >= minimum;
    }

    internal enum UiLayoutFailure { InvalidLayout, ClippedContent, OccludedContent }

    internal sealed class UiLayoutException : ArgumentException
    {
        internal UiLayoutException(UiLayoutFailure failure, string path, string message) : base(message + " At " + path + ".")
        {
            Failure = failure;
            Path = path;
        }

        internal UiLayoutFailure Failure { get; }
        internal string Path { get; }
    }
}
