using UnityEngine;
using UnityEngine.EventSystems;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Releases only an owning subtree's selection, never a control selected in another surface.</summary>
    internal static class TopiaForgeFocus
    {
        internal static void Release(GameObject root)
        {
            var events = EventSystem.current;
            var selected = events == null ? null : events.currentSelectedGameObject;
            if (selected != null && root != null && (selected == root || selected.transform.IsChildOf(root.transform)))
                events!.SetSelectedGameObject(null);
        }
    }
}
