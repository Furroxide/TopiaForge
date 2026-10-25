using System;
using System.Text.RegularExpressions;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// Text another player wrote, prepared for a label: a host's server name, gamemode, world, kick message or admission
    /// lines, or a player's display name. A host may write anything there, an address included, so every such text is run
    /// through the runtime's redactor first, then bounded to one plain line. Labels and toasts render it as plain text, so
    /// markup in it shows as written.
    /// </summary>
    internal static class HostText
    {
        internal const int NameChars = 48;
        internal const int LineChars = 256;
        internal const string HostSaysPrefix = "The host says: ";

        // Four dotted numbers, as the redactor's IPv4 rule finds them, even inside a dotted name or a version.
        private static readonly Regex DottedQuad = new Regex(
            @"(?<![0-9])[0-9]{1,3}(?:\.[0-9]{1,3}){3}(?![0-9])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        /// <summary>Redacts and bounds <paramref name="text"/> for display; empty stays empty.</summary>
        internal static string ForDisplay(string? text, int maximumChars = NameChars)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var redacted = MultiplayerDiagnosticRedactor.Redact(text);
            var plain = TopiaForgePlainText.Sanitize(redacted, maximumChars);
            // Removing a direction control or splitting a token can reveal an address the original text did not contain.
            // Check the displayed form too, and keep the bound if its replacement is longer than the original token.
            return ReferenceEquals(plain, redacted) ? plain :
                TopiaForgePlainText.Sanitize(MultiplayerDiagnosticRedactor.Redact(plain), maximumChars);
        }

        /// <summary>Attributes the host's own words to it, redacted; empty when it said nothing.</summary>
        internal static string HostSays(string? message)
        {
            var shown = ForDisplay(message, LineChars);
            return shown.Length == 0 ? string.Empty : HostSaysPrefix + shown;
        }

        /// <summary>
        /// Prepares one of this game's own problem lines, from its check of a host's packages, for display. The words are
        /// this game's, but they name the host's packages by the ids and versions its descriptor gives, and a mod id or a
        /// version may be four dotted numbers: an IPv4 address the host chose, this player's own perhaps. Those go, as the
        /// redactor removes them. The rest stays readable. A descriptor's ids and versions hold no colon, slash, at sign
        /// or bracket, so no IPv6 address, URL or port can be among them; a dotted host name, or an IPv4 address in a
        /// hexadecimal, octal or shortened form, can. Those stay, because the redactor's rules for them would take every
        /// dotted mod id, and versions such as 1.0.300, with them.
        /// </summary>
        internal static string ForOwnLine(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string redacted;
            try
            {
                redacted = DottedQuad.Replace(text, MultiplayerDiagnosticRedactor.Replacement);
            }
            catch (RegexMatchTimeoutException)
            {
                return ForDisplay(text, LineChars);
            }

            return TopiaForgePlainText.Sanitize(redacted, LineChars);
        }
    }
}
