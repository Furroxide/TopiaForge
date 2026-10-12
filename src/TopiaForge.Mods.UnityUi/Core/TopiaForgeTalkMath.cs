using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Voice state shown by a talk indicator.</summary>
    public enum TopiaForgeTalkState
    {
        /// <summary>Voice is available and silent.</summary>
        Idle,

        /// <summary>The player is transmitting or being heard.</summary>
        Talking,

        /// <summary>The player muted themselves.</summary>
        Muted,

        /// <summary>The session host muted the player.</summary>
        MutedByHost,

        /// <summary>Another feature holds the microphone.</summary>
        MicBusy,

        /// <summary>No microphone is available.</summary>
        NoMic,
    }

    /// <summary>The glyph a talk indicator draws.</summary>
    public enum TopiaForgeTalkGlyph
    {
        /// <summary>A microphone.</summary>
        Microphone,

        /// <summary>A struck-through microphone.</summary>
        MicrophoneOff,

        /// <summary>A warning mark: the microphone is in use elsewhere.</summary>
        Busy,

        /// <summary>A cross: there is no device.</summary>
        NoDevice,
    }

    /// <summary>The halo drawn around a talk glyph.</summary>
    public enum TopiaForgeTalkRing
    {
        /// <summary>No halo.</summary>
        None,

        /// <summary>A fixed halo.</summary>
        Static,

        /// <summary>A halo that breathes while motion is allowed.</summary>
        Pulsing,
    }

    /// <summary>
    /// The shape-level presentation of a talk state. Every state differs from every other in glyph
    /// or halo, so the indicator stays meaningful without color.
    /// </summary>
    public readonly struct TopiaForgeTalkPresentation
    {
        private TopiaForgeTalkPresentation(TopiaForgeTalkGlyph glyph, TopiaForgeTalkRing ring, string caption)
        {
            Glyph = glyph;
            Ring = ring;
            Caption = caption;
        }

        /// <summary>Gets the glyph.</summary>
        public TopiaForgeTalkGlyph Glyph { get; }

        /// <summary>Gets the halo.</summary>
        public TopiaForgeTalkRing Ring { get; }

        /// <summary>Gets the cached caption shown beside the glyph when captions are enabled.</summary>
        public string Caption { get; }

        /// <summary>Returns the presentation for a state; unknown values present as idle.</summary>
        public static TopiaForgeTalkPresentation For(TopiaForgeTalkState state)
        {
            switch (state)
            {
                case TopiaForgeTalkState.Talking:
                    return new TopiaForgeTalkPresentation(TopiaForgeTalkGlyph.Microphone, TopiaForgeTalkRing.Pulsing, "TALKING");
                case TopiaForgeTalkState.Muted:
                    return new TopiaForgeTalkPresentation(TopiaForgeTalkGlyph.MicrophoneOff, TopiaForgeTalkRing.None, "MUTED");
                case TopiaForgeTalkState.MutedByHost:
                    return new TopiaForgeTalkPresentation(TopiaForgeTalkGlyph.MicrophoneOff, TopiaForgeTalkRing.Static, "MUTED BY HOST");
                case TopiaForgeTalkState.MicBusy:
                    return new TopiaForgeTalkPresentation(TopiaForgeTalkGlyph.Busy, TopiaForgeTalkRing.None, "MIC BUSY");
                case TopiaForgeTalkState.NoMic:
                    return new TopiaForgeTalkPresentation(TopiaForgeTalkGlyph.NoDevice, TopiaForgeTalkRing.None, "NO MIC");
                default:
                    return new TopiaForgeTalkPresentation(TopiaForgeTalkGlyph.Microphone, TopiaForgeTalkRing.None, "IDLE");
            }
        }
    }

    /// <summary>
    /// The subtle breathing curve shared by the talking halo and the active phase step. Amplitude
    /// follows the effective motion intensity, and zero motion (reduced motion) yields a still
    /// mark at <see cref="RestAlpha"/>.
    /// </summary>
    public static class TopiaForgeBreath
    {
        /// <summary>Breaths per second.</summary>
        public const float FrequencyHertz = 1.6f;

        /// <summary>Halo opacity when still, and at the top of each breath.</summary>
        public const float RestAlpha = 0.9f;

        /// <summary>Halo opacity at the bottom of a full-intensity breath.</summary>
        public const float MinAlpha = 0.4f;

        /// <summary>Extra halo scale per unit of motion intensity at the top of a breath.</summary>
        public const float GrowthPerMotion = 0.08f;

        /// <summary>The largest motion intensity the curve honours.</summary>
        public const float MaxMotion = 2f;

        /// <summary>Evaluates the halo's scale and opacity at a time, for a motion intensity.</summary>
        public static void Evaluate(float timeSeconds, float motion, out float scale, out float alpha)
        {
            if (float.IsNaN(motion) || motion <= 0f)
            {
                scale = 1f;
                alpha = RestAlpha;
                return;
            }

            if (motion > MaxMotion)
            {
                motion = MaxMotion;
            }

            var breath = Breath(timeSeconds);
            var depth = motion < 1f ? motion : 1f;
            scale = 1f + (GrowthPerMotion * motion * breath);
            alpha = RestAlpha - ((RestAlpha - MinAlpha) * depth * (1f - breath));
        }

        private static float Breath(float timeSeconds)
        {
            if (float.IsNaN(timeSeconds) || float.IsInfinity(timeSeconds))
            {
                return 1f;
            }

            var cycles = (double)timeSeconds * FrequencyHertz;
            var phase = cycles - Math.Floor(cycles);
            return (float)(0.5 + (0.5 * Math.Sin(phase * Math.PI * 2.0)));
        }
    }
}
