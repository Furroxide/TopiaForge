using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>The visible cap and spoken control name selected for the viewing device.</summary>
    internal readonly struct UiActionBindingPresentation
    {
        private UiActionBindingPresentation(string label, string spokenControl, UiGlyph? cue = null)
        {
            Label = label;
            SpokenControl = spokenControl;
            Cue = cue;
        }

        internal string Label { get; }
        internal string SpokenControl { get; }
        internal UiGlyph? Cue { get; }

        internal static UiActionBindingPresentation Resolve(IReadOnlyList<InputBinding>? live,
            IReadOnlyList<UiInputBindingHint> defaults, UiInputDevice device)
        {
            if (live != null)
            {
                for (var index = 0; index < live.Count; index++)
                {
                    var binding = live[index];
                    if (UiRebinding.Device(binding.Kind) == device) return Face(binding.Kind, binding.Control);
                }
                if (live.Count != 0) return Face(live[0].Kind, live[0].Control);
            }
            else
            {
                for (var index = 0; index < defaults.Count; index++)
                {
                    var binding = defaults[index];
                    if (UiRebinding.Device(binding.Kind) == device) return Face(binding.Kind, binding.Control);
                }
                if (defaults.Count != 0) return Face(defaults[0].Kind, defaults[0].Control);
            }
            return new UiActionBindingPresentation("Unbound", "Unbound", UiGlyph.Info);
        }

        private static UiActionBindingPresentation Face(InputBindingKind kind, string control)
        {
            var label = UiRebinding.KeyLabel(kind, control);
            var spoken = kind switch
            {
                InputBindingKind.GamepadButton => control switch
                {
                    "LeftShoulder" => "left bumper",
                    "RightShoulder" => "right bumper",
                    "LeftStick" => "left stick click",
                    "RightStick" => "right stick click",
                    _ => label,
                },
                InputBindingKind.GamepadAxis => control switch
                {
                    "LeftTrigger" => "left trigger",
                    "RightTrigger" => "right trigger",
                    "LeftX" => "left stick horizontal",
                    "LeftY" => "left stick vertical",
                    "RightX" => "right stick horizontal",
                    "RightY" => "right stick vertical",
                    _ => label,
                },
                _ => label,
            };
            return new UiActionBindingPresentation(label, spoken);
        }
    }
}
