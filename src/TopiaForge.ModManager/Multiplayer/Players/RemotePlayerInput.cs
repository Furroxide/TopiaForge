using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Bounded input mailbox. Several frame edges survive until the next canonical tick, once.</summary>
    internal sealed class RemotePlayerInput : IPlayerInput
    {
        internal const int MaximumActions = 64;
        private readonly Dictionary<string, State> actions = new Dictionary<string, State>(StringComparer.Ordinal);
        private ulong tick = ulong.MaxValue;
        internal void Retain(IReadOnlyList<NetInputAction> registered)
        {
            var removed = new List<string>();
            foreach (var name in actions.Keys)
            {
                var found = false;
                for (var i = 0; i < Math.Min(registered.Count, MaximumActions); i++) if (registered[i].Name == name) { found = true; break; }
                if (!found) removed.Add(name);
            }
            for (var i = 0; i < removed.Count; i++) actions.Remove(removed[i]);
        }
        public bool IsHeld(string actionName) => actions.TryGetValue(actionName, out var s) && s.Held;
        public bool WasPressed(string actionName) => actions.TryGetValue(actionName, out var s) && s.Pressed;
        public bool WasReleased(string actionName) => actions.TryGetValue(actionName, out var s) && s.Released;
        internal void Take(string name, bool held, bool pressed, bool released, long now)
        {
            if (!actions.TryGetValue(name, out var state))
            {
                if (actions.Count >= MaximumActions) return;
                actions.Add(name, state = new State());
            }
            state.PendingPressed |= pressed || (!state.PendingHeld && held);
            state.PendingReleased |= released || (state.PendingHeld && !held);
            state.PendingHeld = held;
            state.LastReceived = now;
        }
        internal void Advance(ulong current, long now)
        {
            if (tick == current) return;
            tick = current;
            foreach (var state in actions.Values)
            {
                var stale = now - state.LastReceived > 2_500_000;
                state.Pressed = !stale && state.PendingPressed;
                state.Released = state.PendingReleased || (stale && state.Held);
                state.Held = !stale && state.PendingHeld;
                state.PendingPressed = state.PendingReleased = false;
                if (stale) state.PendingHeld = false;
            }
        }
        private sealed class State
        {
            internal long LastReceived;
            internal bool Held, Pressed, Released, PendingHeld, PendingPressed, PendingReleased;
        }
    }
}
