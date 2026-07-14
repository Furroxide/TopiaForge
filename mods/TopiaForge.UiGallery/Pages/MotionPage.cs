using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    /// <summary>Motion system demo: presets, pulse, and the motion-intensity scalar.</summary>
    internal static class MotionPage
    {
        public static void Build(TopiaForgeContainer page)
        {
            var host = page.Host;
            page.SectionHeader("MOTION SETTINGS");
            page.Slider(
                "Motion intensity",
                0f,
                2f,
                host.AccessibilityProfile.MotionIntensity,
                value => SetMotionIntensity(host, value));
            page.Label("0 disables this host's pulses and punches without mutating another mod's UI.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            page.SectionHeader("PRESETS");
            var target = page.Panel(TopiaForgePanelStyle.Card);
            target.FixedHeight(72f);
            var inner = target.Column(TopiaForgeGap.Xs, TopiaForgeGap.Md);
            inner.Label("ANIMATION TARGET", TopiaForgeTextStyle.Heading);
            inner.Label("Watch this card.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);

            var row = page.Row(TopiaForgeGap.Sm);
            row.Button("FADE", () => TopiaForgeTween.FadeTo(target, 0f, 1f, TopiaForgeTokens.DurationSlow), TopiaForgeButtonStyle.Outline);
            row.Button("POP", () => TopiaForgeTween.ScaleTo(target, 0.9f, 1f, TopiaForgeTokens.DurationSlow, TopiaForgeEase.OutBack), TopiaForgeButtonStyle.Outline);
            row.Button("PUNCH", () => TopiaForgeMotion.Punch(target), TopiaForgeButtonStyle.Outline);

            page.SectionHeader("PULSE");
            var pulseRow = page.Row(TopiaForgeGap.Sm);
            var pulseBadge = pulseRow.Badge("REACTOR CRITICAL", TopiaForgeTone.Danger);
            TopiaForgeMotion.Pulse(pulseBadge, frequency: 2f, alphaAmplitude: 0.25f, scaleAmplitude: 0.04f);
            pulseRow.Label("Breathing pulse — amplitude follows the motion slider.", TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
        }

        private static void SetMotionIntensity(UiHost host, float value)
        {
            var current = host.AccessibilityProfile;
            host.SetAccessibilityProfile(new TopiaForgeAccessibilityProfile(
                current.HighContrast,
                current.UiScale,
                current.ReducedMotion,
                value));
        }
    }
}
