using System;
using System.Globalization;

namespace TopiaForge.Mods
{
    internal static partial class UiWireCodec
    {
        private sealed partial class Decoder
        {
            private UiNode ControlNode(byte tag, int depth)
            {
                var id = LayoutId();
                if (tag == TooltipTag)
                {
                    var message = Text(r, 256);
                    return new UiTooltip(LayoutChild(depth), message, id);
                }
                if (id == null) throw Bad();
                return tag switch
                {
                    CheckboxTag => new UiCheckbox(id, Text(r), Present(), value => reply(id, value ? "1" : "0"), Present()),
                    TabsTag => Tabs(id, depth),
                    DecoratedTextInputTag => TextInput(id, Text(r), decorated: true),
                    SteppedSliderTag => Slider(id, Text(r), stepped: true),
                    KeyBindingFieldTag => KeyBindingField(id),
                    _ => throw Bad(),
                };
            }

            private UiTabs Tabs(string id, int depth)
            {
                var style = (UiTabsStyle)Byte(r, (byte)UiTabsStyle.Rail);
                var selected = Text(r, 64);
                var enabled = Present();
                var count = Byte(r, 12);
                if (count < 2) throw Bad();
                var tabs = new UiTab[count];
                for (var index = 0; index < tabs.Length; index++)
                    tabs[index] = new UiTab(Text(r, 64), Text(r, 32), LayoutChild(depth));
                return new UiTabs(id, tabs, selected, value => reply(id, value), style, enabled);
            }

            private UiTextInput TextInput(string id, string label, bool decorated)
            {
                var value = Text(r);
                var placeholder = Text(r);
                var limit = Count(4096);
                var enabled = Present();
                if (!decorated) return new UiTextInput(id, label, value, changed => reply(id, changed), placeholder, limit, enabled);
                var error = Text(r, 256);
                return UiTextInput.WithDecoration(id, label, value, changed => reply(id, changed),
                    new UiInputDecoration(placeholder, error.Length == 0 ? null : error), limit, enabled);
            }

            private UiSlider Slider(string id, string label, bool stepped)
            {
                var minimum = Number(r);
                var maximum = Number(r);
                var value = Number(r);
                var enabled = Present();
                if (!stepped) return new UiSlider(id, label, minimum, maximum, value, Reply, enabled);
                var divisions = Count(1000);
                var kind = (UiValueFormatKind)Byte(r, (byte)UiValueFormatKind.Multiplier);
                var decimals = Byte(r, 3);
                return new UiSlider(id, label, minimum, maximum, value, Reply, divisions, UiValueFormat.FromWire(kind, decimals), enabled);

                void Reply(float changed) => reply(id, changed.ToString("R", CultureInfo.InvariantCulture));
            }

            private UiKeyBindingField KeyBindingField(string id)
            {
                var action = Text(r, 64);
                var label = Text(r);
                var device = (UiInputDevice)Byte(r, (byte)UiInputDevice.Gamepad);
                var enabled = Present();
                return new UiKeyBindingField(id, action, label.Length == 0 ? null : label, device, enabled, ReadInputDefaults());
            }
        }
    }
}
