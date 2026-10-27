using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.Worlds.Maps
{
    // The launch protocol retains its loading-world phase. This owned kit HUD supplies
    // the detail while that readiness hold is active, including during scene changes.
    internal sealed class MapLoadProgress : IDisposable
    {
        private IUiSurface? surface;
        private bool building;
        internal MapLoadProgress(IUiService ui)
        {
            var created = ui.CreateSurface(new UiSurfaceRequest("map-loading", "Loading map", "Downloading the map",
                UiSurfaceKind.Hud, 440, 110).WithHudVisibility(UiHudVisibility.Always));
            if (created.TryGetValue(out var value)) surface = value;
            // Progress captures Unity's synchronization context. Queued callbacks are harmless after disposal.
            Download = new Progress<long>(bytes =>
            {
                if (!building) surface?.SetBody("Downloading the map (" + ((bytes + 1023) / 1024).ToString(CultureInfo.InvariantCulture) + " KiB)");
            });
        }
        internal IProgress<long> Download { get; }
        internal void Building() { building = true; surface?.SetBody("Building the map"); }
        public void Dispose() { var old = surface; surface = null; old?.Dispose(); }
    }
}
