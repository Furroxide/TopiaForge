using System;
using System.Text;

namespace TopiaForge.Mods
{
    /// <summary>Display-only default binding from the owning action definition.</summary>
    internal readonly struct UiInputBindingHint
    {
        internal const int MaximumDefaults = 4;
        internal const int MaximumControlBytes = 32;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal UiInputBindingHint(InputBindingKind kind, string control)
        {
            if (string.IsNullOrWhiteSpace(control) || Utf8.GetByteCount(control) > MaximumControlBytes)
                throw new ArgumentException("A remote default input control must fit in 32 UTF-8 bytes.", nameof(control));
            foreach (var character in control)
                if (char.IsControl(character)) throw new ArgumentException("A remote default input control must be plain text.", nameof(control));

            var known = kind switch
            {
                InputBindingKind.Key => true,
                InputBindingKind.MouseButton => Named<InputMouseButton>(control),
                InputBindingKind.Axis => Named<InputAxis>(control),
                InputBindingKind.GamepadButton => Named<InputGamepadButton>(control),
                InputBindingKind.GamepadAxis => Named<InputGamepadAxis>(control),
                _ => false,
            };
            if (!known) throw new ArgumentException("A remote default input binding uses an unknown control.", nameof(control));
            Kind = kind;
            Control = control;
        }

        internal InputBindingKind Kind { get; }
        internal string Control { get; }

        private static bool Named<T>(string text) where T : struct, Enum =>
            Enum.TryParse<T>(text, out var value) && Enum.IsDefined(typeof(T), value) &&
            string.Equals(value.ToString(), text, StringComparison.Ordinal);
    }
}
