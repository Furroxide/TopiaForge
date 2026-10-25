using System;
using System.Reflection;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Reads whether a UI Toolkit panel is taking typing, Unity-free, through UI Toolkit's public focus members: the panel
    /// handler's <c>panel</c>, that panel's <c>focusController</c>, its <c>focusedElement</c>, and whether that element, or
    /// one it sits in (<c>parent</c>), is a text input field: a <c>TextInputBaseField&lt;T&gt;</c>, as TextField and the
    /// number fields are. A menu button with the focus, or a panel with none, as after its menu closed, takes no typing.
    /// The kit takes no UIElements reference, so this degrades instead of failing to load: a Unity without UI Toolkit has
    /// no panel handler to select, and if a member cannot be found or read, a selected panel counts as typing, as every
    /// selection did before, so a text field never loses its keys.
    /// </summary>
    internal sealed class TopiaForgeUiToolkitFocus
    {
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        private readonly PropertyInfo? panel;
        private readonly PropertyInfo? focusController;
        private readonly PropertyInfo? focusedElement;
        private readonly PropertyInfo? parent;
        private readonly Type? visualElement;
        private readonly Type? textField;

        private TopiaForgeUiToolkitFocus(Type? handler, Type? visualElement, Type? textField)
        {
            HandlerType = handler;
            this.visualElement = visualElement;
            this.textField = textField != null && textField.IsGenericTypeDefinition ? textField : null;
            try
            {
                panel = handler?.GetProperty("panel", PublicInstance);
                focusController = panel?.PropertyType.GetProperty("focusController", PublicInstance);
                focusedElement = focusController?.PropertyType.GetProperty("focusedElement", PublicInstance);
                parent = visualElement?.GetProperty("parent", PublicInstance);
            }
            catch (Exception)
            {
                // A member declared twice, or a type that cannot load, leaves panels unread: a selected one holds the keys.
                panel = null;
                focusController = null;
                focusedElement = null;
                parent = null;
            }
        }

        /// <summary>Gets the panel handler's type, which Unity selects for a panel; null in a Unity without UI Toolkit.</summary>
        public Type? HandlerType { get; }

        /// <summary>Gets whether every member was found, so a panel's focused element can be read.</summary>
        public bool Readable => focusedElement != null && parent != null && textField != null;

        /// <summary>Finds UI Toolkit in the running Unity. Unity 6 moved the panel handler from UIElements to Unity UI.</summary>
        public static TopiaForgeUiToolkitFocus Find()
        {
            return Find(name => Type.GetType(name, false));
        }

        /// <summary>Finds UI Toolkit's types through <paramref name="type"/>, which resolves an assembly-qualified name or gives null.</summary>
        public static TopiaForgeUiToolkitFocus Find(Func<string, Type?> type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            Type? Resolve(string name)
            {
                try
                {
                    return type(name);
                }
                catch (Exception)
                {
                    // A type that cannot be resolved or loaded is not there to read.
                    return null;
                }
            }

            return new TopiaForgeUiToolkitFocus(
                Resolve("UnityEngine.UIElements.PanelEventHandler, UnityEngine.UI")
                    ?? Resolve("UnityEngine.UIElements.PanelEventHandler, UnityEngine.UIElementsModule"),
                Resolve("UnityEngine.UIElements.VisualElement, UnityEngine.UIElementsModule"),
                Resolve("UnityEngine.UIElements.TextInputBaseField`1, UnityEngine.UIElementsModule"));
        }

        /// <summary>
        /// Gets whether the panel that <paramref name="handler"/> serves has a text field focused: false for a focused button
        /// or no focus at all, and true whenever the panel cannot be read.
        /// </summary>
        public bool TakesTyping(object handler)
        {
            if (!Readable)
            {
                return true;
            }

            try
            {
                var shown = panel!.GetValue(handler);
                var controller = shown != null ? focusController!.GetValue(shown) : null;
                var element = controller != null ? focusedElement!.GetValue(controller) : null;
                while (element != null)
                {
                    if (IsTextField(element.GetType()))
                    {
                        return true;
                    }

                    element = visualElement!.IsInstanceOfType(element) ? parent!.GetValue(element) : null;
                }

                return false;
            }
            catch (Exception)
            {
                // A panel that cannot be read is treated as typing, so a text field never loses its keys.
                return true;
            }
        }

        private bool IsTextField(Type type)
        {
            for (var at = type; at != null; at = at.BaseType)
            {
                if (at.IsGenericType && at.GetGenericTypeDefinition() == textField)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
