using System;

namespace TopiaForge.Mods
{
    public sealed partial class UiSurfaceRequest
    {
        /// <summary>Gets whether Back closes an interactive overlay. Defaults to true.</summary>
        /// <remarks>This setting applies to overlays.</remarks>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public bool Dismissible { get; } = true;

        /// <summary>Returns a copy of this overlay request with the specified Back behavior.</summary>
        /// <param name="dismissible">Whether Esc or controller Back closes the interactive overlay.</param>
        /// <returns>A new request with the chosen Back behavior and the same surface settings.</returns>
        /// <remarks>Admission of a non-dismissible overlay requires a reachable enabled button in its content.</remarks>
        /// <exception cref="InvalidOperationException">This option requires an overlay request.</exception>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiSurfaceRequest WithDismissible(bool dismissible)
        {
            if (Kind != UiSurfaceKind.Overlay)
                throw new InvalidOperationException("WithDismissible applies to overlay surfaces only.");

            return new UiSurfaceRequest(this, HudAnchor, HudVisibility, Theme, dismissible);
        }
    }
}
