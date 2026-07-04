using System;

namespace Robotopia.Mods.UnityUi
{
    /// <summary>
    /// One framing solution for an orthographic thumbnail camera: where to put it,
    /// how wide to open it, and how tight the near/far planes can be.
    /// Offset is the unit direction from the subject's center toward the camera.
    /// </summary>
    public readonly struct QwPreviewFraming
    {
        public QwPreviewFraming(float offsetX, float offsetY, float offsetZ, float distance, float orthoHalfSize, float nearPlane, float farPlane)
        {
            OffsetX = offsetX;
            OffsetY = offsetY;
            OffsetZ = offsetZ;
            Distance = distance;
            OrthoHalfSize = orthoHalfSize;
            NearPlane = nearPlane;
            FarPlane = farPlane;
        }

        public float OffsetX { get; }
        public float OffsetY { get; }
        public float OffsetZ { get; }
        public float Distance { get; }
        public float OrthoHalfSize { get; }
        public float NearPlane { get; }
        public float FarPlane { get; }
    }

    /// <summary>
    /// Framing math for prefab thumbnails: fits an axis-aligned bounds into a square
    /// orthographic view from a yaw/pitch three-quarter angle. Pure and unit-tested;
    /// the Unity-side renderer only copies the numbers onto a camera.
    /// </summary>
    public static class QwPreviewMath
    {
        /// <summary>The classic product-shot angle used for prop thumbnails.</summary>
        public const float DefaultYawDegrees = 45f;
        public const float DefaultPitchDegrees = 30f;
        public const float DefaultMargin = 1.15f;

        /// <summary>Floor for the ortho size so degenerate bounds still yield a valid camera.</summary>
        public const float MinHalfSize = 0.01f;

        private const float DepthPadding = 1f;
        private const float MinNearPlane = 0.01f;

        /// <summary>
        /// Frames an axis-aligned box of half-extents (extentX, extentY, extentZ),
        /// viewed from yaw degrees around the vertical axis and pitch degrees above the
        /// horizon, with margin as a multiplier on the fitted size (1 = touching edges).
        /// </summary>
        public static QwPreviewFraming Frame(
            float extentX,
            float extentY,
            float extentZ,
            float yawDegrees = DefaultYawDegrees,
            float pitchDegrees = DefaultPitchDegrees,
            float margin = DefaultMargin)
        {
            var ex = Math.Abs(extentX);
            var ey = Math.Abs(extentY);
            var ez = Math.Abs(extentZ);

            var yaw = yawDegrees * (Math.PI / 180.0);
            var pitch = pitchDegrees * (Math.PI / 180.0);

            // Unit direction from the subject center toward the camera.
            var ox = (float)(Math.Cos(pitch) * Math.Sin(yaw));
            var oy = (float)Math.Sin(pitch);
            var oz = (float)(Math.Cos(pitch) * Math.Cos(yaw));

            // Camera basis: right is horizontal (perpendicular to the offset around the
            // vertical axis), up completes the frame. Both unit-length by construction.
            var rx = (float)Math.Cos(yaw);
            var rz = -(float)Math.Sin(yaw);

            // up = offset × right (offset plays the role of "backward"); unit-length
            // because offset ⊥ right by construction.
            var ux = oy * rz;
            var uy = (oz * rx) - (ox * rz);
            var uz = -(oy * rx);

            // Half-extents of the box projected onto the camera axes: for an AABB the
            // support along a unit axis u is ex|u.x| + ey|u.y| + ez|u.z|.
            var halfWidth = (ex * Math.Abs(rx)) + (ez * Math.Abs(rz));
            var halfHeight = (ex * Math.Abs(ux)) + (ey * Math.Abs(uy)) + (ez * Math.Abs(uz));
            var halfDepth = (ex * Math.Abs(ox)) + (ey * Math.Abs(oy)) + (ez * Math.Abs(oz));

            // Square render target (aspect 1): the ortho half-size must cover both axes.
            var half = (float)Math.Max(halfWidth, halfHeight) * Math.Max(margin, 0.01f);
            if (half < MinHalfSize)
            {
                half = MinHalfSize;
            }

            var distance = (float)halfDepth + DepthPadding;
            var near = Math.Max(MinNearPlane, distance - (float)halfDepth - DepthPadding);
            var far = distance + (float)halfDepth + DepthPadding;

            return new QwPreviewFraming(ox, oy, oz, distance, half, near, far);
        }
    }
}
