using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    internal static partial class UiWireCodec
    {
        /// <summary>Captures the owning action definitions for a candidate retained presentation.</summary>
        internal static UiNode? ProjectInputDefaults(UiNode? node, Func<string, IReadOnlyList<InputBinding>?>? defaults)
        {
            UiComposition.Validate(node);
            return UiTreeProjection.Rewrite(node, candidate =>
            {
                if (candidate is not UiKeyBindingField field) return candidate;
                var bindings = defaults?.Invoke(field.ActionName);
                if (bindings == null)
                    throw new ArgumentException("This package has no registered input action named '" + field.ActionName + "'.");
                if (bindings.Count > UiInputBindingHint.MaximumDefaults)
                    throw new ArgumentException("A remote key binding field supports up to four action defaults.");
                var hints = new UiInputBindingHint[bindings.Count];
                for (var index = 0; index < hints.Length; index++)
                {
                    var binding = bindings[index];
                    hints[index] = new UiInputBindingHint(binding.Kind, binding.Control);
                }
                return new UiKeyBindingField(field.Id!, field.ActionName, field.Label, field.Device, field.Enabled, Array.AsReadOnly(hints));
            });
        }
    }
}
