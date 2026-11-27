using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>An immutable labeled checkbox.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class UiCheckbox : UiNode
    {
        /// <summary>Creates a checkbox that reports the player's new checked value.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiCheckbox(string id, string label, bool value, Action<bool> changed, bool enabled = true)
            : base(id ?? throw new ArgumentNullException(nameof(id)))
        {
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A checkbox label is required.", nameof(label));
            Label = label;
            Value = value;
            Changed = changed ?? throw new ArgumentNullException(nameof(changed));
            Enabled = enabled;
        }

        /// <summary>Gets the visible accessible label.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string Label { get; }

        /// <summary>Gets the initial checked value.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public bool Value { get; }

        /// <summary>Gets the callback for a change in checked value.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public Action<bool> Changed { get; }

        /// <summary>Gets whether the checkbox accepts input.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public bool Enabled { get; }
    }

    /// <summary>Specifies the device class a key binding field edits.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public enum UiInputDevice
    {
        /// <summary>Changes keyboard and mouse bindings.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        KeyboardMouse = 0,
        /// <summary>Changes gamepad bindings.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        Gamepad = 1,
    }

    /// <summary>Displays and edits a local binding for an action owned by the same package.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class UiKeyBindingField : UiNode
    {
        private readonly IReadOnlyList<UiInputBindingHint>? wireDefaults;

        /// <summary>Creates a rebind field that changes bindings for the selected device class.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiKeyBindingField(string id, string actionName, string? label = null,
            UiInputDevice device = UiInputDevice.KeyboardMouse, bool enabled = true)
            : base(id ?? throw new ArgumentNullException(nameof(id)))
        {
            UiControlText.Require(actionName, 64, nameof(actionName));
            if (label != null && string.IsNullOrWhiteSpace(label))
                throw new ArgumentException("A label override must contain visible text.", nameof(label));
            if (device != UiInputDevice.KeyboardMouse && device != UiInputDevice.Gamepad)
                throw new ArgumentOutOfRangeException(nameof(device));
            ActionName = actionName;
            Label = label;
            Device = device;
            Enabled = enabled;
        }

        internal UiKeyBindingField(string id, string actionName, string? label, UiInputDevice device, bool enabled,
            IReadOnlyList<UiInputBindingHint> defaults)
            : this(id, actionName, label, device, enabled)
        {
            wireDefaults = defaults;
        }

        /// <summary>Gets the package's registered action name.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string ActionName { get; }

        /// <summary>Gets the replacement label, or null to show the action's display name.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string? Label { get; }

        /// <summary>Gets the device class whose bindings the player can edit.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiInputDevice Device { get; }

        /// <summary>Gets whether this field accepts input when its action is available.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public bool Enabled { get; }

        internal IReadOnlyList<UiInputBindingHint> WireDefaults => wireDefaults ?? Array.Empty<UiInputBindingHint>();
        internal bool HasWireDefaults => wireDefaults != null;
    }

    /// <summary>Shows help associated with a node on pointer hover or keyboard and controller focus.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class UiTooltip : UiNode
    {
        /// <summary>Creates help with a plain-text message of 1 to 256 units.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiTooltip(UiNode child, string message, string? id = null) : base(id)
        {
            Child = child ?? throw new ArgumentNullException(nameof(child));
            UiArtValues.Label(message, false, nameof(message));
            Message = message;
        }

        /// <summary>Gets the displayed trigger content.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiNode Child { get; }

        /// <summary>Gets the plain-text help shown to the player.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string Message { get; }
    }

    internal static class UiControlText
    {
        internal static void Require(string text, int maximum, string parameter)
        {
            if (text == null) throw new ArgumentNullException(parameter);
            if (text.Length > maximum || string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("A value of 1 to " + maximum + " text units is required.", parameter);
        }
    }
}
