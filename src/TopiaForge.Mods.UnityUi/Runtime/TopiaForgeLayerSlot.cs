using System;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Sits on every kit canvas and gives the canvas's sorting slot back when anything destroys it: a scene that unloads
    /// it, the game or another mod. The kit releases a canvas it destroys itself first, so this then finds nothing left to
    /// release. It is added while the canvas is still active, so Unity calls <see cref="OnDestroy"/> for it even if the
    /// canvas is hidden by then.
    /// </summary>
    internal sealed class TopiaForgeLayerSlot : MonoBehaviour
    {
        internal GameObject? Root;
        private UiThemeDescriptor? descriptor;
        private TopiaForgeResolvedTheme? resolved;

        internal bool SelectTheme(UiThemeDescriptor? value)
        {
            if (ReferenceEquals(descriptor, value)) return false;
            descriptor = value;
            resolved = null;
            return true;
        }

        internal TopiaForgeResolvedTheme? ResolveTheme(TopiaForgeScheme scheme, bool highContrast, int version)
        {
            if (descriptor == null) return null;
            if (resolved == null || resolved.ThemeVersion != version || resolved.Scheme != scheme)
                resolved = new TopiaForgeResolvedTheme(scheme, null, highContrast, version, descriptor);
            return resolved;
        }

        /// <summary>Called once, before the slot goes back, when the canvas is destroyed outside the kit.</summary>
        internal Action<GameObject>? Lost;

        private void OnDestroy()
        {
            var root = Root;
            var lost = Lost;
            Root = null;
            Lost = null;
            resolved = null;
            descriptor = null;
            if (!(root is null))
            {
                TopiaForgeLayers.ReleaseDestroyed(root, lost);
            }
        }
    }
}
