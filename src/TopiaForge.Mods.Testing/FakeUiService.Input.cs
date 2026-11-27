using System;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiService
    {
        private FakeInputService? fixtureInput;
        private string? fixtureInputOwner;

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
            if (node is UiKeyBindingField field && FindInputAction(field.ActionName, null) == null)
                throw new ArgumentException("Key binding field '" + field.Id + "' has no input action named '" + field.ActionName + "'.");
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
