using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>Identifies a mouse button by its conventional purpose instead of a native button ordinal.</summary>
    public enum InputMouseButton
    {
        /// <summary>The primary select or fire button.</summary>
        Primary = 0,

        /// <summary>The secondary context or alternate-fire button.</summary>
        Secondary = 1,

        /// <summary>The middle or wheel-click button.</summary>
        Middle = 2,

        /// <summary>The first auxiliary button, conventionally browser back.</summary>
        Back = 3,

        /// <summary>The second auxiliary button, conventionally browser forward.</summary>
        Forward = 4
    }

    /// <summary>Identifies a gamepad button by physical position or purpose instead of a native ordinal.</summary>
    public enum InputGamepadButton
    {
        /// <summary>The lower face button, commonly labelled A or Cross.</summary>
        South = 0,

        /// <summary>The right face button, commonly labelled B or Circle.</summary>
        East = 1,

        /// <summary>The left face button, commonly labelled X or Square.</summary>
        West = 2,

        /// <summary>The upper face button, commonly labelled Y or Triangle.</summary>
        North = 3,

        /// <summary>The left shoulder button.</summary>
        LeftShoulder = 4,

        /// <summary>The right shoulder button.</summary>
        RightShoulder = 5,

        /// <summary>The view, back, or select button.</summary>
        View = 6,

        /// <summary>The menu, options, or start button.</summary>
        Menu = 7,

        /// <summary>The left stick click.</summary>
        LeftStick = 8,

        /// <summary>The right stick click.</summary>
        RightStick = 9
    }

    /// <summary>Identifies a pointer or ordinary movement axis using SDK-stable names.</summary>
    public enum InputAxis
    {
        /// <summary>The ordinary left/right movement axis.</summary>
        Horizontal = 0,

        /// <summary>The ordinary forward/back movement axis.</summary>
        Vertical = 1,

        /// <summary>Horizontal pointer motion.</summary>
        PointerX = 2,

        /// <summary>Vertical pointer motion.</summary>
        PointerY = 3,

        /// <summary>The signed mouse-wheel or equivalent scroll delta.</summary>
        Scroll = 4
    }

    /// <summary>Identifies a continuous gamepad control using SDK-stable names.</summary>
    public enum InputGamepadAxis
    {
        /// <summary>The left stick horizontal axis.</summary>
        LeftX = 0,

        /// <summary>The left stick vertical axis.</summary>
        LeftY = 1,

        /// <summary>The right stick horizontal axis.</summary>
        RightX = 2,

        /// <summary>The right stick vertical axis.</summary>
        RightY = 3,

        /// <summary>The left analog trigger.</summary>
        LeftTrigger = 4,

        /// <summary>The right analog trigger.</summary>
        RightTrigger = 5
    }

    /// <summary>Identifies the kind of physical control used by an input binding.</summary>
    public enum InputBindingKind
    {
        /// <summary>A keyboard key named by a platform-independent SDK key name such as <c>F</c> or <c>Space</c>.</summary>
        Key = 0,

        /// <summary>A mouse button identified by its conventional purpose.</summary>
        MouseButton = 1,

        /// <summary>A named continuous movement or pointer axis.</summary>
        Axis = 2,

        /// <summary>A gamepad button identified by its physical position or purpose.</summary>
        GamepadButton = 3,

        /// <summary>A continuous gamepad stick or trigger axis.</summary>
        GamepadAxis = 4
    }

    /// <summary>Describes one default physical binding for a named mod action.</summary>
    public readonly struct InputBinding : IEquatable<InputBinding>
    {
        /// <summary>Creates an input binding.</summary>
        /// <param name="kind">The physical binding kind.</param>
        /// <param name="control">The SDK key or control name.</param>
        /// <param name="scale">The multiplier applied to an axis value.</param>
        private InputBinding(InputBindingKind kind, string control, float scale = 1f)
        {
            if (string.IsNullOrWhiteSpace(control))
            {
                throw new ArgumentException("An input control is required.", nameof(control));
            }

            if (float.IsNaN(scale) || float.IsInfinity(scale))
            {
                throw new ArgumentOutOfRangeException(nameof(scale));
            }

            Kind = kind;
            Control = control;
            Scale = scale;
        }

        /// <summary>Gets the physical binding kind.</summary>
        public InputBindingKind Kind { get; }

        /// <summary>Gets the SDK-stable key or control name.</summary>
        public string Control { get; }

        /// <summary>Gets the multiplier applied to an axis value.</summary>
        public float Scale { get; }

        /// <summary>Creates a keyboard-key binding.</summary>
        public static InputBinding Key(string keyName)
        {
            return new InputBinding(InputBindingKind.Key, keyName);
        }

        /// <summary>Creates a mouse-button binding.</summary>
        public static InputBinding MouseButton(InputMouseButton button)
        {
            if (!Enum.IsDefined(typeof(InputMouseButton), button))
            {
                throw new ArgumentOutOfRangeException(nameof(button));
            }

            return new InputBinding(InputBindingKind.MouseButton, button.ToString());
        }

        /// <summary>Creates a continuous movement or pointer-axis binding.</summary>
        public static InputBinding Axis(InputAxis axis, float scale = 1f)
        {
            if (!Enum.IsDefined(typeof(InputAxis), axis))
            {
                throw new ArgumentOutOfRangeException(nameof(axis));
            }

            return new InputBinding(InputBindingKind.Axis, axis.ToString(), scale);
        }

        /// <summary>Creates a gamepad-button binding.</summary>
        public static InputBinding GamepadButton(InputGamepadButton button)
        {
            if (!Enum.IsDefined(typeof(InputGamepadButton), button))
            {
                throw new ArgumentOutOfRangeException(nameof(button));
            }

            return new InputBinding(InputBindingKind.GamepadButton, button.ToString());
        }

        /// <summary>Creates a continuous gamepad-axis binding.</summary>
        public static InputBinding GamepadAxis(InputGamepadAxis axis, float scale = 1f)
        {
            if (!Enum.IsDefined(typeof(InputGamepadAxis), axis))
            {
                throw new ArgumentOutOfRangeException(nameof(axis));
            }

            return new InputBinding(InputBindingKind.GamepadAxis, axis.ToString(), scale);
        }

        /// <inheritdoc/>
        public bool Equals(InputBinding other)
        {
            return Kind == other.Kind && string.Equals(Control, other.Control, StringComparison.Ordinal) && Scale.Equals(other.Scale);
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is InputBinding other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var controlHash = StringComparer.Ordinal.GetHashCode(Control ?? string.Empty);
                return ((((int)Kind * 397) ^ controlHash) * 397) ^ Scale.GetHashCode();
            }
        }
    }

    /// <summary>Describes two registered actions currently sharing the same physical control.</summary>
    public sealed class InputConflict
    {
        /// <summary>Creates input conflict information.</summary>
        public InputConflict(string actionName, string otherActionName, InputBinding binding)
        {
            if (string.IsNullOrWhiteSpace(actionName))
            {
                throw new ArgumentException("An action name is required.", nameof(actionName));
            }

            if (string.IsNullOrWhiteSpace(otherActionName))
            {
                throw new ArgumentException("The other action name is required.", nameof(otherActionName));
            }

            ActionName = actionName;
            OtherActionName = otherActionName;
            Binding = binding;
        }

        /// <summary>Gets the current mod action affected by the conflict.</summary>
        public string ActionName { get; }

        /// <summary>Gets the fully qualified conflicting action.</summary>
        public string OtherActionName { get; }

        /// <summary>Gets the shared physical control.</summary>
        public InputBinding Binding { get; }
    }

    /// <summary>Describes a discoverable, rebindable action owned by the current mod.</summary>
    public sealed class InputActionDefinition
    {
        private readonly IReadOnlyList<InputBinding> defaultBindings;

        /// <summary>Creates an input action definition.</summary>
        /// <param name="name">A stable name unique inside the current mod.</param>
        /// <param name="displayName">A short user-facing label.</param>
        /// <param name="defaultBindings">One or more default bindings.</param>
        /// <param name="suppressWhileUiFocused">Whether UI keyboard or pointer focus suppresses the action.</param>
        public InputActionDefinition(
            string name,
            string displayName,
            IEnumerable<InputBinding> defaultBindings,
            bool suppressWhileUiFocused = true)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("An input action name is required.", nameof(name));
            }

            if (string.IsNullOrWhiteSpace(displayName))
            {
                throw new ArgumentException("An input action display name is required.", nameof(displayName));
            }

            if (defaultBindings == null)
            {
                throw new ArgumentNullException(nameof(defaultBindings));
            }

            var bindings = new List<InputBinding>(defaultBindings);
            if (bindings.Count == 0)
            {
                throw new ArgumentException("At least one default input binding is required.", nameof(defaultBindings));
            }

            Name = name;
            DisplayName = displayName;
            this.defaultBindings = bindings.AsReadOnly();
            SuppressWhileUiFocused = suppressWhileUiFocused;
        }

        /// <summary>Gets the stable name unique inside the current mod.</summary>
        public string Name { get; }

        /// <summary>Gets the short user-facing label.</summary>
        public string DisplayName { get; }

        /// <summary>Gets the default physical bindings.</summary>
        public IReadOnlyList<InputBinding> DefaultBindings => defaultBindings;

        /// <summary>Gets whether UI keyboard or pointer focus suppresses the action.</summary>
        public bool SuppressWhileUiFocused { get; }
    }

    /// <summary>Provides the current sampled state of a named input action.</summary>
    public interface IInputAction : IDisposable
    {
        /// <summary>Gets the stable name from the action definition.</summary>
        string Name { get; }

        /// <summary>Gets the current effective bindings after user or programmatic rebinding.</summary>
        IReadOnlyList<InputBinding> Bindings { get; }

        /// <summary>Gets the current signed action value.</summary>
        float Value { get; }

        /// <summary>Gets whether the action is currently held.</summary>
        bool IsHeld { get; }

        /// <summary>Gets whether the action became held during the current frame.</summary>
        bool WasPressed { get; }

        /// <summary>Gets whether the action stopped being held during the current frame.</summary>
        bool WasReleased { get; }

        /// <summary>Replaces the action's bindings while preserving its stable name.</summary>
        OperationResult<bool> Rebind(IEnumerable<InputBinding> bindings);

        /// <summary>Restores the definition's default bindings.</summary>
        OperationResult<bool> ResetBindings();
    }

    /// <summary>Creates owner-scoped named input actions.</summary>
    public interface IInputService
    {
        /// <summary>Gets whether the framework currently detects focused UI that should consume gameplay input.</summary>
        bool IsUiFocused { get; }

        /// <summary>Returns current physical-control conflicts involving this mod's registered actions.</summary>
        IReadOnlyList<InputConflict> GetConflicts();

        /// <summary>Registers and lifetime-tracks a named input action.</summary>
        /// <param name="definition">The action metadata and default bindings.</param>
        /// <returns>
        /// The sampled action handle, or a stable <see cref="ModErrorCode.Conflict"/>,
        /// <see cref="ModErrorCode.Unavailable"/>, <see cref="ModErrorCode.InvalidState"/>, or
        /// <see cref="ModErrorCode.Cancelled"/> failure.
        /// </returns>
        OperationResult<IInputAction> RegisterAction(InputActionDefinition definition);
    }
}
