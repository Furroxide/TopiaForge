using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Supplies the world position a name tag follows. Queried once per frame on the main thread;
    /// keep it cheap and allocation-free. Returning false (no position yet, despawned) hides the tag
    /// without removing it.
    /// </summary>
    public interface ITopiaForgeNametagAnchor
    {
        /// <summary>Reads the anchor's current world position (the tag adds its vertical offset).</summary>
        bool TryGetPosition(out Vec3 position);
    }

    /// <summary>
    /// A settable world point: fixed markers, or positions a caller moves itself each frame.
    /// Uses the engine-independent <see cref="Vec3"/>, so callers need no Unity types.
    /// </summary>
    public sealed class TopiaForgeWorldPointAnchor : ITopiaForgeNametagAnchor
    {
        /// <summary>Creates an active anchor at a position.</summary>
        public TopiaForgeWorldPointAnchor(Vec3 position)
        {
            Position = position;
        }

        /// <summary>Gets or sets the world position.</summary>
        public Vec3 Position { get; set; }

        /// <summary>Gets or sets whether the anchor reports a position; an inactive anchor hides its tag.</summary>
        public bool Active { get; set; } = true;

        /// <inheritdoc/>
        public bool TryGetPosition(out Vec3 position)
        {
            position = Position;
            return Active;
        }
    }

    /// <summary>
    /// Follows a scene transform, such as an avatar's head. Reports no position once the
    /// transform is destroyed or inactive, so a despawned avatar's tag hides by itself.
    /// </summary>
    public sealed class TopiaForgeTransformAnchor : ITopiaForgeNametagAnchor
    {
        private readonly Transform target;

        /// <summary>Creates an anchor that follows <paramref name="target"/>.</summary>
        /// <exception cref="System.ArgumentNullException"><paramref name="target"/> is null.</exception>
        public TopiaForgeTransformAnchor(Transform target)
        {
            if (target == null)
            {
                throw new System.ArgumentNullException(nameof(target));
            }

            this.target = target;
        }

        /// <inheritdoc/>
        public bool TryGetPosition(out Vec3 position)
        {
            if (target == null || !target.gameObject.activeInHierarchy)
            {
                position = Vec3.Zero;
                return false;
            }

            var world = target.position;
            position = new Vec3(world.x, world.y, world.z);
            return true;
        }
    }
}
