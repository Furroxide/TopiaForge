using System;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Publishes explicit committed state observations without invoking factories or controllers.</summary>
    public sealed class FakeWorldSessionService : IWorldSessionService
    {
        private int sequence;
        /// <inheritdoc/>
        public WorldSessionSnapshot Current { get; private set; } = new WorldSessionSnapshot(WorldSessionPhase.Idle, null, null, 0);
        /// <inheritdoc/>
        public event Action<WorldSessionSnapshot> StateChanged = delegate { };
        /// <summary>Commits a test state and then notifies observers.</summary>
        public void Publish(WorldSessionPhase phase, IWorldSession? session = null, WorldReadiness? world = null)
        {
            var next = new WorldSessionSnapshot(phase, session, world, checked(sequence + 1));
            sequence = next.Sequence;
            Current = next;
            StateChanged(next);
        }
    }
}
