using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>Where SDK HUD surfaces dock. Unity-free and unit-tested.</summary>
    internal static class UiHudAnchors
    {
        /// <summary>
        /// The lower HUD stacks leave the bottom 12% of the screen for the game's subtitles. Moving the anchor,
        /// rather than adding canvas units, keeps that fraction at every resolution and accessibility scale.
        /// Every panel in a lower stack shares this reserve, including an explicitly anchored always-visible HUD.
        /// </summary>
        public static float SubtitleReserve(TopiaForgeCorner corner)
        {
            return corner == TopiaForgeCorner.BottomLeft || corner == TopiaForgeCorner.Bottom
                || corner == TopiaForgeCorner.BottomRight ? 0.12f : 0f;
        }

        /// <summary>Resolves an explicit anchor or the default for the HUD's visibility.</summary>
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
