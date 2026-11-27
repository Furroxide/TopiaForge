using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Bounds and neutralizes untrusted display text (player names, chat messages) before it reaches
    /// a label. Control characters and every kind of whitespace become single spaces, runs collapse,
    /// bidirectional overrides are removed so text cannot visually reorder its neighbours, lone
    /// surrogates become U+FFFD, and long text is cut on a character boundary with an ellipsis.
    /// Labels that show the result must also disable rich text and escape parsing.
    /// </summary>
    public static class TopiaForgePlainText
    {
        /// <summary>The ellipsis appended to truncated text.</summary>
        public const char Ellipsis = UiPlainText.Ellipsis;

        /// <summary>
        /// Returns single-line display text of at most <paramref name="maxChars"/> UTF-16 code units.
        /// Clean input that already fits is returned as the same instance, so repeated calls with an
        /// unchanged value allocate nothing.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxChars"/> is less than one.</exception>
        public static string Sanitize(string? text, int maxChars) => UiPlainText.Sanitize(text, maxChars);
    }
}
