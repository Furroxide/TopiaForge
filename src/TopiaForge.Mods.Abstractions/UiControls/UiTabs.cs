using System;
using System.Collections.Generic;
using System.Globalization;

namespace TopiaForge.Mods
{
    /// <summary>Specifies where the tab controls are arranged.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public enum UiTabsStyle
    {
        /// <summary>Places a horizontal strip above the selected tab's content.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        Bar = 0,
        /// <summary>Places a vertical rail alongside the selected tab's content.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        Rail = 1,
    }

    /// <summary>A labeled page in a tab group.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class UiTab
    {
        /// <summary>Creates a tab with a stable ID, visible label, and child tree.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiTab(string id, string label, UiNode child)
        {
            UiControlText.Require(id, 64, nameof(id));
            UiControlText.Require(label, 32, nameof(label));
            Id = id;
            Label = label;
            Child = child ?? throw new ArgumentNullException(nameof(child));
        }

        /// <summary>Gets the stable ID used for selection and retention.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string Id { get; }

        /// <summary>Gets the visible accessible label.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string Label { get; }

        /// <summary>Gets the child tree displayed when this tab is selected.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiNode Child { get; }
    }

    /// <summary>Displays one page of a tab group at a time with local selection.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class UiTabs : UiNode
    {
        /// <summary>Creates a group of 2 to 12 tabs with distinct IDs and a known initial selection.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiTabs(string id, IEnumerable<UiTab> tabs, string selectedTabId,
            Action<string>? selected = null, UiTabsStyle style = UiTabsStyle.Bar, bool enabled = true)
            : base(id ?? throw new ArgumentNullException(nameof(id)))
        {
            if (tabs == null) throw new ArgumentNullException(nameof(tabs));
            if (selectedTabId == null) throw new ArgumentNullException(nameof(selectedTabId));
            if (style != UiTabsStyle.Bar && style != UiTabsStyle.Rail) throw new ArgumentOutOfRangeException(nameof(style));
            var pages = new List<UiTab>();
            var children = new List<UiNode>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tab in tabs)
            {
                if (pages.Count == 12 || tab == null || !ids.Add(tab.Id))
                    throw new ArgumentException("A tab group accepts 2 to 12 nonnull tabs with distinct IDs.", nameof(tabs));
                pages.Add(tab);
                children.Add(tab.Child);
            }

            if (pages.Count < 2) throw new ArgumentException("A tab group needs at least two tabs.", nameof(tabs));
            if (!ids.Contains(selectedTabId)) throw new ArgumentException("The selected tab must belong to the group.", nameof(selectedTabId));
            Tabs = pages.AsReadOnly();
            Children = children.AsReadOnly();
            Buttons = new UiButton[pages.Count];
            for (var index = 0; index < pages.Count; index++)
                Buttons[index] = new UiButton("tab-" + index.ToString(CultureInfo.InvariantCulture), pages[index].Label,
                    static () => { }, UiButtonStyle.Ghost, enabled);
            SelectedTabId = selectedTabId;
            Selected = selected;
            Style = style;
            Enabled = enabled;
        }

        /// <summary>Gets the tabs in reading order, including their retained child trees.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public IReadOnlyList<UiTab> Tabs { get; }

        /// <summary>Gets the authored selection. The player can change the visible page locally.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public string SelectedTabId { get; }

        /// <summary>Gets the optional callback for a player's change of tab.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public Action<string>? Selected { get; }

        /// <summary>Gets the position of the tab controls.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiTabsStyle Style { get; }

        /// <summary>Gets whether the player can change tabs.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public bool Enabled { get; }

        internal IReadOnlyList<UiNode> Children { get; }
        internal UiButton[] Buttons { get; }

        internal int IndexOf(string id)
        {
            for (var index = 0; index < Tabs.Count; index++)
                if (Tabs[index].Id == id) return index;
            return -1;
        }
    }

    internal readonly struct UiTabSelection
    {
        internal UiTabSelection(string authored, string active)
        {
            Authored = authored;
            Active = active;
        }

        internal string Authored { get; }
        internal string Active { get; }

        internal static UiTabSelection Restore(UiTabs tabs, UiTabSelection previous) =>
            previous.Authored == tabs.SelectedTabId && tabs.IndexOf(previous.Active) >= 0
                ? previous : new UiTabSelection(tabs.SelectedTabId, tabs.SelectedTabId);
    }

    /// <summary>Reusable compositions built from existing safe nodes.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public static class UiPatterns
    {
        /// <summary>Creates a card with a title, optional subtitle, and optional leading and trailing content.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public static UiNode ListTile(string title, string? subtitle = null, UiNode? leading = null,
            UiNode? trailing = null, bool selected = false, string? id = null)
        {
            var text = new UiText(title, UiTextStyle.Body);
            var captions = subtitle == null ? new UiColumn(text)
                : new UiColumn(text, new UiText(subtitle, UiTextStyle.Caption, UiTone.Muted));
            var row = new List<UiNode>(3);
            if (leading != null) row.Add(leading);
            row.Add(new UiExpanded(captions));
            if (trailing != null) row.Add(trailing);
            return new UiCard(new UiRow(row, new UiFlexLayout(UiSpace.Md, crossAxisAlignment: UiCrossAxisAlignment.Center)),
                selected: selected, padding: new UiInsets(UiSpace.Md), id: id);
        }
    }
}
