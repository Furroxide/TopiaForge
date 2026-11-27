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

        /// <summary>
        /// A joined player's plan (WS3.13): the host's scale and mode, this process's own player exemption, and always a
        /// native write, since online the shared world never yields to one player's pause menu.
        /// </summary>
        public static TimeScalePlan Follow(LeaseLedger ledger, float hostScale, TimeMode hostMode)
        {
            return new TimeScalePlan(
                hostScale,
                hostMode,
                ledger.AnyExemptPlayer && hostScale < 1f,
                writeNativeScale: true);
        }
    }
}
