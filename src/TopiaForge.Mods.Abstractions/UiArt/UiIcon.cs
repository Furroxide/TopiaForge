using System;

namespace TopiaForge.Mods
{
    /// <summary>Identifies a built-in kit glyph with a stable plain-text label.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public enum UiGlyph
    {
        /// <summary>An accepted or completed state.</summary>
        Check = 0,
        /// <summary>A failed or declined state.</summary>
        Cross = 1,
        /// <summary>A downward disclosure.</summary>
        Down = 2,
        /// <summary>The next item or step.</summary>
        Next = 3,
        /// <summary>A search action.</summary>
        Search = 4,
        /// <summary>A warning that may require attention.</summary>
        Warning = 5,
        /// <summary>An informational message.</summary>
        Info = 6,
        /// <summary>An available use action.</summary>
        Use = 7,
        /// <summary>A microphone.</summary>
        Microphone = 8,
        /// <summary>A microphone with capture off.</summary>
        MicrophoneOff = 9,
        /// <summary>A conversation or speaking state.</summary>
        Talk = 10,
        /// <summary>A vote or ballot.</summary>
        Vote = 11,
        /// <summary>A timer or countdown.</summary>
        Timer = 12,
        /// <summary>A locked or unavailable state.</summary>
        Lock = 13,
        /// <summary>A map or location overview.</summary>
        Map = 14,
        /// <summary>Written notes.</summary>
        Notes = 15,
        /// <summary>A visibility or inspection affordance.</summary>
        Eye = 16,
        /// <summary>A notification or alert.</summary>
        Bell = 17,
        /// <summary>A marked point or objective.</summary>
        Flag = 18,
        /// <summary>A highlighted or favorite item.</summary>
        Star = 19,
        /// <summary>Health or integrity.</summary>
        Health = 20,
        /// <summary>Protection or armor.</summary>
        Shield = 21,
        /// <summary>Energy or charge.</summary>
        Energy = 22,
        /// <summary>Settings or configuration.</summary>
        Settings = 23,
        /// <summary>One player.</summary>
        Player = 24,
        /// <summary>A group of players.</summary>
        Players = 25,
        /// <summary>The session host.</summary>
        Host = 26,
        /// <summary>Signal or connection quality.</summary>
        Signal = 27,
        /// <summary>Audio output or volume.</summary>
        Volume = 28,
        /// <summary>A leave or exit action.</summary>
        Leave = 29,
        /// <summary>A play or start action.</summary>
        Play = 30
    }

    /// <summary>Draws a built-in semantic glyph at a readable size.</summary>
    [RequiresTopiaForge(UiKitRelease.K1)]
    public sealed class UiIcon : UiNode
    {
        /// <summary>Creates an icon with a built-in label or a supplied plain-text label.</summary>
        /// <remarks>
        /// Size is between 18 and 128 reference units and follows readable UI scaling.
        /// A decorative icon accepts no label and is hidden from semantic output.
        /// </remarks>
        public UiIcon(UiGlyph glyph, string? semanticLabel = null, float size = 24,
            UiTone tone = UiTone.Neutral, bool decorative = false, string? id = null)
            : base(id)
        {
            UiGlyphLabels.Validate(glyph, nameof(glyph));
            if (!(size >= 18f && size <= 128f))
                throw new ArgumentOutOfRangeException(nameof(size), "Icon size must be between 18 and 128 units.");
            UiWidgetValues.Tone(tone);

            var label = semanticLabel ?? (decorative ? string.Empty : UiGlyphLabels.Get(glyph));
            UiArtValues.Label(label, decorative, nameof(semanticLabel));
            Glyph = glyph;
            SemanticLabel = label;
            Size = size;
            Tone = tone;
            Decorative = decorative;
        }

        /// <summary>Gets the built-in glyph to draw.</summary>
        public UiGlyph Glyph { get; }
        /// <summary>Gets the resolved label, with an empty value for decorative icons.</summary>
        public string SemanticLabel { get; }
        /// <summary>Gets the glyph size in reference units before readable scaling.</summary>
        public float Size { get; }
        /// <summary>Gets the semantic foreground tone.</summary>
        public UiTone Tone { get; }
        /// <summary>Gets whether this icon is purely visual.</summary>
        public bool Decorative { get; }
    }

    internal static class UiGlyphLabels
    {
        private static readonly string[] Labels =
        {
            "check", "cross", "down", "next", "search", "warning", "info", "use",
            "microphone", "microphone off", "talk", "vote", "timer", "lock", "map", "notes",
            "eye", "bell", "flag", "star", "health", "shield", "energy", "settings",
            "player", "players", "host", "signal", "volume", "leave", "play"
        };

        internal static void Validate(UiGlyph glyph, string parameter)
        {
            if ((uint)glyph >= (uint)Labels.Length)
                throw new ArgumentOutOfRangeException(parameter, "The glyph is not defined by the UI kit.");
        }

        internal static string Get(UiGlyph glyph) => Labels[(int)glyph];
    }
}
