namespace TopiaForge.Chronos
{
    // Pure release bookkeeping shared by the Unity service and its off-game regression tests. A release only has
    // side effects when the exact ledger entry still exists. This is important after ForceReset and driver
    // replacement: old handles must not decrement a new suspension count or clear a newer driver.
    internal static class LeaseLifecycle
    {
        public static LeaseReleaseEffects Release(
            LeaseLedger ledger,
            int id,
            bool wasSuspend,
            ref int driverLeaseId,
            ref int suspendRefCount)
        {
            if (!ledger.Remove(id))
            {
                return default;
            }

            var releasedDriver = id == driverLeaseId;
            if (releasedDriver)
            {
                driverLeaseId = 0;
            }

            var releasePlayerSuspend = false;
            if (wasSuspend && suspendRefCount > 0)
            {
                suspendRefCount--;
                releasePlayerSuspend = suspendRefCount == 0;
            }

            return new LeaseReleaseEffects(true, releasedDriver, releasePlayerSuspend);
        }
    }

    internal readonly struct LeaseReleaseEffects
    {
        public LeaseReleaseEffects(bool removed, bool releasedDriver, bool releasePlayerSuspend)
        {
            Removed = removed;
            ReleasedDriver = releasedDriver;
            ReleasePlayerSuspend = releasePlayerSuspend;
        }

        public bool Removed { get; }
        public bool ReleasedDriver { get; }
        public bool ReleasePlayerSuspend { get; }
    }
}
