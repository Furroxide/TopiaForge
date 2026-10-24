using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Observes native pickup edges once a frame; the game remains responsible for its hold and throw physics.</summary>
    internal sealed class HeldItemOwnershipTracker
    {
        private readonly NetworkWorldMotion motion;
        private readonly INetworkMotionBackend? backend;
        private IEntity? held;
        private NetEntityMotion? lease;

        internal HeldItemOwnershipTracker(NetworkWorldMotion motion, INetworkMotionBackend? backend)
        {
            this.motion = motion;
            this.backend = backend;
        }

        internal void Poll()
        {
            if (backend == null) return;
            backend.TryGetHeldEntity(out var current);
            if (held != null && (current == null || current.Id != held.Id))
            {
                lease?.BeginRelease();
                lease?.Dispose();
                held = null;
                lease = null;
            }

            if (current == null) return;
            if (held == null)
            {
                held = current;
                lease = motion.AcquireNative(current);
            }

            if (lease == null || lease.Ended)
            {
                backend.DropHeldEntity(current);
                held = null;
                lease = null;
            }
        }

        internal void Reset()
        {
            if (held != null) backend?.DropHeldEntity(held);
            held = null;
            lease = null;
        }
    }
}
