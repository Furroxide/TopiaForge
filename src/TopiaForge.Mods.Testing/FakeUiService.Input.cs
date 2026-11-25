using System;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiService
    {
        private FakeInputService? fixtureInput;
        private string? fixtureInputOwner;
        private UiInputDevice lastInputDevice;

        /// <summary>Gets or sets the device the viewing player's binding glyphs prefer.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiInputDevice LastInputDevice
        {
            get => lastInputDevice;
            set
            {
                if (value != UiInputDevice.KeyboardMouse && value != UiInputDevice.Gamepad)
                    throw new ArgumentOutOfRangeException(nameof(value));
                lastInputDevice = value;
            }
        }

        /// <summary>Uses the supplied input registrations for binding controls on this fake UI service.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public void UseInput(FakeInputService input)
        {
            gameThread.AssertCurrent();
            fixtureInput = input ?? throw new ArgumentNullException(nameof(input));
        }

        internal void FollowInput(FakeInputService input, string ownerId)
        {
            UseInput(input);
            fixtureInputOwner = ownerId;
        }

        internal FakeInputAction? FindInputAction(string name, UiWireEnvelope? receiver) =>
            receiver == null || string.Equals(receiver.OwnerPackageId, fixtureInputOwner, StringComparison.Ordinal)
                ? fixtureInput?.FindAction(name) : null;

        internal void CheckLocalInput(UiNode? node)
        {
            if (node == null) return;
            var name = node switch
            {
                UiKeyBindingField field => field.ActionName,
                UiKeyGlyph glyph => glyph.ActionName,
                UiActionSlot slot => slot.ActionName,
                _ => null,
            };
            if (name != null && FindInputAction(name, null) == null)
                throw new ArgumentException((node.Id == null ? node.GetType().Name : "UI node '" + node.Id + "'") +
                    " has no owning input action named '" + name + "'.");
            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++) CheckLocalInput(children[index]);
        }

        internal string? ConflictingAction(string actionName, InputBinding binding)
        {
            var conflicts = fixtureInput?.GetConflicts();
            if (conflicts == null) return null;
            foreach (var conflict in conflicts)
                if (conflict.ActionName == actionName && conflict.Binding.Kind == binding.Kind &&
                    string.Equals(conflict.Binding.Control, binding.Control, StringComparison.OrdinalIgnoreCase))
                    return fixtureInput?.FindAction(conflict.OtherActionName)?.Definition.DisplayName ?? conflict.OtherActionName;
            return null;
        }
    }
}
