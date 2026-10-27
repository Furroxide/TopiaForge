using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed class RuntimeWorldViews : IMultiplayerRuntimeObserver
    {
        private readonly Dictionary<IInternalWorldViewRenderer, Entry> worlds = new Dictionary<IInternalWorldViewRenderer, Entry>();
        internal WorldViewLeases For(IInternalWorldViewRenderer renderer)
        {
            if (worlds.TryGetValue(renderer, out var value)) return value.Leases;
            void End() { renderer.Stopped -= End; worlds.Remove(renderer); }
            value = new Entry(new WorldViewLeases(renderer), () => renderer.Stopped -= End); worlds.Add(renderer, value);
            renderer.Stopped += End;
            return value.Leases;
        }
        public void Attached(IInternalMultiplayerControl control) { }
        public void Detaching(IInternalMultiplayerControl control) { }
        public void RealmChanged(NetRealm previous, NetRealm next)
        {
            if (previous == next) return;
            // Multiplayer delivery owns the next stage. Never carry an offline-only view into a shared world.
            var failures = new List<Exception>();
            var prior = worlds.Values.ToArray(); worlds.Clear();
            foreach (var view in prior)
            {
                view.Detach();
                try { view.Leases.Dispose(); } catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
        private sealed class Entry
        {
            internal readonly WorldViewLeases Leases;
            internal readonly Action Detach;
            internal Entry(WorldViewLeases leases, Action detach) { Leases = leases; Detach = detach; }
        }
    }
}
