using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace TopiaForge.Mods
{
    /// <summary>Base type for immutable, engine-free UI composition nodes.</summary>
    public abstract class UiNode
    {
        internal UiNode(string? id)
        {
            if (id != null && (string.IsNullOrWhiteSpace(id) || id.Length > 128))
            {
                throw new ArgumentException("A UI node id must contain 1-128 characters.", nameof(id));
            }

            Id = id;
        }

        /// <summary>Gets the stable control id, or <c>null</c> for noninteractive presentation nodes.</summary>
        public string? Id { get; }
    }

    /// <summary>Semantic text styles supported by safe UI composition.</summary>
    public enum UiTextStyle
    {
        /// <summary>Ordinary readable body copy.</summary>
        Body = 0,

        /// <summary>A compact section heading.</summary>
        Heading = 1,

        /// <summary>Secondary compact copy.</summary>
        Caption = 2
    }

    /// <summary>Semantic button presentations supported by safe UI composition.</summary>
    public enum UiButtonStyle
    {
        /// <summary>The primary filled action.</summary>
        Primary = 0,

        /// <summary>A secondary outlined action.</summary>
        Secondary = 1,

        /// <summary>A quiet inline action.</summary>
        Ghost = 2,

        /// <summary>A destructive action using the danger role.</summary>
        Danger = 3
    }

    /// <summary>Immutable text content.</summary>
    public sealed class UiText : UiNode
    {
        /// <summary>Creates a text node.</summary>
        public UiText(
            string text,
            UiTextStyle style = UiTextStyle.Body,
            UiTone tone = UiTone.Neutral)
            : base(null)
        {
            if (!Enum.IsDefined(typeof(UiTextStyle), style)) throw new ArgumentOutOfRangeException(nameof(style));
            if (!Enum.IsDefined(typeof(UiTone), tone)) throw new ArgumentOutOfRangeException(nameof(tone));
            Text = text ?? string.Empty;
            Style = style;
            Tone = tone;
        }

        /// <summary>Gets the displayed text.</summary>
        public string Text { get; }

        /// <summary>Gets the semantic text style.</summary>
        public UiTextStyle Style { get; }

        /// <summary>Gets the semantic color tone.</summary>
        public UiTone Tone { get; }
    }

    /// <summary>Base type for immutable multi-child layout nodes.</summary>
    public abstract class UiLayoutNode : UiNode
    {
        internal UiLayoutNode(IEnumerable<UiNode> children)
            : base(null)
        {
            if (children == null) throw new ArgumentNullException(nameof(children));
            var copy = new List<UiNode>();
            foreach (var child in children)
            {
                copy.Add(child ?? throw new ArgumentException("UI layout children cannot be null.", nameof(children)));
                if (copy.Count > 256)
                {
                    throw new ArgumentException("A UI layout cannot contain more than 256 direct children.", nameof(children));
                }
            }

            Children = new ReadOnlyCollection<UiNode>(copy);
        }

        /// <summary>Gets the immutable child sequence.</summary>
        public IReadOnlyList<UiNode> Children { get; }
    }

    /// <summary>Arranges children vertically.</summary>
    public sealed class UiColumn : UiLayoutNode
    {
        /// <summary>Creates a vertical layout.</summary>
        public UiColumn(IEnumerable<UiNode> children) : base(children) { }

        /// <summary>Creates a vertical layout.</summary>
        public UiColumn(params UiNode[] children) : base(children) { }
    }

    /// <summary>Arranges children horizontally.</summary>
    public sealed class UiRow : UiLayoutNode
    {
        /// <summary>Creates a horizontal layout.</summary>
        public UiRow(IEnumerable<UiNode> children) : base(children) { }

        /// <summary>Creates a horizontal layout.</summary>
        public UiRow(params UiNode[] children) : base(children) { }
    }

    /// <summary>Places one composition subtree in a TopiaForgeUi scroll view.</summary>
    public sealed class UiScroll : UiNode
    {
        /// <summary>Creates a scroll node.</summary>
        public UiScroll(UiNode content, float height = 240f) : base(null)
        {
            Content = content ?? throw new ArgumentNullException(nameof(content));
            if (float.IsNaN(height) || float.IsInfinity(height) || height < 80f || height > 1200f)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            Height = height;
        }

        /// <summary>Gets the immutable scroll content.</summary>
        public UiNode Content { get; }

        /// <summary>Gets the bounded viewport height in scaled UI units.</summary>
        public float Height { get; }
    }

    /// <summary>An immutable button description with an isolated activation callback.</summary>
    public sealed class UiButton : UiNode
    {
        /// <summary>Creates a button.</summary>
        public UiButton(
            string id,
            string label,
            Action activated,
            UiButtonStyle style = UiButtonStyle.Primary,
            bool enabled = true)
            : base(id)
        {
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A button label is required.", nameof(label));
            if (!Enum.IsDefined(typeof(UiButtonStyle), style)) throw new ArgumentOutOfRangeException(nameof(style));
            Label = label;
            Activated = activated ?? throw new ArgumentNullException(nameof(activated));
            Style = style;
            Enabled = enabled;
        }

        /// <summary>Gets the visible accessible label.</summary>
        public string Label { get; }

        /// <summary>Gets the activation callback.</summary>
        public Action Activated { get; }

        /// <summary>Gets the semantic button style.</summary>
        public UiButtonStyle Style { get; }

        /// <summary>Gets whether the button initially accepts input.</summary>
        public bool Enabled { get; }
    }

    /// <summary>An immutable labeled toggle description.</summary>
    public sealed class UiToggle : UiNode
    {
        /// <summary>Creates a toggle.</summary>
        public UiToggle(string id, string label, bool value, Action<bool> changed, bool enabled = true)
            : base(id)
        {
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A toggle label is required.", nameof(label));
            Label = label;
            Value = value;
            Changed = changed ?? throw new ArgumentNullException(nameof(changed));
            Enabled = enabled;
        }

        /// <summary>Gets the visible accessible label.</summary>
        public string Label { get; }

        /// <summary>Gets the initial value.</summary>
        public bool Value { get; }

        /// <summary>Gets the change callback.</summary>
        public Action<bool> Changed { get; }

        /// <summary>Gets whether the toggle initially accepts input.</summary>
        public bool Enabled { get; }
    }

    /// <summary>An immutable labeled bounded slider description.</summary>
    public sealed class UiSlider : UiNode
    {
        /// <summary>Creates a slider.</summary>
        public UiSlider(
            string id,
            string label,
            float minimum,
            float maximum,
            float value,
            Action<float> changed,
            bool enabled = true)
            : base(id)
        {
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A slider label is required.", nameof(label));
            if (!IsFinite(minimum) || !IsFinite(maximum) || minimum >= maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(maximum));
            }

            if (!IsFinite(value) || value < minimum || value > maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            Label = label;
            Minimum = minimum;
            Maximum = maximum;
            Value = value;
            Changed = changed ?? throw new ArgumentNullException(nameof(changed));
            Enabled = enabled;
        }

        /// <summary>Gets the visible accessible label.</summary>
        public string Label { get; }

        /// <summary>Gets the minimum accepted value.</summary>
        public float Minimum { get; }

        /// <summary>Gets the maximum accepted value.</summary>
        public float Maximum { get; }

        /// <summary>Gets the initial value.</summary>
        public float Value { get; }

        /// <summary>Gets the change callback.</summary>
        public Action<float> Changed { get; }

        /// <summary>Gets whether the slider initially accepts input.</summary>
        public bool Enabled { get; }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>An immutable labeled text-input description.</summary>
    public sealed class UiTextInput : UiNode
    {
        /// <summary>Creates a text input.</summary>
        public UiTextInput(
            string id,
            string label,
            string value,
            Action<string> changed,
            string placeholder = "",
            int maximumLength = 256,
            bool enabled = true)
            : base(id)
        {
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A text-input label is required.", nameof(label));
            if (maximumLength < 1 || maximumLength > 4096) throw new ArgumentOutOfRangeException(nameof(maximumLength));
            Label = label;
            Value = Truncate(value ?? string.Empty, maximumLength);
            Placeholder = placeholder ?? string.Empty;
            MaximumLength = maximumLength;
            Changed = changed ?? throw new ArgumentNullException(nameof(changed));
            Enabled = enabled;
        }

        /// <summary>Gets the visible accessible label.</summary>
        public string Label { get; }

        /// <summary>Gets the initial text.</summary>
        public string Value { get; }

        /// <summary>Gets the placeholder text.</summary>
        public string Placeholder { get; }

        /// <summary>Gets the maximum accepted character count.</summary>
        public int MaximumLength { get; }

        /// <summary>Gets the change callback.</summary>
        public Action<string> Changed { get; }

        /// <summary>Gets whether the input initially accepts input.</summary>
        public bool Enabled { get; }

        internal static string Truncate(string value, int maximumLength) =>
            value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
    }

    /// <summary>One immutable dropdown choice.</summary>
    public sealed class UiChoice
    {
        /// <summary>Creates a dropdown choice.</summary>
        public UiChoice(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A choice value is required.", nameof(value));
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A choice label is required.", nameof(label));
            Value = value;
            Label = label;
        }

        /// <summary>Gets the stable value delivered to callbacks.</summary>
        public string Value { get; }

        /// <summary>Gets the visible accessible label.</summary>
        public string Label { get; }
    }

    /// <summary>An immutable labeled dropdown description.</summary>
    public sealed class UiDropdown : UiNode
    {
        /// <summary>Creates a dropdown.</summary>
        public UiDropdown(
            string id,
            string label,
            IEnumerable<UiChoice> choices,
            string selectedValue,
            Action<string> changed,
            bool enabled = true)
            : base(id)
        {
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("A dropdown label is required.", nameof(label));
            if (choices == null) throw new ArgumentNullException(nameof(choices));
            var copy = new List<UiChoice>();
            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (var choice in choices)
            {
                if (choice == null) throw new ArgumentException("Dropdown choices cannot be null.", nameof(choices));
                if (!values.Add(choice.Value)) throw new ArgumentException("Dropdown choice values must be unique.", nameof(choices));
                copy.Add(choice);
                if (copy.Count > 256) throw new ArgumentException("A dropdown cannot contain more than 256 choices.", nameof(choices));
            }

            if (copy.Count == 0) throw new ArgumentException("A dropdown requires at least one choice.", nameof(choices));
            if (selectedValue == null || !values.Contains(selectedValue))
            {
                throw new ArgumentException("The selected dropdown value is not a choice.", nameof(selectedValue));
            }
            Label = label;
            Choices = new ReadOnlyCollection<UiChoice>(copy);
            SelectedValue = selectedValue;
            Changed = changed ?? throw new ArgumentNullException(nameof(changed));
            Enabled = enabled;
        }

        /// <summary>Gets the visible accessible label.</summary>
        public string Label { get; }

        /// <summary>Gets the immutable choices.</summary>
        public IReadOnlyList<UiChoice> Choices { get; }

        /// <summary>Gets the initially selected stable value.</summary>
        public string SelectedValue { get; }

        /// <summary>Gets the change callback.</summary>
        public Action<string> Changed { get; }

        /// <summary>Gets whether the dropdown initially accepts input.</summary>
        public bool Enabled { get; }
    }

    /// <summary>One immutable row in a virtualized safe list.</summary>
    public sealed class UiListItem
    {
        /// <summary>Creates a virtualized list item.</summary>
        public UiListItem(string id, string title, string subtitle = "", string badge = "")
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A list item id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A list item title is required.", nameof(title));
            Id = id;
            Title = title;
            Subtitle = subtitle ?? string.Empty;
            Badge = badge ?? string.Empty;
        }

        /// <summary>Gets the stable value delivered to the selection callback.</summary>
        public string Id { get; }

        /// <summary>Gets the primary row label.</summary>
        public string Title { get; }

        /// <summary>Gets optional secondary row text.</summary>
        public string Subtitle { get; }

        /// <summary>Gets optional trailing badge text.</summary>
        public string Badge { get; }
    }

    /// <summary>An immutable bounded data set rendered by TopiaForgeUi's pooled virtual list.</summary>
    public sealed class UiVirtualList : UiNode
    {
        /// <summary>Creates a virtualized list.</summary>
        public UiVirtualList(
            string id,
            IEnumerable<UiListItem> items,
            Action<string> selected,
            string? selectedItemId = null,
            int visibleRows = 6,
            bool enabled = true)
            : base(id)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            if (visibleRows < 1 || visibleRows > 20) throw new ArgumentOutOfRangeException(nameof(visibleRows));
            var copy = new List<UiListItem>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item == null) throw new ArgumentException("List items cannot be null.", nameof(items));
                if (!ids.Add(item.Id)) throw new ArgumentException("Virtual-list item ids must be unique.", nameof(items));
                copy.Add(item);
                if (copy.Count > 4096) throw new ArgumentException("A virtual list cannot contain more than 4096 items.", nameof(items));
            }

            if (selectedItemId != null && !ids.Contains(selectedItemId))
            {
                throw new ArgumentException("The selected list item id was not found.", nameof(selectedItemId));
            }

            Items = new ReadOnlyCollection<UiListItem>(copy);
            Selected = selected ?? throw new ArgumentNullException(nameof(selected));
            SelectedItemId = selectedItemId;
            VisibleRows = visibleRows;
            Enabled = enabled;
        }

        /// <summary>Gets the immutable bounded item set.</summary>
        public IReadOnlyList<UiListItem> Items { get; }

        /// <summary>Gets the initially selected item id, or <c>null</c>.</summary>
        public string? SelectedItemId { get; }

        /// <summary>Gets the number of rows requested in the viewport.</summary>
        public int VisibleRows { get; }

        /// <summary>Gets the selection callback.</summary>
        public Action<string> Selected { get; }

        /// <summary>Gets whether the list initially accepts input.</summary>
        public bool Enabled { get; }
    }

    internal static class UiComposition
    {
        public static void Validate(UiNode? root)
        {
            if (root == null) return;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            Visit(root, 0, ids, ref count);
        }

        public static bool ContainsInteractive(UiNode? node)
        {
            if (node == null) return false;
            if (node is UiButton || node is UiToggle || node is UiSlider || node is UiTextInput ||
                node is UiDropdown || node is UiVirtualList)
            {
                return true;
            }

            if (node is UiLayoutNode layout)
            {
                foreach (var child in layout.Children)
                {
                    if (ContainsInteractive(child)) return true;
                }
            }
            else if (node is UiScroll scroll)
            {
                return ContainsInteractive(scroll.Content);
            }

            return false;
        }

        private static void Visit(UiNode node, int depth, HashSet<string> ids, ref int count)
        {
            if (depth > 32) throw new ArgumentException("A UI composition cannot be deeper than 32 nodes.");
            if (++count > 4096) throw new ArgumentException("A UI composition cannot contain more than 4096 nodes.");
            if (node.Id != null && !ids.Add(node.Id))
            {
                throw new ArgumentException("A UI composition contains duplicate control id '" + node.Id + "'.");
            }

            if (node is UiLayoutNode layout)
            {
                foreach (var child in layout.Children) Visit(child, depth + 1, ids, ref count);
            }
            else if (node is UiScroll scroll)
            {
                Visit(scroll.Content, depth + 1, ids, ref count);
            }
        }
    }
}
