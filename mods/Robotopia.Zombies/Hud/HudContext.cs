using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.Zombies
{
    /// <summary>
    /// Shared references handed to every HUD module: the gameplay controller the HUD
    /// reads, the mod config (timings/thresholds), the kit host, and the HUD layer the
    /// modules build onto. Also carries the absolute-placement helpers the panels use.
    /// </summary>
    internal sealed class HudContext
    {
        public HudContext(ZombiesController controller, ZombiesConfig config, UiHost ui, QwHudLayer hud)
        {
            Controller = controller;
            Config = config;
            Ui = ui;
            Hud = hud;
        }

        public ZombiesController Controller { get; }

        public ZombiesConfig Config { get; }

        public UiHost Ui { get; }

        public QwHudLayer Hud { get; }

        public QwResolvedTheme Theme => Ui.Theme(QwScheme.Hud);

        /// <summary>Top-left anchored placement inside a panel (legacy Place semantics).</summary>
        public static void Place(QwWidget widget, float x, float y, float width, float height)
        {
            QwAnchors.Place(widget.Rect, x, y, width, height);
        }

        /// <summary>Center-anchored free rect (reticle ticks, hit markers, the bearing wedge).</summary>
        public static void CenterAnchor(QwWidget widget)
        {
            widget.Rect.anchorMin = new Vector2(0.5f, 0.5f);
            widget.Rect.anchorMax = new Vector2(0.5f, 0.5f);
            widget.Rect.pivot = new Vector2(0.5f, 0.5f);
        }
    }
}
