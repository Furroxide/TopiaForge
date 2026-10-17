using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// Text another player wrote, prepared for a label: a host's server name, gamemode, world, kick message or admission
    /// lines, or a player's display name. A host may write anything there, an address included, so every such text is run
    /// through the runtime's redactor first, then bounded to one plain line. Labels still render it as plain text.
    /// </summary>
    internal static class HostText
    {
        internal const int NameChars = 48;
        internal const int LineChars = 256;
        internal const string HostSaysPrefix = "The host says: ";

        /// <summary>Redacts and bounds <paramref name="text"/> for display; empty stays empty.</summary>
        internal static string ForDisplay(string? text, int maximumChars = NameChars)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return TopiaForgePlainText.Sanitize(MultiplayerDiagnosticRedactor.Redact(text), maximumChars);
        }

        /// <summary>Attributes the host's own words to it, redacted; empty when it said nothing.</summary>
        internal static string HostSays(string? message)
        {
            var shown = ForDisplay(message, LineChars);
            return shown.Length == 0 ? string.Empty : HostSaysPrefix + shown;
        }
    }
}
