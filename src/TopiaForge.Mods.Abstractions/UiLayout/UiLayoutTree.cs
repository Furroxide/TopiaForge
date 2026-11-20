using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TopiaForge.Mods
{
    internal static class UiLayoutTree
    {
        internal const int MaximumDepth = 32;
        internal const int MaximumNodes = 4096;

        internal static void Validate(UiNode? root)
        {
            if (root == null) return;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            Span<int> path = stackalloc int[MaximumDepth];
            Visit(root, null, path, 1, ids, ref count);
        }

        internal static bool ContainsInteractive(UiNode? node)
        {
            if (node == null) return false;
            if (IsInteractive(node)) return true;
            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++)
            {
                if (ContainsInteractive(children[index])) return true;
            }
            return false;
        }

        internal static bool IsInteractive(UiNode node) => node is UiButton or UiToggle or UiSlider or UiTextInput or
            UiDropdown or UiVirtualList or UiGraphCanvas;

        internal static bool IsDisabled(UiNode node) => node switch
        {
            UiCard card => card.Disabled,
            UiButton button => !button.Enabled,
            UiToggle toggle => !toggle.Enabled,
            UiSlider slider => !slider.Enabled,
            UiTextInput input => !input.Enabled,
            UiDropdown dropdown => !dropdown.Enabled,
            UiVirtualList list => !list.Enabled,
            UiGraphCanvas graph => !graph.Enabled,
            _ => false,
        };

        internal static bool TryFind(UiNode? root, string id, out UiNode? node, out bool disabled) =>
            TryFind(root, id, inheritedDisabled: false, out node, out disabled);

        private static bool TryFind(UiNode? node, string id, bool inheritedDisabled, out UiNode? match, out bool disabled)
        {
            match = null;
            disabled = false;
            if (node == null || string.IsNullOrWhiteSpace(id)) return false;
            inheritedDisabled |= IsDisabled(node);
            if (string.Equals(node.Id, id, StringComparison.Ordinal))
            {
                match = node;
                disabled = inheritedDisabled;
                return true;
            }

            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++)
            {
                if (TryFind(children[index], id, inheritedDisabled, out match, out disabled)) return true;
            }
            return false;
        }

        private static void Visit(UiNode node, UiNode? parent, Span<int> path, int depth, HashSet<string> ids, ref int count)
        {
            if (depth > MaximumDepth) throw Invalid(path, "A UI composition cannot be deeper than 32 levels.");
            var currentPath = path.Slice(0, depth - 1);
            if (++count > MaximumNodes) throw Invalid(currentPath, "A UI composition cannot contain more than 4096 nodes.");
            if (node.Id != null && !ids.Add(node.Id)) throw Invalid(currentPath, "A UI composition contains a duplicate node id.");
            if ((node is UiExpanded || node is UiSpacer) && parent is not (UiRow or UiColumn))
                throw Invalid(currentPath, "Expanded and Spacer require an immediate Row or Column parent.");
            if (node is UiPositioned && parent is not UiStack)
                throw Invalid(currentPath, "Positioned requires an immediate Stack parent.");
            ValidateText(node, currentPath);

            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++)
            {
                path[depth - 1] = index;
                Visit(children[index], node, path, depth + 1, ids, ref count);
            }
        }

        private static void ValidateText(UiNode node, ReadOnlySpan<int> path)
        {
            switch (node)
            {
                case UiText text: Text(text.Text, path); break;
                case UiButton button: Text(button.Label, path); break;
                case UiToggle toggle: Text(toggle.Label, path); break;
                case UiSlider slider: Text(slider.Label, path); break;
                case UiTextInput input:
                    Text(input.Label, path);
                    Text(input.Value, path);
                    Text(input.Placeholder, path);
                    break;
                case UiDropdown dropdown:
                    Text(dropdown.Label, path);
                    foreach (var choice in dropdown.Choices)
                    {
                        Text(choice.Value, path);
                        Text(choice.Label, path);
                    }
                    break;
                case UiVirtualList list:
                    foreach (var item in list.Items)
                    {
                        Text(item.Id, path);
                        Text(item.Title, path);
                        Text(item.Subtitle, path);
                        Text(item.Badge, path);
                    }
                    break;
            }
        }

        private static void Text(string value, ReadOnlySpan<int> path)
        {
            if (value.Length > 4096) throw Invalid(path, "UI text fields are limited to 4096 UTF-16 units.");
        }

        private static UiLayoutException Invalid(ReadOnlySpan<int> path, string message)
        {
            var location = new StringBuilder("/0");
            foreach (var index in path) location.Append('/').Append(index.ToString(CultureInfo.InvariantCulture));
            return new UiLayoutException(UiLayoutFailure.InvalidLayout, location.ToString(), message);
        }
    }
}
