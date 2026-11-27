using TopiaForge.Mods.Internal;

namespace TopiaForge.Chronos
{
    /// <summary>
    /// Unity's own clock, for a Chronos whose context carries no runtime clock. In the game the runtime always gives one,
    /// which passes straight through to the same Unity time, so the two behave alike.
    /// </summary>
    internal sealed class UnityChronosClock : IInternalGameClock
    {
        internal static readonly UnityChronosClock Instance = new UnityChronosClock();

        private UnityChronosClock()
        {
        }

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
    }
}
