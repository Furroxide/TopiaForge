namespace TopiaForge.ModManager.TestDoubles
{
    /// <summary>
    /// The game's clock off the game (WS3.13): a time scale and a fixed step a test reads, and Robotopia's pause menu as the
    /// game runs it, which freezes the world as it opens and puts back the scale it found as it closes. It counts every read
    /// and write of the scale and of the menu's state, so a test can show that an offline frame never touches them. The
    /// ModRuntime and ModManager harnesses both compile it.
    /// </summary>
    internal sealed class FakeGameClock : IMultiplayerGameClock
    {
        private float timeScale = 1f;
        private float savedScale = 1f;
        private bool paused;

        public float TimeScale
        {
            get
            {
                ScaleReads++;
                return timeScale;
            }

            set
            {
                ScaleWrites++;
                timeScale = value;
            }
        }

        public float FixedDeltaTime { get; set; } = 0.02f;

        public float DeltaTime { get; private set; }

        public float Time { get; private set; }

        public float UnscaledDeltaTime { get; private set; } = 0.016f;

        public float UnscaledTime { get; private set; }

        public bool IsPaused
        {
            get
            {
                PauseReads++;
                return paused;
            }
        }

        /// <summary>Gets the scale without counting a read.</summary>
        internal float Scale => timeScale;

        /// <summary>Gets whether the menu is open, without counting a read.</summary>
        internal bool MenuOpen => paused;

        internal int ScaleReads { get; private set; }

        internal int ScaleWrites { get; private set; }

        internal int PauseReads { get; private set; }

        /// <summary>Gets every read and write of the scale and of the menu's state.</summary>
        internal int Touches => ScaleReads + ScaleWrites + PauseReads;

        /// <summary>Opens the game's pause menu as the game does: it keeps the scale it finds, then freezes the world.</summary>
        internal void OpenPauseMenu()
        {
            paused = true;
            savedScale = timeScale;
            timeScale = 0f;
        }

        /// <summary>Closes the game's pause menu as the game does: it puts back the scale it kept as it opened.</summary>
        internal void ClosePauseMenu()
        {
            paused = false;
            timeScale = savedScale;
        }

        /// <summary>Sets the scale as another writer would, without counting it.</summary>
        internal void Set(float scale) => timeScale = scale;

        /// <summary>Moves both clocks on by one frame of <paramref name="seconds"/> of real time.</summary>
        internal void Advance(float seconds)
        {
            UnscaledDeltaTime = seconds;
            UnscaledTime += seconds;
            DeltaTime = seconds * timeScale;
            Time += DeltaTime;
        }

        internal void ResetCounts()
        {
            ScaleReads = 0;
            ScaleWrites = 0;
            PauseReads = 0;
        }
    }
}
