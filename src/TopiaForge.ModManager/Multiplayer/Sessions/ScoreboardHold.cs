namespace TopiaForge.ModManager.Sessions
{
    /// <summary>A held control never opens the scoreboard after typing, capture, a modal or a scene change ends.</summary>
    internal sealed class ScoreboardHold
    {
        private bool released = true;
        internal bool IsOpen { get; private set; }
        internal bool Tick(bool held, bool pressed, bool blocked, bool available)
        {
            if (!held) { released = true; IsOpen = false; return false; }
            if (blocked || !available) { Dismiss(); return false; }
            if (released && pressed) { IsOpen = true; released = false; }
            return IsOpen;
        }
        internal void Dismiss() { IsOpen = false; released = false; }
    }
}
