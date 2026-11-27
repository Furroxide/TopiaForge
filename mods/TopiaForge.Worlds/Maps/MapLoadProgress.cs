using System;
using System.Globalization;
using TopiaForge.Mods;

namespace TopiaForge.Worlds.Maps
{
    // The launch protocol retains its loading-world phase. This owned kit HUD supplies
    // the detail while that readiness hold is active, including during scene changes.
    internal sealed class MapLoadProgress : IDisposable
    {
        private readonly object lifetime = new object();
        private IUiSurface? surface;
        private bool building;
        internal MapLoadProgress(IUiService ui)
        {
            var created = ui.CreateSurface(new UiSurfaceRequest("map-loading", "Loading map", "Downloading the map",
                UiSurfaceKind.Hud, 440, 110).WithHudVisibility(UiHudVisibility.Always));
            if (created.TryGetValue(out var value)) surface = value;
            // Keep Progress's captured context. The lifetime gate also covers callbacks already
            // executing when disposal begins (including the thread-pool fallback without a context).
            Download = new Progress<long>(bytes =>
            {
                lock (lifetime)
                {
                    if (!building) surface?.SetBody("Downloading the map (" + ((bytes + 1023) / 1024).ToString(CultureInfo.InvariantCulture) + " KiB)");
                }
            });
        }
        internal IProgress<long> Download { get; }
        internal void Building()
        {
            lock (lifetime) { building = true; surface?.SetBody("Building the map"); }
        }
        public void Dispose()
        {
            lock (lifetime) { var old = surface; surface = null; old?.Dispose(); }
        }
    }
}
