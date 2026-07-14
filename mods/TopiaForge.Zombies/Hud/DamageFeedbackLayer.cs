using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.Zombies
{
    /// <summary>
    /// Full-screen damage flash, the low-integrity vignette pulse, and the incoming-
    /// damage bearing wedge, on a dynamic (canvas-isolated) stack. Ported verbatim:
    /// flash alpha from DamageFlashSeconds/DamageFlashMaxAlpha, vignette pulse at
    /// 7.5 Hz (12.6 Hz when critical) scaled by the motion setting, max edge alpha
    /// 0.72 in high contrast vs 0.46 standard, and the wedge at -bearing fading over 1s.
    /// </summary>
    internal sealed class DamageFeedbackLayer
    {
        private const float LowPulseFrequency = 7.5f;
        private const float CriticalPulseFrequency = 12.6f;
        private const float HighContrastMaxAlpha = 0.72f;
        private const float StandardMaxAlpha = 0.46f;
        private const float WedgeSeconds = 1f;
        private const float WedgeMaxAlpha = 0.75f;

        private readonly HudContext context;
        private readonly TopiaForgeImage flash;
        private readonly TopiaForgeImage[] edges = new TopiaForgeImage[4];
        private readonly TopiaForgeImage wedge;

        private float damageTime = -999f;
        private float damageBearing;

        public DamageFeedbackLayer(HudContext context, TopiaForgeContainer parent)
        {
            this.context = context;
            var stack = parent.Stack("DamageFeedback").Dynamic();

            flash = stack.FreeImage("DamageFlash").Stretch();
            flash.SetTone(TopiaForgeTone.Danger);
            flash.SetAlpha(0f);

            for (var index = 0; index < edges.Length; index++)
            {
                edges[index] = stack.FreeImage("Vignette" + index);
                edges[index].SetTone(TopiaForgeTone.Danger);
                edges[index].SetAlpha(0f);
            }

            SetEdge(edges[0], new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, 90f));
            SetEdge(edges[1], new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 90f));
            SetEdge(edges[2], new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(90f, 0f));
            SetEdge(edges[3], new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(90f, 0f));

            wedge = stack.FreeImage("DamageBearing");
            HudContext.CenterAnchor(wedge);
            wedge.Rect.anchoredPosition = new Vector2(0f, 160f);
            wedge.SetSize(52f, 16f);
            wedge.SetTone(TopiaForgeTone.Danger);
            wedge.SetAlpha(0f);
        }

        public void FlashDamage(float bearingDegrees)
        {
            damageTime = Time.time;
            damageBearing = bearingDegrees;
        }

        public void Reset()
        {
            damageTime = -999f;
            flash.SetAlpha(0f);
            for (var index = 0; index < edges.Length; index++)
            {
                edges[index].SetAlpha(0f);
            }

            wedge.SetAlpha(0f);
        }

        public void Tick()
        {
            var controller = context.Controller;
            var config = context.Config;

            var flashAge = Time.time - damageTime;
            var flashAlpha = 0f;
            if (flashAge < config.DamageFlashSeconds)
            {
                flashAlpha = config.DamageFlashMaxAlpha * (1f - (flashAge / config.DamageFlashSeconds));
            }

            flash.SetAlpha(flashAlpha);

            var fraction = controller.MaxPlayerIntegrity > 0f
                ? controller.PlayerIntegrity / controller.MaxPlayerIntegrity
                : 1f;
            var edgeAlpha = 0f;
            if (fraction < config.LowIntegrityVignetteThreshold && config.LowIntegrityVignetteThreshold > 0f)
            {
                var intensity = (config.LowIntegrityVignetteThreshold - fraction) / config.LowIntegrityVignetteThreshold;
                var frequency = fraction < config.CriticalIntegrityThreshold ? CriticalPulseFrequency : LowPulseFrequency;
                var pulse = Mathf.Lerp(0.65f, 1f, 0.5f + (0.5f * Mathf.Sin(Time.time * frequency * context.Ui.EffectiveMotion)));
                edgeAlpha = Mathf.Lerp(
                    0f,
                    context.Ui.EffectiveHighContrast ? HighContrastMaxAlpha : StandardMaxAlpha,
                    intensity) * pulse;
            }

            for (var index = 0; index < edges.Length; index++)
            {
                edges[index].SetAlpha(edgeAlpha);
            }

            if (flashAge < WedgeSeconds)
            {
                wedge.SetRotation(-damageBearing);
                wedge.SetAlpha(WedgeMaxAlpha * (1f - flashAge));
            }
            else
            {
                wedge.SetAlpha(0f);
            }
        }

        private static void SetEdge(TopiaForgeImage image, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 size)
        {
            var rect = image.Rect;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
        }
    }
}
