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
    /// <item>So is one that hands back before its first sample, one whose link closes before its first sample, and one
    /// that hands back, fails validation or loses its link after another player in range was refused the body during
    /// its hold, so letting go and grabbing again cannot keep a contested body.</item>
    /// <item>A hold can end before anyone's request reaches it, when a grab, a sample and a hand-back arrive together.
    /// A request that such a hold made stale, from another player in range of the free body within the cooldown after
    /// it ended, makes its owner yield too.</item>
    /// <item>Every participant has its own cooldown, so one player's yield never frees another. A yield the
    /// participant earned itself, a strike, doubles its last strike's cooldown, up to a cap, when it comes within the
    /// cap after its last cooldown ended: going quiet, failing validation, or letting go of a hold shorter than half the
    /// release window, which a client's claim never makes. Yielding a body held that long to another player's contest
    /// or late request is taking turns, which another player could provoke, so it costs only the base cooldown.</item>
    /// </list>
    /// Participants are slots that the runtime reuses once their participant leaves, so it must call
    /// <see cref="Disconnect"/> for a leaver on every machine before the slot goes to someone else. Otherwise the
    /// newcomer inherits the leaver's body or cooldown. A participant whose link closes but who may still come back
    /// gets <see cref="LinkClosed"/> instead, which keeps its cooldown.
    /// </remarks>
    internal sealed class OwnershipStateMachine
    {
        private readonly OwnershipPolicy policy;
        private long credit;
        private long lastValidatedMicros;
        private long grantedMicros;

        // One entry per participant slot, allocated at the first yield: until when it is refused, and the length of its
        // last strike, which the next strike within the memory doubles.
        private long[]? cooldownUntil;
        private long[]? strikeLength;

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

        /// <summary>
        /// Whether, during the current hold, another participant within the hold range asked for the body and was
        /// denied. The owner then yields once it hands back, fails validation or loses its link. A request from out of
        /// range does not count, so a player far away cannot make a holder wait.
        /// </summary>
        public bool Contested { get; private set; }

        /// <summary>
        /// The participant slot whose hold last ended uncontested, by its own hand-back, a failed validation or a closed
        /// link, while the body has been free since; otherwise zero. A request that hold made stale can still make it
        /// yield.
        /// </summary>
        public int LastReleaser { get; private set; }

        /// <summary>When <see cref="LastReleaser"/>'s hold ended.</summary>
        public long LastReleaseMicros { get; private set; }

        /// <summary>
        /// Whether a yield after <see cref="LastReleaser"/>'s release would be a strike: its hold failed validation, or
        /// lasted less than half the release window.
        /// </summary>
        public bool LastReleaseStrikes { get; private set; }

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
                    if (observedEpoch != Epoch)
                    {
                        // Someone close enough asked under the epoch of a hold too short to contest while it lasted.
                        if (IsInHoldRange(bodyPosition, requesterPosition)) YieldAfterRace(participant, nowMicros);
                        return Report(OwnershipResult.StaleEpoch);
                    }

                    if (!IsInHoldRange(bodyPosition, requesterPosition)) return Report(OwnershipResult.OutOfRange);
                    if (IsYielding(participant, nowMicros)) return Report(OwnershipResult.Yielding);
                    return Grant(participant, bodyPosition, nowMicros);
                case OwnershipState.Owned:
                    if (Owner.Participant != participant)
                    {
                        // Someone close enough to take the body is kept from it, so the holder yields once it lets go.
                        if (IsInHoldRange(bodyPosition, requesterPosition)) Contested = true;
                        return Report(OwnershipResult.Denied);
                    }

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
                return TakeBack(OwnershipReclaimReason.ValidationFailed, OwnershipResult.Rejected, nowMicros, Contested, true);
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
                return TakeBack(OwnershipReclaimReason.ValidationFailed, OwnershipResult.Rejected, nowMicros, Contested, true);
            }

            State = OwnershipState.ReleaseWindow;
            ReleaseDeadlineMicros = nowMicros + policy.ReleaseWindowMicros;
            LastStreamMicros = Math.Max(LastStreamMicros, nowMicros);
            return Report(OwnershipResult.Releasing);
        }

        /// <summary>
        /// The owner returns the body, normally when its release window closes. Its position must pass the same
        /// checks as a sample; velocities longer than the limits are scaled down, and non-finite ones become zero. An
        /// owner that hands back before its first sample, or after a contested hold, yields the body for the cooldown.
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
                return TakeBack(OwnershipReclaimReason.ValidationFailed, OwnershipResult.Rejected, nowMicros, Contested, true);
            }

            accepted = new MotionHandback(
                handback.Position,
                ClampMagnitude(handback.LinearVelocity, policy.MaximumSpeedMetersPerSecond),
                ClampMagnitude(handback.AngularVelocity, policy.MaximumAngularSpeedRadiansPerSecond));

            // Handing back before any sample is going quiet; handing back a contested body lets the other player go first.
            return TakeBack(OwnershipReclaimReason.None, OwnershipResult.HandedBack, nowMicros, AwaitingFirstSample || Contested, true);
        }

        /// <summary>
        /// A participant's link closed, and it may rejoin within the engine's grace period. The host takes back a body
        /// it holds at once. A hold that had no first sample yet, or that another player in range was refused, ends as
        /// going quiet does: the participant yields. Otherwise its hold ends as an uncontested hand-back does, remembered
        /// in case a request it made stale arrives. Its cooldown and release stay, so a quick rejoin clears nothing.
        /// </summary>
        public OwnershipOutcome LinkClosed(int participant, long nowMicros)
        {
            RequireParticipant(participant);
            RequireTime(nowMicros);
            if (State == OwnershipState.Server || Owner.Participant != participant) return Report(OwnershipResult.NoChange);
            return TakeBack(OwnershipReclaimReason.Disconnected, OwnershipResult.Reclaimed, nowMicros, AwaitingFirstSample || Contested, true);
        }

        /// <summary>
        /// A participant left for good. Its body goes back to the server at once, and its cooldown and release are
        /// forgotten, so whoever gets the slot next starts clean.
        /// </summary>
        public OwnershipOutcome Disconnect(int participant)
        {
            RequireParticipant(participant);
            if (cooldownUntil != null)
            {
                cooldownUntil[participant] = 0;
                strikeLength![participant] = 0;
            }

            if (LastReleaser == participant) ClearRelease();
            if (State == OwnershipState.Server || Owner.Participant != participant) return Report(OwnershipResult.NoChange);
            return Reclaim(OwnershipReclaimReason.Disconnected, OwnershipResult.Reclaimed);
        }

        /// <summary>The host takes the body back for its own reasons, such as a despawn. Nobody yields.</summary>
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
                return TakeBack(OwnershipReclaimReason.NoFirstSample, OwnershipResult.Reclaimed, nowMicros, true, false);
            }

            if (State == OwnershipState.ReleaseWindow && nowMicros - ReleaseDeadlineMicros > policy.SilenceTimeoutMicros)
            {
                return TakeBack(OwnershipReclaimReason.HandbackTimeout, OwnershipResult.Reclaimed, nowMicros, true, false);
            }

            if (nowMicros - LastStreamMicros > policy.SilenceTimeoutMicros)
            {
                return TakeBack(OwnershipReclaimReason.Silence, OwnershipResult.Reclaimed, nowMicros, true, false);
            }

            return Report(OwnershipResult.NoChange);
        }

        /// <summary>
        /// Whether a participant is refused the body for now, because it recently went quiet holding it, or let go of
        /// it while another player in range wanted it.
        /// </summary>
        public bool IsYielding(int participant, long nowMicros)
        {
            RequireParticipant(participant);
            return cooldownUntil != null && nowMicros < cooldownUntil[participant];
        }

        /// <summary>A participant's cooldown: until when it is refused the body, and how long its last strike was.</summary>
        public YieldCooldown CooldownOf(int participant)
        {
            RequireParticipant(participant);
            return cooldownUntil == null
                ? default
                : new YieldCooldown(cooldownUntil[participant], strikeLength![participant]);
        }

        private OwnershipOutcome Grant(int participant, NetQuantizedPosition position, long nowMicros)
        {
            Epoch = unchecked(Epoch + 1);
            Owner = NetOwner.ForParticipant(participant);
            State = OwnershipState.Owned;
            LastValidatedPosition = position;
            LastStreamMicros = nowMicros;
            lastValidatedMicros = nowMicros;
            grantedMicros = nowMicros;
            credit = policy.BurstCreditUnits;
            AwaitingFirstSample = true;
            FirstSampleDeadlineMicros = nowMicros + policy.FirstSampleTimeoutMicros;
            Contested = false;
            ClearRelease();
            return Report(OwnershipResult.Granted, OwnershipReclaimReason.None, true);
        }

        /// <summary>
        /// A request named an epoch the last hold made stale, from another participant within range of the free body.
        /// If that hold ended uncontested within the cooldown, it ended before the request could reach it, so its
        /// owner yields from now as if the hold had been contested. Each release makes its owner yield at most once.
        /// </summary>
        private void YieldAfterRace(int participant, long nowMicros)
        {
            if (LastReleaser == 0 || LastReleaser == participant) return;
            if (nowMicros - LastReleaseMicros >= policy.YieldCooldownMicros) return;
            Yield(LastReleaser, nowMicros, LastReleaseStrikes);
            ClearRelease();
        }

        /// <summary>
        /// Starts a participant's cooldown. Taking turns costs the base cooldown. A strike costs the base cooldown too,
        /// or twice the participant's last strike, up to the cap, when it comes within the cap after the participant's
        /// last cooldown ended.
        /// </summary>
        private void Yield(int participant, long nowMicros, bool strike)
        {
            if (policy.YieldCooldownMicros == 0) return;
            if (cooldownUntil == null)
            {
                cooldownUntil = new long[policy.MaximumParticipants + 1];
                strikeLength = new long[policy.MaximumParticipants + 1];
            }

            var length = policy.YieldCooldownMicros;
            if (strike)
            {
                var last = strikeLength![participant];
                if (last > 0 && nowMicros < cooldownUntil[participant] + policy.MaximumYieldCooldownMicros)
                {
                    length = Math.Min(policy.MaximumYieldCooldownMicros, 2 * last);
                }

                strikeLength[participant] = length;
            }

            cooldownUntil[participant] = nowMicros + length;
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

        /// <summary>
        /// The body goes back to the server, and the owner it leaves yields for the cooldown if it must. An owner that
        /// let go, failed validation or lost its link with nobody kept waiting is remembered instead, when
        /// <paramref name="remember"/> says so, in case a request its hold made stale arrives within the cooldown. Going
        /// quiet and failing validation are strikes; so is a hand-back or closed link before the first sample, or before
        /// the hold has lasted half the release window.
        /// </summary>
        private OwnershipOutcome TakeBack(OwnershipReclaimReason reason, OwnershipResult result, long nowMicros, bool yield, bool remember)
        {
            var participant = Owner.Participant;
            var letGo = reason == OwnershipReclaimReason.None || reason == OwnershipReclaimReason.Disconnected;
            var strike = !letGo || AwaitingFirstSample || nowMicros - grantedMicros < policy.ShortHoldMicros;
            ChangeToServer();
            if (yield)
            {
                Yield(participant, nowMicros, strike);
            }
            else if (remember)
            {
                LastReleaser = participant;
                LastReleaseMicros = nowMicros;
                LastReleaseStrikes = strike;
            }

            return Report(result, reason, true);
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
            Contested = false;
        }

        private void ClearRelease()
        {
            LastReleaser = 0;
            LastReleaseMicros = 0;
            LastReleaseStrikes = false;
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

        private void RequireParticipant(int participant)
        {
            if (participant < 1 || participant > policy.MaximumParticipants) throw new ArgumentOutOfRangeException(nameof(participant));
        }

        private static void RequireTime(long nowMicros)
        {
            if (nowMicros < 0) throw new ArgumentOutOfRangeException(nameof(nowMicros));
        }
    }
}
