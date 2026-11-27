using System;

namespace TopiaForge.Mods
{
    internal static partial class UiWireCodec
    {
        private const byte CheckboxTag = 46;
        private const byte TabsTag = 47;
        private const byte TooltipTag = 48;
        private const byte DecoratedTextInputTag = 49;
        private const byte SteppedSliderTag = 50;
        private const byte KeyBindingFieldTag = 51;

        private static byte ControlTag(UiNode? node) => node switch
        {
            UiCheckbox => CheckboxTag,
            UiTabs => TabsTag,
            UiTooltip => TooltipTag,
            UiTextInput { UsesExplicitDecoration: true } => DecoratedTextInputTag,
            UiSlider { UsesExplicitFormat: true } => SteppedSliderTag,
            UiKeyBindingField => KeyBindingFieldTag,
            _ => 0,
        };

        private static void WriteControlNode(IUiWireWriter writer, UiNode node, byte tag,
            string clockEpoch, bool retained, ref bool timed)
        {
            writer.WriteByte(tag);
            switch (node)
            {
                case UiCheckbox checkbox:
                    Text(writer, checkbox.Id!);
                    Text(writer, checkbox.Label);
                    Flag(writer, checkbox.Value);
                    Flag(writer, checkbox.Enabled);
                    return;
                case UiTabs tabs:
                    Text(writer, tabs.Id!);
                    writer.WriteByte((byte)tabs.Style);
                    Text(writer, tabs.SelectedTabId);
                    Flag(writer, tabs.Enabled);
                    writer.WriteByte((byte)tabs.Tabs.Count);
                    for (var index = 0; index < tabs.Tabs.Count; index++)
                    {
                        var tab = tabs.Tabs[index];
                        Text(writer, tab.Id);
                        Text(writer, tab.Label);
                        WriteNode(writer, tab.Child, clockEpoch, retained, ref timed);
                    }
                    return;
                case UiTooltip tooltip:
                    Text(writer, tooltip.Id ?? string.Empty);
                    Text(writer, tooltip.Message);
                    WriteNode(writer, tooltip.Child, clockEpoch, retained, ref timed);
                    return;
                case UiTextInput text:
                    WriteTextInputFields(writer, text);
                    Text(writer, text.Error ?? string.Empty);
                    return;
                case UiSlider slider:
                    WriteSliderFields(writer, slider);
                    writer.WriteVarUInt((ulong)slider.Divisions);
                    writer.WriteByte((byte)slider.Format.Kind);
                    writer.WriteByte((byte)slider.Format.Decimals);
                    return;
                case UiKeyBindingField field:
                    Text(writer, field.Id!);
                    Text(writer, field.ActionName);
                    Text(writer, field.Label ?? string.Empty);
                    writer.WriteByte((byte)field.Device);
                    Flag(writer, field.Enabled);
                    writer.WriteByte((byte)field.WireDefaults.Count);
                    for (var index = 0; index < field.WireDefaults.Count; index++)
                    {
                        var binding = field.WireDefaults[index];
                        writer.WriteByte((byte)binding.Kind);
                        writer.WriteString(binding.Control);
                    }
                    return;
                default:
                    throw new ArgumentException("This UI control cannot be sent to a player.");
            }
        }

        private static void WriteSliderFields(IUiWireWriter writer, UiSlider slider)
        {
            Text(writer, slider.Id!);
            Text(writer, slider.Label);
            writer.WriteSingle(slider.Minimum);
            writer.WriteSingle(slider.Maximum);
            writer.WriteSingle(slider.Value);
            Flag(writer, slider.Enabled);
        }

        private static void WriteTextInputFields(IUiWireWriter writer, UiTextInput text)
        {
            Text(writer, text.Id!);
            Text(writer, text.Label);
            Text(writer, text.Value);
            Text(writer, text.Placeholder);
            writer.WriteVarUInt((ulong)text.MaximumLength);
            Flag(writer, text.Enabled);
        }
    }
}
