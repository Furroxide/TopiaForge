using System;

namespace TopiaForge.Mods
{
    /// <summary>The bounded composition contract shared by player UI, mirrored HUDs and testing hosts.</summary>
    internal static class UiWireValidation
    {
        internal static void Validate(UiNode? node)
        {
            UiComposition.Validate(node);
            Visit(node);
        }

        private static void Text(string? value, int maximum = 4096)
        {
            if (value == null || value.Length > maximum)
                throw new ArgumentException("Remote UI text exceeds its " + maximum + " character limit.");
        }

        private static void Visit(UiNode? node)
        {
            if (node == null) return;
            if (node.Id != null) Text(node.Id, 128);
            switch (node)
            {
                case UiText n: Text(n.Text); break;
                case UiColumn n: Children(n); break;
                case UiRow n: Children(n); break;
                case UiScroll n: Visit(n.Content); break;
                case UiSplitPane n: Visit(n.Primary); Visit(n.Secondary); break;
                case UiProgress n: Text(n.Label, 256); break;
                case UiCountdown n: Text(n.Label, 256); break;
                case UiButton n: Text(n.Label); break;
                case UiToggle n: Text(n.Label); break;
                case UiSlider n: Text(n.Label); break;
                case UiTextInput n: Text(n.Label); Text(n.Value); Text(n.Placeholder); break;
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
        }

        private static void Children(UiLayoutNode node)
        {
            if (node.Children.Count > 256) throw new ArgumentException("Remote UI layouts support at most 256 direct children.");
            foreach (var child in node.Children) Visit(child);
        }
    }
}
