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
    /// (Unity uGUI convention); pure and unit-tested.
    /// </summary>
    public static class TopiaForgeWindowMath
    {
        /// <summary>The snap threshold design-token value.</summary>
        public const float SnapThreshold = 12f;
        /// <summary>The max viewport fraction design-token value.</summary>
        public const float MaxViewportFraction = 0.9f;

        /// <summary>Caps a window size to the viewport fraction and a sane minimum.</summary>
        public static (float Width, float Height) ClampSize(
            float width,
            float height,
            float screenWidth,
            float screenHeight,
            float minWidth,
            float minHeight)
        {
            var maxW = screenWidth * MaxViewportFraction;
            var maxH = screenHeight * MaxViewportFraction;
            var w = Math.Min(Math.Max(width, minWidth), maxW);
            var h = Math.Min(Math.Max(height, minHeight), maxH);
            return (w, h);
        }

        /// <summary>Clamps a window rect so it sits fully inside the screen.</summary>
        public static TopiaForgeRect ClampToScreen(TopiaForgeRect rect, float screenWidth, float screenHeight)
        {
            var x = Clamp(rect.X, 0f, Math.Max(0f, screenWidth - rect.Width));
            var y = Clamp(rect.Y, 0f, Math.Max(0f, screenHeight - rect.Height));
            return new TopiaForgeRect(x, y, rect.Width, rect.Height);
        }

        /// <summary>Snaps a rect's edges to the screen edges when within the threshold.</summary>
        public static TopiaForgeRect SnapToEdges(TopiaForgeRect rect, float screenWidth, float screenHeight, float threshold = SnapThreshold)
        {
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
    }
}
