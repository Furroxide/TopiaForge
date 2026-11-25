namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiSurfaceReapplyTests
    {
        private const string GraphBoundaries = """
            using System;
            using System.Collections.Generic;
            using TopiaForge.Mods;
            using UnityEngine;
            using UnityEngine.UI;

            namespace TopiaForge.Mods.UnityUi
            {
                internal sealed class TopiaForgeListViewRuntime : MonoBehaviour
                {
                    internal double RequireRowsFit(double width) => 0d;
                    internal void PlaceRows(double width) { }
                }

                public sealed partial class TopiaForgeGraphCanvas : TopiaForgeWidget
                {
                    private UiGraphCanvas graph = null!;
                    private UiGraphViewport viewport;
                    private string? selectedNodeId, pendingSourceNodeId, pendingSourcePortId;
                    private bool enabledState;
                    private readonly List<GraphNodeView> nodeViews = new();

                    internal TopiaForgeGraphCanvas(TopiaForgeContainer parent, UiGraphCanvas initial)
                        : base(parent.Host, parent.CreateChildGameObject("GraphCanvas")) => SetGraph(initial);

                    internal UiGraphCanvas Graph => graph;
                    internal UiGraphViewport Viewport => viewport;
                    internal string? SelectedNodeId => selectedNodeId;
                    internal string? PendingPort => pendingSourcePortId;
                    internal bool Enabled => enabledState;
                    internal Vector2 FirstPosition => nodeViews[0].GraphPosition;
                    internal GameObject FirstHeader => nodeViews[0].Header;
                    internal GameObject FirstPort => nodeViews[0].Port;
                    internal int BindCount { get; private set; }
                    private TopiaForgeResolvedTheme Theme => ResolvedTheme;

                    internal void SetGraph(UiGraphCanvas replacement)
                    {
                        BindCount++;
                        graph = replacement;
                        viewport = replacement.Viewport;
                        selectedNodeId = replacement.SelectedNodeId;
                        pendingSourceNodeId = null;
                        pendingSourcePortId = null;
                        enabledState = replacement.Enabled;
                        if (Host.FailNextGraphBind)
                        {
                            Host.FailNextGraphBind = false;
                            nodeViews[0].Port.Shown = false;
                            nodeViews[0].SetGraphPosition(default);
                            throw new InvalidOperationException("Native graph binding error fixture");
                        }
                        while (nodeViews.Count < replacement.Nodes.Count) nodeViews.Add(new GraphNodeView(Go));
                        for (var index = 0; index < nodeViews.Count; index++)
                        {
                            var view = nodeViews[index];
                            var shown = index < replacement.Nodes.Count;
                            view.Header.Shown = shown;
                            view.Port.Shown = shown && replacement.Nodes[index].Ports.Count != 0;
                            if (shown)
                            {
                                var position = replacement.Nodes[index].Position;
                                view.SetGraphPosition(new Vector2(position.X, position.Y));
                            }
                        }
                    }

                    internal void SetLiveState(UiGraphViewport camera, string selected, string pending, Vector2 drag)
                    {
                        viewport = camera;
                        selectedNodeId = selected;
                        pendingSourceNodeId = graph.Nodes[0].Id;
                        pendingSourcePortId = pending;
                        nodeViews[0].SetGraphPosition(drag);
                    }

                    private void RenderGeometry() { }
                    private void ApplyTheme(TopiaForgeResolvedTheme theme) { }

                    private sealed class GraphNodeView
                    {
                        internal GraphNodeView(GameObject parent)
                        {
                            Header.transform.SetParent(parent.transform, false);
                            Header.AddComponent<Selectable>();
                            Port.transform.SetParent(parent.transform, false);
                            Port.AddComponent<Selectable>();
                        }
                        internal GameObject Header { get; } = new();
                        internal GameObject Port { get; } = new();
                        internal Vector2 GraphPosition { get; private set; }
                        internal void SetGraphPosition(Vector2 value) => GraphPosition = value;
                    }
                }

                internal sealed class TopiaForgeControlFocusEvents : MonoBehaviour { }
                internal sealed class TopiaForgeRoundedImage : Image
                {
                    internal void SetShape(float? radius, float border = 0f) { }
                }
                internal enum TopiaForgeIcon { Check }
                internal static class TopiaForgeSprites
                {
                    internal static object? Icon(TopiaForgeIcon icon) => null;
                }
                internal static class TopiaForgeAnchors
                {
                    internal static void Stretch(RectTransform rect)
                    {
                        rect.anchorMin = default;
                        rect.anchorMax = new Vector2(1, 1);
                        rect.offsetMin = default;
                        rect.offsetMax = default;
                    }
                    internal static void Place(RectTransform rect, float x, float y, float width, float height)
                    {
                        rect.anchoredPosition = new Vector2(x, -y);
                        rect.rect = new Rect { width = width, height = height };
                    }
                }
            }
            """;
    }
}
