namespace TopiaForge.ModManager
{
    /// <summary>
    /// The game's clock over Unity (WS3.13): <c>UnityEngine.Time</c>, which Chronos drives the world's scale through, and
    /// Robotopia's <c>UIState.IsPaused</c>, which says whether the game's own pause menu is open. Every member passes
    /// straight through, so single player keeps every behaviour it had. Main thread.
    /// </summary>
    internal sealed class UnityGameClock : IMultiplayerGameClock
    {
        public float TimeScale
        {
            get => UnityEngine.Time.timeScale;
            set => UnityEngine.Time.timeScale = value;
        }

        public float FixedDeltaTime
        {
            get => UnityEngine.Time.fixedDeltaTime;
            set => UnityEngine.Time.fixedDeltaTime = value;
        }

        public float DeltaTime => UnityEngine.Time.deltaTime;

        public float Time => UnityEngine.Time.time;

        public float UnscaledDeltaTime => UnityEngine.Time.unscaledDeltaTime;

        public float UnscaledTime => UnityEngine.Time.unscaledTime;

        public bool IsPaused => UIState.IsPaused.Value;
    }
}
