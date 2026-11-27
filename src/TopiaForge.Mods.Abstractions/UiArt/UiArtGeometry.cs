using System;

namespace TopiaForge.Mods
{
    /// <summary>Normalizes an authored image using the package's verified sprite metadata.</summary>
    internal readonly struct UiImageFrame
    {
        private readonly int pixelWidth;
        private readonly int pixelHeight;
        private readonly UiImageFit fit;

        private UiImageFrame(float width, float height, int pixelWidth, int pixelHeight, UiImageFit fit)
        {
            Width = width;
            Height = height;
            this.pixelWidth = pixelWidth;
            this.pixelHeight = pixelHeight;
            this.fit = fit;
        }

        internal float Width { get; }
        internal float Height { get; }

        internal static UiImageFrame Resolve(UiImage image, int pixelWidth, int pixelHeight, bool isMask)
        {
            if (pixelWidth < 1 || pixelWidth > 2048 || pixelHeight < 1 || pixelHeight > 2048)
                throw new ArgumentException("Verified sprite dimensions must each be between 1 and 2,048 pixels.");
            if (image.Tint.HasValue && !isMask)
                throw new ArgumentException("Only a verified mask image can be tinted.", nameof(image));

            var width = image.Width ?? pixelWidth;
            var height = image.Height ?? pixelHeight;
            UiArtValues.ImageDimension(width, nameof(image.Width));
            UiArtValues.ImageDimension(height, nameof(image.Height));
            return new UiImageFrame(width, height, pixelWidth, pixelHeight, image.Fit);
        }

        internal UiImagePlacement Place(float boxWidth, float boxHeight)
        {
            if (!(boxWidth > 0f && boxWidth <= float.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(boxWidth));
            if (!(boxHeight > 0f && boxHeight <= float.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(boxHeight));

            var scaleX = (double)boxWidth / pixelWidth;
            var scaleY = (double)boxHeight / pixelHeight;
            if (fit == UiImageFit.Contain)
            {
                var scale = Math.Min(scaleX, scaleY);
                var width = (float)(scale * pixelWidth);
                var height = (float)(scale * pixelHeight);
                return new UiImagePlacement((boxWidth - width) * 0.5f, (boxHeight - height) * 0.5f,
                    width, height, 0f, 0f, 1f, 1f);
            }

            // Crop within the source rectangle so even an extreme aspect ratio never makes a large draw rectangle.
            var coverScale = Math.Max(scaleX, scaleY);
            var sourceWidth = (float)(scaleX / coverScale);
            var sourceHeight = (float)(scaleY / coverScale);
            return new UiImagePlacement(0f, 0f, boxWidth, boxHeight,
                (1f - sourceWidth) * 0.5f, (1f - sourceHeight) * 0.5f, sourceWidth, sourceHeight);
        }
    }

    /// <summary>The bounded drawing rectangle and its sample rectangle in sprite-relative coordinates.</summary>
    internal readonly struct UiImagePlacement
    {
        internal UiImagePlacement(float x, float y, float width, float height,
            float sourceLeft, float sourceTop, float sourceWidth, float sourceHeight)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            SourceLeft = sourceLeft;
            SourceTop = sourceTop;
            SourceWidth = sourceWidth;
            SourceHeight = sourceHeight;
        }

        internal float X { get; }
        internal float Y { get; }
        internal float Width { get; }
        internal float Height { get; }

        // Fractions of the sprite rectangle, measured from its top-left before atlas conversion.
        internal float SourceLeft { get; }
        internal float SourceTop { get; }
        internal float SourceWidth { get; }
        internal float SourceHeight { get; }
    }

    /// <summary>Places a bounded pip track in reference units and exposes each mark's fill extent.</summary>
    internal readonly struct UiPipTrack
    {
        // Marks have a semantic minimum. Leave a gap so boundaries remain distinct.
        private const float MinimumWidth = 18f;
        private const float Gap = 4f;

        private readonly int total;
        private readonly int filled;
        private readonly float fraction;
        private readonly int columns;
        private readonly float markWidth;
        private readonly float markHeight;

        internal UiPipTrack(UiPips pips, float availableWidth)
            : this(pips.Total, pips.Filled, pips.Fraction, pips.Presentation, availableWidth) { }

        internal UiPipTrack(UiRefillPresentation pips, float availableWidth)
            : this(pips.Total, pips.Filled, pips.Fraction, pips.Style, availableWidth) { }

        private UiPipTrack(int total, int filled, float fraction, UiPipStyle presentation, float availableWidth)
        {
            if (!(availableWidth >= MinimumWidth && availableWidth <= 4096f))
                throw new ArgumentOutOfRangeException(nameof(availableWidth),
                    "A pip track requires room for at least one readable mark.");

            this.total = total;
            this.filled = filled;
            this.fraction = fraction;
            columns = Math.Min(total, (int)((availableWidth + Gap) / (MinimumWidth + Gap)));
            var segments = presentation == UiPipStyle.Segments;
            markWidth = segments ? (availableWidth - ((columns - 1) * Gap)) / columns : MinimumWidth;
            markHeight = segments ? 8f : MinimumWidth;
            var rows = (total + columns - 1) / columns;
            Width = (columns * markWidth) + ((columns - 1) * Gap);
            Height = (rows * markHeight) + ((rows - 1) * Gap);
        }

        internal float Width { get; }
        internal float Height { get; }

        internal UiPipMark Mark(int index)
        {
            if ((uint)index >= (uint)total)
                throw new ArgumentOutOfRangeException(nameof(index));

            var fill = index < filled ? 1f : index == filled ? fraction : 0f;
            return new UiPipMark((index % columns) * (markWidth + Gap), (index / columns) * (markHeight + Gap),
                markWidth, markHeight, fill);
        }
    }

    internal readonly struct UiPipMark
    {
        private readonly float fraction;

        internal UiPipMark(float x, float y, float width, float height, float fraction)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            this.fraction = fraction;
        }

        internal float X { get; }
        internal float Y { get; }
        internal float Width { get; }
        internal float Height { get; }

        internal UiPipFill Fill(float stroke, float radius)
        {
            var inner = new UiLayoutRect(X + stroke, Y + stroke,
                Math.Max(0f, Width - 2f * stroke), Math.Max(0f, Height - 2f * stroke));
            return new UiPipFill(inner, fraction, Math.Max(0f, radius - stroke));
        }
    }

    internal readonly struct UiPipFill
    {
        internal UiPipFill(UiLayoutRect interior, float fraction, float radius)
        {
            Interior = interior;
            VisibleBounds = new UiLayoutRect(interior.X, interior.Y, interior.Width * fraction, interior.Height);
            Radius = radius;
        }

        internal UiLayoutRect Interior { get; }
        internal UiLayoutRect VisibleBounds { get; }
        internal float Radius { get; }
    }
}
