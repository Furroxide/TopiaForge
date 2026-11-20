using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class TopiaForgeGraphCanvas
    {
        private TopiaForgeGraphConnections? connectionList;

        internal TopiaForgeGraphConnections? ConnectionList
        {
            get => connectionList;
            set
            {
                if (ReferenceEquals(connectionList, value)) return;
                connectionList = value;
                UpdateConnectionNavigation();
            }
        }

        internal bool HasActionableConnections => enabledState && UiGraphConnections.IsActionable(graph);

        internal bool RestoreConnectionsFocus(GameObject? previous)
        {
            if (!Go.activeInHierarchy) return false;
            if (previous != null && previous.transform.IsChildOf(Go.transform) &&
                previous.TryGetComponent<Selectable>(out var control) && TryFocus(control)) return true;
            foreach (var node in nodeViews)
                if (node.Active && TryFocus(node.FocusControl)) return true;
            return false;

            static bool TryFocus(Selectable selectable)
            {
                if (!selectable.IsActive() || !selectable.IsInteractable()) return false;
                selectable.Select();
                return true;
            }
        }

        internal void ReportConnectionFocus(GameObject? control) => connectionList?.ObserveFocus(this, control);

        private void UpdateConnectionNavigation()
        {
            foreach (var edge in edgeViews) edge.SetDirectPickingOnly(connectionList != null);
        }
    }

    internal sealed class TopiaForgeGraphFocusRelay : MonoBehaviour, ISelectHandler
    {
        private TopiaForgeGraphCanvas? owner;

        internal void Attach(TopiaForgeGraphCanvas graph) => owner = graph;

        public void OnSelect(BaseEventData eventData) => owner?.ReportConnectionFocus(gameObject);
    }
}
