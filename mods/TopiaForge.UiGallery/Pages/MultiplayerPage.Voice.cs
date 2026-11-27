using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.UiGallery.Pages
{
    internal static partial class MultiplayerPage
    {
        private const float DefaultThresholdDecibels = -36f;

        private static void BuildVoice(TopiaForgeContainer page, Demo demo)
        {
            page.SectionHeader("TALK INDICATORS");
            page.Label(
                "Every state differs in glyph or halo, not only in tone. The talking halo breathes with the motion setting and holds still under reduced motion.",
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
            var states = page.Row(TopiaForgeGap.Lg);
            foreach (var state in (TopiaForgeTalkState[])Enum.GetValues(typeof(TopiaForgeTalkState)))
            {
                states.TalkIndicator().ShowCaption().SetState(state);
            }

            page.SectionHeader("MICROPHONE LEVEL");
            page.Label(
                "A synthetic voice drives the meter every frame. The marker is the voice-activation threshold: the glyph beside the slider "
                + "talks only above it. SHOUT pushes into the hot and clipping zones; the thin mark after the bar is the peak hold.",
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
            var meter = page.LevelMeter().FixedHeight(14f);
            meter.SetThreshold(DefaultThresholdDecibels);
            demo.Meter = meter;

            var controls = page.Row(TopiaForgeGap.Md);
            demo.LiveTalk = controls.TalkIndicator().ShowCaption();
            controls.Slider("Threshold dB", -60f, 0f, DefaultThresholdDecibels, value =>
            {
                demo.ThresholdDecibels = value;
                meter.SetThreshold(value);
            }).Flex(1f, 0f);
            controls.Button("SHOUT", demo.Shout, TopiaForgeButtonStyle.Outline);
            controls.Toggle("Mute", false, value => demo.Muted = value);
        }

        private sealed partial class Demo
        {
            private readonly Random hiss = new Random(1234);
            private double clock;
            private float shoutLeft;
            private float talkHold;

            public TopiaForgeLevelMeter? Meter { get; set; }

            public TopiaForgeTalkIndicator? LiveTalk { get; set; }

            public float ThresholdDecibels { get; set; } = DefaultThresholdDecibels;

            public bool Muted { get; set; }

            public void Shout()
            {
                shoutLeft = 0.8f;
            }

            private void TickVoice(float deltaTime)
            {
                if (Meter == null)
                {
                    return;
                }

                // Phrases at about 0.3 Hz carrying syllables at about 4 Hz, over a faint seeded hiss.
                clock += deltaTime;
                var phrase = 0.5 + (0.5 * Math.Sin(clock * Math.PI * 2.0 * 0.31));
                var syllable = Math.Max(0.0, Math.Sin(clock * Math.PI * 2.0 * 4.3));
                var noise = hiss.NextDouble();
                var rms = phrase > 0.4 ? 0.02 + (0.22 * syllable * phrase) : 0.002 + (0.003 * noise);
                if (shoutLeft > 0f)
                {
                    shoutLeft -= deltaTime;
                    rms = 0.6 + (0.35 * syllable);
                }

                var peak = Math.Min(1.0, rms * (1.25 + (0.3 * noise)));
                if (Muted)
                {
                    rms = 0.0;
                    peak = 0.0;
                }

                Meter.SetLevel((float)rms, (float)peak);
                var active = !Muted && TopiaForgeLevelMath.ToDecibels((float)rms) >= ThresholdDecibels;
                talkHold = active ? 0.3f : Math.Max(0f, talkHold - deltaTime);
                LiveTalk?.SetState(Muted ? TopiaForgeTalkState.Muted : talkHold > 0f ? TopiaForgeTalkState.Talking : TopiaForgeTalkState.Idle);
            }
        }
    }
}
