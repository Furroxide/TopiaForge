using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Whether the game's own UI is taking typing, for the mod actions that yield to it: the EventSystem selection is a live
    /// control that is not the kit's, and it is a focused text field, uGUI's or TextMesh Pro's, or the handler of a UI Toolkit
    /// panel whose focused element is a text field (TopiaForgeUiToolkitFocus). Robotopia's menus are UI Toolkit, and Unity
    /// selects a panel's handler on any click into the panel and keeps it selected after the menu closes: a click on RESUME
    /// GAME left it selected through the level, and every mod key waited. A selection alone holds nothing now; the game's
    /// reply field, while it has the keyboard, holds every key. Main thread only.
    /// </summary>
    internal sealed class TopiaForgeGameTextFocus
    {
        /// <summary>The one that reads the running game's UI Toolkit.</summary>
        internal static readonly TopiaForgeGameTextFocus Current = new TopiaForgeGameTextFocus(TopiaForgeUiToolkitFocus.Find());

        private readonly TopiaForgeUiToolkitFocus toolkit;
        private readonly Type? handlerType;
        private GameObject? selection;
        private Component? panelHandler;

        /// <summary>Creates one that reads UI Toolkit through <paramref name="toolkit"/>.</summary>
        public TopiaForgeGameTextFocus(TopiaForgeUiToolkitFocus toolkit)
        {
            this.toolkit = toolkit ?? throw new ArgumentNullException(nameof(toolkit));

            // Only a component can be looked up on the selection; Unity throws for any other type.
            handlerType = toolkit.HandlerType != null && typeof(Component).IsAssignableFrom(toolkit.HandlerType) ? toolkit.HandlerType : null;
        }

        /// <summary>Gets whether <paramref name="selected"/>, the EventSystem selection, is one of the game's text fields with the keyboard.</summary>
        public bool HasKeyboard(GameObject? selected)
        {
            if (selected == null || !selected.activeInHierarchy || TopiaForgeInputOwnership.IsKitObject(selected))
            {
                return false;
            }

            if ((selected.TryGetComponent(out TMP_InputField text) && text.isFocused)
                || (selected.TryGetComponent(out InputField legacy) && legacy.isFocused))
            {
                return true;
            }

            if (!ReferenceEquals(selected, selection))
            {
                selection = selected;
                panelHandler = handlerType != null ? selected.GetComponent(handlerType) : null;
            }

            return panelHandler != null && toolkit.TakesTyping(panelHandler);
        }
    }
}
