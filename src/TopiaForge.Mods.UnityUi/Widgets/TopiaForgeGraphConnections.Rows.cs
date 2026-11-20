using TopiaForge.Mods;

namespace TopiaForge.Mods.UnityUi
{
    internal sealed partial class TopiaForgeGraphConnections
    {
        private void RenderRows()
        {
            var graph = selection.Graph;
            if (graph == null) return;
            var theme = Theme;
            var scale = Host.EffectiveUiScale;
            var count = selection.DrawnRows;
            while (rows.Count < count)
            {
                var slot = rows.Count;
                var row = new TopiaForgeGraphConnectionControl(this, Go.transform, "Connection row", () => ActivateRow(slot), MoveRow);
                row.Button.Selected = () => SelectRow(slot);
                rows.Add(row);
            }

            var selectedLabel = string.Empty;
            var selectedFits = false;
            for (var slot = 0; slot < rows.Count; slot++)
            {
                var row = rows[slot];
                var drawn = slot < count;
                if (row.Button.gameObject.activeSelf != drawn) row.Button.gameObject.SetActive(drawn);
                if (!drawn) continue;
                var index = selection.First + slot;
                var selected = index == selection.SelectedIndex;
                var box = viewport.Row(slot);
                row.Paint(theme, scale, UiPaintRole.Surface, enabled: true, selected: selected);
                row.SetEdge(graph, index, box, selected ? selection.LabelPage : 0);
                if (selected)
                {
                    selectedLabel = row.SemanticLabel;
                    selectedFits = row.LabelPages != 0;
                }
                Place(row.Rect, box);
            }

            var removeLabel = UiGraphConnections.RemovePrefix + selectedLabel;
            remove.Paint(theme, scale, UiPaintRole.Surface, enabled: selectedFits);
            remove.SetRemoveText(removeLabel, viewport.Remove);
            if (remove.DisplayLabel.Length == 0) remove.Button.interactable = false;
            Place(remove.Rect, viewport.Remove);
        }

        private void ActivateRow(int slot)
        {
            if (!IsOpen || slot >= selection.DrawnRows) return;
            if (selection.Select(selection.First + slot)) RenderRows();
            else if (selection.AdvanceLabel(rows[slot].LabelPages)) rows[slot].ShowLabelPage(selection.LabelPage);
        }

        private void SelectRow(int slot)
        {
            if (!IsOpen || slot >= selection.DrawnRows) return;
            if (selection.Select(selection.First + slot)) RenderRows();
        }
    }
}
