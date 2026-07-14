using Robotopia.Mods.UnityUi;

namespace Robotopia.UiGallery.Pages
{
    /// <summary>Motion system demo: presets, pulse, and the motion-intensity scalar.</summary>
    internal static class MotionPage
    {
        public static void Build(QwContainer page)
        {
            var host = page.Host;
            page.SectionHeader("MOTION SETTINGS");
            page.Slider(
                "Motion intensity",
                0f,
                2f,
                host.AccessibilityProfile.MotionIntensity,
                value => SetMotionIntensity(host, value));
            page.Label("0 disables this host's pulses and punches without mutating another mod's UI.", QwTextStyle.Caption).Tone(QwTone.Muted);

            page.SectionHeader("PRESETS");
            var target = page.Panel(QwPanelStyle.Card);
            target.FixedHeight(72f);
            var inner = target.Column(QwGap.Xs, QwGap.Md);
            inner.Label("ANIMATION TARGET", QwTextStyle.Heading);
            inner.Label("Watch this card.", QwTextStyle.Caption).Tone(QwTone.Muted);

            var row = page.Row(QwGap.Sm);
            row.Button("FADE", () => QwTween.FadeTo(target, 0f, 1f, QwTokens.DurationSlow), QwButtonStyle.Outline);
            row.Button("POP", () => QwTween.ScaleTo(target, 0.9f, 1f, QwTokens.DurationSlow, QwEase.OutBack), QwButtonStyle.Outline);
            row.Button("PUNCH", () => QwMotion.Punch(target), QwButtonStyle.Outline);

            page.SectionHeader("PULSE");
            var pulseRow = page.Row(QwGap.Sm);
            var pulseBadge = pulseRow.Badge("REACTOR CRITICAL", QwTone.Danger);
            QwMotion.Pulse(pulseBadge, frequency: 2f, alphaAmplitude: 0.25f, scaleAmplitude: 0.04f);
            pulseRow.Label("Breathing pulse — amplitude follows the motion slider.", QwTextStyle.Caption).Tone(QwTone.Muted);
        }

        private static void SetMotionIntensity(UiHost host, float value)
        {
            var current = host.AccessibilityProfile;
            host.SetAccessibilityProfile(new QwAccessibilityProfile(
                current.HighContrast,
                current.UiScale,
                current.ReducedMotion,
                value));
        }
    }
}
