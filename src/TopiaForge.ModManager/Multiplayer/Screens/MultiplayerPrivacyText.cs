namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The approved player-facing wording on address exposure and microphone use, word for word from
    /// docs/internal/MultiplayerPrivacyText.md, approved by the maintainer on 2026-10-14 as a release gate. Every in-game
    /// screen takes these strings from here and nowhere else; MultiplayerPrivacyTextTests fails when any of them differs
    /// from the document. Changing their meaning needs a fresh approval before it ships.
    /// </summary>
    internal static class MultiplayerPrivacyText
    {
        /// <summary>
        /// The version of the first-time disclosure. Raising it after the wording changes shows it to every player again;
        /// the launcher's <c>MultiplayerSettings.currentPrivacyDisclosure</c> counts the same versions.
        /// </summary>
        internal const int DisclosureVersion = 1;

        /// <summary>The setting's name, as the approved wording calls it.</summary>
        internal const string HideMyIp = "Hide my IP";

        internal const string DisclosureTitle = "Your address and multiplayer";

        internal const string DisclosureExposure =
            "When you play directly with others, they can see your IP address and you can see theirs. An IP address shows "
            + "roughly where you are and which internet provider you use, and someone could use it to disrupt your connection.";

        internal const string DisclosureHidden =
            "Hide my IP is on by default: your sessions go through a relay or stay on your local network, so nobody else sees "
            + "your address. A relayed session can be a little slower.";

        internal const string DisclosureChoice =
            "You can allow a direct connection for one session when you choose to. TopiaForge never turns it on for you.";

        internal const string DisclosureButton = "I understand";

        internal const string DirectTitle = "Allow a direct connection?";

        internal const string DirectBody =
            "This session can only be reached directly. If you continue, the other players in it will see your IP address for "
            + "this session only. Hide my IP stays on for every other session.";

        internal const string DirectAllowButton = "Allow for this session";

        internal const string DirectCancelButton = "Cancel";

        /// <summary>The version of the first-use microphone consent, for the voice slice; the launcher counts the same versions.</summary>
        internal const int VoiceConsentVersion = 1;

        internal const string VoiceTitle = "Voice chat";

        internal const string VoiceBody =
            "Voice chat sends what your microphone picks up to the other players in the session, through the host's game. "
            + "TopiaForge does not record it. Voice stays off until you turn it on, you can choose push-to-talk or voice "
            + "activation, and you can turn it off at any time in Voice settings.";

        internal const string VoiceAcceptButton = "Turn on voice chat";

        internal const string VoiceDeclineButton = "Not now";

        /// <summary>Gets the disclosure's paragraphs, in order.</summary>
        internal static string[] DisclosureParagraphs() => new[] { DisclosureExposure, DisclosureHidden, DisclosureChoice };
    }
}
