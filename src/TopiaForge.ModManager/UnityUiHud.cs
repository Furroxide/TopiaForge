using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        /// <summary>
        /// Builds a HUD on a persistent canvas: a panel of the requested size, docked at its corner or edge and stacked with
        /// every other mod's HUD there, holding a text column that fills the panel inside its padding. The title stays on one
        /// line, and the body wraps only past the panel's inner width, so a short line such as "Shared count: 12" never
        /// wraps. A gameplay HUD starts hidden when the game is not in gameplay, and shows by itself when it is.
        /// </summary>
        private UnityUiSurface CreateHud(UiSurfaceRequest request, ref TopiaForgeWidget? nativeRoot)
        {
            var layer = host.HudLayer(request.Id, persistent: true);
            nativeRoot = layer;
            var column = TopiaForgeHudColumn.For(request.Width, request.Height);
            var panel = layer.Panel(TopiaForgePanelStyle.HudPanel).Size(column.PanelWidth, column.PanelHeight);
            var text = panel.Column(TopiaForgeGap.Sm, TopiaForgeGap.Md).Stretch();
            text.Label(request.Title, TopiaForgeTextStyle.Heading).Ellipsis();
            var scroll = text.Scroll(TopiaForgeGap.Sm, TopiaForgeGap.None);
            var body = scroll.Content.Label(request.Body, TopiaForgeTextStyle.Body);
            var corner = UiHudAnchors.CornerOf(request.HudAnchor, request.HudVisibility);
            var surface = UnityUiSurface.ForHud(
                request.Id,
                layer,
                body,
                scroll.Content,
                followsGameplay: request.HudVisibility == UiHudVisibility.Gameplay,
                gameplay: gameplay.IsGameplay,
                lifetime,
                logger,
                ReleaseSurface);
            surface.AttachDock(hudPlacement, hudPlacement.Add(host, panel.Rect, corner, column.PanelHeight));
            return surface;
        }
    }
}
