using Robotopia.Mods.UnityUi;
using UnityEngine;

namespace Robotopia.Zombies
{
    /// <summary>
    /// Center-screen reticle + hit markers on a dynamic (canvas-isolated) stack.
    /// Ported verbatim: gap = base + (1 - zapperReady) * bloom + charge * bloom, the
    /// 0.08s white crosshair hit flash, the 18 → 42/30 marker radius lerp with per-kind
    /// colors over HitMarkerSeconds, and the charge-full amber / hijackable violet
    /// reticle coloring.
    /// </summary>
    internal sealed class ReticleLayer
    {
        private const float HitFlashSeconds = 0.08f;
        private const float TickLength = 12f;
        private const float TickThickness = 3f;

        private readonly HudContext context;
        private readonly QwImage[] ticks = new QwImage[5];
        private readonly QwImage[] markers = new QwImage[4];

        private float crosshairHitTime = -999f;
        private float chargeFraction;
        private float markerTime = -999f;
        private ZombieHitKind markerKind;

        public ReticleLayer(HudContext context, QwContainer parent)
        {
            this.context = context;
            var stack = parent.Stack("Reticle").Dynamic();

            for (var index = 0; index < ticks.Length; index++)
            {
                ticks[index] = stack.FreeImage("Reticle" + index);
                HudContext.CenterAnchor(ticks[index]);
            }

            for (var index = 0; index < markers.Length; index++)
            {
                markers[index] = stack.FreeImage("HitMarker" + index);
                HudContext.CenterAnchor(markers[index]);
                markers[index].SetSize(4f, 22f);
                markers[index].SetRotation(index < 2 ? 45f : -45f);
                markers[index].SetAlpha(0f);
            }
        }

        public void FlashHitMarker(ZombieHitKind kind)
        {
            markerKind = kind;
            markerTime = Time.time;
        }

        public void FlashCrosshairHit()
        {
            crosshairHitTime = Time.time;
        }

        public void SetChargeFraction(float fraction)
        {
            chargeFraction = Mathf.Clamp01(fraction);
        }

        public void Reset()
        {
            crosshairHitTime = -999f;
            markerTime = -999f;
            chargeFraction = 0f;
            for (var index = 0; index < markers.Length; index++)
            {
                markers[index].SetAlpha(0f);
            }
        }

        public void Tick()
        {
            var controller = context.Controller;
            var config = context.Config;

            var gap = config.CrosshairBaseGapPixels + ((1f - controller.ZapperReadyFraction) * config.CrosshairBloomGapPixels);
            gap += chargeFraction * config.CrosshairBloomGapPixels;

            var hitFlash = Mathf.Clamp01(1f - ((Time.time - crosshairHitTime) / HitFlashSeconds));
            var baseColor = ReticleColor();
            var color = hitFlash > 0f
                ? Color.Lerp(baseColor, context.Theme.ToneColor(QwTone.Neutral), hitFlash)
                : baseColor;

            SetTick(ticks[0], 0f, gap + (TickLength * 0.5f), TickThickness, TickLength, color);
            SetTick(ticks[1], 0f, -gap - (TickLength * 0.5f), TickThickness, TickLength, color);
            SetTick(ticks[2], -gap - (TickLength * 0.5f), 0f, TickLength, TickThickness, color);
            SetTick(ticks[3], gap + (TickLength * 0.5f), 0f, TickLength, TickThickness, color);
            var dot = color;
            dot.a = 0.72f;
            SetTick(ticks[4], 0f, 0f, 3f, 3f, dot);

            TickHitMarkers();
        }

        private void TickHitMarkers()
        {
            var config = context.Config;
            var age = Time.time - markerTime;
            if (age >= config.HitMarkerSeconds)
            {
                for (var index = 0; index < markers.Length; index++)
                {
                    markers[index].SetAlpha(0f);
                }

                return;
            }

            var t = age / Mathf.Max(0.01f, config.HitMarkerSeconds);
            var isKill = markerKind == ZombieHitKind.Kill || markerKind == ZombieHitKind.HeadshotKill;
            var headshot = markerKind == ZombieHitKind.Headshot || markerKind == ZombieHitKind.HeadshotKill;
            var radius = Mathf.Lerp(18f, isKill ? 42f : 30f, t);
            var color = context.Theme.ToneColor(isKill
                ? QwTone.Danger
                : headshot ? QwTone.Warning : QwTone.Neutral);
            color.a = 1f - t;

            markers[0].SetPosition(-radius, radius);
            markers[1].SetPosition(radius, -radius);
            markers[2].SetPosition(radius, radius);
            markers[3].SetPosition(-radius, -radius);
            for (var index = 0; index < markers.Length; index++)
            {
                markers[index].SetColor(color);
            }
        }

        private Color ReticleColor()
        {
            if (chargeFraction >= 1f)
            {
                return context.Theme.ToneColor(QwTone.Warning);
            }

            var controller = context.Controller;
            return context.Theme.ToneColor(controller.OverrideHudEnabled && controller.OverrideAimingHijackable
                ? QwTone.Primary
                : QwTone.Accent);
        }

        private static void SetTick(QwImage image, float x, float y, float width, float height, Color color)
        {
            image.SetPosition(x, y);
            image.SetSize(width, height);
            image.SetColor(color);
        }
    }
}
