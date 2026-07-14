using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.Zombies
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
        private readonly TopiaForgePanel panel;
        private readonly TopiaForgeLabel title;
        private readonly TopiaForgePipRow pips;
        private readonly TopiaForgeLabel jack;
        private readonly TopiaForgeLabel broadcast;
        private readonly TopiaForgeStatBar pressure;

        public UplinkPanel(HudContext context, TopiaForgeContainer parent)
        {
            this.context = context;
            panel = parent.Panel(TopiaForgePanelStyle.HudPanel).Dock(TopiaForgeCorner.Bottom, 22f).Size(560f, 112f).Dynamic();

            title = panel.Label("UPLINK", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).AlignCenter();
            HudContext.Place(title, 18f, 8f, 524f, 18f);

            pips = panel.PipRow().AlignCenter();
            HudContext.Place(pips, 190f, 30f, 180f, 14f);

            jack = panel.Label(TopiaForgeTextStyle.Heading).AlignCenter();
            HudContext.Place(jack, 18f, 48f, 250f, 28f);

            broadcast = panel.Label(TopiaForgeTextStyle.Heading).AlignCenter();
            HudContext.Place(broadcast, 292f, 48f, 250f, 28f);

            pressure = panel.StatBar("HORDE PRESSURE");
            pressure.Tone(TopiaForgeTone.Danger);
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

            title.SetText("UPLINK  ", controller.OverrideCharges, "/", maxCharges);

            if (!controller.ConversationAvailable)
            {
                jack.SetText("E  JACK-IN OFFLINE");
                jack.SetTone(TopiaForgeTone.Muted);
            }
            else if (controller.OverrideAimingHijackable && controller.OverrideCharges > 0)
            {
                jack.SetText("E  JACK IN");
                jack.SetTone(TopiaForgeTone.Accent);
            }
            else
            {
                jack.SetText(controller.OverrideCharges > 0 ? "E  AIM A ROBOT" : "E  NO CHARGE");
                jack.SetTone(TopiaForgeTone.Muted);
            }

            var broadcastReady = controller.BroadcastReadyFraction >= 1f;
            broadcast.SetText(broadcastReady ? "Q  STAND-DOWN" : "Q  RECHARGING");
            broadcast.SetTone(broadcastReady ? TopiaForgeTone.Primary : TopiaForgeTone.Muted);

            var pressureValue = Mathf.Clamp01(controller.Pressure);
            pressure.SetVisible(pressureValue > 0.02f);
            pressure.SetFraction(pressureValue);
        }
    }
}
