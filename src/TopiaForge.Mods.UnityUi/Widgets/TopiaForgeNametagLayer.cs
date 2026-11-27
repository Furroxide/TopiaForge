using System;
using System.Collections.Generic;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Persistent world-space name tags. Unlike the time-limited floaters, a tag stays until it is
    /// removed and follows an anchor (any <see cref="ITopiaForgeNametagAnchor"/>) plus a vertical
    /// offset. Every frame the layer projects each anchor through the camera, hides tags behind the
    /// camera, offscreen or out of range, and fades and scales the rest by distance; that math is
    /// unit-tested in <see cref="TopiaForgeCameraFrame"/> and <see cref="TopiaForgeNametagMath"/>.
    /// A tag has a name, an optional secondary line, a badge (for example HOST) and a talking glyph
    /// slot. Tags are pooled, the layer sits on its own canvas, and the per-frame pass writes only
    /// values that moved, so steady state allocates nothing.
    /// </summary>
    public sealed class TopiaForgeNametagLayer : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        /// <summary>The most tags one layer can track.</summary>
        public const int MaxCapacity = 64;

        /// <summary>The default height of a tag above its anchor, in meters.</summary>
        public const float DefaultVerticalOffset = 0.35f;

        private readonly int capacity;
        private readonly TopiaForgeContainer root;
        private readonly List<TopiaForgeNametagSlot> slots = new List<TopiaForgeNametagSlot>();
        private readonly Stack<int> free = new Stack<int>();
        private readonly TopiaForgeNametagDriver driver;
        private TopiaForgeNametagFalloff falloff = TopiaForgeNametagFalloff.Default;
        private float screenMargin = TopiaForgeNametagMath.DefaultScreenMargin;
        private Camera? cachedCamera;
        private Camera[] cameraBuffer = Array.Empty<Camera>();
        private float nextCameraCheck;
        private int activeCount;
        private bool fullWarned;
        private bool anchorFailureWarned;

        internal TopiaForgeNametagLayer(TopiaForgeContainer parent, int maxTags)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("NametagLayer"))
        {
            capacity = Math.Max(1, Math.Min(maxTags, MaxCapacity));
            TopiaForgeAnchors.Stretch(Rect);
            this.Dynamic();
            root = new TopiaForgeContainer(Host, Scheme, Go);
            driver = Go.AddComponent<TopiaForgeNametagDriver>();
            driver.Layer = this;
            driver.enabled = false;
        }

        /// <summary>Gets the most tags this layer tracks at once.</summary>
        public int Capacity => capacity;

        /// <summary>Gets the number of tracked tags.</summary>
        public int Count => activeCount;

        /// <summary>Uses a custom distance falloff for every tag.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public TopiaForgeNametagLayer WithFalloff(TopiaForgeNametagFalloff value)
        {
            falloff = value ?? throw new ArgumentNullException(nameof(value));
            return this;
        }

        /// <summary>Sets how far past the screen edge, in pixels, an anchor may sit before its tag hides.</summary>
        public TopiaForgeNametagLayer ScreenMargin(float pixels)
        {
            screenMargin = float.IsNaN(pixels) || float.IsInfinity(pixels) || pixels < 0f ? 0f : pixels;
            return this;
        }

        /// <summary>
        /// Starts tracking an anchor. Returns null (and logs once) when the layer is full or gone.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="anchor"/> is null.</exception>
        public TopiaForgeNametag? Track(ITopiaForgeNametagAnchor anchor, string name, float verticalOffset = DefaultVerticalOffset)
        {
            if (anchor == null)
            {
                throw new ArgumentNullException(nameof(anchor));
            }

            if (Go == null)
            {
                return null;
            }

            int index;
            if (free.Count > 0)
            {
                index = free.Pop();
            }
            else if (slots.Count < capacity)
            {
                index = slots.Count;
                var created = new TopiaForgeNametagSlot(root, index);
                created.ApplyTheme(Theme);
                slots.Add(created);
            }
            else
            {
                if (!fullWarned)
                {
                    fullWarned = true;
                    TopiaForgeLog.Warn("Name tag layer is full (" + capacity + " tags); ignoring further tags until one is removed.");
                }

                return null;
            }

            var slot = slots[index];
            slot.Activate(anchor, verticalOffset);
            slot.SetName(name);
            activeCount++;
            if (!driver.enabled)
            {
                driver.enabled = true;
            }

            return new TopiaForgeNametag(this, index, slot.Generation);
        }

        /// <summary>Removes every tag; their handles go stale.</summary>
        public void Clear()
        {
            for (var index = 0; index < slots.Count; index++)
            {
                if (slots[index].Active)
                {
                    Release(index);
                }
            }
        }

        /// <summary>Applies the resolved theme to this UI element.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            for (var index = 0; index < slots.Count; index++)
            {
                slots[index].ApplyTheme(theme);
            }
        }

        internal bool IsCurrent(int index, int generation)
        {
            return Go != null && index >= 0 && index < slots.Count && slots[index].Active && slots[index].Generation == generation;
        }

        internal TopiaForgeNametagSlot SlotAt(int index)
        {
            return slots[index];
        }

        internal void Release(int index)
        {
            var slot = slots[index];
            if (!slot.Active)
            {
                return;
            }

            slot.Deactivate();
            free.Push(index);
            activeCount--;
            if (activeCount == 0 && driver != null)
            {
                driver.enabled = false;
            }
        }

        internal void Tick()
        {
            if (activeCount == 0)
            {
                driver.enabled = false;
                return;
            }

            var camera = ResolveCamera();
            if (camera == null)
            {
                HideAll();
                return;
            }

            var frame = FrameOf(camera);
            var minimumDepth = camera.nearClipPlane;
            var viewport = frame.Viewport;
            for (var index = 0; index < slots.Count; index++)
            {
                var slot = slots[index];
                if (!slot.Active)
                {
                    continue;
                }

                if (!TryReadAnchor(slot, out var anchor))
                {
                    slot.Hide();
                    continue;
                }

                var y = anchor.Y + slot.VerticalOffset;
                var presentation = slot.Anchor as ITopiaForgeNametagPresentation;
                var point = frame.Project(anchor.X, y, anchor.Z);
                var placement = TopiaForgeNametagMath.Place(point, frame.DistanceTo(anchor.X, y, anchor.Z), viewport,
                    presentation?.Falloff ?? falloff, screenMargin, minimumDepth);
                if (!placement.Visible
                    || (presentation != null && !CanPresent(presentation, frame, new Vec3(anchor.X, y, anchor.Z)))
                    || !RectTransformUtility.ScreenPointToLocalPointInRectangle(Rect, new Vector2(placement.ScreenX, placement.ScreenY), null, out var local))
                {
                    slot.Hide();
                    continue;
                }

                // The kit's canvas scaler already applies accessibility scale.
                slot.Show(local, placement.Scale, placement.Alpha);
            }
        }

        private static bool CanPresent(ITopiaForgeNametagPresentation presentation, TopiaForgeCameraFrame frame, Vec3 anchor)
        {
            try { return presentation.CanShow(new Vec3(frame.CameraX, frame.CameraY, frame.CameraZ), anchor); }
            catch (Exception) { return false; }
        }

        private bool TryReadAnchor(TopiaForgeNametagSlot slot, out Vec3 position)
        {
            try
            {
                if (slot.Anchor != null && slot.Anchor.TryGetPosition(out position) && position.IsFinite)
                {
                    return true;
                }
            }
            catch (Exception exception)
            {
                if (!anchorFailureWarned)
                {
                    anchorFailureWarned = true;
                    TopiaForgeLog.Warn("A name tag anchor failed (" + exception.GetType().Name + "); its tag stays hidden while it keeps failing.");
                }
            }

            position = Vec3.Zero;
            return false;
        }

        private void HideAll()
        {
            for (var index = 0; index < slots.Count; index++)
            {
                if (slots[index].Active)
                {
                    slots[index].Hide();
                }
            }
        }

        private static TopiaForgeCameraFrame FrameOf(Camera camera)
        {
            var rect = camera.pixelRect;
            var position = camera.transform.position;
            return new TopiaForgeCameraFrame(
                Convert(camera.worldToCameraMatrix),
                Convert(camera.projectionMatrix),
                new TopiaForgeViewport(rect.x, rect.y, rect.width, rect.height),
                position.x,
                position.y,
                position.z);
        }

        private static TopiaForgeMatrix4 Convert(Matrix4x4 m)
        {
            return new TopiaForgeMatrix4(
                m.m00, m.m01, m.m02, m.m03,
                m.m10, m.m11, m.m12, m.m13,
                m.m20, m.m21, m.m22, m.m23,
                m.m30, m.m31, m.m32, m.m33);
        }

        private Camera? ResolveCamera()
        {
            if (cachedCamera != null && cachedCamera.isActiveAndEnabled)
            {
                return cachedCamera;
            }

            if (Time.unscaledTime < nextCameraCheck)
            {
                return null;
            }

            nextCameraCheck = Time.unscaledTime + 0.5f;
            cachedCamera = Camera.main;
            if (cachedCamera == null || !cachedCamera.isActiveAndEnabled)
            {
                cachedCamera = null;
                var count = Camera.allCamerasCount;
                if (cameraBuffer.Length < count) cameraBuffer = new Camera[count];
                count = Camera.GetAllCameras(cameraBuffer);
                for (var index = 0; index < count; index++)
                {
                    if (cameraBuffer[index] != null && cameraBuffer[index].isActiveAndEnabled)
                    {
                        cachedCamera = cameraBuffer[index];
                        break;
                    }
                }
            }

            return cachedCamera;
        }
    }

    /// <summary>Late-frame driver (after cameras move), enabled only while tags are tracked.</summary>
    // CoreGameplayLoop interpolates presence at the default order (0). Project its final pose afterwards.
    [DefaultExecutionOrder(10000)]
    internal sealed class TopiaForgeNametagDriver : MonoBehaviour
    {
        public TopiaForgeNametagLayer? Layer;

        private void LateUpdate()
        {
            Layer?.Tick();
        }
    }
}
