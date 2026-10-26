using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>
    /// One player's health as the host keeps it. The player's own game owns its health; the host refuses what no game
    /// could report: a value that is not finite, health past the maximum or below zero, a maximum past the policy's,
    /// a maximum that changes after the first report, and health that comes back faster than it can regenerate. A
    /// refused value is replaced by the nearest one the rules allow, and health held back that way rises toward the
    /// report as regeneration allows (<see cref="Advance"/>), so an honest player's catches up with no report more. Not
    /// thread-safe.
    /// </summary>
    internal sealed class PresenceHealth
    {
        private readonly PresencePolicy policy;
        private long lastMicros;
        private float allowance;
        private float reported;

        public PresenceHealth(PresencePolicy policy)
        {
            this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        /// <summary>Gets whether a report has been taken.</summary>
        public bool HasValue { get; private set; }

        public float Health { get; private set; }

        public float Maximum { get; private set; }

        /// <summary>Gets how many reports were refused in part or whole.</summary>
        public long Refused { get; private set; }

        /// <summary>
        /// Takes a report at <paramref name="nowMicros"/> and returns whether it stood as reported. Either way
        /// <see cref="Health"/> and <see cref="Maximum"/> hold what the host keeps.
        /// </summary>
        public bool Take(float health, float maximum, long nowMicros)
        {
            if (float.IsNaN(health) || float.IsInfinity(health) || float.IsNaN(maximum) || float.IsInfinity(maximum) ||
                maximum <= 0f || maximum > policy.MaximumHealth || health < 0f || health > maximum)
            {
                Refused++;
                return false;
            }

            if (!HasValue)
            {
                HasValue = true;
                Health = health;
                Maximum = maximum;
                reported = health;
                lastMicros = nowMicros;
                return true;
            }

            var stood = true;
            if (maximum != Maximum)
            {
                // The maximum is the game's own; a player that changes it is refused, and its health read against the first.
                stood = false;
                health = Math.Min(health, Maximum);
            }

            reported = health;
            var regenerated = Regenerated(nowMicros);
            var ceiling = Math.Min(Maximum, regenerated + allowance);
            if (health > ceiling)
            {
                stood = false;
                health = ceiling;
            }

            // A heal the host made itself is spent by the rise it allowed.
            if (health > regenerated) allowance = Math.Max(0f, allowance - (health - regenerated));
            Health = health;
            lastMicros = nowMicros;
            if (!stood) Refused++;
            return stood;
        }

        /// <summary>
        /// Raises health a report could not have all at once toward that report, as fast as regeneration allows, at
        /// <paramref name="nowMicros"/>; returns whether it rose. The host calls it a few times a second.
        /// </summary>
        public bool Advance(long nowMicros)
        {
            if (!HasValue || Health >= reported || nowMicros <= lastMicros) return false;
            var risen = Math.Min(reported, Regenerated(nowMicros));
            lastMicros = nowMicros;
            if (risen <= Health) return false;
            Health = risen;
            return true;
        }

        /// <summary>
        /// The host healed the player itself (WS3.11): its health may rise by <paramref name="amount"/> more than it could
        /// regenerate, until a report spends the allowance.
        /// </summary>
        public void AllowGain(float amount)
        {
            if (!HasValue || float.IsNaN(amount) || amount <= 0f || float.IsInfinity(amount)) return;
            allowance = Math.Min(Maximum, allowance + amount);
        }

        /// <summary>The health kept now plus what it could have regenerated since it was last set, up to the maximum.</summary>
        private float Regenerated(long nowMicros)
        {
            var elapsed = Math.Max(0, nowMicros - lastMicros);
            var gain = (double)Maximum * policy.HealthGainFractionPerSecond * elapsed / 1_000_000d;
            return Math.Min(Maximum, (float)(Health + gain));
        }
    }
}
