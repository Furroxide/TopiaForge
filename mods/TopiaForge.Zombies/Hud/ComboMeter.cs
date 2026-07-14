using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.Zombies
{
    /// <summary>
    /// Right-edge combo meter: a vertical tier-progress bar, a thin decay bar showing
    /// the remaining combo window, and the multiplier numeral. Hidden while no combo is
    /// running. Ported verbatim: the multiplier color table and the
    /// 1 + 0.12 * motion * sin(8t) label pulse; the "xN" string only re-concatenates
    /// when the multiplier changes.
    /// </summary>
    internal sealed class ComboMeter
    {
        private const string MultiplierPrefix = "x";
        private readonly HudContext context;
        private readonly TopiaForgePanel panel;
        private readonly TopiaForgeProgressBar tierBar;
        private readonly TopiaForgeProgressBar decayBar;
        private readonly TopiaForgeLabel label;

        public ComboMeter(HudContext context, TopiaForgeContainer parent)
        {
            this.context = context;
            panel = parent.Panel(TopiaForgePanelStyle.HudPanel).Dock(TopiaForgeCorner.Right, 22f).Size(72f, 230f).Dynamic();

            tierBar = panel.ProgressBar().Vertical().Tone(TopiaForgeTone.Warning);
            PlaceBottomCenter(tierBar, 0f, 18f, 16f, 160f);

            decayBar = panel.ProgressBar().Vertical().Tone(TopiaForgeTone.Danger);
            PlaceBottomCenter(decayBar, 11f, 18f, 4f, 160f);

            label = panel.Label(TopiaForgeTextStyle.Numeral).AlignCenter();
            HudContext.Place(label, 6f, 4f, 60f, 34f);

            panel.SetVisible(false);
        }

        public void Tick()
        {
            var controller = context.Controller;
            if (controller.ComboCount <= 0)
            {
                panel.SetVisible(false);
                return;
            }

            panel.SetVisible(true);
            tierBar.SetFraction(controller.ComboTierProgress);
            decayBar.SetFraction(controller.ComboWindowRemaining);

            var pulse = 1f + (0.12f * context.Ui.EffectiveMotion * Mathf.Sin(Time.time * 8f));
            label.Rect.localScale = new Vector3(pulse, pulse, 1f);
            label.SetText(MultiplierPrefix, controller.ComboMultiplier);
            label.SetTone(ComboTone(controller.ComboMultiplier));
        }

        private static TopiaForgeTone ComboTone(int multiplier)
        {
            switch (multiplier)
            {
                case 2:
                    return TopiaForgeTone.Accent;
                case 3:
                    return TopiaForgeTone.Warning;
                case 4:
                    return TopiaForgeTone.Primary;
                default:
                    return multiplier >= 5 ? TopiaForgeTone.Danger : TopiaForgeTone.Neutral;
            }
        }

        private static void PlaceBottomCenter(TopiaForgeWidget widget, float x, float y, float width, float height)
        {
            var rect = widget.Rect;
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
        }
    }
}
