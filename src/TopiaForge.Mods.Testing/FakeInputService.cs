using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic named-input service with explicit frame transitions.</summary>
    public sealed class FakeInputService : IInputService
    {
        private readonly FakeModLifetime lifetime;
        private readonly Dictionary<string, FakeInputAction> actions =
            new Dictionary<string, FakeInputAction>(StringComparer.Ordinal);

        /// <summary>Creates a fake input service owned by a lifetime.</summary>
        public FakeInputService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <inheritdoc/>
        public bool IsUiFocused { get; set; }

        /// <inheritdoc/>
        public IReadOnlyList<InputConflict> GetConflicts()
        {
            var conflicts = new List<InputConflict>();
            foreach (var action in actions.Values)
            {
                foreach (var other in actions.Values)
                {
                    if (ReferenceEquals(action, other))
                    {
                        continue;
                    }

                    foreach (var binding in action.Bindings)
                    {
                        if (SharesControl(other.Bindings, binding))
                        {
                            conflicts.Add(new InputConflict(action.Name, other.Name, binding));
                        }
                    }
                }
            }

            return conflicts.AsReadOnly();
        }

        /// <summary>Gets the number of actions that have not been disposed.</summary>
        public int ActiveActionCount => actions.Count;

        /// <inheritdoc/>
        public OperationResult<IInputAction> RegisterAction(InputActionDefinition definition)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (actions.ContainsKey(definition.Name))
            {
                return OperationResult<IInputAction>.Failure(
                    ModErrorCode.Conflict,
                    "Input action '" + definition.Name + "' is already registered.");
            }

            var action = new FakeInputAction(definition, this, () => actions.Remove(definition.Name));
            actions.Add(definition.Name, action);
            try
            {
                lifetime.Track(action);
                return OperationResult<IInputAction>.Success(action);
            }
            catch (ObjectDisposedException)
            {
                action.Dispose();
                return OperationResult<IInputAction>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake mod stopped before its input action could be registered.");
            }
        }

        /// <summary>Gets a registered action by its stable name.</summary>
        public FakeInputAction GetAction(string name)
        {
            if (!actions.TryGetValue(name, out var action))
            {
                throw new InvalidOperationException("No fake input action is registered as '" + name + "'.");
            }

            return action;
        }

        /// <summary>Sets an action's value and derives its pressed or released transition.</summary>
        public void SetValue(string name, float value) => GetAction(name).SetValue(value);

        /// <summary>Clears pressed and released transitions at the end of a rendered frame.</summary>
        public void FinishFrame()
        {
            foreach (var action in actions.Values)
            {
                action.FinishFrame();
            }
        }

        internal bool IsSuppressed(InputActionDefinition definition) =>
            IsUiFocused && definition.SuppressWhileUiFocused;

        private static bool SharesControl(IReadOnlyList<InputBinding> bindings, InputBinding candidate)
        {
            foreach (var binding in bindings)
            {
                if (binding.Kind == candidate.Kind
                    && string.Equals(binding.Control, candidate.Control, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>A mutable, inspectable input action returned by <see cref="FakeInputService"/>.</summary>
    public sealed class FakeInputAction : IInputAction
    {
        private readonly InputActionDefinition definition;
        private readonly FakeInputService owner;
        private Action? onDispose;
        private IReadOnlyList<InputBinding> bindings;
        private float rawValue;
        private bool pressed;
        private bool released;

        internal FakeInputAction(InputActionDefinition definition, FakeInputService owner, Action onDispose)
        {
            this.definition = definition;
            this.owner = owner;
            this.onDispose = onDispose;
            bindings = CopyBindings(definition.DefaultBindings).AsReadOnly();
        }

        /// <inheritdoc/>
        public string Name => definition.Name;

        /// <inheritdoc/>
        public IReadOnlyList<InputBinding> Bindings => bindings;

        /// <inheritdoc/>
        public float Value => owner.IsSuppressed(definition) ? 0f : rawValue;

        /// <inheritdoc/>
        public bool IsHeld => Value != 0f;

        /// <inheritdoc/>
        public bool WasPressed => !owner.IsSuppressed(definition) && pressed;

        /// <inheritdoc/>
        public bool WasReleased => !owner.IsSuppressed(definition) && released;

        /// <inheritdoc/>
        public OperationResult<bool> Rebind(IEnumerable<InputBinding> newBindings)
        {
            if (newBindings == null)
            {
                throw new ArgumentNullException(nameof(newBindings));
            }

            if (onDispose == null)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The fake input action is disposed.");
            }

            var copy = CopyBindings(newBindings);
            if (copy.Count == 0)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "At least one binding is required.");
            }

            bindings = copy.AsReadOnly();
            return OperationResult<bool>.Success(true);
        }

        /// <inheritdoc/>
        public OperationResult<bool> ResetBindings() => Rebind(definition.DefaultBindings);

        /// <summary>Sets the sampled value and derives edge transitions from the previous value.</summary>
        public void SetValue(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            var wasHeld = rawValue != 0f;
            var isHeld = value != 0f;
            rawValue = value;
            pressed = !wasHeld && isHeld;
            released = wasHeld && !isHeld;
        }

        /// <summary>Sets exact sampled state for an edge-case test.</summary>
        public void SetState(float value, bool wasPressed, bool wasReleased)
        {
            rawValue = value;
            pressed = wasPressed;
            released = wasReleased;
        }

        /// <summary>Clears this action's edge transitions without changing its held value.</summary>
        public void FinishFrame()
        {
            pressed = false;
            released = false;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            var callback = onDispose;
            onDispose = null;
            callback?.Invoke();
        }

        private static List<InputBinding> CopyBindings(IEnumerable<InputBinding> source)
        {
            var copy = new List<InputBinding>();
            foreach (var binding in source)
            {
                copy.Add(binding);
            }

            return copy;
        }
    }
}
