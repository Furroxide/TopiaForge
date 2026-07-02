using Robotopia.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace Robotopia.Zombies
{
    /// <summary>
    /// Bottom-center OVERRIDE uplink readout: charge pips (rebuilt only when the max
    /// changes, with the kit's 0.28 + 0.5 * regen breathing alpha on the recharging
    /// pip), the E jack-in and Q broadcast status lines, and the horde-pressure bar
    /// that appears above 2% pressure. Hidden entirely when the verb is disabled.
    /// </summary>
    internal sealed class UplinkPanel
    {
        private readonly HudContext context;
        private readonly QwPanel panel;
        private readonly QwLabel title;
        private readonly QwPipRow pips;
        private readonly QwLabel jack;
        private readonly QwLabel broadcast;
        private readonly QwStatBar pressure;

        public UplinkPanel(HudContext context, QwContainer parent)
        {
            this.context = context;
            panel = parent.Panel(QwPanelStyle.HudPanel).Dock(QwCorner.Bottom, 22f).Size(560f, 112f).Dynamic();

            title = panel.Label("UPLINK", QwTextStyle.Caption).Tone(QwTone.Muted).AlignCenter();
            HudContext.Place(title, 18f, 8f, 524f, 18f);

            pips = panel.PipRow();
            HudContext.Place(pips, 190f, 30f, 180f, 14f);
            var pipLayout = pips.Go.GetComponent<HorizontalLayoutGroup>();
            if (pipLayout != null)
            {
                pipLayout.childAlignment = TextAnchor.MiddleCenter;
            }

            jack = panel.Label(QwTextStyle.Heading).AlignCenter();
            HudContext.Place(jack, 18f, 48f, 250f, 28f);

            broadcast = panel.Label(QwTextStyle.Heading).AlignCenter();
            HudContext.Place(broadcast, 292f, 48f, 250f, 28f);

            pressure = panel.StatBar("HORDE PRESSURE");
            pressure.Tone(QwTone.Danger);
            HudContext.Place(pressure, 90f, 82f, 380f, 14f);

            panel.SetVisible(false);
        }

        public void Tick()
        {
            var controller = context.Controller;
            var enabled = controller.OverrideHudEnabled;
            panel.SetVisible(enabled);
            if (!enabled)
            {
                return;
            }

            var maxCharges = Mathf.Max(0, controller.OverrideMaxCharges);
            pips.SetCount(maxCharges);
            pips.SetFilled(controller.OverrideCharges, controller.OverrideRegenFraction);

            title.SetText("UPLINK  " + controller.OverrideCharges + "/" + maxCharges);

            if (!controller.ConversationAvailable)
            {
                jack.SetText("E  JACK-IN OFFLINE");
                jack.SetColor(HudPalette.TextMuted);
            }
            else if (controller.OverrideAimingHijackable && controller.OverrideCharges > 0)
            {
                jack.SetText("E  JACK IN");
                jack.SetColor(HudPalette.Cyan);
            }
            else
            {
                jack.SetText(controller.OverrideCharges > 0 ? "E  AIM A ROBOT" : "E  NO CHARGE");
                jack.SetColor(HudPalette.TextMuted);
            }

            var broadcastReady = controller.BroadcastReadyFraction >= 1f;
            broadcast.SetText(broadcastReady ? "Q  STAND-DOWN" : "Q  RECHARGING");
            broadcast.SetColor(broadcastReady ? HudPalette.Violet : HudPalette.TextMuted);

            var pressureValue = Mathf.Clamp01(controller.Pressure);
            pressure.SetVisible(pressureValue > 0.02f);
            pressure.SetFraction(pressureValue);
        }
    }
}
