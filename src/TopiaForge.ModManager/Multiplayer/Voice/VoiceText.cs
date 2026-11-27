using TopiaForge.ModManager.Sessions;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// What VOICE says, in this game's own words. The consent itself is never here: its approved wording lives in
    /// <see cref="MultiplayerPrivacyText"/> alone. Nothing here names an address or anything about another player's network.
    /// </summary>
    internal static class VoiceText
    {
        internal const string Loading = "Loading your voice settings...";
        internal const string Saving = "Saving your voice settings...";
        internal const string SwitchUnsaved = "Your voice chat choice is not saved for the TopiaForge launcher yet. This game keeps trying.";
        internal const string Unavailable = "Voice chat is not available: live multiplayer is switched off in this build.";
        internal const string ConsentNeeded = "Voice chat is off. Turning it on asks before it uses your microphone.";
        internal const string Off = "Voice chat is off.";
        internal const string Failed = "Voice chat stopped after repeated errors. Restart the game to use it again.";
        internal const string NotOffered = "Voice chat is on, for sessions that offer it.";
        internal const string NotInSession = "Host a game or join one to talk.";
        internal const string Connecting = "Waiting for the session...";
        internal const string NoListeners = "Nobody else is here to hear you yet.";
        internal const string MutedByHost = "The host muted you.";
        internal const string MicBusy = "The game or a robot conversation is using your microphone. Voice chat waits until it is done.";
        internal const string CannotCheckGame = "Voice chat cannot tell whether the game is using your microphone, so it keeps it closed. Update TopiaForge to fix this.";
        internal const string NoMicrophone = "No microphone found. Connect one, then press REFRESH.";
        internal const string MicrophoneFailed = "Your microphone would not start. Choose another one, or close the program that holds it.";
        internal const string Testing = "Testing your microphone. Nothing is sent.";
        internal const string Talking = "Talking.";
        internal const string Listening = "Listening: you are heard when you speak above the activation level.";
        internal const string NoPushToTalk = "Push-to-talk has no key. Choose one below.";

        internal const string ConsentGiven = "You agreed to voice chat. Withdraw your consent to turn it off and be asked again.";
        internal const string ConsentAsked = "TopiaForge asks for your consent before voice chat first uses your microphone.";

        internal const string TestOff = "Turn voice chat on to test your microphone.";
        internal const string TestReady = "The test opens your microphone and shows its level. Nothing is sent or recorded.";
        internal const string TestRunning = "Speak: the bar shows what your microphone hears. Nothing is sent or recorded.";

        internal const string Proximity = "Voices play at full volume for now. Proximity voice arrives with player avatars.";

        internal const string PlayersSolo = "Host a game or join one to hear other players.";
        internal const string PlayersWaiting = "Players show here once the host lists them.";
        internal const string PlayersNobody = "Nobody else is in the session yet.";
        internal const string PlayerGone = "That player left or the session changed, so nothing was changed. Choose again from the list.";
        internal const string MutesFull = "Voice chat keeps at most 256 muted players. Unmute someone first.";
        internal const string UnnamedPlayer = "Player";

        internal const string DefaultDevice = "System default microphone";
        internal const string NoDevices = "No microphone found. Connect one, then press REFRESH.";
        internal const string NotConnectedSuffix = " (not connected)";
        internal const string DeviceMissing = "That microphone is not connected, so voice chat uses the system default microphone until it is.";

        internal const string CapturePrompt = "Press a key or button...";
        internal const string CaptureNote = "Press the key or mouse button to hold for push-to-talk. Esc cancels.";

        internal const string TurnedOn = "Voice chat is on.";
        internal const string TurnedOff = "Voice chat is off.";
        internal const string StaysOff = "Voice chat stays off.";
        internal const string Withdrawn = "You withdrew your consent. Voice chat is off, and turning it on asks again.";
        internal const string BindingsReset = "Push-to-talk is back to Caps Lock and Mouse Forward.";
        internal const string BindingNow = "Push-to-talk is now ";
        internal const string HearNobodyOn = "You hear nobody now.";
        internal const string HearNobodyOff = "You hear other players again.";

        internal static readonly string[] Modes = { "Push-to-talk", "Voice activation" };

        /// <summary>Says how to talk with push-to-talk on <paramref name="first"/>, or on either of two controls.</summary>
        internal static string HoldToTalk(string first, string? second) =>
            second == null ? "Hold " + first + " to talk." : "Hold " + first + " or " + second + " to talk.";

        /// <summary>Says why push-to-talk does not use the control the player chose, and what it uses instead.</summary>
        /// <param name="chosen">The control the player chose.</param>
        /// <param name="boundByGame">Whether the game binds it, rather than it being a control push-to-talk never takes.</param>
        /// <param name="instead">The control used instead, or null when none of its kind is free.</param>
        /// <param name="mouseButton">Whether the slot holds a mouse button rather than a key.</param>
        internal static string FellBack(string chosen, bool boundByGame, string? instead, bool mouseButton)
        {
            var reason = boundByGame ? "The game uses " + chosen : chosen + " cannot be push-to-talk";
            return instead != null
                ? reason + ", so push-to-talk uses " + instead + " instead."
                : reason + ", and no other " + (mouseButton ? "mouse button" : "key") + " is free for push-to-talk.";
        }

        internal static string Muted(string name) => "You no longer hear " + name + ".";

        internal static string Unmuted(string name) => "You hear " + name + " again.";
    }
}
