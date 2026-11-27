using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

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
                var actionName = candidate switch
                {
                    UiKeyBindingField field => field.ActionName,
                    UiKeyGlyph glyph => glyph.ActionName,
                    UiActionSlot slot => slot.ActionName,
                    _ => null,
                };
                if (actionName == null) return candidate;
                var bindings = defaults?.Invoke(actionName);
                if (bindings == null)
                {
                    var nodeName = candidate.Id == null ? candidate.GetType().Name : "UI node '" + candidate.Id + "'";
                    throw new ArgumentException(nodeName + ": This package has no registered input action named '" + actionName + "'.");
                }
                if (bindings.Count > UiInputBindingHint.MaximumDefaults)
                    throw new ArgumentException("Remote UI supports up to four default bindings per action.");
                var hints = new UiInputBindingHint[bindings.Count];
                for (var index = 0; index < hints.Length; index++)
                {
                    var binding = bindings[index];
                    hints[index] = new UiInputBindingHint(binding.Kind, binding.Control);
                }
                var snapshot = Array.AsReadOnly(hints);
                return candidate switch
                {
                    UiKeyBindingField field => new UiKeyBindingField(field.Id!, field.ActionName, field.Label, field.Device, field.Enabled, snapshot),
                    UiKeyGlyph glyph => glyph.WithWireDefaults(snapshot),
                    UiActionSlot slot => slot.WithWireDefaults(snapshot),
                    _ => candidate,
                };
            });
        }

        private static void WriteInputDefaults(IUiWireWriter writer, IReadOnlyList<UiInputBindingHint> defaults)
        {
            writer.WriteByte((byte)defaults.Count);
            for (var index = 0; index < defaults.Count; index++)
            {
                var binding = defaults[index];
                writer.WriteByte((byte)binding.Kind);
                writer.WriteString(binding.Control);
            }
        }

        private sealed partial class Decoder
        {
            private ReadOnlyCollection<UiInputBindingHint> ReadInputDefaults()
            {
                var defaults = new UiInputBindingHint[Byte(r, UiInputBindingHint.MaximumDefaults)];
                for (var index = 0; index < defaults.Length; index++)
                {
                    var kind = (InputBindingKind)Byte(r, (byte)InputBindingKind.GamepadAxis);
                    if (!r.TryReadString(UiInputBindingHint.MaximumControlBytes, out var control)) throw Bad();
                    defaults[index] = new UiInputBindingHint(kind, control);
                }
                return Array.AsReadOnly(defaults);
            }
        }
    }
}
