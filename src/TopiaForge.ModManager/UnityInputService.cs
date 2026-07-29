using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.EventSystems;

namespace TopiaForge.ModManager
{
    internal sealed class OwnerInputService : IInputService
    {
        private readonly string ownerModId;
        private readonly IModLifetime lifetime;
        private readonly UnityInputRegistry registry;

        public OwnerInputService(string ownerModId, IModLifetime lifetime, UnityInputRegistry registry)
        {
            this.ownerModId = ownerModId;
            this.lifetime = lifetime;
            this.registry = registry;
        }

        public bool IsUiFocused
        {
            get
            {
                UnityMainThreadGuard.AssertCurrent();
                return registry.IsUiFocused;
            }
        }

        public IReadOnlyList<InputConflict> GetConflicts()
        {
            UnityMainThreadGuard.AssertCurrent();
            return registry.GetConflicts(ownerModId);
        }

        public OperationResult<IInputAction> RegisterAction(InputActionDefinition definition)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            var registration = registry.Register(ownerModId, definition);
            if (!registration.TryGetValue(out var action))
            {
                return OperationResult<IInputAction>.Failure(
                    registration.ErrorCode,
                    registration.ErrorMessage);
            }

            try
            {
                action.AttachLifetimeLease(lifetime.Track(action));
                return OperationResult<IInputAction>.Success(action);
            }
            catch (ObjectDisposedException)
            {
                action.Dispose();
                return OperationResult<IInputAction>.Failure(
                    ModErrorCode.Cancelled,
                    "The mod stopped before its input action could be registered.");
            }
        }
    }

    internal sealed class UnityInputRegistry : IDisposable
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, UnityInputAction> actions =
            new Dictionary<string, UnityInputAction>(StringComparer.OrdinalIgnoreCase);
        private bool disposed;

        public bool IsUiFocused
        {
            get
            {
                UnityMainThreadGuard.AssertCurrent();
                var events = EventSystem.current;
                return events != null &&
                    (events.currentSelectedGameObject != null || events.IsPointerOverGameObject());
            }
        }

        public OperationResult<UnityInputAction> Register(string ownerModId, InputActionDefinition definition)
        {
            UnityMainThreadGuard.AssertCurrent();
            var key = ownerModId + ":" + definition.Name;
            lock (sync)
            {
                if (disposed)
                {
                    return OperationResult<UnityInputAction>.Failure(
                        ModErrorCode.InvalidState,
                        "Input registration is no longer available because the runtime is stopping.");
                }

                if (actions.ContainsKey(key))
                {
                    return OperationResult<UnityInputAction>.Failure(
                        ModErrorCode.Conflict,
                        "Input action '" + definition.Name + "' is already registered by " + ownerModId + ".");
                }

                var action = new UnityInputAction(this, key, ownerModId, definition);
                actions.Add(key, action);
                return OperationResult<UnityInputAction>.Success(action);
            }
        }

        public IReadOnlyList<InputConflict> GetConflicts(string ownerModId)
        {
            UnityMainThreadGuard.AssertCurrent();
            lock (sync)
            {
                var conflicts = new List<InputConflict>();
                foreach (var action in actions.Values)
                {
                    if (!string.Equals(action.OwnerModId, ownerModId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var other in actions.Values)
                    {
                        if (ReferenceEquals(action, other))
                        {
                            continue;
                        }

                        foreach (var binding in action.Bindings)
                        {
                            if (ContainsControl(other.Bindings, binding))
                            {
                                conflicts.Add(new InputConflict(
                                    action.Name,
                                    other.OwnerModId + ":" + other.Name,
                                    binding));
                            }
                        }
                    }
                }

                conflicts.Sort((left, right) =>
                {
                    var result = StringComparer.OrdinalIgnoreCase.Compare(left.ActionName, right.ActionName);
                    return result != 0
                        ? result
                        : StringComparer.OrdinalIgnoreCase.Compare(left.OtherActionName, right.OtherActionName);
                });
                return conflicts.AsReadOnly();
            }
        }

        private static bool ContainsControl(IReadOnlyList<InputBinding> bindings, InputBinding candidate)
        {
            for (var index = 0; index < bindings.Count; index++)
            {
                var binding = bindings[index];
                if (binding.Kind == candidate.Kind
                    && string.Equals(binding.Control, candidate.Control, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public void Sample()
        {
            UnityMainThreadGuard.AssertCurrent();
            UnityInputAction[] snapshot;
            lock (sync)
            {
                snapshot = new UnityInputAction[actions.Count];
                actions.Values.CopyTo(snapshot, 0);
            }

            var uiFocused = IsUiFocused;
            foreach (var action in snapshot)
            {
                action.Sample(uiFocused);
            }
        }

        public void Dispose()
        {
            UnityMainThreadGuard.AssertCurrent();
            UnityInputAction[] snapshot;
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                snapshot = new UnityInputAction[actions.Count];
                actions.Values.CopyTo(snapshot, 0);
                actions.Clear();
            }

            foreach (var action in snapshot)
            {
                action.DisposeFromRegistry();
            }
        }

        private void Remove(string key, UnityInputAction action)
        {
            lock (sync)
            {
                if (actions.TryGetValue(key, out var registered) && ReferenceEquals(registered, action))
                {
                    actions.Remove(key);
                }
            }
        }

        internal sealed class UnityInputAction : IInputAction
        {
            private UnityInputRegistry? owner;
            private readonly string key;
            private readonly InputActionDefinition definition;
            private IReadOnlyList<InputBinding> bindings;
            private float value;
            private bool held;
            private bool pressed;
            private bool released;
            private IDisposable? lifetimeLease;
            private int disposed;

            public UnityInputAction(
                UnityInputRegistry owner,
                string key,
                string ownerModId,
                InputActionDefinition definition)
            {
                this.owner = owner;
                this.key = key;
                OwnerModId = ownerModId;
                this.definition = definition;
                bindings = CopyBindings(definition.DefaultBindings).AsReadOnly();
            }

            public string OwnerModId { get; }
            public string Name => definition.Name;
            public IReadOnlyList<InputBinding> Bindings => bindings;
            public float Value => value;
            public bool IsHeld => held;
            public bool WasPressed => pressed;
            public bool WasReleased => released;

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            public void Sample(bool uiFocused)
            {
                if (owner == null)
                {
                    return;
                }

                var previous = held;
                value = definition.SuppressWhileUiFocused && uiFocused ? 0f : ReadBindings(bindings);
                held = Math.Abs(value) > 0.0001f;
                pressed = held && !previous;
                released = !held && previous;
            }

            public OperationResult<bool> Rebind(IEnumerable<InputBinding> newBindings)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (newBindings == null)
                {
                    throw new ArgumentNullException(nameof(newBindings));
                }

                if (owner == null)
                {
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The input action is no longer active.");
                }

                var copy = CopyBindings(newBindings);
                if (copy.Count == 0)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.InvalidArgument,
                        "At least one input binding is required.");
                }

                bindings = copy.AsReadOnly();
                return OperationResult<bool>.Success(true);
            }

            public OperationResult<bool> ResetBindings()
            {
                return Rebind(definition.DefaultBindings);
            }

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                var registry = Interlocked.Exchange(ref owner, null);
                registry?.Remove(key, this);
                value = 0f;
                held = false;
                pressed = false;
                released = false;
                Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
            }

            public void DisposeFromRegistry()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0)
                {
                    return;
                }

                Interlocked.Exchange(ref owner, null);
                value = 0f;
                held = false;
                pressed = false;
                released = false;
                Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
            }

            private static float ReadBindings(IReadOnlyList<InputBinding> bindings)
            {
                var value = 0f;
                for (var index = 0; index < bindings.Count; index++)
                {
                    value += ReadBinding(bindings[index]);
                }

                return Mathf.Clamp(value, -1f, 1f);
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

            private static float ReadBinding(InputBinding binding)
            {
                try
                {
                    switch (binding.Kind)
                    {
                        case InputBindingKind.Key:
                            return Enum.TryParse(binding.Control, true, out KeyCode keyCode) && Input.GetKey(keyCode)
                                ? binding.Scale
                                : 0f;
                        case InputBindingKind.MouseButton:
                            return Enum.TryParse(binding.Control, true, out InputMouseButton mouseButton)
                                && Input.GetMouseButton(MouseButtonIndex(mouseButton))
                                ? binding.Scale
                                : 0f;
                        case InputBindingKind.Axis:
                            return Enum.TryParse(binding.Control, true, out InputAxis axis)
                                ? ReadAxis(axis) * binding.Scale
                                : 0f;
                        case InputBindingKind.GamepadButton:
                            return Enum.TryParse(binding.Control, true, out InputGamepadButton gamepadButton)
                                && Enum.TryParse("JoystickButton" + GamepadButtonIndex(gamepadButton), out KeyCode gamepadKey)
                                && Input.GetKey(gamepadKey)
                                    ? binding.Scale
                                    : 0f;
                        case InputBindingKind.GamepadAxis:
                            return Enum.TryParse(binding.Control, true, out InputGamepadAxis gamepadAxis)
                                ? Input.GetAxisRaw(GamepadAxisName(gamepadAxis)) * binding.Scale
                                : 0f;
                        default:
                            return 0f;
                    }
                }
                catch
                {
                    // A platform can omit a legacy input axis. Treat that binding as idle while preserving other
                    // bindings on the same named action.
                    return 0f;
                }
            }

            private static int MouseButtonIndex(InputMouseButton button)
            {
                switch (button)
                {
                    case InputMouseButton.Primary: return 0;
                    case InputMouseButton.Secondary: return 1;
                    case InputMouseButton.Middle: return 2;
                    case InputMouseButton.Back: return 3;
                    case InputMouseButton.Forward: return 4;
                    default: return -1;
                }
            }

            private static int GamepadButtonIndex(InputGamepadButton button)
            {
                switch (button)
                {
                    case InputGamepadButton.South: return 0;
                    case InputGamepadButton.East: return 1;
                    case InputGamepadButton.West: return 2;
                    case InputGamepadButton.North: return 3;
                    case InputGamepadButton.LeftShoulder: return 4;
                    case InputGamepadButton.RightShoulder: return 5;
                    case InputGamepadButton.View: return 6;
                    case InputGamepadButton.Menu: return 7;
                    case InputGamepadButton.LeftStick: return 8;
                    case InputGamepadButton.RightStick: return 9;
                    default: return -1;
                }
            }

            private static float ReadAxis(InputAxis axis)
            {
                switch (axis)
                {
                    case InputAxis.Horizontal: return Input.GetAxisRaw("Horizontal");
                    case InputAxis.Vertical: return Input.GetAxisRaw("Vertical");
                    case InputAxis.PointerX: return Input.GetAxisRaw("Mouse X");
                    case InputAxis.PointerY: return Input.GetAxisRaw("Mouse Y");
                    case InputAxis.Scroll: return Input.mouseScrollDelta.y;
                    default: return 0f;
                }
            }

            private static string GamepadAxisName(InputGamepadAxis axis)
            {
                switch (axis)
                {
                    case InputGamepadAxis.LeftX: return "Horizontal";
                    case InputGamepadAxis.LeftY: return "Vertical";
                    case InputGamepadAxis.RightX: return "RightStickHorizontal";
                    case InputGamepadAxis.RightY: return "RightStickVertical";
                    case InputGamepadAxis.LeftTrigger: return "LeftTrigger";
                    case InputGamepadAxis.RightTrigger: return "RightTrigger";
                    default: return string.Empty;
                }
            }
        }
    }
}
