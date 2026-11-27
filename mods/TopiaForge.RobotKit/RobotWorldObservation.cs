using System;
using TopiaForge.Mods;

namespace TopiaForge.RobotKit
{
    // One provider-scoped subscription. Current reads on actor hot paths return the same committed snapshot.
    internal sealed class RobotWorldObservation : IDisposable
    {
        private IWorldSessionService? source;
        private readonly Action changed;

        internal RobotWorldObservation(IWorldSessionService? source, Action changed)
        {
            this.source = source;
            this.changed = changed;
            if (source == null) return;
            Current = source.Current;
            source.StateChanged += Observe;
        }

        internal WorldSessionSnapshot? Current { get; private set; }

        private void Observe(WorldSessionSnapshot snapshot)
        {
            Current = snapshot;
            changed();
        }

        public void Dispose()
        {
            if (source == null) return;
            source.StateChanged -= Observe;
            source = null;
            Current = null;
        }
    }
}
