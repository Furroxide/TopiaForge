using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// How a world-space name tag fades and shrinks with distance. At or inside
    /// <see cref="NearDistance"/>, and at or beyond <see cref="FarDistance"/>, the tag is hidden; it
    /// fades in over <see cref="NearFadeLength"/> and out over <see cref="FarFadeLength"/>, and its scale
    /// moves linearly from <see cref="NearScale"/> to <see cref="FarScale"/> across the range.
    /// </summary>
    public sealed class TopiaForgeNametagFalloff
    {
        /// <summary>Creates a falloff.</summary>
        /// <exception cref="ArgumentOutOfRangeException">A value is not finite, the range is empty, the fades overlap, or a scale is outside (0, 4].</exception>
        public TopiaForgeNametagFalloff(
            float nearDistance,
            float nearFadeLength,
            float farDistance,
            float farFadeLength,
            float nearScale = 1f,
            float farScale = 0.55f)
        {
            if (!IsFinite(nearDistance) || nearDistance < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(nearDistance), "The near distance must be finite and non-negative.");
            }

            if (!IsFinite(farDistance) || farDistance <= nearDistance)
            {
                throw new ArgumentOutOfRangeException(nameof(farDistance), "The far distance must be finite and beyond the near distance.");
            }

            if (!IsFinite(nearFadeLength) || nearFadeLength < 0f || !IsFinite(farFadeLength) || farFadeLength < 0f
                || nearFadeLength + farFadeLength > farDistance - nearDistance)
            {
                throw new ArgumentOutOfRangeException(nameof(nearFadeLength), "Fade lengths must be non-negative and fit inside the visible range.");
            }

            if (!IsFinite(nearScale) || nearScale <= 0f || nearScale > 4f || !IsFinite(farScale) || farScale <= 0f || farScale > 4f)
            {
                throw new ArgumentOutOfRangeException(nameof(nearScale), "Scales must lie in (0, 4].");
            }

            NearDistance = nearDistance;
            NearFadeLength = nearFadeLength;
            FarDistance = farDistance;
            FarFadeLength = farFadeLength;
            NearScale = nearScale;
            FarScale = farScale;
        }

        /// <summary>
        /// Gets the default falloff: hidden within 0.6 m, fully visible from 1 m, fading out from
        /// 30 m and gone at 40 m (the voice range), shrinking to 55% at the far edge.
        /// </summary>
        public static TopiaForgeNametagFalloff Default { get; } = new TopiaForgeNametagFalloff(0.6f, 0.4f, 40f, 10f);

        /// <summary>Gets the distance, in meters, inside which the tag is hidden.</summary>
        public float NearDistance { get; }

        /// <summary>Gets the distance over which the tag fades in beyond <see cref="NearDistance"/>.</summary>
        public float NearFadeLength { get; }

        /// <summary>Gets the distance, in meters, beyond which the tag is hidden.</summary>
        public float FarDistance { get; }

        /// <summary>Gets the distance over which the tag fades out before <see cref="FarDistance"/>.</summary>
        public float FarFadeLength { get; }

        /// <summary>Gets the scale at the near edge.</summary>
        public float NearScale { get; }

        /// <summary>Gets the scale at the far edge.</summary>
        public float FarScale { get; }

        /// <summary>Returns the opacity for a distance; zero outside the visible range or for NaN.</summary>
        public float Alpha(float distance)
        {
            if (float.IsNaN(distance) || distance <= NearDistance || distance >= FarDistance)
            {
                return 0f;
            }

            var fadeIn = NearFadeLength > 0f ? (distance - NearDistance) / NearFadeLength : 1f;
            var fadeOut = FarFadeLength > 0f ? (FarDistance - distance) / FarFadeLength : 1f;
            var alpha = fadeIn < fadeOut ? fadeIn : fadeOut;
            return alpha >= 1f ? 1f : alpha;
        }

        /// <summary>Returns the scale for a distance, clamped to the near and far scales.</summary>
        public float Scale(float distance)
        {
            if (float.IsNaN(distance))
            {
                return NearScale;
            }

            var t = (distance - NearDistance) / (FarDistance - NearDistance);
            t = t <= 0f ? 0f : t >= 1f ? 1f : t;
            return NearScale + ((FarScale - NearScale) * t);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    /// <summary>Why a name tag is or is not drawn this frame.</summary>
    public enum TopiaForgeNametagVisibility
    {
        /// <summary>Drawn.</summary>
        Visible,

        /// <summary>The anchor reported no position.</summary>
        NoAnchor,

        /// <summary>The anchor is behind the camera or inside its near plane.</summary>
        BehindCamera,

        /// <summary>The anchor projects outside the viewport and its margin.</summary>
        Offscreen,

        /// <summary>The anchor is too near or too far.</summary>
        OutOfRange,
    }

    /// <summary>Where and how a name tag is drawn this frame.</summary>
    public readonly struct TopiaForgeNametagPlacement
    {
        private TopiaForgeNametagPlacement(TopiaForgeNametagVisibility visibility, float x, float y, float alpha, float scale)
        {
            Visibility = visibility;
            ScreenX = x;
            ScreenY = y;
            Alpha = alpha;
            Scale = scale;
        }

        /// <summary>Gets the visibility verdict.</summary>
        public TopiaForgeNametagVisibility Visibility { get; }

        /// <summary>Gets whether the tag is drawn.</summary>
        public bool Visible => Visibility == TopiaForgeNametagVisibility.Visible;

        /// <summary>Gets the anchor's horizontal screen position in pixels.</summary>
        public float ScreenX { get; }

        /// <summary>Gets the anchor's vertical screen position in pixels (bottom-up).</summary>
        public float ScreenY { get; }

        /// <summary>Gets the tag's opacity.</summary>
        public float Alpha { get; }

        /// <summary>Gets the tag's scale.</summary>
        public float Scale { get; }

        internal static TopiaForgeNametagPlacement Hidden(TopiaForgeNametagVisibility reason)
        {
            return new TopiaForgeNametagPlacement(reason, 0f, 0f, 0f, 1f);
        }

        internal static TopiaForgeNametagPlacement Shown(float x, float y, float alpha, float scale)
        {
            return new TopiaForgeNametagPlacement(TopiaForgeNametagVisibility.Visible, x, y, alpha, scale);
        }
    }

    /// <summary>Per-frame visibility, fade and scale decisions for world-space name tags.</summary>
    public static class TopiaForgeNametagMath
    {
        /// <summary>The default allowance, in pixels, for an anchor just outside the viewport.</summary>
        public const float DefaultScreenMargin = 48f;

        /// <summary>
        /// Places a tag: hidden behind the camera (depth at or inside <paramref name="minimumDepth"/>),
        /// offscreen beyond <paramref name="screenMargin"/>, or outside the falloff's range; otherwise
        /// drawn at the projected point with the falloff's opacity and scale.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="falloff"/> is null.</exception>
        public static TopiaForgeNametagPlacement Place(
            in TopiaForgeScreenPoint point,
            float distance,
            in TopiaForgeViewport viewport,
            TopiaForgeNametagFalloff falloff,
            float screenMargin = DefaultScreenMargin,
            float minimumDepth = 0f)
        {
            if (falloff == null)
            {
                throw new ArgumentNullException(nameof(falloff));
            }

            if (!point.Projected || !(point.Depth > minimumDepth) || float.IsInfinity(point.Depth))
            {
                return TopiaForgeNametagPlacement.Hidden(TopiaForgeNametagVisibility.BehindCamera);
            }

            var margin = screenMargin > 0f && !float.IsInfinity(screenMargin) ? screenMargin : 0f;
            if (!(point.X >= viewport.X - margin) || !(point.X <= viewport.X + viewport.Width + margin)
                || !(point.Y >= viewport.Y - margin) || !(point.Y <= viewport.Y + viewport.Height + margin))
            {
                return TopiaForgeNametagPlacement.Hidden(TopiaForgeNametagVisibility.Offscreen);
            }

            var alpha = falloff.Alpha(distance);
            if (alpha <= 0f)
            {
                return TopiaForgeNametagPlacement.Hidden(TopiaForgeNametagVisibility.OutOfRange);
            }

            return TopiaForgeNametagPlacement.Shown(point.X, point.Y, alpha, falloff.Scale(distance));
        }
    }
}
