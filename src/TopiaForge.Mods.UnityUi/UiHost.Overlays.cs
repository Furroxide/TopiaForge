using TopiaForge.Mods;

namespace TopiaForge.Mods.UnityUi
{
    public sealed partial class UiHost
    {
        internal TopiaForgeOverlay Overlay(string id, string title, float width, float height,
            UiOverlayContentKind contentKind, bool dismissible) =>
            CreateOverlay(KitLayer("overlay:" + id, TopiaForgeLayerPriority.Overlay, TopiaForgeScheme.Hud,
                interactive: false, persistent: true), title, width, height, contentKind, dismissible, trust: false);

        internal TopiaForgeOverlay TrustOverlay(string title, float width) =>
            CreateOverlay(TrustLayer(persistent: true, overlay: true), title, width, float.MaxValue,
                UiOverlayContentKind.Interactive, dismissible: true, trust: true);

        private TopiaForgeOverlay CreateOverlay(TopiaForgeContainer layer, string title, float width, float height,
            UiOverlayContentKind contentKind, bool dismissible, bool trust)
        {
            try
            {
                if (trust) SetSurfaceTheme(layer.Go, UiThemePackParser.Default);
                var overlay = new TopiaForgeOverlay(layer, title, width, height, contentKind, dismissible, trust);
                overlays.Add(overlay);
                return overlay;
            }
            catch
            {
                DestroyLayer(layer.Go);
                throw;
            }
        }
    }
}
