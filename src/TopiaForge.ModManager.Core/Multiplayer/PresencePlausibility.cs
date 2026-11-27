using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>The presence flags a pose carries, as the wire writes them.</summary>
    [Flags]
    internal enum PresenceFlags : byte
    {
        None = 0,
        Crouching = 1,
        Talking = 2,
        Typing = 4,
        Away = 8,

        /// <summary>The player's own game moved it in one step, as a respawn, a level load or a placement does.</summary>
        Teleported = 16
    }

    /// <summary>What the host decided about one pose.</summary>
    internal enum PresenceVerdict
    {
        /// <summary>The pose stands: it is relayed, and the next one is measured from it.</summary>
        Accepted = 0,

        /// <summary>The pose was sent before the player heard the host's latest correction; it is dropped and costs nothing.</summary>
        Ignored = 1,

        /// <summary>The pose broke a rule: it is dropped, the player is moved back, and the strike counts.</summary>
        Corrected = 2,

        /// <summary>The pose broke a rule once too often: the player is removed.</summary>
        Kick = 3
    }

    /// <summary>Which rule a pose broke.</summary>
    internal enum PresenceViolation
    {
        None = 0,

        /// <summary>It moved farther than the speed limit allows.</summary>
        Speed = 1,

        /// <summary>It stood outside the world's bounds.</summary>
        Bounds = 2,

        /// <summary>It said it teleported, past the allowance of teleports.</summary>
        Teleport = 3,

        /// <summary>It kept to a correction the host replaced a while ago, so the player never took the new one.</summary>
        IgnoredCorrection = 4
    }

    /// <summary>The host's decision about one pose, and what to do about it.</summary>
    internal readonly struct PresenceCheck
    {
        public PresenceCheck(PresenceVerdict verdict, PresenceViolation violation, PresenceFlags flags, bool hasCorrection, NetQuantizedPosition correctTo, byte correction, int strikes)
        {
            Verdict = verdict;
            Violation = violation;
            Flags = flags;
            HasCorrection = hasCorrection;
            CorrectTo = correctTo;
            Correction = correction;
            Strikes = strikes;
        }

        public PresenceVerdict Verdict { get; }

        public PresenceViolation Violation { get; }

        /// <summary>The flags that stand for an accepted pose: a claim of being away while moving is cleared.</summary>
        public PresenceFlags Flags { get; }

        /// <summary>Whether a correction names a trusted destination: a validated pose, pending placement or readiness spawn.</summary>
        public bool HasCorrection { get; }

        /// <summary>Where the player goes back to; a trusted first-pose target is not itself an accepted pose.</summary>
        public NetQuantizedPosition CorrectTo { get; }

        /// <summary>The correction's number, which the player's poses carry once it took it.</summary>
        public byte Correction { get; }

        /// <summary>The player's strikes after this pose.</summary>
        public int Strikes { get; }
    }

    /// <summary>
    /// One player's poses as the host checks them: a distance budget at the speed limit, the world's bounds, an allowance
    /// of flagged teleports, and strikes that end in a kick. A violation is corrected first: the pose is dropped, the
    /// player is sent back to its last validated position under a new correction number, and poses it sent before it
    /// heard the correction are ignored for a moment rather than struck. Strikes are forgiven one at a time while the
    /// player keeps to the rules. Exact integer arithmetic on the 1/512 m grid and in microseconds, so every platform
    /// decides alike. Not thread-safe; the host's main thread drives it.
    /// </summary>
    internal sealed class PresencePlausibility
    {
        private readonly PresencePolicy policy;
        private readonly TokenBucket teleports;
        private long credit;
        private long fallCredit;
        private long anchorMicros;
        private long lastStrikeMicros;
        private long correctedAtMicros;
        private bool placementExpected;
        private NetQuantizedPosition placement;
        private long placementDeadlineMicros;
        private bool hasInitialPosition;
        private NetQuantizedPosition initialPosition;
        private long initialMicros;

        public PresencePlausibility(PresencePolicy policy, long nowMicros)
        {
            this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
            if (nowMicros < 0) throw new ArgumentOutOfRangeException(nameof(nowMicros));
            teleports = new TokenBucket(policy.TeleportBurst * policy.TeleportRefillSeconds, 1, nowMicros);
            credit = policy.BankUnits;
            fallCredit = policy.FallBankUnits;
            anchorMicros = nowMicros;
        }

        /// <summary>Gets whether a pose has been accepted since the start or the last restart.</summary>
        public bool HasAnchor { get; private set; }

        /// <summary>Gets the last accepted position.</summary>
        public NetQuantizedPosition Anchor { get; private set; }

        /// <summary>Gets the last accepted rotation's bits.</summary>
        public uint AnchorRotation { get; private set; }

        /// <summary>Gets when the last pose was accepted, in the host's time.</summary>
        public long LastAcceptedMicros { get; private set; }

        /// <summary>Gets the correction number the player's poses must carry.</summary>
        public byte Correction { get; private set; }

        /// <summary>Gets the strikes as of the last check, before forgiving any since.</summary>
        public int Strikes { get; private set; }

        /// <summary>Gets the running credit left, in grid units; down to minus the tolerance after an overdraw.</summary>
        public long CreditUnits => credit;

        /// <summary>Gets the strikes still counted at <paramref name="nowMicros"/>.</summary>
        public int StrikesAt(long nowMicros)
        {
            if (Strikes == 0 || nowMicros <= lastStrikeMicros) return Strikes;
            var forgiven = (nowMicros - lastStrikeMicros) / policy.StrikeForgivenessMicros;
            return forgiven >= Strikes ? 0 : Strikes - (int)forgiven;
        }

        /// <summary>Gets whether the last accepted position is recent enough to stand for the player now.</summary>
        public bool IsFresh(long nowMicros) => HasAnchor && nowMicros - LastAcceptedMicros <= policy.StaleAfterMicros;

        /// <summary>
        /// Waits for a trusted start in a new world, keeping strikes and both movement allowances.
        /// </summary>
        public void Restart(long nowMicros)
        {
            HasAnchor = false;
            placementExpected = false;
            hasInitialPosition = false;
            Accrue(nowMicros);
        }

        /// <summary>Supplies the host's readiness spawn once per world; this is not an accepted player pose.</summary>
        public void SetInitialPosition(NetQuantizedPosition position, long nowMicros)
        {
            if (HasAnchor || hasInitialPosition) return;
            hasInitialPosition = true;
            initialPosition = position;
            initialMicros = nowMicros;
        }

        /// <summary>The host placed the player at <paramref name="position"/>: the pose that lands there is taken as it.</summary>
        public void ExpectPlacement(NetQuantizedPosition position, long nowMicros)
        {
            placementExpected = true;
            placement = position;
            placementDeadlineMicros = nowMicros + policy.PlacementWindowMicros;
        }

        /// <summary>Checks one pose, received at <paramref name="nowMicros"/>, and moves the budget on.</summary>
        public PresenceCheck Check(
            NetQuantizedPosition position,
            uint rotation,
            PresenceFlags flags,
            byte correction,
            long nowMicros,
            in PresenceBounds bounds)
        {
            if (placementExpected && nowMicros > placementDeadlineMicros) placementExpected = false;
            // Without a trusted start there is no place to validate or correct to. Wait without spending any allowance.
            if (!HasAnchor && !hasInitialPosition && !placementExpected)
                return new PresenceCheck(PresenceVerdict.Ignored, PresenceViolation.None, PresenceFlags.None, false, default, Correction, Strikes);
            Forgive(nowMicros);
            if (correction != Correction)
            {
                // Poses the player sent before it heard the correction are on their way; they cost nothing for a moment.
                if (nowMicros - correctedAtMicros <= policy.CorrectionGraceMicros)
                {
                    return new PresenceCheck(PresenceVerdict.Ignored, PresenceViolation.None, PresenceFlags.None, false, Anchor, Correction, Strikes);
                }

                return Strike(PresenceViolation.IgnoredCorrection, nowMicros);
            }

            if (!bounds.Contains(position)) return Strike(PresenceViolation.Bounds, nowMicros);
            if (placementExpected && Lands(position))
            {
                placementExpected = false;
                return Accept(position, rotation, flags, nowMicros, resetCredit: true);
            }

            // A pose in flight before the placement may still be near the readiness spawn. It must prove that start,
            // even with a teleport flag, and must not consume the pending host landing.
            if (!HasAnchor)
            {
                if (!hasInitialPosition || !NearInitialPosition(position, nowMicros)) return Strike(PresenceViolation.Speed, nowMicros);
                return Accept(position, rotation, flags, nowMicros, resetCredit: false);
            }

            Accrue(nowMicros);
            var dx = (long)position.X - Anchor.X;
            var dy = (long)position.Y - Anchor.Y;
            var dz = (long)position.Z - Anchor.Z;
            var up = Math.Max(dy, 0);
            var down = Math.Max(-dy, 0);
            var run = NetIntegerMath.CeilingSqrt(dx * dx + dz * dz + up * up);
            if (run <= credit + policy.ToleranceUnits && down <= fallCredit + policy.ToleranceUnits)
            {
                // The tolerance is slack once, not on every pose: what a pose overdraws is owed until the credit comes back,
                // so no run of small overdraws adds up to a speed past the limit.
                credit -= run;
                fallCredit -= down;
                return Accept(position, rotation, flags, nowMicros, resetCredit: false);
            }

            if ((flags & PresenceFlags.Teleported) == 0) return Strike(PresenceViolation.Speed, nowMicros);
            if (!teleports.TryTake(nowMicros, policy.TeleportRefillSeconds)) return Strike(PresenceViolation.Teleport, nowMicros);
            return Accept(position, rotation, flags, nowMicros, resetCredit: true);
        }

        private bool Lands(NetQuantizedPosition position)
        {
            var tolerance = (long)policy.PlacementToleranceUnits;
            return position.DistanceSquaredTo(placement) <= tolerance * tolerance;
        }

        private bool NearInitialPosition(NetQuantizedPosition position, long nowMicros)
        {
            // Allow honest movement during delivery, but never grow a first-pose region beyond two seconds of travel.
            var elapsed = Math.Min(policy.SpeedBankMicros, Math.Max(0, nowMicros - initialMicros));
            var dx = (long)position.X - initialPosition.X;
            var dy = (long)position.Y - initialPosition.Y;
            var dz = (long)position.Z - initialPosition.Z;
            var up = Math.Max(dy, 0);
            var run = NetIntegerMath.CeilingSqrt(dx * dx + dz * dz + up * up);
            return run <= policy.PlacementToleranceUnits + policy.SpeedUnitsPerSecond * elapsed / 1_000_000
                && Math.Max(-dy, 0) <= policy.PlacementToleranceUnits + policy.FallUnitsPerSecond * elapsed / 1_000_000;
        }

        private PresenceCheck Accept(NetQuantizedPosition position, uint rotation, PresenceFlags flags, long nowMicros, bool resetCredit)
        {
            // A player that moved, or turned, since its last pose is not away, whatever it says.
            if ((flags & PresenceFlags.Away) != 0 && HasAnchor && IsActive(position, rotation)) flags &= ~PresenceFlags.Away;
            if (resetCredit)
            {
                credit = policy.BankUnits;
                fallCredit = policy.FallBankUnits;
            }

            HasAnchor = true;
            Anchor = position;
            AnchorRotation = rotation;
            anchorMicros = nowMicros;
            LastAcceptedMicros = nowMicros;
            return new PresenceCheck(PresenceVerdict.Accepted, PresenceViolation.None, flags & ~PresenceFlags.Teleported, false, position, Correction, Strikes);
        }

        private bool IsActive(NetQuantizedPosition position, uint rotation)
        {
            var still = (long)policy.AwayStillUnits;
            if (position.DistanceSquaredTo(Anchor) > still * still) return true;
            if (rotation == AnchorRotation) return false;

            // About ten degrees: the dot of two unit quaternions is the cosine of half the angle between them.
            var before = NetQuantization.DequantizeRotation(AnchorRotation);
            var after = NetQuantization.DequantizeRotation(rotation);
            var dot = Math.Abs((double)before.X * after.X + (double)before.Y * after.Y + (double)before.Z * after.Z + (double)before.W * after.W);
            return dot < 0.9962;
        }

        private PresenceCheck Strike(PresenceViolation violation, long nowMicros)
        {
            Strikes++;
            lastStrikeMicros = nowMicros;
            if (Strikes >= policy.StrikesBeforeKick)
            {
                return new PresenceCheck(PresenceVerdict.Kick, violation, PresenceFlags.None, false, Anchor, Correction, Strikes);
            }

            Correction = unchecked((byte)(Correction + 1));
            correctedAtMicros = nowMicros;

            // The player goes back to where it last stood within the rules, with the credit it had: a correction never refills
            // the bank, so a cheat corrected once is struck again as soon as it runs past the limit.
            var target = HasAnchor ? Anchor : placementExpected ? placement : initialPosition;
            return new PresenceCheck(PresenceVerdict.Corrected, violation, PresenceFlags.None,
                HasAnchor || placementExpected || hasInitialPosition, target, Correction, Strikes);
        }

        private void Forgive(long nowMicros)
        {
            if (Strikes == 0 || nowMicros <= lastStrikeMicros) return;
            var forgiven = (nowMicros - lastStrikeMicros) / policy.StrikeForgivenessMicros;
            if (forgiven == 0) return;
            if (forgiven >= Strikes)
            {
                Strikes = 0;
                return;
            }

            Strikes -= (int)forgiven;
            lastStrikeMicros += forgiven * policy.StrikeForgivenessMicros;
        }

        private void Accrue(long nowMicros)
        {
            if (nowMicros <= anchorMicros) return;
            var elapsed = Math.Min(nowMicros - anchorMicros, policy.SpeedBankMicros);
            credit = Math.Min(policy.BankUnits, credit + policy.SpeedUnitsPerSecond * elapsed / 1_000_000);
            fallCredit = Math.Min(policy.FallBankUnits, fallCredit + policy.FallUnitsPerSecond * elapsed / 1_000_000);
            anchorMicros = nowMicros;
        }
    }
}
