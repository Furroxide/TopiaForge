using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>Who simulates a body, from the point of view of one peer.</summary>
    internal enum OwnershipState
    {
        /// <summary>The host simulates the body; peers show a replica.</summary>
        Server = 0,

        /// <summary>A client asked for the body and is already simulating it, before the host answers.</summary>
        Requested = 1,

        /// <summary>A participant simulates the body and streams it to the host.</summary>
        Owned = 2,

        /// <summary>The owner threw the body and keeps simulating it until the release window ends.</summary>
        ReleaseWindow = 3
    }

    internal enum OwnershipResult
    {
        /// <summary>Nothing changed.</summary>
        NoChange = 0,

        /// <summary>The requester owns the body: a new grant, a steal from a release window, or a regrab.</summary>
        Granted = 1,

        /// <summary>The requester already owns the body at this epoch.</summary>
        AlreadyOwned = 2,

        /// <summary>Another participant is holding the body. The first grab wins.</summary>
        Denied = 3,

        /// <summary>The message names an epoch other than the current one and is refused.</summary>
        StaleEpoch = 4,

        /// <summary>The sender does not own the body.</summary>
        NotOwner = 5,

        /// <summary>A streamed sample passed validation.</summary>
        Accepted = 6,

        /// <summary>A sample or hand-back failed validation, and the host reclaimed the body.</summary>
        Rejected = 7,

        /// <summary>The release window is open.</summary>
        Releasing = 8,

        /// <summary>The owner handed the body back with its final state.</summary>
        HandedBack = 9,

        /// <summary>The host took the body back.</summary>
        Reclaimed = 10,

        /// <summary>The requester's avatar is farther from the body than the hold range.</summary>
        OutOfRange = 11,

        /// <summary>
        /// The requester recently went quiet holding this body, or let go of it while another player in range wanted
        /// it, so other players go first until its own cooldown ends.
        /// </summary>
        Yielding = 12
    }

    internal enum OwnershipReclaimReason
    {
        None = 0,

        /// <summary>The owner sent nothing for longer than the silence timeout.</summary>
        Silence = 1,

        /// <summary>The release window ended and no hand-back came within the silence timeout.</summary>
        HandbackTimeout = 2,

        /// <summary>The owner's link closed, or it left the session.</summary>
        Disconnected = 3,

        /// <summary>A sample or hand-back moved too fast, or a sample, hand-back or throw held the body out of range.</summary>
        ValidationFailed = 4,

        /// <summary>The host took the body for its own reasons, such as a despawn.</summary>
        Host = 5,

        /// <summary>A new owner sent no first sample before the first-sample deadline.</summary>
        NoFirstSample = 6
    }

    /// <summary>The answer to one ownership event: what happened, and the state that resulted.</summary>
    internal readonly struct OwnershipOutcome
    {
        public OwnershipOutcome(
            OwnershipResult result,
            OwnershipReclaimReason reclaimReason,
            OwnershipState state,
            NetOwner owner,
            uint epoch,
            bool epochChanged)
        {
            Result = result;
            ReclaimReason = reclaimReason;
            State = state;
            Owner = owner;
            Epoch = epoch;
            EpochChanged = epochChanged;
        }

        public OwnershipResult Result { get; }

        public OwnershipReclaimReason ReclaimReason { get; }

        public OwnershipState State { get; }

        public NetOwner Owner { get; }

        /// <summary>The current epoch, which a refused sender adopts before trying again.</summary>
        public uint Epoch { get; }

        /// <summary>Whether ownership changed hands, so every peer must hear the new owner and epoch.</summary>
        public bool EpochChanged { get; }

        public override string ToString() =>
            Result + "(" + ReclaimReason + ") " + State + " " + Owner + " @" + Epoch + (EpochChanged ? " changed" : string.Empty);
    }

    /// <summary>
    /// A request a client's claim sends: the number the host's answer must echo, and the epoch the request names.
    /// Numbers count up per claim, across resets, so an answer to a request the claim gave up on is told apart.
    /// </summary>
    internal readonly struct OwnershipRequest
    {
        public OwnershipRequest(uint number, uint epoch)
        {
            Number = number;
            Epoch = epoch;
        }

        public uint Number { get; }

        public uint Epoch { get; }

        public override string ToString() => "#" + Number + "@" + Epoch;
    }

    /// <summary>Why a client's claim last stopped waiting for what it had sent.</summary>
    internal enum OwnershipClaimRecovery
    {
        None = 0,

        /// <summary>A request went unanswered for the answer timeout, so the claim took it as lost.</summary>
        AnswerTimeout = 1,

        /// <summary>An answer named a later request, so the requests before it never reached the host.</summary>
        AnswerSkipped = 2,

        /// <summary>The runtime reset the claim because the link to the host closed.</summary>
        LinkClosed = 3,

        /// <summary>The runtime reset the claim because the client reconnected or rejoined.</summary>
        Reconnected = 4,

        /// <summary>The runtime reset the claim because the session was replaced or ended.</summary>
        SessionReplaced = 5,

        /// <summary>The runtime reset the claim because the client's own channel dropped a message from the host.</summary>
        ChannelDropped = 6
    }

    /// <summary>One participant's cooldown on a body: until when it is refused, and how long its last strike was.</summary>
    internal readonly struct YieldCooldown
    {
        public YieldCooldown(long untilMicros, long strikeMicros)
        {
            UntilMicros = untilMicros;
            StrikeMicros = strikeMicros;
        }

        /// <summary>When the participant may take the body again; zero if it never had to yield.</summary>
        public long UntilMicros { get; }

        /// <summary>
        /// How long the cooldown of its last strike was, which its next strike within the cap after its last cooldown
        /// ended doubles; zero if it never struck.
        /// </summary>
        public long StrikeMicros { get; }

        public override string ToString() => UntilMicros + " (" + StrikeMicros + ")";
    }

    /// <summary>The state an owner hands back when its release window ends.</summary>
    internal readonly struct MotionHandback
    {
        public MotionHandback(NetQuantizedPosition position, NetVector3 linearVelocity, NetVector3 angularVelocity)
        {
            Position = position;
            LinearVelocity = linearVelocity;
            AngularVelocity = angularVelocity;
        }

        public NetQuantizedPosition Position { get; }

        /// <summary>Metres per second.</summary>
        public NetVector3 LinearVelocity { get; }

        /// <summary>Radians per second.</summary>
        public NetVector3 AngularVelocity { get; }
    }

    /// <summary>
    /// Timing and validation limits for motion ownership. Speeds are checked with a distance budget: credit accrues
    /// at the speed limit, up to a quarter-second burst plus a tolerance, and each sample spends its displacement.
    /// </summary>
    internal sealed class OwnershipPolicy
    {
        public const long DefaultReleaseWindowMicros = 1_000_000;
        public const long DefaultSilenceTimeoutMicros = 1_000_000;
        public const int DefaultMaximumSpeedMetersPerSecond = 64;
        public const long DefaultSpeedBurstMicros = 250_000;
        public const int DefaultMaximumHoldRangeMeters = 100;
        public const int DefaultToleranceUnits = 256;
        public const int DefaultMaximumAngularSpeedRadiansPerSecond = 64;
        public const long DefaultFirstSampleTimeoutMicros = 250_000;
        public const long DefaultYieldCooldownMicros = 1_000_000;
        public const long DefaultMaximumYieldCooldownMicros = 8_000_000;
        public const int DefaultMaximumParticipants = 16;
        public const long DefaultAnswerTimeoutMicros = 2_000_000;

        public OwnershipPolicy(
            long releaseWindowMicros = DefaultReleaseWindowMicros,
            long silenceTimeoutMicros = DefaultSilenceTimeoutMicros,
            int maximumSpeedMetersPerSecond = DefaultMaximumSpeedMetersPerSecond,
            long speedBurstMicros = DefaultSpeedBurstMicros,
            int maximumHoldRangeMeters = DefaultMaximumHoldRangeMeters,
            int toleranceUnits = DefaultToleranceUnits,
            int maximumAngularSpeedRadiansPerSecond = DefaultMaximumAngularSpeedRadiansPerSecond,
            long firstSampleTimeoutMicros = DefaultFirstSampleTimeoutMicros,
            long yieldCooldownMicros = DefaultYieldCooldownMicros,
            long maximumYieldCooldownMicros = DefaultMaximumYieldCooldownMicros,
            int maximumParticipants = DefaultMaximumParticipants,
            long answerTimeoutMicros = DefaultAnswerTimeoutMicros)
        {
            Require(releaseWindowMicros, 1, 60_000_000, nameof(releaseWindowMicros));
            Require(silenceTimeoutMicros, 1, 60_000_000, nameof(silenceTimeoutMicros));
            Require(maximumSpeedMetersPerSecond, 1, 1_000, nameof(maximumSpeedMetersPerSecond));
            Require(speedBurstMicros, 1, 10_000_000, nameof(speedBurstMicros));
            Require(maximumHoldRangeMeters, 1, 4_096, nameof(maximumHoldRangeMeters));
            Require(toleranceUnits, 0, 64 * NetQuantization.PositionUnitsPerMeter, nameof(toleranceUnits));
            Require(maximumAngularSpeedRadiansPerSecond, 1, 1_000, nameof(maximumAngularSpeedRadiansPerSecond));
            Require(firstSampleTimeoutMicros, 1, 60_000_000, nameof(firstSampleTimeoutMicros));
            Require(yieldCooldownMicros, 0, 60_000_000, nameof(yieldCooldownMicros));
            Require(maximumYieldCooldownMicros, yieldCooldownMicros, 60_000_000, nameof(maximumYieldCooldownMicros));
            Require(maximumParticipants, 1, 256, nameof(maximumParticipants));
            Require(answerTimeoutMicros, 1, 60_000_000, nameof(answerTimeoutMicros));
            ReleaseWindowMicros = releaseWindowMicros;
            SilenceTimeoutMicros = silenceTimeoutMicros;
            MaximumSpeedMetersPerSecond = maximumSpeedMetersPerSecond;
            SpeedBurstMicros = speedBurstMicros;
            MaximumHoldRangeMeters = maximumHoldRangeMeters;
            ToleranceUnits = toleranceUnits;
            MaximumAngularSpeedRadiansPerSecond = maximumAngularSpeedRadiansPerSecond;
            FirstSampleTimeoutMicros = firstSampleTimeoutMicros;
            YieldCooldownMicros = yieldCooldownMicros;
            MaximumYieldCooldownMicros = maximumYieldCooldownMicros;
            MaximumParticipants = maximumParticipants;
            AnswerTimeoutMicros = answerTimeoutMicros;
            SpeedUnitsPerSecond = (long)maximumSpeedMetersPerSecond * NetQuantization.PositionUnitsPerMeter;
            BurstCreditUnits = SpeedUnitsPerSecond * speedBurstMicros / 1_000_000 + toleranceUnits;
            var rangeUnits = (long)maximumHoldRangeMeters * NetQuantization.PositionUnitsPerMeter + toleranceUnits;
            MaximumHoldRangeSquaredUnits = rangeUnits * rangeUnits;
        }

        public static OwnershipPolicy Default { get; } = new OwnershipPolicy();

        /// <summary>How long a thrower keeps simulating its throw before handing it back.</summary>
        public long ReleaseWindowMicros { get; }

        /// <summary>
        /// Letting go of a hold shorter than this is a strike: half the release window. A client's claim holds a body at
        /// least the whole window, since it hands back only when its window closes; the other half allows for the grab
        /// reaching the host later than the hand-back does, after a retransmission.
        /// </summary>
        public long ShortHoldMicros => ReleaseWindowMicros / 2;

        /// <summary>How long an owner may send nothing before the host reclaims the body.</summary>
        public long SilenceTimeoutMicros { get; }

        /// <summary>
        /// How long a new owner has to send its first sample. A client streams from the moment it grabs, so a grant
        /// that hears nothing this long is abandoned, and the body goes back to the server well before the silence
        /// timeout. A value above the silence timeout has no effect, because silence reclaims first.
        /// </summary>
        public long FirstSampleTimeoutMicros { get; }

        /// <summary>
        /// How long a participant that lost the body by going quiet (no first sample, silence, no hand-back, or a closed
        /// link before its first sample), or let go of it while another player in range wanted it, is refused it, so
        /// another player's request wins first. It is also how long after an uncontested release a stale request still
        /// counts. Zero turns yielding off.
        /// </summary>
        public long YieldCooldownMicros { get; }

        /// <summary>
        /// The longest cooldown, and how long after its last cooldown ended a participant's strikes are remembered. A
        /// strike (going quiet, failing validation, or letting go of a hold shorter than <see cref="ShortHoldMicros"/>)
        /// within that time doubles the participant's last strike, up to this; taking turns always costs the base
        /// cooldown.
        /// </summary>
        public long MaximumYieldCooldownMicros { get; }

        /// <summary>
        /// The highest participant slot the machine accepts. The runtime's slots are dense, so this is the session's
        /// largest participant count; it bounds the cooldown table, one entry per slot.
        /// </summary>
        public int MaximumParticipants { get; }

        /// <summary>
        /// How long a client's claim waits for the host to answer a request before it takes the request as lost. The
        /// system channels drop messages past their bounds without telling the sender, so an answer can be missing on a
        /// link that stays open.
        /// </summary>
        public long AnswerTimeoutMicros { get; }

        public int MaximumSpeedMetersPerSecond { get; }

        public long SpeedBurstMicros { get; }

        /// <summary>
        /// The farthest a body may be from the participant's avatar when it is granted, held or thrown. During the
        /// release window a thrown body has no range limit; the speed budget still applies.
        /// </summary>
        public int MaximumHoldRangeMeters { get; }

        /// <summary>Slack, in grid units, for quantization and timing noise.</summary>
        public int ToleranceUnits { get; }

        public int MaximumAngularSpeedRadiansPerSecond { get; }

        public long SpeedUnitsPerSecond { get; }

        /// <summary>The most distance credit a body can bank, in grid units.</summary>
        public long BurstCreditUnits { get; }

        public long MaximumHoldRangeSquaredUnits { get; }

        private static void Require(long value, long minimum, long maximum, string parameterName)
        {
            if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
