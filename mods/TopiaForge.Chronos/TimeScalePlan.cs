using TopiaForge.Mods;

namespace TopiaForge.Chronos
{
    /// <summary>
    /// Unity-free derived state for one Chronos application pass. Keeping the logical state independent from the
    /// native write lets leases continue to compose while Robotopia's own pause menu owns the engine clock.
    /// </summary>
    internal readonly struct TimeScalePlan
    {
        private TimeScalePlan(float worldScale, TimeMode mode, bool exemptPlayer, bool writeNativeScale)
        {
            WorldScale = worldScale;
            Mode = mode;
            ExemptPlayer = exemptPlayer;
            WriteNativeScale = writeNativeScale;
        }

        public float WorldScale { get; }

        public TimeMode Mode { get; }

        public bool ExemptPlayer { get; }

        public bool WriteNativeScale { get; }

        public static TimeScalePlan Derive(
            LeaseLedger ledger,
            float driverScale,
            bool turnBased,
            bool nativePaused)
        {
            var scale = ledger.EffectiveScale(driverScale);
            var mode = turnBased
                ? TimeMode.TurnBased
                : (ledger.AnyFreeze
                    ? TimeMode.Paused
                    : (scale < 1f ? TimeMode.Slowed : TimeMode.Realtime));

            return new TimeScalePlan(
                scale,
                mode,
                ledger.AnyExemptPlayer && scale < 1f,
                !nativePaused);
        }
    }
}
