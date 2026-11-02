namespace TopiaForge.Mods.Internal
{
    /// <summary>Authenticated, unscaled session timeline. No wall clock or network address is exposed.</summary>
    internal interface IInternalUiClock
    {
        string UiClockEpoch { get; }
        double UiClockTime { get; }
        void BeginUiWorld();
        bool TryAdoptUiClock(double elapsedSeconds);
    }
}
