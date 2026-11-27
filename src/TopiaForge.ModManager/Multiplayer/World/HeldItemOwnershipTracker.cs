using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Observes native pickup edges once a frame; the game remains responsible for its hold and throw physics. Only a
    /// pickup the protocol refuses is dropped: an item that is no network body, such as one the game or an item grant
    /// made after its scene loaded, stays the game's alone, as offline.
    /// </summary>
    internal sealed class HeldItemOwnershipTracker
    {
        private readonly NetworkWorldMotion motion;
        private readonly INetworkMotionBackend? backend;
        private IEntity? held;
        private NetEntityMotion? lease;
        private bool owned;

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
            // A prediction can be picked up before adoption. Once it is marked, ask for ownership even though the
            // native hold never changed. Unmarked items need only an identity lookup, not another scene-index scan.
            if (held == null || (!owned && motion.HasNativeNetworkIdentity(current)))
            {
                held = current;
                owned = motion.TryAcquireNative(current, out lease);
            }

            if (owned && (lease == null || lease.Ended))
            {
                backend.DropHeldEntity(current);
                held = null;
                lease = null;
            }
        }

        /// <summary>
        /// Forgets the hold when the session, the realm or the stream ends. The player keeps what it holds: offline the
        /// item is the game's again, and on a new stream the next poll asks the host for it afresh.
        /// </summary>
        internal void Reset()
        {
            held = null;
            lease = null;
        }
    }
}
