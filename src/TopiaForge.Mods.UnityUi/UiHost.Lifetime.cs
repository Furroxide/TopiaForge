using System;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class UiHost
    {
        /// <summary>
        /// Raised when a canvas this host made is destroyed outside the kit: by a scene that unloads it, the game or another
        /// mod. By then the host has let go of everything on that canvas, closing its windows, tools and dialogs (a dialog as
        /// a cancellation), and the canvas's sorting slot goes back right after. Not raised for a canvas the host destroys
        /// itself, nor while the application quits.
        /// </summary>
        public event Action<GameObject>? LayerLost;

        private void HandleLayerLost(GameObject root)
        {
            if (disposed)
            {
                return;
            }

            foreach (var modal in modalInstances.ToArray())
            {
                if (ReferenceEquals(modal.CanvasRoot, root))
                {
                    modal.Lose();
                }
            }

            foreach (var window in windows.ToArray())
            {
                if (ReferenceEquals(window.CanvasRoot, root))
                {
                    windows.Remove(window);
                    window.Lose();
                }
            }

            foreach (var tool in fullscreenTools.ToArray())
            {
                if (ReferenceEquals(tool.CanvasRoot, root))
                {
                    fullscreenTools.Remove(tool);
                    tool.Lose();
                }
            }

            var rootTransform = TransformOf(root);
            for (var index = widgets.Count - 1; index >= 0; index--)
            {
                var widget = widgets[index];
                if (!WasOn(widget.Go, root, rootTransform))
                {
                    continue;
                }

                TopiaForgeTween.Cancel(widget);
                if (widget is ITopiaForgeThemeAware aware)
                {
                    themeAware.Remove(aware);
                }

                widgets.RemoveAt(index);
            }

            for (var index = layerRoots.Count - 1; index >= 0; index--)
            {
                if (ReferenceEquals(layerRoots[index], root))
                {
                    layerRoots.RemoveAt(index);
                    scalers.RemoveAt(index);
                }
            }

            var handlers = LayerLost;
            if (handlers == null)
            {
                return;
            }

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<GameObject>)handler)(root);
                }
                catch (Exception exception)
                {
                    TopiaForgeLog.Error(exception, "UiHost '" + OwnerId + "' could not report a destroyed canvas to its owner.");
                }
            }
        }

        // A widget belonged to the destroyed canvas when it is that canvas, sits under it, or is already gone itself.
        private static bool WasOn(GameObject go, GameObject root, Transform? rootTransform)
        {
            if (ReferenceEquals(go, root) || go == null)
            {
                return true;
            }

            try
            {
                return rootTransform != null && go.transform.IsChildOf(rootTransform);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Transform? TransformOf(GameObject root)
        {
            try
            {
                return root != null ? root.transform : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
