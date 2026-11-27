namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// A joining player's clock for becoming Ready, as <see cref="TopiaForge.Mods.Internal.InternalReadinessLimits"/> sets
    /// it out: each snapshot's deadline, the readiness holds' bounds, and the whole allowance. It decides from what the
    /// engine reports and says when the join must end; the join ends it. Main thread only.
    /// </summary>
    internal sealed class JoinReadiness
    {
        internal const string LoadingTooLong = "The session did not finish loading in time.";
        internal const string SendingTooLong = "The host did not finish sending the session in time.";

        private readonly LiveJoinTimings timings;
        private long synchronizeDeadline;
        private long allowanceDeadline;
        private long seenReplacements;

        internal JoinReadiness(LiveJoinTimings timings)
        {
            this.timings = timings ?? throw new System.ArgumentNullException(nameof(timings));
        }

        /// <summary>Gets whether the player is waiting to be Ready.</summary>
        internal bool IsWaiting { get; private set; }

        /// <summary>
        /// The link is up and the join is sent. A first join, or a rejoin of a player who was Ready, starts the wait; a
        /// rejoin before the player was Ready needs a new snapshot, but its allowance keeps counting.
        /// </summary>
        internal void LinkUp(long now, long replacements)
        {
            if (!IsWaiting)
            {
                Start(now, replacements);
                return;
            }

            synchronizeDeadline = now + timings.SynchronizeTimeoutMicros;
            seenReplacements = replacements;
        }

        /// <summary>The player is Ready: the next wait starts afresh.</summary>
        internal void Ready() => IsWaiting = false;

        /// <summary>
        /// Returns why the join must end now, in this game's words, or null. <paramref name="synchronizing"/> is whether the
        /// session is Synchronizing, <paramref name="held"/> whether the snapshot is here and the runtime's holds keep the
        /// player waiting, <paramref name="heldSince"/> when holds first applied since the player was last Ready,
        /// <paramref name="progress"/> the latest progress while held, and <paramref name="replacements"/> how many times
        /// the host has replaced the session so far.
        /// </summary>
        internal string? Check(long now, bool synchronizing, bool held, long? heldSince, long progress, long replacements)
        {
            if (synchronizing && !IsWaiting)
            {
                // A session the host replaced after the player was Ready synchronizes again, within the same bounds as a join.
                Start(now, replacements);
            }
            else if (IsWaiting && replacements != seenReplacements)
            {
                // Replaced again before the player was Ready: the new snapshot gets its own deadline, but the allowance and
                // the holds' total count on from where they started, so a host that keeps replacing cannot keep the player.
                seenReplacements = replacements;
                synchronizeDeadline = now + timings.SynchronizeTimeoutMicros;
            }

            if (!IsWaiting) return null;

            // Once the snapshot is here the runtime's holds may keep the player waiting longer than the snapshot's deadline,
            // but not without progress, and not past their total from the first hold.
            if (heldSince.HasValue && now >= heldSince.Value + timings.HoldLimitMicros) return LoadingTooLong;
            if (held && now >= progress + timings.HoldIdleTimeoutMicros) return LoadingTooLong;

            // And never past the whole allowance, even when a hostile host sends replacements without their snapshots.
            if (now >= allowanceDeadline) return held || heldSince.HasValue ? LoadingTooLong : SendingTooLong;
            return !held && now >= synchronizeDeadline ? SendingTooLong : null;
        }

        private void Start(long now, long replacements)
        {
            IsWaiting = true;
            allowanceDeadline = now + timings.AllowanceMicros;
            synchronizeDeadline = now + timings.SynchronizeTimeoutMicros;
            seenReplacements = replacements;
        }
    }
}
