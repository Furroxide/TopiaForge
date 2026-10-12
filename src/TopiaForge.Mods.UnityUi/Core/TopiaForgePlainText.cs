using System;
using System.Text;

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
        public const char Ellipsis = (char)0x2026;

        private const char Replacement = (char)0xFFFD;

        /// <summary>
        /// Returns single-line display text of at most <paramref name="maxChars"/> UTF-16 code units.
        /// Clean input that already fits is returned as the same instance, so repeated calls with an
        /// unchanged value allocate nothing.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxChars"/> is less than one.</exception>
        public static string Sanitize(string? text, int maxChars)
        {
            if (maxChars < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxChars), "The display bound must allow at least one character.");
            }

            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            if (text!.Length <= maxChars && IsClean(text))
            {
                return text;
            }

            var builder = new StringBuilder(text.Length <= maxChars ? text.Length : maxChars + 1);
            var pendingSpace = false;
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (IsBidiControl(character))
                {
                    continue;
                }

                if (char.IsWhiteSpace(character) || char.IsControl(character))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    builder.Append(character).Append(text[index + 1]);
                    index++;
                }
                else if (char.IsSurrogate(character))
                {
                    builder.Append(Replacement);
                }
                else
                {
                    builder.Append(character);
                }

                if (builder.Length > maxChars)
                {
                    // Enough is known to truncate; never walk the rest of an oversized input.
                    break;
                }
            }

            if (builder.Length <= maxChars)
            {
                return builder.ToString();
            }

            var cut = maxChars - 1;
            if (cut > 0 && char.IsLowSurrogate(builder[cut]) && char.IsHighSurrogate(builder[cut - 1]))
            {
                cut--;
            }

            while (cut > 0 && builder[cut - 1] == ' ')
            {
                cut--;
            }

            builder.Length = cut;
            builder.Append(Ellipsis);
            return builder.ToString();
        }

        private static bool IsClean(string text)
        {
            var previousWasSpace = true;
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (character == ' ')
                {
                    if (previousWasSpace)
                    {
                        return false;
                    }

                    previousWasSpace = true;
                    continue;
                }

                if (char.IsWhiteSpace(character) || char.IsControl(character) || IsBidiControl(character))
                {
                    return false;
                }

                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                    {
                        return false;
                    }

                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    return false;
                }

                previousWasSpace = false;
            }

            return !previousWasSpace;
        }

        private static bool IsBidiControl(char character)
        {
            return character == (char)0x061C
                || character == (char)0x200E
                || character == (char)0x200F
                || (character >= (char)0x202A && character <= (char)0x202E)
                || (character >= (char)0x2066 && character <= (char)0x2069);
        }
    }
}
