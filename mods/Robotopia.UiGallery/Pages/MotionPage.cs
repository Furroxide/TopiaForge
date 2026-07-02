using Robotopia.Mods.UnityUi;

namespace Robotopia.UiGallery.Pages
{
    /// <summary>Motion system demo: presets, pulse, and the motion-intensity scalar.</summary>
    internal static class MotionPage
    {
        public static void Build(QwContainer page)
        {
            page.SectionHeader("MOTION SETTINGS");
            page.Slider("Motion intensity", 0f, 2f, QwTheme.MotionScale, value => QwTheme.MotionScale = value);
            page.Label("0 disables pulses and punches (the accessibility contract every HUD inherits).", QwTextStyle.Caption).Tone(QwTone.Muted);

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
    }
}
