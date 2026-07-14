using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// A widget that hosts children and exposes the authoring factories, so consumer
    /// code reads top-down: create a container, then col.Label(...), row.Button(...).
    /// </summary>
    public class TopiaForgeContainer : TopiaForgeWidget
    {
        public TopiaForgeContainer(UiHost host, TopiaForgeScheme scheme, GameObject go)
            : base(host, scheme, go)
        {
        }

        // ---- structure ----

        /// <summary>Vertical stack with token gap/padding.</summary>
        public TopiaForgeContainer Column(TopiaForgeGap gap = TopiaForgeGap.Sm, TopiaForgeGap padding = TopiaForgeGap.None, bool expandChildWidth = true)
        {
            var child = CreateChild("Column");
            TopiaForgeLayout.ApplyColumn(child.Go, gap, padding, expandChildWidth);
            return child;
        }

        /// <summary>Horizontal row with token gap/padding.</summary>
        public TopiaForgeContainer Row(TopiaForgeGap gap = TopiaForgeGap.Sm, TopiaForgeGap padding = TopiaForgeGap.None, bool expandChildWidth = false)
        {
            var child = CreateChild("Row");
            TopiaForgeLayout.ApplyRow(child.Go, gap, padding, expandChildWidth);
            return child;
        }

        /// <summary>No layout management — children place themselves (HUD free rects).</summary>
        public TopiaForgeContainer Stack(string name = "Stack")
        {
            var child = CreateChild(name);
            TopiaForgeAnchors.Stretch(child.Rect);
            return child;
        }

        /// <summary>Fixed-cell grid.</summary>
        public TopiaForgeContainer Grid(float cellWidth, float cellHeight, TopiaForgeGap gap = TopiaForgeGap.Sm, TopiaForgeGap padding = TopiaForgeGap.None)
        {
            var child = CreateChild("Grid");
            TopiaForgeLayout.ApplyGrid(child.Go, cellWidth, cellHeight, gap, padding);
            return child;
        }

        /// <summary>Flexible empty space inside a Row/Column.</summary>
        public TopiaForgeWidget Spacer(float flex = 1f)
        {
            var child = CreateChild("Spacer");
            return child.Flex(flex, flex);
        }

        // ---- widgets ----

        public TopiaForgeLabel Label(string text, TopiaForgeTextStyle style = TopiaForgeTextStyle.Body)
        {
            return new TopiaForgeLabel(this, text, style);
        }

        public TopiaForgeLabel Label(TopiaForgeTextStyle style)
        {
            return new TopiaForgeLabel(this, string.Empty, style);
        }

        public TopiaForgeButton Button(string text, System.Action onClick, TopiaForgeButtonStyle style = TopiaForgeButtonStyle.Filled)
        {
            return new TopiaForgeButton(this, text, onClick, style);
        }

        public TopiaForgeButton IconButton(TopiaForgeIcon icon, System.Action onClick, TopiaForgeButtonStyle style = TopiaForgeButtonStyle.Ghost)
        {
            return new TopiaForgeButton(this, icon, onClick, style);
        }

        public TopiaForgePanel Panel(TopiaForgePanelStyle style = TopiaForgePanelStyle.Plain)
        {
            return new TopiaForgePanel(this, style);
        }

        /// <summary>Free-placed raw image (reticles, vignettes, flashes). Not layout-managed.</summary>
        public TopiaForgeImage FreeImage(string name = "Image")
        {
            return new TopiaForgeImage(this, name, free: true);
        }

        /// <summary>Layout-managed raw image.</summary>
        public TopiaForgeImage Image(string name = "Image")
        {
            return new TopiaForgeImage(this, name, free: false);
        }

        /// <summary>1px brand divider line.</summary>
        public TopiaForgeImage Divider()
        {
            var divider = new TopiaForgeImage(this, "Divider", free: false);
            divider.FixedHeight(TopiaForgeTokens.BorderHairline);
            divider.SetColor(Theme.Tint);
            return divider;
        }

        public TopiaForgeToggle Toggle(string label, bool value, System.Action<bool> onChanged)
        {
            return new TopiaForgeToggle(this, label, value, onChanged, asCheckbox: false);
        }

        public TopiaForgeToggle Checkbox(string label, bool value, System.Action<bool> onChanged)
        {
            return new TopiaForgeToggle(this, label, value, onChanged, asCheckbox: true);
        }

        public TopiaForgeSlider Slider(string label, float min, float max, float value, System.Action<float> onChanged)
        {
            return new TopiaForgeSlider(this, label, min, max, value, onChanged);
        }

        public TopiaForgeTabs Tabs(params string[] labels)
        {
            return new TopiaForgeTabs(this, labels, navRail: false);
        }

        public TopiaForgeTabs NavRail(params string[] labels)
        {
            return new TopiaForgeTabs(this, labels, navRail: true);
        }

        public TopiaForgeInputField Input(string placeholder, string value, System.Action<string> onChanged)
        {
            return new TopiaForgeInputField(this, placeholder, value, onChanged);
        }

        public TopiaForgeInputField SearchInput(string placeholder, System.Action<string> onChanged)
        {
            return new TopiaForgeInputField(this, placeholder, string.Empty, onChanged).Search();
        }

        public TopiaForgeBadge Badge(string text, TopiaForgeTone tone = TopiaForgeTone.Neutral)
        {
            return new TopiaForgeBadge(this, text, tone);
        }

        public TopiaForgeScrollView Scroll(TopiaForgeGap contentGap = TopiaForgeGap.Sm, TopiaForgeGap contentPadding = TopiaForgeGap.None)
        {
            return new TopiaForgeScrollView(this, contentGap, contentPadding);
        }

        public TopiaForgeSectionHeader SectionHeader(string title)
        {
            return new TopiaForgeSectionHeader(this, title);
        }

        public TopiaForgeKeyValueRow KeyValueRow(string key, string value)
        {
            return new TopiaForgeKeyValueRow(this, key, value);
        }

        public TopiaForgeListRow ListRow()
        {
            return new TopiaForgeListRow(this);
        }

        public TopiaForgeProgressBar ProgressBar()
        {
            return new TopiaForgeProgressBar(this, "Progress");
        }

        public TopiaForgeStatBar StatBar(string title)
        {
            return new TopiaForgeStatBar(this, title);
        }

        public TopiaForgePipRow PipRow()
        {
            return new TopiaForgePipRow(this);
        }

        public TopiaForgeKeybindField Keybind(string label, TopiaForgeKey value, System.Action<TopiaForgeKey> onChanged)
        {
            return new TopiaForgeKeybindField(this, label, value, onChanged);
        }

        public TopiaForgeDropdown Dropdown(System.Collections.Generic.IReadOnlyList<string> options, int selected, System.Action<int> onChanged)
        {
            return new TopiaForgeDropdown(this, options, selected, onChanged);
        }

        /// <summary>
        /// Clickable preview card (thumbnail + caption + optional badge) for grids and
        /// galleries. See TopiaForgeCard for the grid-pooling SetActive caveat.
        /// </summary>
        public TopiaForgeCard Card(string title, System.Action onClick)
        {
            return new TopiaForgeCard(this, title, onClick);
        }

        public TopiaForgeListView<T> ListView<T>(float rowHeight = TopiaForgeTokens.ListRowHeight)
        {
            return new TopiaForgeListView<T>(this, rowHeight);
        }

        internal TopiaForgeContainer CreateChild(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(Go.transform, false);
            return new TopiaForgeContainer(Host, Scheme, go);
        }

        internal GameObject CreateChildGameObject(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(Go.transform, false);
            return go;
        }
    }
}
