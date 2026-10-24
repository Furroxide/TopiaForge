using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// One control push-to-talk can use: a key or a mouse button, with the names each input backend and the player know it
    /// by. The binding is the SDK's own <see cref="InputBinding"/>, so a key is named as Unity's <c>KeyCode</c> names it
    /// and a mouse button by <see cref="InputMouseButton"/>.
    /// </summary>
    internal sealed class VoiceControl
    {
        internal VoiceControl(InputBinding binding, string pathName, string inputSystemKey, string display)
        {
            Binding = binding;
            PathName = pathName ?? throw new ArgumentNullException(nameof(pathName));
            InputSystemKey = inputSystemKey ?? string.Empty;
            Display = display ?? throw new ArgumentNullException(nameof(display));
            Stored = (binding.Kind == InputBindingKind.MouseButton ? "mouse:" : "key:") + binding.Control;
        }

        internal InputBinding Binding { get; }

        internal bool IsMouseButton => Binding.Kind == InputBindingKind.MouseButton;

        /// <summary>Gets the control's name at the end of an Input System binding path, lowercase: <c>capslock</c>, <c>forwardbutton</c>.</summary>
        internal string PathName { get; }

        /// <summary>Gets the Input System's <c>Key</c> name for a key; empty for a mouse button.</summary>
        internal string InputSystemKey { get; }

        /// <summary>Gets what the player reads: "Caps Lock", "Mouse Forward", "V".</summary>
        internal string Display { get; }

        /// <summary>Gets how the voice settings store the control: <c>key:CapsLock</c> or <c>mouse:Forward</c>.</summary>
        internal string Stored { get; }
    }

    /// <summary>
    /// Every control push-to-talk knows: letters, digits, function keys, punctuation, modifiers, navigation keys, the number
    /// pad and the five mouse buttons. Which of them a slot may take is <see cref="VoicePushToTalk"/>'s rule. Unity-free.
    /// </summary>
    internal static class VoiceControls
    {
        private static readonly Dictionary<string, VoiceControl> ByStored = new Dictionary<string, VoiceControl>(StringComparer.Ordinal);
        private static readonly Dictionary<string, VoiceControl> Keys = new Dictionary<string, VoiceControl>(StringComparer.Ordinal);
        private static readonly Dictionary<string, VoiceControl> MouseButtons = new Dictionary<string, VoiceControl>(StringComparer.Ordinal);
        private static readonly List<VoiceControl> Ordered = new List<VoiceControl>();

        static VoiceControls()
        {
            for (var letter = 'A'; letter <= 'Z'; letter++)
            {
                var name = letter.ToString();
                Key(name, name.ToLowerInvariant(), name, name);
            }

            for (var digit = 0; digit <= 9; digit++)
            {
                var text = digit.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Key("Alpha" + text, text, "Digit" + text, text);
            }

            for (var number = 1; number <= 12; number++)
            {
                var name = "F" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Key(name, name.ToLowerInvariant(), name, name);
            }

            Key("CapsLock", "capslock", "CapsLock", "Caps Lock");
            Key("Tab", "tab", "Tab", "Tab");
            Key("BackQuote", "backquote", "Backquote", "`");
            Key("Minus", "minus", "Minus", "-");
            Key("Equals", "equals", "Equals", "=");
            Key("LeftBracket", "leftbracket", "LeftBracket", "[");
            Key("RightBracket", "rightbracket", "RightBracket", "]");
            Key("Backslash", "backslash", "Backslash", "\\");
            Key("Semicolon", "semicolon", "Semicolon", ";");
            Key("Quote", "quote", "Quote", "'");
            Key("Comma", "comma", "Comma", ",");
            Key("Period", "period", "Period", ".");
            Key("Slash", "slash", "Slash", "/");
            Key("Space", "space", "Space", "Space");
            Key("Return", "enter", "Enter", "Enter");
            Key("Escape", "escape", "Escape", "Esc");
            Key("Backspace", "backspace", "Backspace", "Backspace");
            Key("LeftShift", "leftshift", "LeftShift", "Left Shift");
            Key("RightShift", "rightshift", "RightShift", "Right Shift");
            Key("LeftControl", "leftctrl", "LeftCtrl", "Left Ctrl");
            Key("RightControl", "rightctrl", "RightCtrl", "Right Ctrl");
            Key("LeftAlt", "leftalt", "LeftAlt", "Left Alt");
            Key("RightAlt", "rightalt", "RightAlt", "Right Alt");
            Key("UpArrow", "uparrow", "UpArrow", "Up Arrow");
            Key("DownArrow", "downarrow", "DownArrow", "Down Arrow");
            Key("LeftArrow", "leftarrow", "LeftArrow", "Left Arrow");
            Key("RightArrow", "rightarrow", "RightArrow", "Right Arrow");
            Key("Insert", "insert", "Insert", "Insert");
            Key("Delete", "delete", "Delete", "Delete");
            Key("Home", "home", "Home", "Home");
            Key("End", "end", "End", "End");
            Key("PageUp", "pageup", "PageUp", "Page Up");
            Key("PageDown", "pagedown", "PageDown", "Page Down");
            for (var digit = 0; digit <= 9; digit++)
            {
                var text = digit.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Key("Keypad" + text, "numpad" + text, "Numpad" + text, "Numpad " + text);
            }

            Key("KeypadEnter", "numpadenter", "NumpadEnter", "Numpad Enter");
            Key("KeypadPlus", "numpadplus", "NumpadPlus", "Numpad +");
            Key("KeypadMinus", "numpadminus", "NumpadMinus", "Numpad -");
            Key("KeypadMultiply", "numpadmultiply", "NumpadMultiply", "Numpad *");
            Key("KeypadDivide", "numpaddivide", "NumpadDivide", "Numpad /");
            Key("KeypadPeriod", "numpadperiod", "NumpadPeriod", "Numpad .");
            Mouse(InputMouseButton.Primary, "leftbutton", "Left Mouse Button");
            Mouse(InputMouseButton.Secondary, "rightbutton", "Right Mouse Button");
            Mouse(InputMouseButton.Middle, "middlebutton", "Middle Mouse Button");
            Mouse(InputMouseButton.Back, "backbutton", "Mouse Back");
            Mouse(InputMouseButton.Forward, "forwardbutton", "Mouse Forward");
        }

        /// <summary>Gets every control, keys first, in the order the player would scan them.</summary>
        internal static IReadOnlyList<VoiceControl> All => Ordered;

        /// <summary>
        /// Finds the control a binding names; false for one push-to-talk does not know, such as a gamepad button. It
        /// allocates nothing, so push-to-talk and the page can ask every frame.
        /// </summary>
        internal static bool TryFind(InputBinding binding, [NotNullWhen(true)] out VoiceControl? control)
        {
            control = null;
            if (binding.Control == null) return false;
            if (binding.Kind == InputBindingKind.Key) return Keys.TryGetValue(binding.Control, out control);
            return binding.Kind == InputBindingKind.MouseButton && MouseButtons.TryGetValue(binding.Control, out control);
        }

        /// <summary>Finds a control by the name the voice settings store it under; false for anything else.</summary>
        internal static bool TryParseStored(string? stored, [NotNullWhen(true)] out VoiceControl? control)
        {
            control = null;
            return stored != null && ByStored.TryGetValue(stored, out control);
        }

        /// <summary>Gets the player's name for a binding, or "Unbound" for none.</summary>
        internal static string Describe(InputBinding? binding) =>
            binding.HasValue && TryFind(binding.Value, out var control) ? control.Display : "Unbound";

        internal static VoiceControl Find(InputBinding binding) =>
            TryFind(binding, out var control) ? control : throw new ArgumentException("Push-to-talk does not know " + binding.Control + ".", nameof(binding));

        private static void Key(string keyCode, string pathName, string inputSystemKey, string display) =>
            Add(new VoiceControl(InputBinding.Key(keyCode), pathName, inputSystemKey, display));

        private static void Mouse(InputMouseButton button, string pathName, string display) =>
            Add(new VoiceControl(InputBinding.MouseButton(button), pathName, string.Empty, display));

        private static void Add(VoiceControl control)
        {
            ByStored.Add(control.Stored, control);
            (control.IsMouseButton ? MouseButtons : Keys).Add(control.Binding.Control, control);
            Ordered.Add(control);
        }
    }
}
