using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.Zombies
{
    /// <summary>
    /// Top-left threat readout: wave/score, hostile/incoming/ally line, archetype
    /// tally, the integrity bar (success/warning/danger thresholds from config), the
    /// zapper readiness bar, and the state line.
    /// </summary>
    internal sealed class ThreatPanel
    {
        private readonly HudContext context;
        private readonly QwLabel wave;
        private readonly QwLabel score;
        private readonly QwLabel threat;
        private readonly QwLabel tally;
        private readonly QwLabel state;
        private readonly QwStatBar integrity;
        private readonly QwStatBar zapper;

        public ThreatPanel(HudContext context, QwContainer parent)
        {
            this.context = context;
            var panel = parent.Panel(QwPanelStyle.HudPanel).Dock(QwCorner.TopLeft, 18f).Size(380f, 232f).Dynamic();

            var title = panel.Label("ZOMBIES // LIVE FIRE", QwTextStyle.Heading).Tone(QwTone.Success);
            HudContext.Place(title, 18f, 14f, 330f, 28f);

            wave = panel.Label(QwTextStyle.Numeral);
            HudContext.Place(wave, 18f, 46f, 160f, 38f);

            score = panel.Label(QwTextStyle.Heading).Tone(QwTone.Warning).AlignRight();
            HudContext.Place(score, 188f, 52f, 172f, 28f);

            threat = panel.Label(QwTextStyle.Label).NoWrap();
            HudContext.Place(threat, 18f, 88f, 342f, 24f);

            tally = panel.Label(QwTextStyle.Caption).Tone(QwTone.Muted).NoWrap();
            HudContext.Place(tally, 18f, 114f, 342f, 22f);

            // Original color logic: danger below CriticalIntegrityThreshold, warning below
            // LowIntegrityVignetteThreshold, else success — exactly Thresholds(warn, crit).
            integrity = panel.StatBar("INTEGRITY");
            integrity.Thresholds(context.Config.LowIntegrityVignetteThreshold, context.Config.CriticalIntegrityThreshold);
            HudContext.Place(integrity, 18f, 146f, 220f, 18f);

            zapper = panel.StatBar("ZAPPER");
            zapper.Tone(QwTone.Accent);
            HudContext.Place(zapper, 250f, 146f, 110f, 18f);

            state = panel.Label(QwTextStyle.Label).Tone(QwTone.Muted).NoWrap();
            HudContext.Place(state, 18f, 176f, 342f, 28f);
        }

        public void Tick()
        {
            var controller = context.Controller;
            wave.SetText("WAVE ", controller.Wave);
            score.SetText(controller.Score.ToString("N0"));

            var allies = controller.ConvertedAllyCount;
            var line = "HOSTILES " + controller.HostileCount + "  //  INCOMING " + controller.RemainingToSpawn;
            if (allies > 0)
            {
                line += "  //  ALLIES " + allies;
                if (controller.WaveringAllyCount > 0)
                {
                    line += " (" + controller.WaveringAllyCount + " WAVERING)";
                }
            }

            threat.SetText(line);

            controller.GetArchetypeTally(out var grunts, out var sprinters, out var brutes, out var runts);
            tally.SetText("GRUNT " + grunts + "   SPRINTER " + sprinters + "   BRUTE " + brutes + "   RUNT " + runts);

            var integrityFraction = controller.MaxPlayerIntegrity > 0f
                ? controller.PlayerIntegrity / controller.MaxPlayerIntegrity
                : 0f;
            integrity.SetFraction(integrityFraction);
            integrity.SetLabel("INTEGRITY ", Mathf.CeilToInt(controller.PlayerIntegrity));

            zapper.SetFraction(controller.ZapperReadyFraction);
            state.SetText(controller.StateText.ToUpperInvariant());
        }
    }
}
