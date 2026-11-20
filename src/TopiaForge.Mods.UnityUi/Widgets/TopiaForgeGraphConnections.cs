using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>One local graph action and pooled connection list for an owning surface.</summary>
    internal sealed partial class TopiaForgeGraphConnections : TopiaForgeWidget, ITopiaForgeThemeAware, ITopiaForgeDismissable
    {
        private readonly TopiaForgeContainer titleBar;
        private readonly TopiaForgeContainer content;
        private readonly GameObject launcherSlot;
        private readonly LayoutElement launcherSize;
        private readonly LayoutElement titleBarSize;
        private readonly float originalTitleHeight;
        private readonly ScrollRect scroll;
        private readonly CanvasGroup authoredContent;
        private readonly Image paper;
        private readonly TopiaForgeGraphConnectionControl launcher;
        private readonly TopiaForgeGraphConnectionControl remove;
        private readonly List<TopiaForgeGraphConnectionControl> rows = new List<TopiaForgeGraphConnectionControl>();
        private readonly UiGraphConnections selection = new UiGraphConnections();
        private IReadOnlyDictionary<string, TopiaForgeGraphCanvas>? graphs;
        private TopiaForgeGraphCanvas? focusedGraph;
        private GameObject? graphFocus;
        private TopiaForgeGraphCanvas? activeGraph;
        private UiGraphConnectionsViewport viewport;
        private Vector2 lastAperture;
        private float lastTitleWidth = float.NaN;
        private float launcherTextWidth;
        private bool launcherShown;
        private bool layoutSuspended;
        private float suspendedTitleHeight;

        internal TopiaForgeGraphConnections(TopiaForgeContainer titleBar, TopiaForgeContainer content, ScrollRect scroll)
            : base(content.Host, content.Scheme, content.CreateChildGameObject("Graph connections list"))
        {
            this.titleBar = titleBar;
            this.content = content;
            this.scroll = scroll;
            authoredContent = scroll.gameObject.AddComponent<CanvasGroup>();
            EnsureLayoutElement().ignoreLayout = true;
            TopiaForgeAnchors.Stretch(Rect);
            paper = Go.AddComponent<Image>();
            paper.raycastTarget = true;
            titleBar.Go.AddComponent<TopiaForgeGraphConnectionsFrame>().Initialize(ObserveViewport);
            Go.AddComponent<TopiaForgeGraphConnectionsScroll>().Initialize(Scroll);

            launcherSlot = titleBar.CreateChildGameObject("Graph connections action");
            launcherSlot.transform.SetSiblingIndex(titleBar.Rect.childCount - 2);
            launcherSize = launcherSlot.AddComponent<LayoutElement>();
            titleBarSize = titleBar.EnsureLayoutElement();
            originalTitleHeight = titleBarSize.preferredHeight;
            launcher = new TopiaForgeGraphConnectionControl(this, launcherSlot.transform, "Connections", Toggle, MoveLauncher);
            launcher.SetText(UiGraphConnections.LauncherLabel);
            TopiaForgeAnchors.Stretch(launcher.Rect, UiGraphConnections.Gutter, UiGraphConnections.Gutter,
                UiGraphConnections.Gutter, UiGraphConnections.Gutter);

            remove = new TopiaForgeGraphConnectionControl(this, Go.transform, "Remove connection", Remove, MoveRemove);
            launcherSlot.SetActive(false);
            Go.SetActive(false);
            ApplyTheme(Theme);
        }

        TopiaForgeLayerBand ITopiaForgeDismissable.Band => TopiaForgeLayerBand.Window;

        void ITopiaForgeDismissable.Dismiss() => Close(restoreFocus: true);

        internal TopiaForgeGraphConnectionControl Launcher => launcher;
        internal TopiaForgeGraphConnectionControl RemoveControl => remove;
        internal IReadOnlyList<TopiaForgeGraphConnectionControl> Rows => rows;
        internal bool IsOpen => activeGraph != null;
        internal UiGraphCanvas? ActiveGraph => selection.Graph;
        internal int First => selection.First;
        internal string? SelectedEdgeId => selection.Selected?.Id;
        internal int LabelPage => selection.LabelPage;

        internal void Bind(IReadOnlyDictionary<string, TopiaForgeGraphCanvas>? current)
        {
            if (current == null && !layoutSuspended) suspendedTitleHeight = titleBarSize.preferredHeight;
            layoutSuspended = current == null;
            if (graphs != null)
            {
                foreach (var item in graphs)
                    if (current == null || !current.TryGetValue(item.Key, out var next) || next != item.Value)
                        item.Value.ConnectionList = null;
            }

            graphs = current;
            if (current != null)
                foreach (var item in current) item.Value.ConnectionList = this;
            if (focusedGraph != null && !IsAdmitted(focusedGraph))
            {
                focusedGraph = null;
                graphFocus = null;
            }

            if (activeGraph is { } open)
            {
                if (IsAdmitted(open) && open.HasActionableConnections)
                {
                    if (selection.Bind(open.Graph))
                    {
                        var rowFocused = OwnsRowFocus();
                        RenderRows();
                        if (rowFocused) FocusRow();
                    }
                }
                else Close(restoreFocus: current != null);
            }

            RefreshLauncher();
        }

        internal void ObserveFocus(TopiaForgeGraphCanvas graph, GameObject? control)
        {
            if (!IsAdmitted(graph)) return;
            focusedGraph = graph;
            graphFocus = control;
            RefreshLauncher();
        }

        internal void Reset(bool retire)
        {
            if (Go != null) Close(restoreFocus: false);
            else TopiaForgeDismissStack.Remove(this);
            if (!retire) return;
            if (graphs != null)
            {
                foreach (var item in graphs)
                    if (item.Value.Go != null) item.Value.ConnectionList = null;
            }
            graphs = null;
            focusedGraph = null;
            graphFocus = null;
            activeGraph = null;
            selection.Bind(null);
        }

        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            paper.color = theme.Surface;
            PaintLauncher(theme);
            launcherTextWidth = launcher.PreferredWidth;
            lastTitleWidth = float.NaN;
            foreach (var row in rows) row.InvalidateLabel();
            RenderRows();
        }

        private bool IsAdmitted(TopiaForgeGraphCanvas graph) =>
            graphs != null && graphs.TryGetValue(graph.Graph.Id!, out var admitted) && ReferenceEquals(admitted, graph);

        private TopiaForgeGraphCanvas? FindFirstGraph()
        {
            if (graphs == null) return null;
            foreach (var item in graphs)
                if (item.Value.HasActionableConnections) return item.Value;
            return null;
        }

        private void RefreshLauncher()
        {
            launcherShown = FindFirstGraph() != null;
            PaintLauncher(Theme);
            UpdateLauncher();
        }

        private void PaintLauncher(TopiaForgeResolvedTheme theme) =>
            launcher.Paint(theme, Host.EffectiveUiScale, UiPaintRole.Raised,
                IsOpen || viewport.Rows > 0 && (focusedGraph?.HasActionableConnections ?? launcherShown), selected: IsOpen);

        private void UpdateLauncher()
        {
            var width = titleBar.Rect.rect.width;
            lastTitleWidth = width;
            var height = UiGraphConnectionsViewport.RowSlot;
            var preferred = (float)UiGraphConnectionsViewport.LauncherWidth(width, launcherTextWidth);
            var shown = launcherShown && preferred > 0f;
            // Keep the reserved title height until the underlying viewport genuinely recovers.
            var barHeight = layoutSuspended ? suspendedTitleHeight :
                shown ? Math.Max(originalTitleHeight, height) : originalTitleHeight;
            titleBarSize.minHeight = titleBarSize.preferredHeight = barHeight;
            if (shown)
            {
                launcherSize.minWidth = launcherSize.preferredWidth = preferred;
                launcherSize.minHeight = launcherSize.preferredHeight = height;
            }

            if (launcherSlot.activeSelf != shown) launcherSlot.SetActive(shown);
        }

        private void Toggle()
        {
            ObserveViewport();
            if (IsOpen)
            {
                Close(restoreFocus: true);
                return;
            }

            var next = focusedGraph ?? FindFirstGraph();
            if (next == null || !next.HasActionableConnections) return;
            if (viewport.Rows == 0) return;

            activeGraph = next;
            selection.Bind(next.Graph);
            selection.Resize(viewport.Rows);
            scroll.StopMovement();
            authoredContent.alpha = 0f;
            authoredContent.interactable = authoredContent.blocksRaycasts = false;
            SetShown(true);
            RenderRows();
            PaintLauncher(Theme);
            TopiaForgeDismissStack.Push(this, TopiaForgeInputClaim.Back, Go);
            FocusRow();
        }

        private void Close(bool restoreFocus)
        {
            var prior = activeGraph;
            if (prior == null) return;
            var selected = EventSystem.current?.currentSelectedGameObject;
            var ownsFocus = selected != null && (selected == launcher.Button.gameObject || selected.transform.IsChildOf(Go.transform));
            activeGraph = null;
            selection.Bind(null);
            TopiaForgeDismissStack.Remove(this);
            ReleaseFocus();
            SetShown(false);
            authoredContent.alpha = 1f;
            authoredContent.interactable = authoredContent.blocksRaycasts = true;
            PaintLauncher(Theme);
            if (!restoreFocus || !ownsFocus) return;

            var graph = IsAdmitted(prior) ? prior : FindFirstGraph();
            if (graph != null && graph.RestoreConnectionsFocus(ReferenceEquals(graph, focusedGraph) ? graphFocus : null)) return;
            if (launcher.Button.IsActive() && launcher.Button.IsInteractable()) launcher.Button.Select();
        }

        private void ObserveViewport()
        {
            var width = titleBar.Rect.rect.width;
            if (width != lastTitleWidth) UpdateLauncher();
            var aperture = content.Rect.rect.size;
            if (aperture.Equals(lastAperture)) return;
            lastAperture = aperture;
            viewport = new UiGraphConnectionsViewport(aperture.x, aperture.y);
            if (!IsOpen)
            {
                PaintLauncher(Theme);
                return;
            }
            if (viewport.Rows == 0)
            {
                Close(restoreFocus: true);
                return;
            }

            var rowFocused = OwnsRowFocus();
            selection.Resize(viewport.Rows);
            RenderRows();
            if (rowFocused) FocusRow();
        }

        private void FocusRow()
        {
            var slot = selection.SelectedIndex - selection.First;
            if (slot >= 0 && slot < selection.DrawnRows && Go.activeInHierarchy) rows[slot].Button.Select();
        }

        private bool OwnsRowFocus()
        {
            var selected = EventSystem.current?.currentSelectedGameObject;
            return selected != null && selected != remove.Button.gameObject && selected.transform.IsChildOf(Go.transform);
        }

        private void Remove()
        {
            var graph = activeGraph;
            if (graph == null || !IsAdmitted(graph) || !remove.Button.interactable || selection.Selected is not { } edge) return;
            graph.HandleEdgeSelected(edge.Id);
            if (ReferenceEquals(activeGraph, graph) && EventSystem.current?.currentSelectedGameObject == remove.Button.gameObject)
                FocusRow();
        }

        private bool MoveRow(MoveDirection direction)
        {
            switch (direction)
            {
                case MoveDirection.Up:
                    if (selection.SelectedIndex <= 0) launcher.Button.Select();
                    else Scroll(-1);
                    return true;
                case MoveDirection.Down:
                    Scroll(1);
                    return true;
                case MoveDirection.Right:
                    if (remove.Button.IsInteractable()) remove.Button.Select();
                    return true;
                case MoveDirection.Left:
                    launcher.Button.Select();
                    return true;
                default:
                    return false;
            }
        }

        private bool MoveRemove(MoveDirection direction)
        {
            if (direction != MoveDirection.Left && direction != MoveDirection.Up) return false;
            FocusRow();
            return true;
        }

        private bool MoveLauncher(MoveDirection direction)
        {
            if (!IsOpen || direction != MoveDirection.Down) return false;
            FocusRow();
            return true;
        }

        private void Scroll(int step)
        {
            if (!IsOpen || !selection.Move(step)) return;
            RenderRows();
            FocusRow();
        }

        private static void Place(RectTransform rect, UiLayoutRect box) =>
            TopiaForgeGraphNodeView.SetControlBox(rect, ((float)box.X, (float)box.Y, (float)box.Width, (float)box.Height));
    }

    internal sealed class TopiaForgeGraphConnectionsFrame : MonoBehaviour
    {
        private Action? update;

        internal void Initialize(Action refresh) => update = refresh;

        private void LateUpdate() => update?.Invoke();
    }

    internal sealed class TopiaForgeGraphConnectionsScroll : MonoBehaviour, IScrollHandler
    {
        private Action<int>? scroll;

        internal void Initialize(Action<int> move) => scroll = move;

        public void OnScroll(PointerEventData eventData) => scroll?.Invoke(-Math.Sign(eventData.scrollDelta.y));
    }
}
