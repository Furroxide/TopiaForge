using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>
    /// The host's authority over who simulates one body. Every change of owner increments the epoch, and every
    /// message names the epoch it was sent under, so a message from before a change is refused rather than applied.
    /// The host is never in <see cref="OwnershipState.Requested"/>: a request is answered the moment it arrives.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A grab of a server-owned body is granted to the first request that names the current epoch.</item>
    /// <item>A held body is denied to everyone else; a body in its owner's release window can be taken (a steal).</item>
    /// <item>The owner streams samples; the host checks the speed budget and, while held, the hold range.</item>
    /// <item>A throw opens the release window; the owner then hands back position and velocities.</item>
    /// <item>A silent or disconnected owner, or one that fails validation, loses the body to the host.</item>
    /// </list>
    /// </remarks>
    internal sealed class OwnershipStateMachine
    {
        private readonly OwnershipPolicy policy;
        private long credit;
        private long lastValidatedMicros;

        public OwnershipStateMachine(OwnershipPolicy? policy = null, uint initialEpoch = 0)
        {
            this.policy = policy ?? OwnershipPolicy.Default;
            Epoch = initialEpoch;
        }

        public OwnershipPolicy Policy => policy;

        public OwnershipState State { get; private set; } = OwnershipState.Server;

        public NetOwner Owner { get; private set; } = NetOwner.Server;

        public uint Epoch { get; private set; }

        /// <summary>The newest position that passed validation; where the host resumes after a reclaim.</summary>
        public NetQuantizedPosition LastValidatedPosition { get; private set; }

        public long LastStreamMicros { get; private set; }

        public long ReleaseDeadlineMicros { get; private set; }

        /// <summary>The distance the owner may still move the body, in grid units.</summary>
        public long DistanceCreditUnits => credit;

        /// <summary>A participant asks to simulate the body.</summary>
        /// <param name="participant">The requester's participant slot.</param>
        /// <param name="observedEpoch">The epoch the requester last heard.</param>
        /// <param name="currentPosition">Where the host has the body now: the baseline for the new owner's samples.</param>
        /// <param name="nowMicros">The host's session time.</param>
        public OwnershipOutcome Request(int participant, uint observedEpoch, NetQuantizedPosition currentPosition, long nowMicros)
        {
            RequireParticipant(participant);
            RequireTime(nowMicros);
            switch (State)
            {
                case OwnershipState.Server:
                    return observedEpoch == Epoch
                        ? Grant(participant, currentPosition, nowMicros)
                        : Report(OwnershipResult.StaleEpoch);
                case OwnershipState.Owned:
                    if (Owner.Participant != participant) return Report(OwnershipResult.Denied);
                    return Report(observedEpoch == Epoch ? OwnershipResult.AlreadyOwned : OwnershipResult.StaleEpoch);
                default:
                    if (observedEpoch != Epoch) return Report(OwnershipResult.StaleEpoch);
                    if (Owner.Participant != participant) return Grant(participant, currentPosition, nowMicros);

                    // The thrower caught its own throw: the release window closes and nothing changes hands.
                    State = OwnershipState.Owned;
                    LastStreamMicros = Math.Max(LastStreamMicros, nowMicros);
                    return Report(OwnershipResult.Granted);
            }
        }

        /// <summary>The owner streams one sample of the body.</summary>
        /// <param name="holderPosition">Where the host has the owner's avatar, for the hold-range check.</param>
        public OwnershipOutcome Stream(
            int participant,
            uint epoch,
            NetQuantizedPosition position,
            NetQuantizedPosition holderPosition,
            long nowMicros)
        {
            RequireParticipant(participant);
            RequireTime(nowMicros);
            var refusal = CheckSender(participant, epoch);
            if (refusal.HasValue) return Report(refusal.Value);
            if (!IsWithinHoldRange(position, holderPosition) || !Spend(position, nowMicros))
            {
                return Reclaim(OwnershipReclaimReason.ValidationFailed, OwnershipResult.Rejected);
            }

            LastStreamMicros = Math.Max(LastStreamMicros, nowMicros);
            return Report(OwnershipResult.Accepted);
        }

        /// <summary>The owner threw the body: it keeps simulating until the release window closes.</summary>
        public OwnershipOutcome BeginRelease(int participant, uint epoch, long nowMicros)
        {
            RequireParticipant(participant);
            RequireTime(nowMicros);
            var refusal = CheckSender(participant, epoch);
            if (refusal.HasValue) return Report(refusal.Value);
            if (State == OwnershipState.ReleaseWindow) return Report(OwnershipResult.Releasing);
            State = OwnershipState.ReleaseWindow;
            ReleaseDeadlineMicros = nowMicros + policy.ReleaseWindowMicros;
            LastStreamMicros = Math.Max(LastStreamMicros, nowMicros);
            return Report(OwnershipResult.Releasing);
        }

        /// <summary>
        /// The owner returns the body, normally when its release window closes. Its position must pass the same
        /// checks as a sample; velocities longer than the limits are scaled down, and non-finite ones become zero.
        /// </summary>
        public OwnershipOutcome Handback(
            int participant,
            uint epoch,
            MotionHandback handback,
            NetQuantizedPosition holderPosition,
            long nowMicros,
            out MotionHandback accepted)
        {
            RequireParticipant(participant);
            RequireTime(nowMicros);
            accepted = default;
            var refusal = CheckSender(participant, epoch);
            if (refusal.HasValue) return Report(refusal.Value);
            if (!IsWithinHoldRange(handback.Position, holderPosition) || !Spend(handback.Position, nowMicros))
            {
                return Reclaim(OwnershipReclaimReason.ValidationFailed, OwnershipResult.Rejected);
            }

            accepted = new MotionHandback(
                handback.Position,
                ClampMagnitude(handback.LinearVelocity, policy.MaximumSpeedMetersPerSecond),
                ClampMagnitude(handback.AngularVelocity, policy.MaximumAngularSpeedRadiansPerSecond));
            ChangeToServer();
            return Report(OwnershipResult.HandedBack, OwnershipReclaimReason.None, true);
        }

        public OwnershipOutcome Disconnect(int participant)
        {
            RequireParticipant(participant);
            if (State == OwnershipState.Server || Owner.Participant != participant) return Report(OwnershipResult.NoChange);
            return Reclaim(OwnershipReclaimReason.Disconnected, OwnershipResult.Reclaimed);
        }

        /// <summary>The host takes the body back for its own reasons.</summary>
        public OwnershipOutcome Reclaim()
        {
            if (State == OwnershipState.Server) return Report(OwnershipResult.NoChange);
            return Reclaim(OwnershipReclaimReason.Host, OwnershipResult.Reclaimed);
        }

        /// <summary>Reclaims from an owner that has been silent, or that never handed back after its window.</summary>
        public OwnershipOutcome Poll(long nowMicros)
        {
            RequireTime(nowMicros);
            if (State == OwnershipState.Server) return Report(OwnershipResult.NoChange);
            if (State == OwnershipState.ReleaseWindow && nowMicros - ReleaseDeadlineMicros > policy.SilenceTimeoutMicros)
            {
                return Reclaim(OwnershipReclaimReason.HandbackTimeout, OwnershipResult.Reclaimed);
            }

            if (nowMicros - LastStreamMicros > policy.SilenceTimeoutMicros)
            {
                return Reclaim(OwnershipReclaimReason.Silence, OwnershipResult.Reclaimed);
            }

            return Report(OwnershipResult.NoChange);
        }

        private OwnershipOutcome Grant(int participant, NetQuantizedPosition position, long nowMicros)
        {
            Epoch = unchecked(Epoch + 1);
            Owner = NetOwner.ForParticipant(participant);
            State = OwnershipState.Owned;
            LastValidatedPosition = position;
            LastStreamMicros = nowMicros;
            lastValidatedMicros = nowMicros;
            credit = policy.BurstCreditUnits;
            return Report(OwnershipResult.Granted, OwnershipReclaimReason.None, true);
        }

        private OwnershipResult? CheckSender(int participant, uint epoch)
        {
            if (epoch != Epoch) return OwnershipResult.StaleEpoch;
            if (State == OwnershipState.Server || Owner.Participant != participant) return OwnershipResult.NotOwner;
            return null;
        }

        private bool IsWithinHoldRange(NetQuantizedPosition position, NetQuantizedPosition holderPosition) =>
            State != OwnershipState.Owned ||
            position.DistanceSquaredTo(holderPosition) <= policy.MaximumHoldRangeSquaredUnits;

        /// <summary>Accrues distance credit for the elapsed time, then spends the displacement if it is covered.</summary>
        private bool Spend(NetQuantizedPosition position, long nowMicros)
        {
            if (nowMicros > lastValidatedMicros)
            {
                var elapsed = Math.Min(nowMicros - lastValidatedMicros, policy.SpeedBurstMicros);
                credit = Math.Min(policy.BurstCreditUnits, credit + policy.SpeedUnitsPerSecond * elapsed / 1_000_000);
                lastValidatedMicros = nowMicros;
            }

            var distanceSquared = LastValidatedPosition.DistanceSquaredTo(position);
            if (distanceSquared > credit * credit) return false;
            credit -= NetIntegerMath.CeilingSqrt(distanceSquared);
            LastValidatedPosition = position;
            return true;
        }

        private OwnershipOutcome Reclaim(OwnershipReclaimReason reason, OwnershipResult result)
        {
            ChangeToServer();
            return Report(result, reason, true);
        }

        private void ChangeToServer()
        {
            Epoch = unchecked(Epoch + 1);
            Owner = NetOwner.Server;
            State = OwnershipState.Server;
            credit = 0;
        }

        private OwnershipOutcome Report(
            OwnershipResult result,
            OwnershipReclaimReason reason = OwnershipReclaimReason.None,
            bool epochChanged = false) =>
            new OwnershipOutcome(result, reason, State, Owner, Epoch, epochChanged);

        private static NetVector3 ClampMagnitude(NetVector3 value, int maximum)
        {
            if (!value.IsFinite) return NetVector3.Zero;
            var lengthSquared = value.LengthSquared;
            if (lengthSquared <= (double)maximum * maximum) return value;
            var scale = maximum / Math.Sqrt(lengthSquared);
            return new NetVector3((float)(value.X * scale), (float)(value.Y * scale), (float)(value.Z * scale));
        }

        private static void RequireParticipant(int participant)
        {
            if (participant < 1) throw new ArgumentOutOfRangeException(nameof(participant));
        }

        private static void RequireTime(long nowMicros)
        {
            if (nowMicros < 0) throw new ArgumentOutOfRangeException(nameof(nowMicros));
        }
    }
}
