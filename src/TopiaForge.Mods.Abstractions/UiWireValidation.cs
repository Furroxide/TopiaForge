using System;

namespace TopiaForge.Mods
{
    /// <summary>The bounded composition contract shared by player UI, mirrored HUDs and testing hosts.</summary>
    internal static class UiWireValidation
    {
        internal static void Validate(UiNode? node, bool requireWireMetadata = false)
        {
            UiComposition.Validate(node);
            Visit(node, requireWireMetadata);
        }

        private static void Text(string? value, int maximum = 4096)
        {
            if (value == null || value.Length > maximum)
                throw new ArgumentException("Remote UI text exceeds its " + maximum + " character limit.");
        }

        private static void Visit(UiNode? node, bool requireWireMetadata)
        {
            if (node == null) return;
            if (node.Id != null) Text(node.Id, 128);
            switch (node)
            {
                case UiText n: Text(n.Text); break;
                case UiColumn or UiRow or UiScroll or UiSplitPane or UiPadding or UiSizedBox or UiExpanded or UiSpacer or
                    UiWrap or UiGrid or UiStack or UiPositioned or UiCard or UiDivider or UiBox:
                    break;
                case UiProgress n: Text(n.Label, 256); break;
                case UiCountdown n: Text(n.Label, 256); break;
                case UiStatBar n: Text(n.Label, 24); break;
                case UiIcon n: Text(n.SemanticLabel, 256); break;
                case UiImage n:
                    Text(n.SemanticLabel, 256);
                    if (requireWireMetadata &&
                        (!n.Width.HasValue || !n.Height.HasValue || n.PackDescriptorDigest.Length != 32))
                        throw new ArgumentException("Resolve the image box and its package-local descriptor before encoding remote UI.");
                    break;
                case UiBadge n: Text(n.Text, 256); break;
                case UiPips n: Text(n.Label, 256); break;
                case UiButton n: Text(n.Label); break;
                case UiToggle n: Text(n.Label); break;
                case UiCheckbox n: Text(n.Label); break;
                case UiSlider n: Text(n.Label); break;
                case UiTextInput n: Text(n.Label); Text(n.Value); Text(n.Placeholder); Text(n.Error ?? string.Empty, 256); break;
                case UiTabs n:
                    Text(n.SelectedTabId, 64);
                    foreach (var tab in n.Tabs) { Text(tab.Id, 64); Text(tab.Label, 32); }
                    break;
                case UiTooltip n: Text(n.Message, 256); break;
                case UiKeyBindingField n:
                    Text(n.ActionName, 64); Text(n.Label ?? string.Empty);
                    if ((requireWireMetadata && !n.HasWireDefaults) || n.WireDefaults.Count > UiInputBindingHint.MaximumDefaults)
                        throw new ArgumentException("Resolve the owning action's default bindings before encoding remote UI.");
                    break;
                case UiDropdown n:
                    Text(n.Label); Text(n.SelectedValue);
                    if (n.Choices.Count > 256) throw new ArgumentException("UiDropdown supports at most 256 remote choices.");
                    foreach (var choice in n.Choices) { Text(choice.Value); Text(choice.Label); }
                    break;
                case UiVirtualList n:
                    Text(n.SelectedItemId ?? "");
                    if (n.Items.Count > 4096) throw new ArgumentException("UiVirtualList supports at most 4096 remote items.");
                    foreach (var item in n.Items) { Text(item.Id); Text(item.Title); Text(item.Subtitle); Text(item.Badge); }
                    break;
                default:
                    throw new ArgumentException(node.GetType().Name + " cannot be sent to a player. Use supported declarative UI nodes.");
            }

            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++) Visit(children[index], requireWireMetadata);
        }
    }
}
