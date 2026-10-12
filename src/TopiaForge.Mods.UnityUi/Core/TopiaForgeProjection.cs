using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// A Unity-free 4x4 matrix using the column-vector convention and the same <c>m{row}{column}</c>
    /// layout as Unity's <c>Matrix4x4</c>, so a camera's matrices copy across field for field.
    /// </summary>
    public readonly struct TopiaForgeMatrix4
    {
        internal readonly float M00, M01, M02, M03;
        internal readonly float M10, M11, M12, M13;
        internal readonly float M20, M21, M22, M23;
        internal readonly float M30, M31, M32, M33;

        /// <summary>Creates a matrix from its elements in row order.</summary>
        public TopiaForgeMatrix4(
            float m00, float m01, float m02, float m03,
            float m10, float m11, float m12, float m13,
            float m20, float m21, float m22, float m23,
            float m30, float m31, float m32, float m33)
        {
            M00 = m00; M01 = m01; M02 = m02; M03 = m03;
            M10 = m10; M11 = m11; M12 = m12; M13 = m13;
            M20 = m20; M21 = m21; M22 = m22; M23 = m23;
            M30 = m30; M31 = m31; M32 = m32; M33 = m33;
        }

        /// <summary>Gets the identity matrix.</summary>
        public static TopiaForgeMatrix4 Identity => new TopiaForgeMatrix4(
            1f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            0f, 0f, 0f, 1f);

        /// <summary>Gets an element by row and column (both 0 to 3).</summary>
        public float this[int row, int column]
        {
            get
            {
                switch ((row * 4) + column)
                {
                    case 0: return M00;
                    case 1: return M01;
                    case 2: return M02;
                    case 3: return M03;
                    case 4: return M10;
                    case 5: return M11;
                    case 6: return M12;
                    case 7: return M13;
                    case 8: return M20;
                    case 9: return M21;
                    case 10: return M22;
                    case 11: return M23;
                    case 12: return M30;
                    case 13: return M31;
                    case 14: return M32;
                    case 15: return M33;
                    default: throw new ArgumentOutOfRangeException(nameof(row), "Matrix indices run from 0 to 3.");
                }
            }
        }

        /// <summary>Returns <c>this * other</c>: applying the result equals applying <paramref name="other"/> first.</summary>
        public TopiaForgeMatrix4 Multiply(in TopiaForgeMatrix4 other)
        {
            return new TopiaForgeMatrix4(
                (M00 * other.M00) + (M01 * other.M10) + (M02 * other.M20) + (M03 * other.M30),
                (M00 * other.M01) + (M01 * other.M11) + (M02 * other.M21) + (M03 * other.M31),
                (M00 * other.M02) + (M01 * other.M12) + (M02 * other.M22) + (M03 * other.M32),
                (M00 * other.M03) + (M01 * other.M13) + (M02 * other.M23) + (M03 * other.M33),
                (M10 * other.M00) + (M11 * other.M10) + (M12 * other.M20) + (M13 * other.M30),
                (M10 * other.M01) + (M11 * other.M11) + (M12 * other.M21) + (M13 * other.M31),
                (M10 * other.M02) + (M11 * other.M12) + (M12 * other.M22) + (M13 * other.M32),
                (M10 * other.M03) + (M11 * other.M13) + (M12 * other.M23) + (M13 * other.M33),
                (M20 * other.M00) + (M21 * other.M10) + (M22 * other.M20) + (M23 * other.M30),
                (M20 * other.M01) + (M21 * other.M11) + (M22 * other.M21) + (M23 * other.M31),
                (M20 * other.M02) + (M21 * other.M12) + (M22 * other.M22) + (M23 * other.M32),
                (M20 * other.M03) + (M21 * other.M13) + (M22 * other.M23) + (M23 * other.M33),
                (M30 * other.M00) + (M31 * other.M10) + (M32 * other.M20) + (M33 * other.M30),
                (M30 * other.M01) + (M31 * other.M11) + (M32 * other.M21) + (M33 * other.M31),
                (M30 * other.M02) + (M31 * other.M12) + (M32 * other.M22) + (M33 * other.M32),
                (M30 * other.M03) + (M31 * other.M13) + (M32 * other.M23) + (M33 * other.M33));
        }
    }

    /// <summary>A camera's pixel rectangle on the screen (origin bottom-left, as Unity reports it).</summary>
    public readonly struct TopiaForgeViewport
    {
        /// <summary>Creates a viewport.</summary>
        public TopiaForgeViewport(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>Gets the left edge in pixels.</summary>
        public float X { get; }

        /// <summary>Gets the bottom edge in pixels.</summary>
        public float Y { get; }

        /// <summary>Gets the width in pixels.</summary>
        public float Width { get; }

        /// <summary>Gets the height in pixels.</summary>
        public float Height { get; }
    }

    /// <summary>A world point projected into screen pixels.</summary>
    public readonly struct TopiaForgeScreenPoint
    {
        /// <summary>Creates a projected point.</summary>
        public TopiaForgeScreenPoint(float x, float y, float depth, bool projected)
        {
            X = x;
            Y = y;
            Depth = depth;
            Projected = projected;
        }

        /// <summary>Gets the horizontal screen position in pixels.</summary>
        public float X { get; }

        /// <summary>Gets the vertical screen position in pixels (bottom-up).</summary>
        public float Y { get; }

        /// <summary>Gets the distance in front of the camera along its view axis; negative is behind.</summary>
        public float Depth { get; }

        /// <summary>Gets whether the projection was defined (false on the camera plane or for non-finite input).</summary>
        public bool Projected { get; }
    }

    /// <summary>
    /// One frame's camera: world-to-clip projection, viewport and position, precomputed once so
    /// projecting many points costs a few multiply-adds each. Mirrors Unity's
    /// <c>Camera.WorldToScreenPoint</c>: the screen position comes from projection * view, and the
    /// depth is the distance in front of the camera (camera space looks down negative Z).
    /// </summary>
    public readonly struct TopiaForgeCameraFrame
    {
        private const float MinimumW = 1e-6f;

        private readonly TopiaForgeMatrix4 viewProjection;
        private readonly float depthX;
        private readonly float depthY;
        private readonly float depthZ;
        private readonly float depthW;

        /// <summary>Creates a frame from a camera's world-to-camera and projection matrices.</summary>
        public TopiaForgeCameraFrame(
            in TopiaForgeMatrix4 worldToCamera,
            in TopiaForgeMatrix4 projection,
            TopiaForgeViewport viewport,
            float cameraX,
            float cameraY,
            float cameraZ)
        {
            viewProjection = projection.Multiply(worldToCamera);
            depthX = -worldToCamera.M20;
            depthY = -worldToCamera.M21;
            depthZ = -worldToCamera.M22;
            depthW = -worldToCamera.M23;
            Viewport = viewport;
            CameraX = cameraX;
            CameraY = cameraY;
            CameraZ = cameraZ;
        }

        /// <summary>Gets the camera's pixel rectangle.</summary>
        public TopiaForgeViewport Viewport { get; }

        /// <summary>Gets the camera's world x position.</summary>
        public float CameraX { get; }

        /// <summary>Gets the camera's world y position.</summary>
        public float CameraY { get; }

        /// <summary>Gets the camera's world z position.</summary>
        public float CameraZ { get; }

        /// <summary>Projects a world point into screen pixels.</summary>
        public TopiaForgeScreenPoint Project(float x, float y, float z)
        {
            var depth = (depthX * x) + (depthY * y) + (depthZ * z) + depthW;
            var clipX = (viewProjection.M00 * x) + (viewProjection.M01 * y) + (viewProjection.M02 * z) + viewProjection.M03;
            var clipY = (viewProjection.M10 * x) + (viewProjection.M11 * y) + (viewProjection.M12 * z) + viewProjection.M13;
            var clipW = (viewProjection.M30 * x) + (viewProjection.M31 * y) + (viewProjection.M32 * z) + viewProjection.M33;
            if (!(Math.Abs(clipW) >= MinimumW) || float.IsNaN(clipX) || float.IsNaN(clipY) || float.IsInfinity(clipW))
            {
                return new TopiaForgeScreenPoint(0f, 0f, float.IsNaN(depth) ? 0f : depth, projected: false);
            }

            var screenX = Viewport.X + (((clipX / clipW) + 1f) * 0.5f * Viewport.Width);
            var screenY = Viewport.Y + (((clipY / clipW) + 1f) * 0.5f * Viewport.Height);
            return new TopiaForgeScreenPoint(screenX, screenY, depth, projected: true);
        }

        /// <summary>Returns the straight-line distance from the camera to a world point.</summary>
        public float DistanceTo(float x, float y, float z)
        {
            var dx = x - CameraX;
            var dy = y - CameraY;
            var dz = z - CameraZ;
            return (float)Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }
    }
}
