using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.Zombies
{
    /// <summary>
    /// The SIGNAL LOST screen: backdrop + centered HUD panel with the reached wave,
    /// the final score, and the RESTART RUN / RETURN TO MENU buttons wired to the same
    /// controller calls as before. Mode switching (and the cursor lease) is owned by
    /// the behaviour shell.
    /// </summary>
    internal sealed class GameOverModal
    {
        private readonly HudContext context;
        private readonly QwContainer root;
        private readonly QwLabel wave;
        private readonly QwLabel score;

        public GameOverModal(HudContext context, QwContainer parent)
        {
            this.context = context;
            root = parent.Stack("GameOver");

            var backdrop = root.FreeImage("Backdrop").Stretch();
            backdrop.SetColor(context.Theme.Backdrop);
            backdrop.Image.raycastTarget = true;

            var panel = root.Panel(QwPanelStyle.HudPanel).Dock(QwCorner.Center).Size(520f, 360f);

            var title = panel.Label("SIGNAL LOST", QwTextStyle.Display).Tone(QwTone.Danger).AlignCenter();
            HudContext.Place(title, 24f, 34f, 472f, 58f);

            wave = panel.Label(QwTextStyle.Heading).AlignCenter();
            HudContext.Place(wave, 24f, 112f, 472f, 30f);

            score = panel.Label(QwTextStyle.Heading).Tone(QwTone.Warning).AlignCenter();
            HudContext.Place(score, 24f, 148f, 472f, 30f);

            var restart = panel.Button("RESTART RUN", () => this.context.Controller.Restart(), QwButtonStyle.Filled);
            HudContext.Place(restart, 110f, 210f, 300f, 46f);

            var menu = panel.Button("RETURN TO MENU", () => this.context.Controller.ReturnToMenu(), QwButtonStyle.Outline);
            HudContext.Place(menu, 110f, 268f, 300f, 46f);

            root.SetVisible(false);
        }

        public void SetVisible(bool visible)
        {
            root.SetVisible(visible);
        }

        public void Tick()
        {
            var controller = context.Controller;
            wave.SetText("WAVE REACHED  ", controller.Wave);
            score.SetNumber("FINAL SCORE  ", controller.Score, "N0");
        }
    }
}
