using System.Collections.Generic;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using InputBinding = TopiaForge.Mods.InputBinding;
using LegacyInputField = UnityEngine.UI.InputField;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// The keyboard and mouse for push-to-talk: Unity's legacy input while the game has it, as the game does now, or the
    /// Input System's devices otherwise (<see cref="TopiaForgeInput.LegacyAvailable"/>). Each control is looked up once and
    /// kept, so reading a held key allocates nothing. A text field that has the keyboard, the game's or the kit's, means
    /// the player types. Main thread only.
    /// </summary>
    internal sealed class UnityVoiceInput : IVoiceInput
    {
        private readonly Dictionary<VoiceControl, Probe> probes = new Dictionary<VoiceControl, Probe>();
        private int takenFrame = -1;
        private GameObject? selected;
        private TMP_InputField? selectedText;
        private LegacyInputField? selectedLegacyText;

        public bool IsTyping
        {
            get
            {
                var events = EventSystem.current;
                var current = events == null ? null : events.currentSelectedGameObject;
                if (current == null)
                {
                    selected = null;
                    selectedText = null;
                    selectedLegacyText = null;
                    return false;
                }

                if (!ReferenceEquals(current, selected))
                {
                    selected = current;
                    selectedText = current.GetComponent<TMP_InputField>();
                    selectedLegacyText = current.GetComponent<LegacyInputField>();
                }

                return (selectedText != null && selectedText.isFocused) || (selectedLegacyText != null && selectedLegacyText.isFocused);
            }
        }

        public bool IsHeld(InputBinding binding) => VoiceControls.TryFind(binding, out var control) && ProbeOf(control).IsHeld();

        public bool TryTakePressed(out InputBinding pressed)
        {
            pressed = default;
            var frame = Time.frameCount;
            if (frame == takenFrame) return false;
            var all = VoiceControls.All;
            for (var index = 0; index < all.Count; index++)
            {
                var control = all[index];
                if (!ProbeOf(control).WasPressed()) continue;
                takenFrame = frame;
                pressed = control.Binding;
                return true;
            }

            return false;
        }

        private Probe ProbeOf(VoiceControl control)
        {
            if (!probes.TryGetValue(control, out var probe))
            {
                probe = new Probe(control);
                probes.Add(control, probe);
            }

            return probe;
        }

        /// <summary>One control as both input backends read it.</summary>
        private sealed class Probe
        {
            private readonly KeyCode keyCode = KeyCode.None;
            private readonly Key key = Key.None;
            private readonly int mouseButton = -1;

            internal Probe(VoiceControl control)
            {
                if (control.IsMouseButton)
                {
                    mouseButton = System.Enum.TryParse(control.Binding.Control, out InputMouseButton button) ? (int)button : -1;
                    return;
                }

                if (System.Enum.TryParse(control.Binding.Control, out KeyCode parsedCode)) keyCode = parsedCode;
                if (System.Enum.TryParse(control.InputSystemKey, out Key parsedKey)) key = parsedKey;
            }

            internal bool IsHeld()
            {
                if (TopiaForgeInput.LegacyAvailable)
                {
                    return mouseButton >= 0 ? Input.GetMouseButton(mouseButton) : keyCode != KeyCode.None && Input.GetKey(keyCode);
                }

                var button = Button();
                return button != null && button.isPressed;
            }

            internal bool WasPressed()
            {
                if (TopiaForgeInput.LegacyAvailable)
                {
                    return mouseButton >= 0 ? Input.GetMouseButtonDown(mouseButton) : keyCode != KeyCode.None && Input.GetKeyDown(keyCode);
                }

                var button = Button();
                return (button != null && button.wasPressedThisFrame) || (key != Key.None && TopiaForgeInputOwnership.KeyPressedThisFrame(key));
            }

            private ButtonControl? Button()
            {
                if (mouseButton < 0)
                {
                    var keyboard = Keyboard.current;
                    return keyboard == null || key == Key.None ? null : keyboard[key];
                }

                var mouse = Mouse.current;
                if (mouse == null) return null;
                switch ((InputMouseButton)mouseButton)
                {
                    case InputMouseButton.Primary: return mouse.leftButton;
                    case InputMouseButton.Secondary: return mouse.rightButton;
                    case InputMouseButton.Middle: return mouse.middleButton;
                    case InputMouseButton.Back: return mouse.backButton;
                    default: return mouse.forwardButton;
                }
            }
        }
    }
}
