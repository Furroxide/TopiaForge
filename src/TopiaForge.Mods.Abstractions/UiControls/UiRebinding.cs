using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    internal static class UiRebinding
    {
        internal static UiInputDevice Device(InputBindingKind kind) =>
            kind is InputBindingKind.GamepadAxis or InputBindingKind.GamepadButton
                ? UiInputDevice.Gamepad : UiInputDevice.KeyboardMouse;

        internal static string? ReservedBy(InputBinding binding)
        {
            if (binding.Kind == InputBindingKind.GamepadButton && Is(binding.Control, nameof(InputGamepadButton.East)))
                return "Back";
            if (binding.Kind != InputBindingKind.Key) return null;
            if (Is(binding.Control, "Escape") || Is(binding.Control, "Esc")) return "Back";
            if (Is(binding.Control, "F5")) return "TopiaForge Workbench";
            if (Is(binding.Control, "F8")) return "TopiaForge Gallery";
            if (Is(binding.Control, "F10")) return "TopiaForge Manager";
            return null;
        }

        internal static string? Rejection(UiInputDevice device, InputBinding binding)
        {
            if (string.IsNullOrEmpty(binding.Control) || Device(binding.Kind) != device)
                return "Choose a control from the field's input device.";
            return ReservedBy(binding) is { } owner ? "Reserved by " + owner : null;
        }

        internal static IReadOnlyList<InputBinding> ReplaceDevice(IReadOnlyList<InputBinding> bindings,
            UiInputDevice device, InputBinding replacement)
        {
            var next = new List<InputBinding>(bindings.Count + 1);
            var replaced = false;
            for (var index = 0; index < bindings.Count; index++)
            {
                var current = bindings[index];
                if (Device(current.Kind) != device) next.Add(current);
                else if (!replaced)
                {
                    next.Add(replacement);
                    replaced = true;
                }
            }
            if (!replaced) next.Add(replacement);
            return next;
        }

        internal static string KeyLabel(InputBindingKind kind, string control) => kind switch
        {
            InputBindingKind.Key when Is(control, "Escape") => "Esc",
            InputBindingKind.MouseButton => control switch
            {
                "Primary" => "Mouse 1",
                "Secondary" => "Mouse 2",
                "Middle" => "Mouse 3",
                "Back" => "Mouse 4",
                "Forward" => "Mouse 5",
                _ => control,
            },
            InputBindingKind.GamepadButton => control switch
            {
                "South" => "A",
                "East" => "B",
                "West" => "X",
                "North" => "Y",
                "LeftShoulder" => "LB",
                "RightShoulder" => "RB",
                "LeftStick" => "LS",
                "RightStick" => "RS",
                _ => control,
            },
            InputBindingKind.GamepadAxis => control switch
            {
                "LeftTrigger" => "LT",
                "RightTrigger" => "RT",
                "LeftX" => "LS ↔",
                "LeftY" => "LS ↕",
                "RightX" => "RS ↔",
                "RightY" => "RS ↕",
                _ => control,
            },
            _ => control,
        };

        private static bool Is(string control, string name) => string.Equals(control, name, StringComparison.OrdinalIgnoreCase);
    }
}
