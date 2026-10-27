using System;
using System.Text;

namespace TopiaForge.Mods.Internal
{
    /// <summary>Removes local paths before text leaves a diagnostic boundary, on any host platform.</summary>
    internal static class DiagnosticPathRedactor
    {
        internal const string Placeholder = "[path]";
        private static readonly string Home = NormalizeHome(ReadHome());

        internal static string Redact(string? text) => Redact(text, Home, normalized: true);

        internal static string Redact(string? text, string home) => Redact(text, home, normalized: false);

        /// <summary>
        /// Finds the next local path at or after <paramref name="index"/>, as <see cref="Redact(string?)"/> removes it, and
        /// moves <paramref name="index"/> past it. <paramref name="quote"/> carries the quote a path may be inside from one
        /// call to the next; both start at zero.
        /// </summary>
        internal static bool TryFindPath(string text, ref int index, ref char quote, out int start, out int end) =>
            TryFindPath(text, Home, ref index, ref quote, out start, out end);

        private static string Redact(string? text, string home, bool normalized)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var value = text!;
            var match = normalized ? home : NormalizeHome(home);
            StringBuilder? result = null;
            var copied = 0;
            var index = 0;
            var quote = '\0';
            while (TryFindPath(value, match, ref index, ref quote, out var start, out var end))
            {
                result ??= new StringBuilder(value.Length);
                result.Append(value, copied, start - copied).Append(Placeholder);
                copied = end;
            }

            return result == null ? value : result.Append(value, copied, value.Length - copied).ToString();
        }

        private static bool TryFindPath(string text, string home, ref int index, ref char quote, out int start, out int end)
        {
            for (; index < text.Length; index++)
            {
                var value = text[index];
                if (value == '\r' || value == '\n') quote = '\0';
                if (value == '\'' || value == '"' || value == '`')
                {
                    if (quote == value) quote = '\0';
                    if (quote == '\0' && OpensQuote(text, index, home)) quote = value;
                    continue;
                }

                if (!StartsPath(text, index, quote) && !StartsHome(text, index, home)) continue;
                start = index;
                end = index;
                // Unquoted paths may contain spaces. With no reliable end marker, withhold the rest of the
                // line rather than leaking the account or directory after its first space. Stack frames on
                // subsequent lines remain useful; quoted paths retain the surrounding failure explanation.
                // An interior quote can precede any filename character, including spaces and punctuation. Use the last
                // matching quote on the line; multiple quoted fragments may be withheld together. Unquoted paths have
                // no reliable terminator. No matching quote remains in the suffix, so each quote style scans once.
                var closingQuote = -1;
                while (end < text.Length && text[end] != '\r' && text[end] != '\n')
                {
                    if (quote != '\0' && text[end] == quote) closingQuote = end;
                    end++;
                }

                if (closingQuote >= 0) end = closingQuote;
                index = end;
                return true;
            }

            start = text.Length;
            end = text.Length;
            return false;
        }

        // Only a quote immediately before a path opens its quoted form, even if it also closes an earlier path.
        // Punctuation in prose, or replacing an earlier address, must not change where an unquoted path ends.
        private static bool OpensQuote(string text, int index, string home) => index + 1 < text.Length &&
            (StartsPath(text, index + 1, text[index]) || StartsHome(text, index + 1, home));

        private static bool StartsPath(string text, int index, char quote)
        {
            var value = text[index];
            if (index + 2 < text.Length && IsLetter(value) && text[index + 1] == ':' && IsSlash(text[index + 2]) &&
                (index == 0 || !char.IsLetterOrDigit(text[index - 1]))) return true;
            if (index + 7 <= text.Length && string.Compare(text, index, "file://", 0, 7, StringComparison.OrdinalIgnoreCase) == 0) return true;
            if (!IsSlash(value)) return false;
            // A slash on its own, as in "Wave 3 / 10", is punctuation: a root that names nothing reveals nothing.
            // A root immediately inside quotes can instead name a component beginning with whitespace.
            var quotedRoot = quote != '\0' && index > 0 && text[index - 1] == quote;
            if (!quotedRoot && (index + 1 == text.Length || char.IsWhiteSpace(text[index + 1]))) return false;
            if (index == 0) return true;
            var previous = text[index - 1];
            // A web URL is not a filesystem root. Address redaction is the multiplayer boundary's job.
            if (value == '/' && previous == ':' && index > 1 && IsLetter(text[index - 2])) return false;
            return char.IsWhiteSpace(previous) || "\"'`([{=,:;".IndexOf(previous) >= 0;
        }

        private static bool StartsHome(string text, int index, string home)
        {
            if (home.Length < 2 || index + home.Length > text.Length) return false;
            for (var offset = 0; offset < home.Length; offset++)
            {
                if (char.ToUpperInvariant(text[index + offset] == '\\' ? '/' : text[index + offset]) !=
                    char.ToUpperInvariant(home[offset])) return false;
            }

            return true;
        }

        private static bool IsSlash(char value) => value == '/' || value == '\\';
        private static bool IsLetter(char value) => (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

        private static string NormalizeHome(string home) => home.Replace('\\', '/').TrimEnd('/');

        private static string ReadHome()
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            catch { return string.Empty; }
        }
    }
}
