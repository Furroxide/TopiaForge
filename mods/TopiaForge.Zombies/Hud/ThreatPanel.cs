using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.Zombies
{
    /// <summary>
    /// Top-left threat readout: wave/score, hostile/incoming/ally line, archetype
    /// tally, the integrity bar (success/warning/danger thresholds from config), the
    /// zapper readiness bar, and the state line.
    /// </summary>
    internal sealed class ThreatPanel
    {
        private readonly HudContext context;
        private readonly TopiaForgeLabel wave;
        private readonly TopiaForgeLabel score;
        private readonly TopiaForgeLabel threat;
        private readonly TopiaForgeLabel tally;
        private readonly TopiaForgeLabel state;
        private readonly TopiaForgeLabel? credits;
        private readonly TopiaForgeStatBar integrity;
        private readonly TopiaForgeStatBar zapper;
        private int lastHostiles = int.MinValue;
        private int lastIncoming = int.MinValue;
        private int lastAllies = int.MinValue;
        private int lastWavering = int.MinValue;
        private string lastState = string.Empty;

        public ThreatPanel(HudContext context, TopiaForgeContainer parent)
        {
            this.context = context;
            // The credits row only exists when the shop does; without it the panel keeps its old height.
            var shopEnabled = context.Config.ShopEnabled;
            var panel = parent.Panel(TopiaForgePanelStyle.HudPanel).Dock(TopiaForgeCorner.TopLeft, 18f).Size(380f, shopEnabled ? 258f : 232f).Dynamic();

            var title = panel.Label("ZOMBIES // LIVE FIRE", TopiaForgeTextStyle.Heading).Tone(TopiaForgeTone.Success);
            HudContext.Place(title, 18f, 14f, 330f, 28f);

            wave = panel.Label(TopiaForgeTextStyle.Numeral);
            HudContext.Place(wave, 18f, 46f, 160f, 38f);

            score = panel.Label(TopiaForgeTextStyle.Heading).Tone(TopiaForgeTone.Warning).AlignRight();
            HudContext.Place(score, 188f, 52f, 172f, 28f);

            threat = panel.Label(TopiaForgeTextStyle.Label).NoWrap();
            HudContext.Place(threat, 18f, 88f, 342f, 24f);

            tally = panel.Label(TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).NoWrap();
            HudContext.Place(tally, 18f, 114f, 342f, 22f);

            // Original color logic: danger below CriticalIntegrityThreshold, warning below
            // LowIntegrityVignetteThreshold, else success — exactly Thresholds(warn, crit).
            integrity = panel.StatBar("INTEGRITY");
            integrity.Thresholds(context.Config.LowIntegrityVignetteThreshold, context.Config.CriticalIntegrityThreshold);
            HudContext.Place(integrity, 18f, 146f, 220f, 18f);

            zapper = panel.StatBar("ZAPPER");
            zapper.Tone(TopiaForgeTone.Accent);
            HudContext.Place(zapper, 250f, 146f, 110f, 18f);

            state = panel.Label(TopiaForgeTextStyle.Label).Tone(TopiaForgeTone.Muted).NoWrap();
            HudContext.Place(state, 18f, 176f, 342f, 28f);

            if (shopEnabled)
            {
                credits = panel.Label(TopiaForgeTextStyle.Label).Tone(TopiaForgeTone.Warning).NoWrap();
                HudContext.Place(credits, 18f, 204f, 342f, 24f);
            }
        }

        public void Tick()
        {
            var controller = context.Controller;
            wave.SetText("WAVE ", controller.Wave);
            score.SetNumber(controller.Score, "N0");

            var allies = controller.ConvertedAllyCount;
            var hostiles = controller.HostileCount;
            var incoming = controller.RemainingToSpawn;
            var wavering = controller.WaveringAllyCount;
            if (hostiles != lastHostiles || incoming != lastIncoming || allies != lastAllies || wavering != lastWavering)
            {
                var line = "HOSTILES " + hostiles + "  //  INCOMING " + incoming;
                if (allies > 0)
                {
                    line += "  //  ALLIES " + allies;
                    if (wavering > 0)
                    {
                        line += " (" + wavering + " WAVERING)";
                    }
                }

                lastHostiles = hostiles;
                lastIncoming = incoming;
                lastAllies = allies;
                lastWavering = wavering;
                threat.SetText(line);
            }

            controller.GetArchetypeTally(out var grunts, out var sprinters, out var brutes, out var runts);
            tally.SetText("GRUNT ", grunts, "   SPRINTER ", sprinters, "   BRUTE ", brutes, "   RUNT ", runts);

            var integrityFraction = controller.MaxPlayerIntegrity > 0f
                ? controller.PlayerIntegrity / controller.MaxPlayerIntegrity
                : 0f;
            integrity.SetFraction(integrityFraction);
            integrity.SetLabel("INTEGRITY ", Mathf.CeilToInt(controller.PlayerIntegrity));

            zapper.SetFraction(controller.ZapperReadyFraction);
            var stateText = controller.StateText;
            if (!string.Equals(lastState, stateText, System.StringComparison.Ordinal))
            {
                lastState = stateText;
                state.SetText(stateText.ToUpperInvariant());
            }

            credits?.SetText("CREDITS ", controller.Credits);
        }
    }
}
