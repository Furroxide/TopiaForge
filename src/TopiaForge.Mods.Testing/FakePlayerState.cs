using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>One fake player's configurable state and captured presentation.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class FakePlayerState
    {
        internal FakePlayerState(FakeModLifetime lifetime)
        {
            Player = new FakeLocalPlayerService(lifetime) { Health = new PlayerHealthSnapshot(100, 100), Snapshot = new PlayerSnapshot(default, new Ray(default, new Vec3(0, 0, 1))) };
            Ui = new FakeUiService(lifetime);
        }
        /// <summary>Gets the health and position adapter used by this player's handle.</summary>
        public FakeLocalPlayerService Player { get; }
        /// <summary>Gets this player's captured UI.</summary>
        public FakeUiService Ui { get; }
        /// <summary>Gets this player's simulated actions.</summary>
        public FakePlayerInput Input { get; } = new FakePlayerInput();
        /// <summary>Gets or sets the player's avatar entity.</summary>
        public IEntity? Entity { get; set; }
    }

    /// <summary>Per-player input edges for a deterministic canonical tick.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class FakePlayerInput : IPlayerInput
    {
        private readonly Dictionary<string, State> actions = new Dictionary<string, State>(StringComparer.Ordinal);
        /// <summary>Samples an action, reconstructing its press or release from its previous held state.</summary>
        public void SetHeld(string name, bool held)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An action name is required.", nameof(name));
            if (!actions.TryGetValue(name, out var state)) actions.Add(name, state = new State());
            state.Pressed |= held && !state.Held; state.Released |= !held && state.Held; state.Held = held;
        }
        /// <summary>Begins another tick while keeping held state.</summary>
        public void AdvanceTick() { foreach (var state in actions.Values) { state.Pressed = false; state.Released = false; } }
        /// <inheritdoc/>
        public bool IsHeld(string actionName) => actions.TryGetValue(actionName, out var state) && state.Held;
        /// <inheritdoc/>
        public bool WasPressed(string actionName) => actions.TryGetValue(actionName, out var state) && state.Pressed;
        /// <inheritdoc/>
        public bool WasReleased(string actionName) => actions.TryGetValue(actionName, out var state) && state.Released;
        private sealed class State { internal bool Held, Pressed, Released; }
    }
}
