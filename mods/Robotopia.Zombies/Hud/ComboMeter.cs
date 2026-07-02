using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.Zombies
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
        private static readonly Color ComboOrange = new Color(1f, 0.50f, 0.12f, 1f);

        private readonly HudContext context;
        private readonly QwPanel panel;
        private readonly QwProgressBar tierBar;
        private readonly QwProgressBar decayBar;
        private readonly QwLabel label;

        public ComboMeter(HudContext context, QwContainer parent)
        {
            this.context = context;
            panel = parent.Panel(QwPanelStyle.HudPanel).Dock(QwCorner.Right, 22f).Size(72f, 230f).Dynamic();

            tierBar = panel.ProgressBar().Vertical().Tone(QwTone.Warning);
            PlaceBottomCenter(tierBar, 0f, 18f, 16f, 160f);

            decayBar = panel.ProgressBar().Vertical().Tone(QwTone.Danger);
            PlaceBottomCenter(decayBar, 11f, 18f, 4f, 160f);

            label = panel.Label(QwTextStyle.Numeral).AlignCenter();
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

            var pulse = 1f + (0.12f * QwTheme.EffectiveMotion * Mathf.Sin(Time.time * 8f));
            label.Rect.localScale = new Vector3(pulse, pulse, 1f);
            label.SetText(MultiplierPrefix, controller.ComboMultiplier);
            label.SetColor(ComboColor(controller.ComboMultiplier));
        }

        private static Color ComboColor(int multiplier)
        {
            switch (multiplier)
            {
                case 2:
                    return HudPalette.Cyan;
                case 3:
                    return HudPalette.Amber;
                case 4:
                    return ComboOrange;
                default:
                    return multiplier >= 5 ? HudPalette.Danger : HudPalette.Text;
            }
        }

        private static void PlaceBottomCenter(QwWidget widget, float x, float y, float width, float height)
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
