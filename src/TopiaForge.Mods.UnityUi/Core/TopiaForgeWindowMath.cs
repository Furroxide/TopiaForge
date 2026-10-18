using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Unity-free rect value for window clamp/snap math and persistence.</summary>
    public readonly struct TopiaForgeRect
    {
        /// <summary>Gets x.</summary>
        public readonly float X;
        /// <summary>Gets y.</summary>
        public readonly float Y;
        /// <summary>Gets width.</summary>
        public readonly float Width;
        /// <summary>Gets height.</summary>
        public readonly float Height;

        /// <summary>Creates a rect.</summary>
        public TopiaForgeRect(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return FormattableString.Invariant($"TopiaForgeRect({X:0.#}, {Y:0.#}, {Width:0.#}x{Height:0.#})");
        }
    }

    /// <summary>
    /// Window placement math: clamp fully on-screen, cap size to a viewport fraction,
    /// and snap to edges. Coordinates are canvas-space with origin at bottom-left
    /// (Unity uGUI convention); pure and unit-tested. A screen without a measured size
    /// bounds nothing: no method then shrinks or moves the window.
    /// </summary>
    public static class TopiaForgeWindowMath
    {
        /// <summary>The snap threshold design-token value.</summary>
        public const float SnapThreshold = 12f;
        /// <summary>The max viewport fraction design-token value.</summary>
        public const float MaxViewportFraction = 0.9f;
        /// <summary>The minimum window width design-token value.</summary>
        public const float MinWidth = 240f;
        /// <summary>The minimum window height design-token value.</summary>
        public const float MinHeight = 120f;

        /// <summary>Whether a screen size can bound a window: both sides positive and finite.</summary>
        public static bool HasViewport(float screenWidth, float screenHeight)
        {
            return IsMeasured(screenWidth) && IsMeasured(screenHeight);
        }

        /// <summary>
        /// Places a window when it is shown. A window of fixed size is capped from its designed size,
        /// so a cap taken on a smaller screen does not stick; a window sized by its content keeps the
        /// size it has, since that size is never written back. The rect is then pulled fully on-screen.
        /// Returns false, with the rect unchanged, while the screen is unmeasured.
        /// </summary>
        public static bool TryPlace(
            TopiaForgeRect rect,
            float designedWidth,
            float designedHeight,
            bool sizedByContent,
            float screenWidth,
            float screenHeight,
            out TopiaForgeRect placed)
        {
            if (!HasViewport(screenWidth, screenHeight))
            {
                placed = rect;
                return false;
            }

            var (width, height) = sizedByContent
                ? (rect.Width, rect.Height)
                : ClampSize(designedWidth, designedHeight, screenWidth, screenHeight, MinWidth, MinHeight);
            placed = ClampToScreen(new TopiaForgeRect(rect.X, rect.Y, width, height), screenWidth, screenHeight);
            return true;
        }

        /// <summary>
        /// Caps a window size to the viewport fraction and a sane minimum. Without a viewport
        /// only the minimum applies, so an unmeasured canvas never shrinks a window.
        /// </summary>
        public static (float Width, float Height) ClampSize(
            float width,
            float height,
            float screenWidth,
            float screenHeight,
            float minWidth,
            float minHeight)
        {
            var w = Math.Max(width, minWidth);
            var h = Math.Max(height, minHeight);
            if (!HasViewport(screenWidth, screenHeight))
            {
                return (w, h);
            }

            return (Math.Min(w, screenWidth * MaxViewportFraction), Math.Min(h, screenHeight * MaxViewportFraction));
        }

        /// <summary>Clamps a window rect so it sits fully inside the screen.</summary>
        public static TopiaForgeRect ClampToScreen(TopiaForgeRect rect, float screenWidth, float screenHeight)
        {
            if (!HasViewport(screenWidth, screenHeight))
            {
                return rect;
            }

            var x = Clamp(rect.X, 0f, Math.Max(0f, screenWidth - rect.Width));
            var y = Clamp(rect.Y, 0f, Math.Max(0f, screenHeight - rect.Height));
            return new TopiaForgeRect(x, y, rect.Width, rect.Height);
        }

        /// <summary>Snaps a rect's edges to the screen edges when within the threshold.</summary>
        public static TopiaForgeRect SnapToEdges(TopiaForgeRect rect, float screenWidth, float screenHeight, float threshold = SnapThreshold)
        {
            if (!HasViewport(screenWidth, screenHeight))
            {
                return rect;
            }

            var x = rect.X;
            var y = rect.Y;

            if (Math.Abs(x) <= threshold)
            {
                x = 0f;
            }
            else if (Math.Abs(screenWidth - (x + rect.Width)) <= threshold)
            {
                x = screenWidth - rect.Width;
            }

            if (Math.Abs(y) <= threshold)
            {
                y = 0f;
            }
            else if (Math.Abs(screenHeight - (y + rect.Height)) <= threshold)
            {
                y = screenHeight - rect.Height;
            }

            return new TopiaForgeRect(x, y, rect.Width, rect.Height);
        }

        private static float Clamp(float value, float min, float max)
        {
            return value < min ? min : value > max ? max : value;
        }

        private static bool IsMeasured(float extent)
        {
            return extent > 0f && float.IsFinite(extent);
        }
    }
}
