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
        private readonly bool authenticatedDefaults;

        internal UiSurfaceInputContext(UnityInputRegistry? registry, string ownerPackageId)
            : this(registry, ownerPackageId, false) { }

        private UiSurfaceInputContext(UnityInputRegistry? registry, string ownerPackageId, bool authenticatedDefaults)
        {
            this.registry = registry;
            OwnerPackageId = ownerPackageId;
            this.authenticatedDefaults = authenticatedDefaults;
        }

        internal string OwnerPackageId { get; }

        internal void Validate(UiNode? node)
        {
            if (node == null) return;
            var name = node switch
            {
                UiKeyBindingField field => field.ActionName,
                UiKeyGlyph glyph => glyph.ActionName,
                UiActionSlot slot => slot.ActionName,
                _ => null,
            };
            if (name != null && registry?.FindUiAction(OwnerPackageId, name) == null)
                throw new ArgumentException((node.Id == null ? node.GetType().Name : "UI node '" + node.Id + "'") +
                    " has no owning input action named '" + name + "'.");
            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++) Validate(children[index]);
        }

        internal Func<(IInputAction Action, string DisplayName)?> Resolve(string name)
        {
            var key = OwnerPackageId + ":" + name;
            return () => registry?.FindUiActionByKey(key, name) is { } action
                ? (action, action.Definition.DisplayName) : null;
        }

        internal UiSurfaceInputContext ForRemote(UiWireEnvelope? envelope)
        {
            var owner = envelope?.OwnerPackageId ?? string.Empty;
            var authentic = envelope != null;
            return authentic == authenticatedDefaults && string.Equals(owner, OwnerPackageId, StringComparison.Ordinal)
                ? this : new UiSurfaceInputContext(registry, owner, authentic);
        }

        internal UiNativeActionBinding BindAction(string name, IReadOnlyList<UiInputBindingHint> defaults)
        {
            var key = OwnerPackageId + ":" + name;
            return new UiNativeActionBinding(() => registry?.FindUiActionByKey(key, name),
                authenticatedDefaults ? defaults : Array.Empty<UiInputBindingHint>());
        }

        internal string? Conflict(IInputAction action, InputBinding binding) => registry?.GetUiBindingConflict(action, binding);
    }
}
