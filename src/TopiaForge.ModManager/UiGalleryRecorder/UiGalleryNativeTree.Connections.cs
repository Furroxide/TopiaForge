using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class UiGalleryNativeTree
    {
        private bool GraphConnectionsLauncher(TopiaForgeGraphConnections connections)
        {
            var launcher = connections.Launcher;
            if (!launcher.Button.gameObject.activeInHierarchy) return false;
            var bar = launcher.Rect.parent?.parent?.GetComponent<Image>();
            return bar != null && GraphConnectionControl("/graph-connections", null, "GraphConnectionsAction", launcher,
                UiGraphConnections.LauncherLabel, UiGraphConnections.LauncherLabel, ToRgb(EffectiveColor(bar)), connections.IsOpen);
        }

        private bool GraphConnectionsList(UiNode fixture, TopiaForgeGraphConnections connections, UiThemeColor? backdrop)
        {
            if (connections.ActiveGraph?.Id is not { } graphId || !UiLayoutTree.TryFind(fixture, graphId, out var item, out _) ||
                item is not UiGraphCanvas graph || !connections.Go.activeInHierarchy) return false;
            var paper = connections.Go.GetComponent<Image>();
            if (paper == null || !paper.isActiveAndEnabled) return false;
            var background = Over(paper, backdrop);
            Add("/0", graphId, "GraphConnectionsList", connections.Rect, EmptyContent, new UiCaptureNodePaint(null, background));
            var selectedLabel = string.Empty;
            for (var slot = 0; slot < connections.Rows.Count; slot++)
            {
                var control = connections.Rows[slot];
                if (!control.Button.gameObject.activeInHierarchy) continue;
                var index = connections.First + slot;
                if (index >= graph.Edges.Count) return false;
                var edge = graph.Edges[index];
                var labels = UiGraphConnections.Labels(graph, edge);
                var full = labels.Full;
                var selected = edge.Id == connections.SelectedEdgeId;
                if (selected) selectedLabel = full;
                var pages = UiGraphConnectionLabelPages.Measure(labels, full, LocalSize(control), control.MeasureText);
                if (!GraphConnectionControl(ChildPath("/0", slot), edge.Id, "GraphConnectionRow", control,
                    pages.Display(selected ? connections.LabelPage : 0), full, background, selected,
                    labelFits: pages.Count != 0)) return false;
            }

            if (selectedLabel.Length == 0) return false;
            var remove = connections.RemoveControl;
            var semantic = UiGraphConnections.RemovePrefix + selectedLabel;
            var text = UiGraphConnectionLabelPages.RemoveCaption(semantic, LocalSize(remove), remove.MeasureText);
            return GraphConnectionControl("/0/remove", connections.SelectedEdgeId, "GraphConnectionRemove",
                remove, text, semantic, background, selected: false, labelFits: text.Length != 0);
        }

        private static UiLayoutRect LocalSize(TopiaForgeGraphConnectionControl control)
        {
            var rect = control.Rect.rect;
            return new UiLayoutRect(0d, 0d, rect.width, rect.height);
        }

        private bool GraphConnectionControl(string path, string? id, string kind, TopiaForgeGraphConnectionControl control,
            string display, string semantic, UiThemeColor? backdrop, bool selected, bool labelFits = true)
        {
            if (control.SemanticLabel != semantic || control.Button.targetGraphic is not { } fill) return false;
            var start = observations.Count;
            var color = Over(fill, backdrop);
            var flags = Flags(control.Button) | ControlMarks(path, control.Button, semantic, color, backdrop);
            if (selected) flags |= UiCaptureNodeFlags.Selected;
            if (!labelFits) flags |= UiCaptureNodeFlags.InvalidLayout;
            var result = Text(path, id, kind, Named<TMP_Text>(control.Rect, "Label"), display, UiTextStyle.Label,
                color, flags, Decoration(control.Rect, fill), semanticLabel: semantic);
            if (!result) return false;
            HitAndFocus(path, control.Button, semantic, backdrop);
            RegisterGroup(start);
            return true;
        }
    }
}
