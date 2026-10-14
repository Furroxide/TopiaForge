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
    /// <item>A grab of a server-owned body is granted to the first request that names the current epoch and comes from
    /// an avatar within the hold range of the body.</item>
    /// <item>A held body is denied to everyone else; a body in its owner's release window can be taken (a steal).</item>
    /// <item>A new owner must send its first sample within a short deadline. The host checks every sample's speed
    /// budget and, while the body is held, the hold range.</item>
    /// <item>A throw from within the hold range opens the release window; the owner then hands back position and
    /// velocities.</item>
    /// <item>An owner that goes quiet (no first sample, silence or no hand-back), disconnects or fails validation loses
    /// the body to the host. One that went quiet is refused the body for a cooldown, so another player can take it.</item>
    /// </list>
    /// Participants are slots that the runtime reuses once their participant leaves, so it must call
    /// <see cref="Disconnect"/> for a leaver on every machine before the slot goes to someone else. Otherwise the
    /// newcomer inherits the leaver's body or cooldown.
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

        /// <summary>Whether the current owner has not yet sent an accepted sample since its grant.</summary>
        public bool AwaitingFirstSample { get; private set; }

        /// <summary>When the current grant lapses if no first sample has arrived.</summary>
        public long FirstSampleDeadlineMicros { get; private set; }

        /// <summary>The participant slot that last lost the body by going quiet, or zero.</summary>
        public int YieldingParticipant { get; private set; }

        /// <summary>Until when <see cref="YieldingParticipant"/> is refused the body.</summary>
        public long YieldUntilMicros { get; private set; }

        /// <summary>A participant asks to simulate the body.</summary>
        /// <param name="participant">The requester's participant slot.</param>
        /// <param name="observedEpoch">The epoch the requester last heard.</param>
        /// <param name="bodyPosition">Where the host has the body now: the baseline for the new owner's samples.</param>
        /// <param name="requesterPosition">Where the host has the requester's avatar, for the hold-range check.</param>
        /// <param name="nowMicros">The host's session time.</param>
        public OwnershipOutcome Request(
            int participant,
            uint observedEpoch,
            NetQuantizedPosition bodyPosition,
            NetQuantizedPosition requesterPosition,
            long nowMicros)
        {
            RequireParticipant(participant);
            RequireTime(nowMicros);
            switch (State)
            {
                case OwnershipState.Server:
                    if (observedEpoch != Epoch) return Report(OwnershipResult.StaleEpoch);
                    if (!IsInHoldRange(bodyPosition, requesterPosition)) return Report(OwnershipResult.OutOfRange);
                    if (IsYielding(participant, nowMicros)) return Report(OwnershipResult.Yielding);
                    return Grant(participant, bodyPosition, nowMicros);
                case OwnershipState.Owned:
                    if (Owner.Participant != participant) return Report(OwnershipResult.Denied);
                    return Report(observedEpoch == Epoch ? OwnershipResult.AlreadyOwned : OwnershipResult.StaleEpoch);
                default:
                    if (observedEpoch != Epoch) return Report(OwnershipResult.StaleEpoch);
                    if (!IsInHoldRange(bodyPosition, requesterPosition)) return Report(OwnershipResult.OutOfRange);
                    if (Owner.Participant != participant)
                    {
                        return IsYielding(participant, nowMicros)
                            ? Report(OwnershipResult.Yielding)
                            : Grant(participant, bodyPosition, nowMicros);
                    }

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
            var held = State == OwnershipState.Owned;
            if ((held && !IsInHoldRange(position, holderPosition)) || !Spend(position, nowMicros))
            {
                return Reclaim(OwnershipReclaimReason.ValidationFailed, OwnershipResult.Rejected);
            }

            AwaitingFirstSample = false;
            LastStreamMicros = Math.Max(LastStreamMicros, nowMicros);
            return Report(OwnershipResult.Accepted);
        }

        /// <summary>
        /// The owner threw or dropped the body: it keeps simulating until the release window closes. A throw must start
        /// within the hold range, so the body's last validated position is checked against the holder's avatar; a body
        /// out of range is reclaimed. A repeated throw inside the window changes nothing.
        /// </summary>
        /// <param name="holderPosition">Where the host has the owner's avatar, for the hold-range check.</param>
        public OwnershipOutcome BeginRelease(int participant, uint epoch, NetQuantizedPosition holderPosition, long nowMicros)
        {
            RequireParticipant(participant);
            RequireTime(nowMicros);
            var refusal = CheckSender(participant, epoch);
            if (refusal.HasValue) return Report(refusal.Value);
            if (State == OwnershipState.ReleaseWindow) return Report(OwnershipResult.Releasing);
            if (!IsInHoldRange(LastValidatedPosition, holderPosition))
            {
                return Reclaim(OwnershipReclaimReason.ValidationFailed, OwnershipResult.Rejected);
            }

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
            var held = State == OwnershipState.Owned;
            if ((held && !IsInHoldRange(handback.Position, holderPosition)) || !Spend(handback.Position, nowMicros))
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

        /// <summary>
        /// A participant left. Its body goes back to the server at once, and its cooldown is forgotten, so whoever
        /// gets the slot next starts clean.
        /// </summary>
        public OwnershipOutcome Disconnect(int participant)
        {
            RequireParticipant(participant);
            if (YieldingParticipant == participant) ClearYield();
            if (State == OwnershipState.Server || Owner.Participant != participant) return Report(OwnershipResult.NoChange);
            return Reclaim(OwnershipReclaimReason.Disconnected, OwnershipResult.Reclaimed);
        }

        /// <summary>The host takes the body back for its own reasons.</summary>
        public OwnershipOutcome Reclaim()
        {
            if (State == OwnershipState.Server) return Report(OwnershipResult.NoChange);
            return Reclaim(OwnershipReclaimReason.Host, OwnershipResult.Reclaimed);
        }

        /// <summary>
        /// Reclaims from an owner that sent no first sample in time, that has been silent, or that never handed back
        /// after its window. Each of those makes the owner yield the body to other players for the cooldown.
        /// </summary>
        public OwnershipOutcome Poll(long nowMicros)
        {
            RequireTime(nowMicros);
            if (State == OwnershipState.Server) return Report(OwnershipResult.NoChange);
            if (AwaitingFirstSample && nowMicros > FirstSampleDeadlineMicros)
            {
                return Lapse(OwnershipReclaimReason.NoFirstSample, nowMicros);
            }

            if (State == OwnershipState.ReleaseWindow && nowMicros - ReleaseDeadlineMicros > policy.SilenceTimeoutMicros)
            {
                return Lapse(OwnershipReclaimReason.HandbackTimeout, nowMicros);
            }

            if (nowMicros - LastStreamMicros > policy.SilenceTimeoutMicros)
            {
                return Lapse(OwnershipReclaimReason.Silence, nowMicros);
            }

            return Report(OwnershipResult.NoChange);
        }

        /// <summary>Whether a participant is refused the body for now because it recently went quiet holding it.</summary>
        public bool IsYielding(int participant, long nowMicros) =>
            participant == YieldingParticipant && participant != 0 && nowMicros < YieldUntilMicros;

        private OwnershipOutcome Grant(int participant, NetQuantizedPosition position, long nowMicros)
        {
            Epoch = unchecked(Epoch + 1);
            Owner = NetOwner.ForParticipant(participant);
            State = OwnershipState.Owned;
            LastValidatedPosition = position;
            LastStreamMicros = nowMicros;
            lastValidatedMicros = nowMicros;
            credit = policy.BurstCreditUnits;
            AwaitingFirstSample = true;
            FirstSampleDeadlineMicros = nowMicros + policy.FirstSampleTimeoutMicros;
            if (YieldingParticipant == participant) ClearYield();
            return Report(OwnershipResult.Granted, OwnershipReclaimReason.None, true);
        }

        private OwnershipResult? CheckSender(int participant, uint epoch)
        {
            if (epoch != Epoch) return OwnershipResult.StaleEpoch;
            if (State == OwnershipState.Server || Owner.Participant != participant) return OwnershipResult.NotOwner;
            return null;
        }

        private bool IsInHoldRange(NetQuantizedPosition body, NetQuantizedPosition avatar) =>
            body.DistanceSquaredTo(avatar) <= policy.MaximumHoldRangeSquaredUnits;

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

        /// <summary>Reclaims from an owner that went quiet, which then yields the body for the cooldown.</summary>
        private OwnershipOutcome Lapse(OwnershipReclaimReason reason, long nowMicros)
        {
            var participant = Owner.Participant;
            var outcome = Reclaim(reason, OwnershipResult.Reclaimed);
            if (policy.YieldCooldownMicros > 0)
            {
                YieldingParticipant = participant;
                YieldUntilMicros = nowMicros + policy.YieldCooldownMicros;
            }

            return outcome;
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
            AwaitingFirstSample = false;
        }

        private void ClearYield()
        {
            YieldingParticipant = 0;
            YieldUntilMicros = 0;
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
