using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService : IUiInputDefaultsSource
    {
        private UnityInputRegistry? uiInput;

        internal void AttachUiInput(UnityInputRegistry input) => uiInput = input;

        bool IUiInputDefaultsSource.TryGetUiInputDefaults(string actionName, out IReadOnlyList<InputBinding> defaults)
        {
            var action = uiInput?.FindUiAction(host.OwnerId, actionName);
            defaults = action?.Definition.DefaultBindings ?? Array.Empty<InputBinding>();
            return action != null;
        }
    }

    /// <summary>Resolves bindings from the package registry on the machine displaying this surface.</summary>
    internal sealed class UiSurfaceInputContext
    {
        private readonly UnityInputRegistry? registry;

        internal UiSurfaceInputContext(UnityInputRegistry? registry, string ownerPackageId)
        {
            this.registry = registry;
            OwnerPackageId = ownerPackageId;
        }

        internal string OwnerPackageId { get; }

        internal void Validate(UiNode? node)
        {
            if (node == null) return;
            if (node is UiKeyBindingField field && registry?.FindUiAction(OwnerPackageId, field.ActionName) == null)
                throw new ArgumentException("Key binding field '" + field.Id + "' has no input action named '" + field.ActionName + "'.");
            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++) Validate(children[index]);
        }

        internal Func<(IInputAction Action, string DisplayName)?> Resolve(string name)
        {
            var key = OwnerPackageId + ":" + name;
            return () => registry?.FindUiActionByKey(key, name) is { } action
                ? (action, action.Definition.DisplayName) : null;
        }

        internal string? Conflict(IInputAction action, InputBinding binding) => registry?.GetUiBindingConflict(action, binding);
    }
}
