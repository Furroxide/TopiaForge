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
        /// The requester recently lost this body by going quiet, so other players go first until its cooldown ends.
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

        public OwnershipPolicy(
            long releaseWindowMicros = DefaultReleaseWindowMicros,
            long silenceTimeoutMicros = DefaultSilenceTimeoutMicros,
            int maximumSpeedMetersPerSecond = DefaultMaximumSpeedMetersPerSecond,
            long speedBurstMicros = DefaultSpeedBurstMicros,
            int maximumHoldRangeMeters = DefaultMaximumHoldRangeMeters,
            int toleranceUnits = DefaultToleranceUnits,
            int maximumAngularSpeedRadiansPerSecond = DefaultMaximumAngularSpeedRadiansPerSecond,
            long firstSampleTimeoutMicros = DefaultFirstSampleTimeoutMicros,
            long yieldCooldownMicros = DefaultYieldCooldownMicros)
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
            ReleaseWindowMicros = releaseWindowMicros;
            SilenceTimeoutMicros = silenceTimeoutMicros;
            MaximumSpeedMetersPerSecond = maximumSpeedMetersPerSecond;
            SpeedBurstMicros = speedBurstMicros;
            MaximumHoldRangeMeters = maximumHoldRangeMeters;
            ToleranceUnits = toleranceUnits;
            MaximumAngularSpeedRadiansPerSecond = maximumAngularSpeedRadiansPerSecond;
            FirstSampleTimeoutMicros = firstSampleTimeoutMicros;
            YieldCooldownMicros = yieldCooldownMicros;
            SpeedUnitsPerSecond = (long)maximumSpeedMetersPerSecond * NetQuantization.PositionUnitsPerMeter;
            BurstCreditUnits = SpeedUnitsPerSecond * speedBurstMicros / 1_000_000 + toleranceUnits;
            var rangeUnits = (long)maximumHoldRangeMeters * NetQuantization.PositionUnitsPerMeter + toleranceUnits;
            MaximumHoldRangeSquaredUnits = rangeUnits * rangeUnits;
        }

        public static OwnershipPolicy Default { get; } = new OwnershipPolicy();

        /// <summary>How long a thrower keeps simulating its throw before handing it back.</summary>
        public long ReleaseWindowMicros { get; }

        /// <summary>How long an owner may send nothing before the host reclaims the body.</summary>
        public long SilenceTimeoutMicros { get; }

        /// <summary>
        /// How long a new owner has to send its first sample. A client streams from the moment it grabs, so a grant
        /// that hears nothing this long is abandoned, and the body goes back to the server well before the silence
        /// timeout. A value above the silence timeout has no effect, because silence reclaims first.
        /// </summary>
        public long FirstSampleTimeoutMicros { get; }

        /// <summary>
        /// How long a participant that lost the body by going quiet (no first sample, silence or no hand-back) is
        /// refused it, so another player's request wins first. Zero turns yielding off.
        /// </summary>
        public long YieldCooldownMicros { get; }

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
