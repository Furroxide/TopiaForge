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
                var label = UiGraphConnections.RowLabel(graph, edge);
                var selected = edge.Id == connections.SelectedEdgeId;
                if (selected) selectedLabel = label;
                if (!GraphConnectionControl(ChildPath("/0", slot), edge.Id, "GraphConnectionRow", control,
                    label, label, background, selected)) return false;
            }

            if (selectedLabel.Length == 0) return false;
            var remove = connections.RemoveControl;
            var semantic = UiGraphConnections.RemovePrefix + selectedLabel;
            var text = remove.DisplayLabel == "Remove" ? "Remove" : semantic;
            return GraphConnectionControl("/0/remove", connections.SelectedEdgeId, "GraphConnectionRemove",
                remove, text, semantic, background, selected: false);
        }

        private bool GraphConnectionControl(string path, string? id, string kind, TopiaForgeGraphConnectionControl control,
            string display, string semantic, UiThemeColor? backdrop, bool selected)
        {
            if (control.SemanticLabel != semantic || control.Button.targetGraphic is not { } fill) return false;
            var start = observations.Count;
            var color = Over(fill, backdrop);
            var flags = Flags(control.Button) | ControlMarks(path, control.Button, semantic, color, backdrop);
            if (selected) flags |= UiCaptureNodeFlags.Selected;
            var result = Text(path, id, kind, Named<TMP_Text>(control.Rect, "Label"), display, UiTextStyle.Label,
                color, flags, Decoration(control.Rect, fill), semanticLabel: semantic);
            if (!result) return false;
            HitAndFocus(path, control.Button, semantic, backdrop);
            RegisterGroup(start);
            return true;
        }
    }
}
