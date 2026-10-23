using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>Where SDK HUD surfaces dock. Unity-free and unit-tested.</summary>
    internal static class UiHudAnchors
    {
        /// <summary>
        /// Resolves where a HUD docks: the anchor the request names, or TopiaForge's default, which keeps clear of the
        /// game's own HUD (the microphone label at the top left, the version at the top, a level's objective at the top
        /// right, the crosshair and the dialogue subtitles at the bottom). A gameplay HUD docks at the bottom left. A HUD
        /// shown everywhere docks at the middle of the left edge, which also stays clear of the main menu's buttons.
        /// </summary>
        public static TopiaForgeCorner CornerOf(UiHudAnchor anchor, UiHudVisibility visibility)
        {
            switch (anchor)
            {
                case UiHudAnchor.TopLeft: return TopiaForgeCorner.TopLeft;
                case UiHudAnchor.Top: return TopiaForgeCorner.Top;
                case UiHudAnchor.TopRight: return TopiaForgeCorner.TopRight;
                case UiHudAnchor.Left: return TopiaForgeCorner.Left;
                case UiHudAnchor.Right: return TopiaForgeCorner.Right;
                case UiHudAnchor.BottomLeft: return TopiaForgeCorner.BottomLeft;
                case UiHudAnchor.Bottom: return TopiaForgeCorner.Bottom;
                case UiHudAnchor.BottomRight: return TopiaForgeCorner.BottomRight;
                default:
                    return visibility == UiHudVisibility.Always ? TopiaForgeCorner.Left : TopiaForgeCorner.BottomLeft;
            }
        }
    }
}
