using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>
    /// HUD primitives demo: spawns a live HUD layer with bars, pips, a banner, and
    /// a banner. World-projected primitives are exercised by runtime adapter tests, keeping
    /// this consumer independent from engine vector and color types.
    /// </summary>
    internal static class HudPage
    {
        private static TopiaForgeHudLayer? hud;
        private static TopiaForgeStatBar? integrity;
        private static TopiaForgePipRow? pips;
        private static TopiaForgeBanner? banner;
        private static float demoIntegrity = 0.87f;

        public static void Build(TopiaForgeContainer page)
        {
            var host = page.Host;

            page.SectionHeader("LIVE HUD DEMO");
            page.Label("Spawns a real HUD layer (dark scheme, corner-docked, dirty-checked bars and pips). Close the gallery to see it over gameplay.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            var row = page.Row(TopiaForgeGap.Sm);
            row.Button("SPAWN HUD", () => EnsureHud(host), TopiaForgeButtonStyle.Outline);
            row.Button("DESPAWN", DestroyHud, TopiaForgeButtonStyle.Ghost);

            page.SectionHeader("DRIVE IT");
            page.Slider("Integrity", 0f, 1f, demoIntegrity, value =>
            {
                demoIntegrity = value;
                integrity?.SetFraction(value);
                integrity?.SetLabel("INTEGRITY " + (int)Math.Ceiling(value * 100f));
            });
            var drive = page.Row(TopiaForgeGap.Sm);
            drive.Button("BANNER", () => banner?.Show("WAVE 3"), TopiaForgeButtonStyle.Outline);
            drive.Button("PIP DRAIN", () => pips?.SetFilled(1, 0.4f), TopiaForgeButtonStyle.Ghost);
        }

        private static void EnsureHud(UiHost host)
        {
            if (hud != null)
            {
                return;
            }

            hud = host.HudLayer("gallery-hud");
            var panel = hud.Scaled.Panel(TopiaForgePanelStyle.HudPanel);
            panel.Dock(TopiaForgeCorner.TopLeft).Size(340f, 190f);
            var column = panel.Column(TopiaForgeGap.Sm, TopiaForgeGap.Md);
            column.Label("GALLERY // HUD DEMO", TopiaForgeTextStyle.Heading).Tone(TopiaForgeTone.Success);
            column.Label("WAVE ", TopiaForgeTextStyle.Numeral).SetText("WAVE ", 3);
            integrity = column.StatBar("INTEGRITY 87");
            integrity.Thresholds(0.5f, 0.25f);
            integrity.SetFraction(demoIntegrity);
            pips = column.PipRow();
            pips.SetCount(5);
            pips.SetFilled(4, 0.2f);

            banner = hud.Banner();
        }

        private static void DestroyHud()
        {
            if (hud != null)
            {
                hud.Destroy();
                hud = null;
                integrity = null;
                pips = null;
                banner = null;
            }
        }

        public static void Reset()
        {
            DestroyHud();
        }
    }
}
