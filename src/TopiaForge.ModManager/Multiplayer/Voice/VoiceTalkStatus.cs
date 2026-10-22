namespace TopiaForge.ModManager.Voice
{
    /// <summary>What voice chat does for this player now, for the transmit state the VOICE page shows and the HUD later.</summary>
    internal enum VoiceTalkStatus
    {
        /// <summary>No voice service: live multiplayer is off in this game.</summary>
        Unavailable = 0,

        /// <summary>The player has not agreed to the current wording of the voice consent.</summary>
        ConsentNeeded = 1,

        /// <summary>The player turned voice chat off.</summary>
        Off = 2,

        /// <summary>The voice service gave up after repeated failures, until the game restarts.</summary>
        Failed = 3,

        /// <summary>The session this game hosts or joined offers no voice chat.</summary>
        NotOffered = 4,

        /// <summary>Playing on one's own.</summary>
        NotInSession = 5,

        /// <summary>Joining or reconnecting: there is no ready session to talk in yet.</summary>
        Connecting = 6,

        /// <summary>Nobody else in the session can hear.</summary>
        NoListeners = 7,

        /// <summary>The host muted this player.</summary>
        MutedByHost = 8,

        /// <summary>The game's own robot recording or a robot conversation holds the microphone.</summary>
        MicBusy = 9,

        /// <summary>The game's state cannot be read, so voice chat cannot tell whether the game records: it keeps the microphone closed.</summary>
        CannotCheckGame = 10,

        /// <summary>No microphone is connected.</summary>
        NoMicrophone = 11,

        /// <summary>A microphone is there but would not start recording.</summary>
        MicrophoneFailed = 12,

        /// <summary>The microphone test runs: the meter moves, and nothing is sent.</summary>
        Testing = 13,

        /// <summary>The player's voice is going out.</summary>
        Talking = 14,

        /// <summary>Voice chat is on and the player can talk: push-to-talk is up, or voice activation listens.</summary>
        Ready = 15
    }
}
