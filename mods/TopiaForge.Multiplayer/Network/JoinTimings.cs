using System;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// How long a join waits for its host once the link is up. Production uses <see cref="Default"/>, the bounds in
    /// <see cref="InternalReadinessLimits"/>; tests shorten the waits so they run in seconds of virtual time.
    /// </summary>
    internal sealed class LiveJoinTimings
    {
        internal LiveJoinTimings(
            long synchronizeTimeoutMicros = InternalReadinessLimits.SynchronizeSeconds * 1_000_000L,
            long holdIdleTimeoutMicros = InternalReadinessLimits.IdleTimeoutSeconds * 1_000_000L,
            long holdLimitMicros = InternalReadinessLimits.MaximumSeconds * 1_000_000L)
        {
            if (synchronizeTimeoutMicros < 1) throw new ArgumentOutOfRangeException(nameof(synchronizeTimeoutMicros));
            if (holdIdleTimeoutMicros < 1) throw new ArgumentOutOfRangeException(nameof(holdIdleTimeoutMicros));
            if (holdLimitMicros < holdIdleTimeoutMicros) throw new ArgumentOutOfRangeException(nameof(holdLimitMicros));
            SynchronizeTimeoutMicros = synchronizeTimeoutMicros;
            HoldIdleTimeoutMicros = holdIdleTimeoutMicros;
            HoldLimitMicros = holdLimitMicros;
        }

        internal static LiveJoinTimings Default { get; } = new LiveJoinTimings();

        /// <summary>
        /// Gets how long the host may take to admit the player and send the session's snapshot once the link is up, and
        /// to send it again after it replaced the session.
        /// </summary>
        internal long SynchronizeTimeoutMicros { get; }

        /// <summary>Gets how long the runtime's readiness holds may go without progress once the snapshot arrived.</summary>
        internal long HoldIdleTimeoutMicros { get; }

        /// <summary>
        /// Gets how long the holds may keep the player short of Ready in all, progress or not, from the first of them until
        /// the player has been Ready, whatever replacements come in between.
        /// </summary>
        internal long HoldLimitMicros { get; }

        /// <summary>
        /// Gets the most the player waits in all until it has been Ready, from link-up or from the replacement that took it
        /// out of the session: a snapshot's wait and the holds'. A host that keeps replacing the session cannot extend it.
        /// </summary>
        internal long AllowanceMicros => SynchronizeTimeoutMicros + HoldLimitMicros;
    }
}
