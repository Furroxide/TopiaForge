using System;
using System.Collections.Generic;
using TMPro;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Theme-aware, clipped graph editor. Node, port, and edge views are retained and
    /// pooled across <see cref="SetGraph"/> calls; drag and pan geometry updates reuse them.
    /// </summary>
    public sealed partial class TopiaForgeGraphCanvas : TopiaForgeWidget, ITopiaForgeThemeAware
    {
        internal const float NodeWidth = 216f;
        internal const float PortRowHeight = 22f;
        private const float NodeHeaderHeight = 62f;

        private readonly Image background;
        private readonly Image ring;
        private readonly RectTransform edgeLayer;
        private readonly RectTransform nodeLayer;
        private readonly TextMeshProUGUI emptyLabel;
        private readonly List<TopiaForgeGraphNodeView> nodeViews = new List<TopiaForgeGraphNodeView>();
        private readonly List<TopiaForgeGraphEdgeView> edgeViews = new List<TopiaForgeGraphEdgeView>();
        private readonly Dictionary<string, TopiaForgeGraphNodeView> nodesById =
            new Dictionary<string, TopiaForgeGraphNodeView>(StringComparer.Ordinal);
        private UiGraphCanvas graph;
        private UiGraphViewport viewport;
        private string? selectedNodeId;
        private string? pendingSourceNodeId;
        private string? pendingSourcePortId;
        private bool enabledState;

        internal TopiaForgeGraphCanvas(TopiaForgeContainer parent, UiGraphCanvas initialGraph)
            : base(parent.Host, parent.Scheme, parent.CreateChildGameObject("GraphCanvas"))
        {
            graph = initialGraph ?? throw new ArgumentNullException(nameof(initialGraph));
            viewport = initialGraph.Viewport;
            selectedNodeId = initialGraph.SelectedNodeId;
            enabledState = initialGraph.Enabled;

            background = Go.AddComponent<Image>();
            background.sprite = TopiaForgeSprites.Fill(TopiaForgeRadius.Card);
            background.type = Image.Type.Sliced;
            background.raycastTarget = true;
            Go.AddComponent<RectMask2D>();

            ring = CreateStretchedImage(
                Go.transform,
                "Ring",
                TopiaForgeSprites.Ring(TopiaForgeRadius.Card, TopiaForgeTokens.BorderStandard),
                raycast: false);

            edgeLayer = CreateLayer("Edges");
            nodeLayer = CreateLayer("Nodes");

            var emptyGo = new GameObject("EmptyState", typeof(RectTransform));
            emptyGo.transform.SetParent(Go.transform, false);
            emptyLabel = TopiaForgeTmp.Create(emptyGo);
            emptyLabel.fontSize = TopiaForgeTokens.BodySize;
            emptyLabel.alignment = TextAlignmentOptions.Center;
            emptyLabel.text = "NO GRAPH NODES";
            emptyLabel.raycastTarget = false;
            TopiaForgeAnchors.Stretch((RectTransform)emptyGo.transform, 24f, 24f, 24f, 24f);

            var input = Go.AddComponent<TopiaForgeGraphViewportInput>();
            input.Initialize(this);
            this.FixedHeight(initialGraph.Height);
            SetGraph(initialGraph);
        }

        /// <summary>Gets the current immutable graph description.</summary>
        public UiGraphCanvas Graph => graph;

        /// <summary>Gets the current viewport, including interactive pan and zoom changes.</summary>
        public UiGraphViewport Viewport => viewport;

        /// <summary>Gets the currently selected node id, or <c>null</c>.</summary>
        public string? SelectedNodeId => selectedNodeId;

        /// <summary>
        /// Rebinds the canvas to an immutable graph while reusing existing native node,
        /// port, and edge views wherever possible.
        /// </summary>
        public void SetGraph(UiGraphCanvas value)
        {
            graph = value ?? throw new ArgumentNullException(nameof(value));
            viewport = value.Viewport;
            selectedNodeId = value.SelectedNodeId;
            pendingSourceNodeId = null;
            pendingSourcePortId = null;
            enabledState = value.Enabled;
            this.FixedHeight(value.Height);

            nodesById.Clear();
            EnsureNodePool(value.Nodes.Count);
            for (var index = 0; index < nodeViews.Count; index++)
            {
                var active = index < value.Nodes.Count;
                var view = nodeViews[index];
                view.SetActive(active);
                if (!active) continue;
                var node = value.Nodes[index];
                view.Bind(node);
                nodesById.Add(node.Id, view);
            }

            EnsureEdgePool(value.Edges.Count);
            for (var index = 0; index < edgeViews.Count; index++)
            {
                var active = index < value.Edges.Count;
                var view = edgeViews[index];
                view.SetActive(active);
                if (active) view.Bind(value.Edges[index]);
            }

            emptyLabel.gameObject.SetActive(value.Nodes.Count == 0);
            RenderGeometry();
            ApplyTheme(Theme);
        }

        /// <summary>Updates the graph viewport without replacing or reallocating graph views.</summary>
        public void SetViewport(UiGraphViewport value)
        {
            viewport = value;
            RenderGeometry();
        }

        /// <summary>Updates selection without invoking the graph's selection callback.</summary>
        public void SetSelectedNode(string? nodeId)
        {
            if (nodeId != null && !nodesById.ContainsKey(nodeId))
            {
                throw new ArgumentException("The selected graph node was not found.", nameof(nodeId));
            }

            selectedNodeId = nodeId;
            ApplyTheme(Theme);
        }

        /// <summary>Dirty-checked graph interactability update.</summary>
        public void SetEnabled(bool value)
        {
            if (enabledState == value) return;
            enabledState = value;
            ApplyTheme(Theme);
        }

        /// <summary>Applies semantic theme colors, including high-contrast focus and status roles.</summary>
        public void ApplyTheme(TopiaForgeResolvedTheme theme)
        {
            background.color = theme.SurfaceSunken;
            ring.color = theme.OutlineStrong;
            emptyLabel.color = theme.TextMuted;
            foreach (var view in nodeViews)
            {
                view.ApplyTheme(
                    theme,
                    enabledState,
                    string.Equals(view.NodeId, selectedNodeId, StringComparison.Ordinal),
                    string.Equals(view.NodeId, pendingSourceNodeId, StringComparison.Ordinal),
                    pendingSourcePortId);
            }

            foreach (var view in edgeViews) view.ApplyTheme(theme, enabledState);
        }

        internal void HandleNodeSelected(string nodeId)
        {
            if (!enabledState || !nodesById.TryGetValue(nodeId, out var view) || !view.Enabled) return;
            selectedNodeId = string.Equals(selectedNodeId, nodeId, StringComparison.Ordinal) ? null : nodeId;
            ApplyTheme(Theme);
            TopiaForgeCallbacks.Invoke(graph.SelectionChanged, selectedNodeId, "Graph selection");
        }

        internal void HandlePortSelected(string nodeId, UiGraphPort port)
        {
            if (!enabledState || !nodesById.TryGetValue(nodeId, out var node) || !node.Enabled) return;
            if (port.Direction == UiGraphPortDirection.Output)
            {
                pendingSourceNodeId = nodeId;
                pendingSourcePortId = port.Id;
                selectedNodeId = nodeId;
                ApplyTheme(Theme);
                TopiaForgeCallbacks.Invoke(graph.SelectionChanged, selectedNodeId, "Graph selection");
                return;
            }

            if (pendingSourceNodeId == null || pendingSourcePortId == null || graph.ConnectionRequested == null)
            {
                HandleNodeSelected(nodeId);
                return;
            }

            var sourcePort = FindPort(pendingSourceNodeId, pendingSourcePortId);
            if (sourcePort != null && string.Equals(sourcePort.DataType, port.DataType, StringComparison.Ordinal))
            {
                var request = new UiGraphConnectionRequest(pendingSourceNodeId, pendingSourcePortId, nodeId, port.Id);
                pendingSourceNodeId = null;
                pendingSourcePortId = null;
                ApplyTheme(Theme);
                TopiaForgeCallbacks.Invoke(graph.ConnectionRequested, request, "Graph connection request");
                return;
            }

            pendingSourceNodeId = null;
            pendingSourcePortId = null;
            ApplyTheme(Theme);
        }

        internal void HandleEdgeSelected(string edgeId)
        {
            if (!enabledState || graph.ConnectionRemoved == null) return;
            TopiaForgeCallbacks.Invoke(graph.ConnectionRemoved, edgeId, "Graph connection removal");
        }

        internal void HandleNodeDrag(string nodeId, Vector2 screenDelta)
        {
            if (!enabledState || graph.NodeMoved == null ||
                !nodesById.TryGetValue(nodeId, out var view) || !view.Enabled) return;
            var scale = CanvasScale();
            var next = view.GraphPosition + screenDelta / (scale * viewport.Zoom);
            next.x = Mathf.Clamp(next.x, -100000f, 100000f);
            next.y = Mathf.Clamp(next.y, -100000f, 100000f);
            view.SetGraphPosition(next);
            RenderGeometry();
        }

        internal void HandleNodeDragEnd(string nodeId)
        {
            if (!enabledState || graph.NodeMoved == null || !nodesById.TryGetValue(nodeId, out var view)) return;
            var position = view.GraphPosition;
            TopiaForgeCallbacks.Invoke(
                graph.NodeMoved,
                new UiGraphNodeMove(nodeId, new Vec2(position.x, position.y)),
                "Graph node move");
        }

        internal void HandlePan(Vector2 screenDelta)
        {
            if (!enabledState) return;
            var delta = screenDelta / (CanvasScale() * viewport.Zoom);
            var offset = viewport.Offset;
            viewport = new UiGraphViewport(
                new Vec2(
                    Mathf.Clamp(offset.X + delta.x, -100000f, 100000f),
                    Mathf.Clamp(offset.Y + delta.y, -100000f, 100000f)),
                viewport.Zoom);
            RenderGeometry();
        }

        internal void HandlePanEnd()
        {
            if (enabledState && graph.ViewportChanged != null)
            {
                TopiaForgeCallbacks.Invoke(graph.ViewportChanged, viewport, "Graph viewport pan");
            }
        }

        internal void HandleZoom(float direction)
        {
            if (!enabledState || Math.Abs(direction) < 0.001f) return;
            var zoom = Mathf.Clamp(
                viewport.Zoom + Mathf.Sign(direction) * 0.1f,
                UiGraphViewport.MinimumZoom,
                UiGraphViewport.MaximumZoom);
            if (Math.Abs(zoom - viewport.Zoom) < 0.001f) return;
            viewport = new UiGraphViewport(viewport.Offset, zoom);
            RenderGeometry();
            if (graph.ViewportChanged != null)
            {
                TopiaForgeCallbacks.Invoke(graph.ViewportChanged, viewport, "Graph viewport zoom");
            }
        }

        internal void HandleCanvasSelected()
        {
            if (!enabledState || selectedNodeId == null) return;
            selectedNodeId = null;
            pendingSourceNodeId = null;
            pendingSourcePortId = null;
            ApplyTheme(Theme);
            TopiaForgeCallbacks.Invoke(graph.SelectionChanged, null, "Graph selection");
        }

        internal Color ToneColor(TopiaForgeResolvedTheme theme, UiTone tone)
        {
            return tone switch
            {
                UiTone.Success => theme.Success,
                UiTone.Warning => theme.Warning,
                UiTone.Danger => theme.Danger,
                _ => theme.Primary,
            };
        }

        private void EnsureNodePool(int count)
        {
            while (nodeViews.Count < count) nodeViews.Add(new TopiaForgeGraphNodeView(this, nodeLayer));
        }

        private void EnsureEdgePool(int count)
        {
            while (edgeViews.Count < count) edgeViews.Add(new TopiaForgeGraphEdgeView(this, edgeLayer));
        }

        private void RenderGeometry()
        {
            foreach (var view in nodeViews)
            {
                if (!view.Active) continue;
                var position = view.GraphPosition;
                view.Rect.anchoredPosition = new Vector2(
                    (position.x + viewport.Offset.X) * viewport.Zoom,
                    (position.y + viewport.Offset.Y) * viewport.Zoom);
            }

            foreach (var edge in edgeViews)
            {
                if (!edge.Active) continue;
                if (nodesById.TryGetValue(edge.SourceNodeId, out var source) &&
                    nodesById.TryGetValue(edge.TargetNodeId, out var target))
                {
                    edge.SetGeometry(source.Rect.anchoredPosition, target.Rect.anchoredPosition);
                }
            }
        }

        private UiGraphPort? FindPort(string nodeId, string portId)
        {
            if (!nodesById.TryGetValue(nodeId, out var view)) return null;
            foreach (var port in view.Node.Ports)
            {
                if (string.Equals(port.Id, portId, StringComparison.Ordinal)) return port;
            }

            return null;
        }

        private float CanvasScale()
        {
            var canvas = Go.GetComponentInParent<Canvas>();
            return canvas != null && canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
        }

        private RectTransform CreateLayer(string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(Go.transform, false);
            var rect = (RectTransform)go.transform;
            TopiaForgeAnchors.Stretch(rect);
            return rect;
        }

        private static Image CreateStretchedImage(Transform parent, string name, Sprite sprite, bool raycast)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            image.raycastTarget = raycast;
            TopiaForgeAnchors.Stretch((RectTransform)go.transform);
            return image;
        }

        internal static float HeightFor(UiGraphNode node)
        {
            var inputs = 0;
            var outputs = 0;
            foreach (var port in node.Ports)
            {
                if (port.Direction == UiGraphPortDirection.Input) inputs++;
                else outputs++;
            }

            return NodeHeaderHeight + Math.Max(1, Math.Max(inputs, outputs)) * PortRowHeight + 10f;
        }
    }
}
