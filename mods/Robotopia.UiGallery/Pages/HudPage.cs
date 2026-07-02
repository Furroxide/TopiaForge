using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.UiGallery.Pages
{
    /// <summary>
    /// HUD primitives demo: spawns a live HUD layer with bars, pips, a banner, and
    /// orbiting fake world anchors feeding the floater/speech pools.
    /// </summary>
    internal static class HudPage
    {
        private static QwHudLayer? hud;
        private static QwStatBar? integrity;
        private static QwPipRow? pips;
        private static QwBanner? banner;
        private static QwFloaterLayer? floaters;
        private static QwFloaterLayer? speech;
        private static float demoIntegrity = 0.87f;

        public static void Build(QwContainer page)
        {
            var host = page.Host;

            page.SectionHeader("LIVE HUD DEMO");
            page.Label("Spawns a real HUD layer (dark scheme, corner-docked, world-projected pools). Close the gallery to see it over gameplay.", QwTextStyle.Caption).Tone(QwTone.Muted);

            var row = page.Row(QwGap.Sm);
            row.Button("SPAWN HUD", () => EnsureHud(host), QwButtonStyle.Outline);
            row.Button("DESPAWN", DestroyHud, QwButtonStyle.Ghost);

            page.SectionHeader("DRIVE IT");
            page.Slider("Integrity", 0f, 1f, demoIntegrity, value =>
            {
                demoIntegrity = value;
                integrity?.SetFraction(value);
                integrity?.SetLabel("INTEGRITY " + Mathf.CeilToInt(value * 100f));
            });
            var drive = page.Row(QwGap.Sm);
            drive.Button("BANNER", () => banner?.Show("WAVE 3"), QwButtonStyle.Outline);
            drive.Button("FLOATER", () =>
            {
                var world = RandomWorldPoint();
                floaters?.Push(world, "+125", new Color(0.3f, 0.9f, 0.5f, 1f));
            }, QwButtonStyle.Outline);
            drive.Button("SPEECH", () =>
            {
                var world = RandomWorldPoint();
                speech?.Push(world, "You cannot patch what you do not understand.", Color.white, 2.5f);
            }, QwButtonStyle.Outline);
            drive.Button("PIP DRAIN", () => pips?.SetFilled(1, 0.4f), QwButtonStyle.Ghost);
        }

        private static void EnsureHud(UiHost host)
        {
            if (hud != null)
            {
                return;
            }

            hud = host.HudLayer("gallery-hud");
            var panel = hud.Scaled.Panel(QwPanelStyle.HudPanel);
            panel.Dock(QwCorner.TopLeft).Size(340f, 190f);
            var column = panel.Column(QwGap.Sm, QwGap.Md);
            column.Label("GALLERY // HUD DEMO", QwTextStyle.Heading).Tone(QwTone.Success);
            column.Label("WAVE ", QwTextStyle.Numeral).SetText("WAVE ", 3);
            integrity = column.StatBar("INTEGRITY 87");
            integrity.Thresholds(0.5f, 0.25f);
            integrity.SetFraction(demoIntegrity);
            pips = column.PipRow();
            pips.SetCount(5);
            pips.SetFilled(4, 0.2f);

            banner = hud.Banner();
            floaters = hud.Floaters();
            speech = hud.SpeechBubbles();
        }

        private static void DestroyHud()
        {
            if (hud != null)
            {
                Object.Destroy(hud.Go);
                hud = null;
                integrity = null;
                pips = null;
                banner = null;
                floaters = null;
                speech = null;
            }
        }

        private static Vector3 RandomWorldPoint()
        {
            var camera = Camera.main;
            if (camera == null)
            {
                return new Vector3(Random.Range(-2f, 2f), 1.5f, Random.Range(4f, 8f));
            }

            var forward = camera.transform;
            return forward.position + (forward.forward * Random.Range(4f, 8f)) + (forward.right * Random.Range(-2.5f, 2.5f));
        }
    }
}
