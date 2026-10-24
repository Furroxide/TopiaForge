using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// How a HUD panel's text column fits the panel. The column fills the panel inside its padding, so the title gets the
    /// panel's inner width and the body that width less the scroll bar's gutter, and a line wraps only when it is wider
    /// than that. Before, the column kept a new GameObject's 100 x 100 units, centred in the panel, so a short line such as
    /// "Shared count: 12" wrapped in a 260-unit panel. Sizes are in canvas units, which UI scale does not change.
    /// </summary>
    public readonly struct TopiaForgeHudColumn
    {
        private TopiaForgeHudColumn(float panelWidth, float panelHeight, float padding, float gutter)
        {
            PanelWidth = panelWidth;
            PanelHeight = panelHeight;
            Padding = padding;
            TextWidth = Math.Max(0f, panelWidth - (2f * padding));
            BodyWidth = Math.Max(0f, TextWidth - gutter);
            Height = Math.Max(0f, panelHeight - (2f * padding));
        }

        /// <summary>Gets the panel's width, as requested.</summary>
        public float PanelWidth { get; }

        /// <summary>Gets the panel's height, as requested.</summary>
        public float PanelHeight { get; }

        /// <summary>Gets the padding between the panel's edge and its text.</summary>
        public float Padding { get; }

        /// <summary>Gets the width the title has: the panel's inner width.</summary>
        public float TextWidth { get; }

        /// <summary>Gets the width the body text wraps at: the inner width less the scroll bar's gutter.</summary>
        public float BodyWidth { get; }

        /// <summary>Gets the height the title and body share; a taller body scrolls.</summary>
        public float Height { get; }

        /// <summary>Lays out the text column of a panel of the requested size, with the kit's HUD padding.</summary>
        public static TopiaForgeHudColumn For(float panelWidth, float panelHeight)
        {
            return For(panelWidth, panelHeight, (float)TopiaForgeGap.Md, TopiaForgeTokens.ScrollGutter);
        }

        /// <summary>Lays out the text column of a panel of the requested size.</summary>
        public static TopiaForgeHudColumn For(float panelWidth, float panelHeight, float padding, float gutter)
        {
            if (!(panelWidth > 0f) || !(panelHeight > 0f) || float.IsInfinity(panelWidth) || float.IsInfinity(panelHeight))
            {
                throw new ArgumentOutOfRangeException(nameof(panelWidth), "A HUD panel needs a positive, finite size.");
            }

            if (!(padding >= 0f) || !(gutter >= 0f))
            {
                throw new ArgumentOutOfRangeException(nameof(padding), "Padding and gutter cannot be negative.");
            }

            return new TopiaForgeHudColumn(panelWidth, panelHeight, padding, gutter);
        }

        /// <summary>Gets whether a line of the given width shows on one line of the body.</summary>
        public bool FitsOnOneLine(float lineWidth) => lineWidth <= BodyWidth;
    }

    /// <summary>
    /// Stacks the HUD panels docked at one corner or edge, so panels from different mods never overlap: the first shown
    /// sits at the safe margin and each later one rests past it, a small gap apart. Only shown panels take space, so the
    /// stack closes up when one hides. Panels on hosts with different UI scales stack by their size on screen. Positions
    /// come back in each panel's own canvas units, as anchored positions for a panel pivoted at its corner (or at the
    /// middle of its edge, for <see cref="TopiaForgeCorner.Left"/> and <see cref="TopiaForgeCorner.Right"/>).
    /// </summary>
    public sealed class TopiaForgeHudDock
    {
        /// <summary>The space between two stacked panels, at UI scale 1.</summary>
        public const float Gap = (float)TopiaForgeGap.Sm;

        private readonly List<Entry> entries = new List<Entry>();
        private int lastHandle;

        /// <summary>Gets a number that changes whenever any panel's position may have changed.</summary>
        public int Version { get; private set; }

        /// <summary>Gets how many panels the dock tracks, shown or not.</summary>
        public int Count => entries.Count;

        /// <summary>Adds a panel, hidden, after every panel already added, and returns its handle.</summary>
        public int Add(TopiaForgeCorner corner, float height, float uiScale)
        {
            if (corner == TopiaForgeCorner.Center)
            {
                throw new ArgumentOutOfRangeException(nameof(corner), "HUD panels dock to an edge or corner, never over the crosshair.");
            }

            if (!(height > 0f) || float.IsInfinity(height))
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            lastHandle = lastHandle == int.MaxValue ? 1 : lastHandle + 1;
            entries.Add(new Entry(lastHandle, corner, height, Scale(uiScale)));
            Version++;
            return lastHandle;
        }

        /// <summary>Shows or hides a panel; only shown panels take space.</summary>
        public void SetShown(int handle, bool shown)
        {
            var index = IndexOf(handle);
            if (index < 0 || entries[index].Shown == shown)
            {
                return;
            }

            entries[index].Shown = shown;
            Version++;
        }

        /// <summary>Sets the UI scale of a panel's host.</summary>
        public void SetScale(int handle, float uiScale)
        {
            var index = IndexOf(handle);
            var scale = Scale(uiScale);
            if (index < 0 || entries[index].UiScale == scale)
            {
                return;
            }

            entries[index].UiScale = scale;
            Version++;
        }

        /// <summary>Removes a panel; the ones after it close up.</summary>
        public void Remove(int handle)
        {
            var index = IndexOf(handle);
            if (index < 0)
            {
                return;
            }

            entries.RemoveAt(index);
            Version++;
        }

        /// <summary>
        /// Gets a shown panel's anchored position in its own canvas units, or false for a hidden or unknown panel. x is
        /// the safe margin in from its side (zero for a centred edge); y runs into the screen from its edge, past the
        /// shown panels before it at the same corner.
        /// </summary>
        public bool TryGetPosition(int handle, out float x, out float y)
        {
            x = 0f;
            y = 0f;
            var index = IndexOf(handle);
            if (index < 0 || !entries[index].Shown)
            {
                return false;
            }

            var entry = entries[index];
            var before = 0f;
            var total = 0f;
            var shownAtCorner = 0;
            for (var other = 0; other < entries.Count; other++)
            {
                var candidate = entries[other];
                if (!candidate.Shown || candidate.Corner != entry.Corner)
                {
                    continue;
                }

                var extent = candidate.Height * candidate.UiScale;
                if (other < index)
                {
                    before += extent + Gap;
                }

                total += extent + (shownAtCorner > 0 ? Gap : 0f);
                shownAtCorner++;
            }

            var margin = TopiaForgeTokens.SafeMargin;
            x = HorizontalSign(entry.Corner) * margin / entry.UiScale;
            switch (entry.Corner)
            {
                case TopiaForgeCorner.TopLeft:
                case TopiaForgeCorner.Top:
                case TopiaForgeCorner.TopRight:
                    y = -(margin + before) / entry.UiScale;
                    break;
                case TopiaForgeCorner.BottomLeft:
                case TopiaForgeCorner.Bottom:
                case TopiaForgeCorner.BottomRight:
                    y = (margin + before) / entry.UiScale;
                    break;
                default:
                    // Left and right: the stack is centred on the edge's middle, first panel at the top.
                    y = ((total / 2f) - before - (entry.Height * entry.UiScale / 2f)) / entry.UiScale;
                    break;
            }

            return true;
        }

        private static float HorizontalSign(TopiaForgeCorner corner)
        {
            switch (corner)
            {
                case TopiaForgeCorner.TopLeft:
                case TopiaForgeCorner.Left:
                case TopiaForgeCorner.BottomLeft:
                    return 1f;
                case TopiaForgeCorner.TopRight:
                case TopiaForgeCorner.Right:
                case TopiaForgeCorner.BottomRight:
                    return -1f;
                default:
                    return 0f;
            }
        }

        private static float Scale(float uiScale)
        {
            return float.IsNaN(uiScale) || uiScale < 0.5f ? 0.5f : uiScale > 2f ? 2f : uiScale;
        }

        private int IndexOf(int handle)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                if (entries[index].Handle == handle)
                {
                    return index;
                }
            }

            return -1;
        }

        private sealed class Entry
        {
            internal Entry(int handle, TopiaForgeCorner corner, float height, float uiScale)
            {
                Handle = handle;
                Corner = corner;
                Height = height;
                UiScale = uiScale;
            }

            internal int Handle { get; }

            internal TopiaForgeCorner Corner { get; }

            internal float Height { get; }

            internal float UiScale { get; set; }

            internal bool Shown { get; set; }
        }
    }
}
