using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitMultiplayerCoreTests
    {
        private const float FieldOfView = 60f;
        private const float Aspect = 16f / 9f;
        private const float Near = 0.3f;
        private const float Far = 1000f;

        private static void ProjectionMirrorsUnityCameraConventions()
        {
            var identity = TopiaForgeMatrix4.Identity;
            AssertNear(identity[2, 2], 1f, "identity diagonal");
            AssertNear(identity[0, 3], 0f, "identity off-diagonal");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = identity[4, 0], "matrix rows stop at 3");

            var translate = new TopiaForgeMatrix4(1f, 0f, 0f, 5f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f);
            var scale = new TopiaForgeMatrix4(2f, 0f, 0f, 0f, 0f, 2f, 0f, 0f, 0f, 0f, 2f, 0f, 0f, 0f, 0f, 1f);
            var composed = translate.Multiply(scale);
            AssertNear(composed[0, 0], 2f, "translate * scale keeps the scale");
            AssertNear(composed[0, 3], 5f, "translate * scale applies the scale first");
            AssertNear(scale.Multiply(translate)[0, 3], 10f, "scale * translate scales the translation");

            var viewport = new TopiaForgeViewport(0f, 0f, 1920f, 1080f);
            var frame = CameraAt(0f, 1.6f, 0f, viewport);

            var ahead = frame.Project(0f, 1.6f, 10f);
            Assert(ahead.Projected, "a point ahead projects");
            AssertNear(ahead.X, 960f, "straight ahead is the horizontal centre", 0.01f);
            AssertNear(ahead.Y, 540f, "straight ahead is the vertical centre", 0.01f);
            AssertNear(ahead.Depth, 10f, "depth is the distance along the view axis", 0.0001f);

            var halfHeight = (float)Math.Tan(FieldOfView * 0.5 * Math.PI / 180.0) * 10f;
            var top = frame.Project(0f, 1.6f + halfHeight, 10f);
            AssertNear(top.Y, 1080f, "the top frustum edge maps to the top pixel row", 0.05f);
            var right = frame.Project(halfHeight * Aspect, 1.6f, 10f);
            AssertNear(right.X, 1920f, "the right frustum edge maps to the right pixel column", 0.05f);
            var left = frame.Project(-halfHeight * Aspect * 0.5f, 1.6f, 10f);
            AssertNear(left.X, 480f, "halfway to the left edge is a quarter across", 0.05f);

            var behind = frame.Project(0f, 1.6f, -10f);
            AssertNear(behind.Depth, -10f, "a point behind the camera has negative depth", 0.0001f);
            var onPlane = frame.Project(3f, 1.6f, 0f);
            Assert(!onPlane.Projected, "a point on the camera plane cannot be projected");
            var nan = frame.Project(float.NaN, 0f, 5f);
            Assert(!nan.Projected, "a NaN point cannot be projected");

            var offset = CameraAt(0f, 1.6f, 0f, new TopiaForgeViewport(100f, 50f, 800f, 600f));
            var centred = offset.Project(0f, 1.6f, 10f);
            AssertNear(centred.X, 500f, "a viewport origin shifts x", 0.01f);
            AssertNear(centred.Y, 350f, "a viewport origin shifts y", 0.01f);

            var moved = CameraAt(10f, 0f, -5f, viewport);
            var target = moved.Project(10f, 0f, 15f);
            AssertNear(target.X, 960f, "camera translation is applied", 0.01f);
            AssertNear(target.Depth, 20f, "depth follows the camera position", 0.0001f);
            AssertNear(moved.DistanceTo(13f, 4f, -5f), 5f, "straight-line distance from the camera");
        }

        private static void NametagFalloffAndPlacement()
        {
            var falloff = new TopiaForgeNametagFalloff(nearDistance: 1f, nearFadeLength: 1f, farDistance: 21f, farFadeLength: 5f, nearScale: 1f, farScale: 0.5f);
            AssertNear(falloff.Alpha(0.5f), 0f, "inside the near distance is hidden");
            AssertNear(falloff.Alpha(1f), 0f, "exactly at the near distance is hidden");
            AssertNear(falloff.Alpha(1.5f), 0.5f, "fading in");
            AssertNear(falloff.Alpha(2f), 1f, "fully visible after the near fade");
            AssertNear(falloff.Alpha(10f), 1f, "fully visible mid-range");
            AssertNear(falloff.Alpha(18.5f), 0.5f, "fading out");
            AssertNear(falloff.Alpha(21f), 0f, "exactly at the far distance is hidden");
            AssertNear(falloff.Alpha(500f), 0f, "beyond the far distance is hidden");
            AssertNear(falloff.Alpha(float.NaN), 0f, "a NaN distance is hidden");
            AssertNear(falloff.Scale(0f), 1f, "near scale below the range");
            AssertNear(falloff.Scale(11f), 0.75f, "scale interpolates across the range");
            AssertNear(falloff.Scale(1000f), 0.5f, "far scale beyond the range");
            AssertNear(falloff.Scale(float.NaN), 1f, "a NaN distance uses the near scale");

            var sharp = new TopiaForgeNametagFalloff(0f, 0f, 10f, 0f);
            AssertNear(sharp.Alpha(0.01f), 1f, "zero fade lengths switch on immediately");
            AssertNear(sharp.Alpha(9.99f), 1f, "zero fade lengths stay on to the far edge");
            AssertNear(sharp.Alpha(0f), 0f, "with zero fades, exactly at the near distance is still hidden");
            AssertNear(sharp.Alpha(10f), 0f, "with zero fades, exactly at the far distance is hidden");

            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgeNametagFalloff(5f, 0f, 5f, 0f), "an empty range is rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgeNametagFalloff(-1f, 0f, 5f, 0f), "a negative near distance is rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgeNametagFalloff(0f, 3f, 5f, 3f), "overlapping fades are rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgeNametagFalloff(0f, 0f, float.PositiveInfinity, 0f), "an infinite far distance is rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgeNametagFalloff(0f, 0f, 5f, 0f, 0f, 1f), "a zero scale is rejected");
            AssertThrows<ArgumentOutOfRangeException>(() => _ = new TopiaForgeNametagFalloff(0f, float.NaN, 5f, 0f), "a NaN fade is rejected");
            Assert(TopiaForgeNametagFalloff.Default.FarDistance > TopiaForgeNametagFalloff.Default.NearDistance, "the default falloff is valid");

            var viewport = new TopiaForgeViewport(0f, 0f, 1920f, 1080f);
            var frame = CameraAt(0f, 0f, 0f, viewport);
            var placed = Place(frame, viewport, falloff, 0f, 0f, 10f);
            Assert(placed.Visible, "a point ahead within range is visible");
            AssertNear(placed.ScreenX, 960f, "placed at the projected x", 0.01f);
            AssertNear(placed.Alpha, 1f, "fully opaque mid-range");
            AssertNear(placed.Scale, 0.775f, "scaled for its distance", 0.0001f);

            AssertEqual(Place(frame, viewport, falloff, 0f, 0f, -10f).Visibility, TopiaForgeNametagVisibility.BehindCamera, "a point behind the camera is hidden");
            AssertEqual(Place(frame, viewport, falloff, 0f, 0f, 0.1f, minimumDepth: 0.3f).Visibility, TopiaForgeNametagVisibility.BehindCamera, "a point inside the near clip plane is hidden");
            AssertEqual(Place(frame, viewport, falloff, 40f, 0f, 10f).Visibility, TopiaForgeNametagVisibility.Offscreen, "a point far to the side is offscreen");
            AssertEqual(Place(frame, viewport, falloff, 0f, 0f, 30f).Visibility, TopiaForgeNametagVisibility.OutOfRange, "a point beyond the far distance is out of range");
            AssertEqual(Place(frame, viewport, falloff, 0f, 0f, 0.8f).Visibility, TopiaForgeNametagVisibility.OutOfRange, "a point inside the near distance is out of range");

            var edgeX = (float)Math.Tan(FieldOfView * 0.5 * Math.PI / 180.0) * Aspect * 10f;
            var justOutside = frame.Project(edgeX * 1.02f, 0f, 10f);
            Assert(TopiaForgeNametagMath.Place(justOutside, 10f, viewport, falloff, screenMargin: 48f).Visible, "an anchor just outside the edge stays within the margin");
            AssertEqual(TopiaForgeNametagMath.Place(justOutside, 10f, viewport, falloff, screenMargin: 0f).Visibility, TopiaForgeNametagVisibility.Offscreen, "without a margin the same anchor is offscreen");
            AssertEqual(TopiaForgeNametagMath.Place(new TopiaForgeScreenPoint(10f, 10f, float.NaN, true), 5f, viewport, falloff).Visibility, TopiaForgeNametagVisibility.BehindCamera, "a NaN depth is hidden");
            AssertEqual(TopiaForgeNametagMath.Place(new TopiaForgeScreenPoint(float.NaN, 10f, 5f, true), 5f, viewport, falloff).Visibility, TopiaForgeNametagVisibility.Offscreen, "a NaN position is hidden");
            AssertThrows<ArgumentNullException>(() => TopiaForgeNametagMath.Place(default(TopiaForgeScreenPoint), 1f, viewport, null!), "a null falloff is rejected");
        }

        private static TopiaForgeNametagPlacement Place(
            TopiaForgeCameraFrame frame,
            TopiaForgeViewport viewport,
            TopiaForgeNametagFalloff falloff,
            float x,
            float y,
            float z,
            float minimumDepth = 0f)
        {
            var point = frame.Project(x, y, z);
            return TopiaForgeNametagMath.Place(point, frame.DistanceTo(x, y, z), viewport, falloff, TopiaForgeNametagMath.DefaultScreenMargin, minimumDepth);
        }

        /// <summary>
        /// Builds the frame Unity reports for an unrotated perspective camera: world-to-camera flips
        /// Z (camera space looks down negative Z) after translating by the camera position, and the
        /// projection is the OpenGL-style matrix Unity's <c>Camera.projectionMatrix</c> returns.
        /// </summary>
        private static TopiaForgeCameraFrame CameraAt(float x, float y, float z, TopiaForgeViewport viewport)
        {
            var worldToCamera = new TopiaForgeMatrix4(
                1f, 0f, 0f, -x,
                0f, 1f, 0f, -y,
                0f, 0f, -1f, z,
                0f, 0f, 0f, 1f);
            var f = (float)(1.0 / Math.Tan(FieldOfView * 0.5 * Math.PI / 180.0));
            var projection = new TopiaForgeMatrix4(
                f / Aspect, 0f, 0f, 0f,
                0f, f, 0f, 0f,
                0f, 0f, (Far + Near) / (Near - Far), 2f * Far * Near / (Near - Far),
                0f, 0f, -1f, 0f);
            return new TopiaForgeCameraFrame(worldToCamera, projection, viewport, x, y, z);
        }
    }
}
