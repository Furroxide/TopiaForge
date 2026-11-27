using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The game's clock as the runtime reads it (WS3.13): the clock first-party providers drive time through, and whether
    /// the game's own pause menu is open, which is Robotopia's <c>UIState.IsPaused</c>. Unity's in the game; tests give a
    /// clock of their own. Main thread.
    /// </summary>
    internal interface IMultiplayerGameClock : IInternalGameClock
    {
        /// <summary>Gets whether the game's own pause menu is open.</summary>
        bool IsPaused { get; }
    }

    /// <summary>A gameplay host that gives the runtime the game's clock. CoreGameplayServices does, over Unity.</summary>
    internal interface IGameClockSource
    {
        IMultiplayerGameClock GameClock { get; }
    }
}
