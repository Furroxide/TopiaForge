using System.Collections.Generic;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Immutable explicit observation of one UI owner and process-wide resource baselines.</summary>
    public sealed class TopiaForgeUiDiagnosticSnapshot
    {
        internal TopiaForgeUiDiagnosticSnapshot(string owner, IReadOnlyList<TopiaForgeUiDiagnosticWidget> widgets, int hosts,
            int ownerCanvases, int canvases, int subscribers, int cursors, int dismiss, int width, int height)
        {
            OwnerId = owner; Widgets = widgets; HostCount = hosts; OwnerCanvasCount = ownerCanvases; TotalCanvasCount = canvases;
            ThemeSubscriberCount = subscribers; CursorLeaseCount = cursors; DismissScopeCount = dismiss; Width = width; Height = height;
        }
        /// <summary>Observed owner.</summary>
        public string OwnerId { get; }
        /// <summary>Measured widgets, including hidden and clipped widgets.</summary>
        public IReadOnlyList<TopiaForgeUiDiagnosticWidget> Widgets { get; }
        /// <summary>Live hosts belonging to this owner.</summary>
        public int HostCount { get; }
        /// <summary>Live canvases named by this owner, including hidden canvases.</summary>
        public int OwnerCanvasCount { get; }
        /// <summary>All scene canvases; compare a captured baseline in an isolated Editor fixture.</summary>
        public int TotalCanvasCount { get; }
        /// <summary>Actual global theme event subscribers.</summary>
        public int ThemeSubscriberCount { get; }
        /// <summary>Actual global cursor leases.</summary>
        public int CursorLeaseCount { get; }
        /// <summary>Actual global dismiss scopes.</summary>
        public int DismissScopeCount { get; }
        /// <summary>Screen width in pixels.</summary>
        public int Width { get; }
        /// <summary>Screen height in pixels.</summary>
        public int Height { get; }
    }
    /// <summary>Primitive geometry and semantics of an actual toolkit widget; contains no actuation handle.</summary>
    public sealed class TopiaForgeUiDiagnosticWidget
    {
        internal TopiaForgeUiDiagnosticWidget(string surface, string id, string kind, string text, string style,
            float x, float y, float width, float height, bool visible, bool enabled, bool focused, bool clipped,
            bool contrast, bool reduced, float scale, float motion,
            bool selected = false, string? value = null, string? foreground = null, string? background = null)
        {
            SurfaceId = surface; NodeId = id; Kind = kind; Text = TopiaForgeUiDiagnosticFormat.Bound(text); Style = style;
            X = x; Y = y; Width = width; Height = height;
            Visible = visible; Enabled = enabled; Focused = focused; Clipped = clipped; HighContrast = contrast;
            ReducedMotion = reduced; UiScale = scale; MotionIntensity = motion;
            Selected = selected; Value = TopiaForgeUiDiagnosticFormat.Bound(value);
            Foreground = foreground ?? string.Empty; Background = background ?? string.Empty;
        }
        /// <summary>Safe SDK surface id; modals use $modal.</summary>
        public string SurfaceId { get; }
        /// <summary>Safe SDK node id; virtual rows use list-id/item-id.</summary>
        public string NodeId { get; }
        /// <summary>Renderer control kind.</summary>
        public string Kind { get; }
        /// <summary>Current text, bounded to 256 characters.</summary>
        public string Text { get; }
        /// <summary>Declared toolkit style.</summary>
        public string Style { get; }
        /// <summary>Left edge in bottom-left screen pixels.</summary>
        public float X { get; }
        /// <summary>Bottom edge in bottom-left screen pixels.</summary>
        public float Y { get; }
        /// <summary>Measured width.</summary>
        public float Width { get; }
        /// <summary>Measured height.</summary>
        public float Height { get; }
        /// <summary>Active through the hierarchy and canvas alpha.</summary>
        public bool Visible { get; }
        /// <summary>Visible and interactable through the hierarchy.</summary>
        public bool Enabled { get; }
        /// <summary>Owns the current EventSystem selection.</summary>
        public bool Focused { get; }
        /// <summary>Bounds extend outside the viewport or an active ancestor mask.</summary>
        public bool Clipped { get; }
        /// <summary>Effective host high contrast.</summary>
        public bool HighContrast { get; }
        /// <summary>Effective host reduced motion.</summary>
        public bool ReducedMotion { get; }
        /// <summary>Effective host UI scale.</summary>
        public float UiScale { get; }
        /// <summary>Effective host motion intensity.</summary>
        public float MotionIntensity { get; }
        /// <summary>List rows: the rendered selected state of the row. Other widgets: false.</summary>
        public bool Selected { get; }
        /// <summary>
        /// Dropdown: caption text of the current option. Toggle: "true"/"false". Slider: invariant value text.
        /// Input: current text. Otherwise empty. Bounded to 256 characters.
        /// </summary>
        public string Value { get; }
        /// <summary>#rrggbb of the first rendered TMP_Text in the widget subtree, or empty.</summary>
        public string Foreground { get; }
        /// <summary>
        /// #rrggbb of the widget's own Image when its alpha is at least 0.5, else of the nearest ancestor Image
        /// with alpha at least 0.5, or empty.
        /// </summary>
        public string Background { get; }
    }
}
