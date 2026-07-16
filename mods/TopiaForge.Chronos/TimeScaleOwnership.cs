namespace TopiaForge.Chronos
{
    internal enum DeferredScaleRestoreAction
    {
        Wait,
        RestoreBaseline,
        Abandon
    }

    internal readonly struct TimeScaleRestorePlan
    {
        public TimeScaleRestorePlan(bool writeBaseline, bool retainOwnership)
        {
            WriteBaseline = writeBaseline;
            RetainOwnership = retainOwnership;
        }

        public bool WriteBaseline { get; }

        public bool RetainOwnership { get; }
    }

    /// <summary>Pure ownership policy for coexisting with the game's native zero-timescale pause.</summary>
    internal static class TimeScaleOwnership
    {
        public static bool IsNativePaused(
            bool explicitNativePause,
            bool hasWritten,
            float ownedScale,
            float observedScale)
        {
            // The explicit signal is essential when the pause menu opens on top of a Chronos freeze: both the
            // observed and Chronos-owned scales are zero, so scale comparison alone cannot identify the overlay.
            // Comparison remains a conservative fallback for another system that writes zero without a signal.
            return explicitNativePause || (observedScale == 0f && (!hasWritten || ownedScale != 0f));
        }

        public static TimeScaleRestorePlan PlanRestore(
            bool explicitNativePause,
            bool hasWritten,
            float ownedScale,
            float observedScale)
        {
            if (!hasWritten)
            {
                return new TimeScaleRestorePlan(writeBaseline: false, retainOwnership: false);
            }

            var nativePaused = IsNativePaused(explicitNativePause, hasWritten, ownedScale, observedScale);
            return new TimeScaleRestorePlan(
                writeBaseline: !nativePaused,
                retainOwnership: nativePaused);
        }

        public static bool CanStep(bool isFrozen, bool nativePaused)
        {
            return isFrozen && !nativePaused;
        }

        public static bool RestoreFixedOnAbandon(bool hasWritten, bool baseFixedCaptured)
        {
            return hasWritten && baseFixedCaptured;
        }

        public static DeferredScaleRestoreAction PlanDeferredRestore(
            bool hasExactPauseRoot,
            bool exactNativePauseActive,
            float ownedScale,
            float observedScale)
        {
            if (hasExactPauseRoot && exactNativePauseActive)
            {
                return DeferredScaleRestoreAction.Wait;
            }

            // A zero-scale handoff is inherently ambiguous without the exact overlay that covered Chronos' own
            // freeze. Never infer release from a failed/missing lookup and accidentally lift somebody else's pause.
            if (!hasExactPauseRoot && ownedScale == 0f)
            {
                return DeferredScaleRestoreAction.Abandon;
            }

            // A scale-only pause has not released yet. Keep waiting for it to put Chronos' saved scale back rather
            // than abandoning the handoff while the clock is still zero.
            if (observedScale == 0f && ownedScale != 0f)
            {
                return DeferredScaleRestoreAction.Wait;
            }

            // Robotopia restores the saved float by assignment, so exact equality is both sufficient and safer:
            // a merely similar scale may belong to a newer external owner and must not be overwritten.
            if (observedScale == ownedScale)
            {
                return DeferredScaleRestoreAction.RestoreBaseline;
            }

            // A different non-zero value means another system took ownership after Chronos began unloading. Never
            // overwrite that newer owner merely to force a baseline.
            return DeferredScaleRestoreAction.Abandon;
        }
    }
}
