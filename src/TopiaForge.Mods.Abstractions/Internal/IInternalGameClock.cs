namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// The game's clock, as the runtime hands it to first-party providers: Unity's time in the game, a test's clock off it.
    /// Chronos drives the world's time scale through it, and the runtime's pause guard keeps the shared world running through
    /// the same clock while a player's own pause menu is open in a multiplayer session, so the two never disagree about which
    /// clock they act on. Main thread.
    /// </summary>
    internal interface IInternalGameClock
    {
        /// <summary>Gets or sets the scale the world's clock runs at: Unity's <c>Time.timeScale</c>.</summary>
        float TimeScale { get; set; }

        /// <summary>Gets or sets the fixed step physics runs at, in seconds: Unity's <c>Time.fixedDeltaTime</c>.</summary>
        float FixedDeltaTime { get; set; }

        /// <summary>Gets this frame's scaled delta time, in seconds.</summary>
        float DeltaTime { get; }

        /// <summary>Gets the scaled time since the game started, in seconds.</summary>
        float Time { get; }

        /// <summary>Gets this frame's unscaled delta time, in seconds.</summary>
        float UnscaledDeltaTime { get; }

        /// <summary>Gets the unscaled time since the game started, in seconds.</summary>
        float UnscaledTime { get; }
    }
}
