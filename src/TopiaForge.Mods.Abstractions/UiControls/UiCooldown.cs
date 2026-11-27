using System;

namespace TopiaForge.Mods
{
    /// <summary>Describes a cooldown or a single refill on the current shared UI clock.</summary>
    /// <remarks>
    /// A cooldown binds to the admitting clock epoch. A retained tree treats a deadline from an ended epoch
    /// as expired. A new tree must supply a deadline from its current epoch.
    /// </remarks>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class UiCooldown
    {
        // Keep 1 exclusive until the deadline even when a double fraction rounds to a float.
        private const float ActiveMaximum = 0.99999994f;

        /// <summary>Creates a deadline with the full duration for its visual sweep.</summary>
        /// <param name="endsAt">A finite nonnegative deadline on <see cref="IUiService.ClockTime"/>.</param>
        /// <param name="durationSeconds">A finite duration greater than zero and at most 86,400 seconds.</param>
        /// <exception cref="ArgumentOutOfRangeException">The deadline or the duration is outside its stated range.</exception>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiCooldown(double endsAt, double durationSeconds)
        {
            if (!double.IsFinite(endsAt) || endsAt < 0)
                throw new ArgumentOutOfRangeException(nameof(endsAt));
            if (!(durationSeconds > 0 && durationSeconds <= 86_400))
                throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            EndsAt = endsAt;
            DurationSeconds = durationSeconds;
        }

        /// <summary>Gets the authored deadline on the shared UI clock.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public double EndsAt { get; }

        /// <summary>Gets the full duration of the cooldown sweep or refill.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public double DurationSeconds { get; }

        /// <summary>Rounds remaining seconds up, saturates at <see cref="int.MaxValue"/>, and returns zero at expiry.</summary>
        /// <param name="clockTime">The finite nonnegative time on the same clock as the deadline.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="clockTime"/> is negative or nonfinite.</exception>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public int RemainingSeconds(double clockTime) => CountSeconds(Delta(clockTime));

        /// <summary>Gets the elapsed fraction, clamped from zero before the start to one at expiry.</summary>
        /// <param name="clockTime">The finite nonnegative time on the same clock as the deadline.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="clockTime"/> is negative or nonfinite.</exception>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public float Elapsed(double clockTime) => ElapsedFrom(Delta(clockTime));

        internal UiClockBinding.EpochIdentity? ClockIdentity { get; private set; }
        internal string? ClockEpoch => ClockIdentity?.Value;

        internal void CheckClock(string epoch)
        {
            if (ClockEpoch is { } bound && !StringComparer.Ordinal.Equals(bound, epoch))
                throw new ArgumentException("UiCooldown belongs to an ended clock epoch. Create a new deadline from IUiService.ClockTime.");
        }

        internal void CommitClock(UiClockBinding.EpochIdentity identity)
        {
            CheckClock(identity.Value);
            ClockIdentity ??= identity;
        }

        internal UiCooldownFrame Sample(double clockTime, string epoch)
        {
            var remaining = Delta(clockTime);
            if (ClockEpoch is { } bound && !StringComparer.Ordinal.Equals(bound, epoch)) remaining = 0;
            return new UiCooldownFrame(CountSeconds(remaining), ElapsedFrom(remaining));
        }

        private double Delta(double clockTime)
        {
            if (!double.IsFinite(clockTime) || clockTime < 0)
                throw new ArgumentOutOfRangeException(nameof(clockTime));
            return EndsAt - clockTime;
        }

        private static int CountSeconds(double remaining) => (int)Math.Min(int.MaxValue, Math.Ceiling(Math.Max(0, remaining)));

        private float ElapsedFrom(double remaining)
        {
            if (remaining <= 0) return 1;
            if (remaining >= DurationSeconds) return 0;
            return Math.Min(ActiveMaximum, (float)((DurationSeconds - remaining) / DurationSeconds));
        }
    }

    internal readonly struct UiCooldownFrame
    {
        internal UiCooldownFrame(int remainingSeconds, float elapsed)
        {
            RemainingSeconds = remainingSeconds;
            Elapsed = elapsed;
        }

        internal int RemainingSeconds { get; }
        internal float Elapsed { get; }
        internal bool IsRunning => RemainingSeconds > 0;
    }

    /// <summary>Describes the authored number of available charges and an optional refill of one charge.</summary>
    /// <remarks>The renderer adds the arriving charge to its displayed count at the deadline.</remarks>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class UiCharges
    {
        /// <summary>Creates the charge count and its optional next refill.</summary>
        /// <param name="available">The available charge count, from zero through <paramref name="maximum"/>.</param>
        /// <param name="maximum">The capacity, from 1 to 32.</param>
        /// <param name="refill">The optional refill deadline. A full charge count has no refill.</param>
        /// <exception cref="ArgumentOutOfRangeException">A count is outside its permitted range.</exception>
        /// <exception cref="ArgumentException">A refill is supplied for a full charge count.</exception>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiCharges(int available, int maximum, UiCooldown? refill = null)
        {
            if (maximum < 1 || maximum > 32) throw new ArgumentOutOfRangeException(nameof(maximum));
            if (available < 0 || available > maximum) throw new ArgumentOutOfRangeException(nameof(available));
            if (available == maximum && refill != null)
                throw new ArgumentException("A full charge count has no refill.", nameof(refill));
            Available = available;
            Maximum = maximum;
            Refill = refill;
        }

        /// <summary>Gets the authored number of available charges.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public int Available { get; }

        /// <summary>Gets the maximum number of charges.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public int Maximum { get; }

        /// <summary>Gets the optional deadline for the next single charge.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public UiCooldown? Refill { get; }
    }
}
