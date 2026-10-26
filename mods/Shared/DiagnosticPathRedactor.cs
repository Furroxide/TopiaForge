using System;
using System.Text;

namespace TopiaForge.Mods.Internal
{
    /// <summary>Removes local paths before text leaves a diagnostic boundary, on any host platform.</summary>
    internal static class DiagnosticPathRedactor
    {
        private static readonly string Home = ReadHome();

        internal static string Redact(string? text) => Redact(text, Home);

        internal static string Redact(string? text, string home)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            StringBuilder? result = null;
            var copied = 0;
            var quote = '\0';
            var normalizedHome = home.Replace('\\', '/').TrimEnd('/');
            for (var index = 0; index < text!.Length; index++)
            {
                var value = text[index];
                if (value == '\r' || value == '\n') quote = '\0';
                if (value == '\'' || value == '"' || value == '`')
                {
                    if (quote == value) quote = '\0';
                    else if (quote == '\0') quote = value;
                    continue;
                }

                if (!StartsPath(text, index) && !StartsHome(text, index, normalizedHome)) continue;
                var end = index;
                // Unquoted paths may contain spaces. With no reliable end marker, withhold the rest of the
                // line rather than leaking the account or directory after its first space. Stack frames on
                // subsequent lines remain useful; quoted paths retain the surrounding failure explanation.
                while (end < text.Length && text[end] != '\r' && text[end] != '\n' &&
                       (quote == '\0' ? text[end] != '\'' && text[end] != '"' && text[end] != '`' : text[end] != quote)) end++;
                result ??= new StringBuilder(text.Length);
                result.Append(text, copied, index - copied).Append("[path]");
                copied = end;
                index = end - 1;
            }

            return result == null ? text : result.Append(text, copied, text.Length - copied).ToString();
        }

        private static bool StartsPath(string text, int index)
        {
            var value = text[index];
            if (index + 2 < text.Length && IsLetter(value) && text[index + 1] == ':' && IsSlash(text[index + 2]) &&
                (index == 0 || !char.IsLetterOrDigit(text[index - 1]))) return true;
            if (index + 7 <= text.Length && string.Compare(text, index, "file://", 0, 7, StringComparison.OrdinalIgnoreCase) == 0) return true;
            if (!IsSlash(value)) return false;
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

        private static string ReadHome()
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
            catch { return string.Empty; }
        }
    }
}
