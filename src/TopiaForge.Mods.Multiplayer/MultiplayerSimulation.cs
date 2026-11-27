using System;

namespace TopiaForge.Mods
{
    /// <summary>
    /// Describes one canonical fixed simulation step delivered through
    /// <see cref="IMultiplayerSession.SubscribeTick"/>. Steps run only where this process holds world authority.
    /// </summary>
    public readonly struct MultiplayerTickContext
    {
        /// <summary>Creates a tick context. Providers and test rigs normally construct this value.</summary>
        public MultiplayerTickContext(NetworkTick tick, double deltaSeconds)
        {
            if (double.IsNaN(deltaSeconds) || deltaSeconds <= 0 || deltaSeconds > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
            }

            Tick = tick;
            DeltaSeconds = deltaSeconds;
        }

        /// <summary>Gets the canonical tick being simulated.</summary>
        public NetworkTick Tick { get; }

        /// <summary>Gets the fixed step length in seconds, which is 1/20 s for the live provider.</summary>
        public double DeltaSeconds { get; }
    }

    /// <summary>Controls how committed changes to replicated state or objects travel to other peers.</summary>
    public enum MultiplayerDelivery
    {
        /// <summary>Changes are coalesced into the next downstream snapshot. This is the default.</summary>
        Coalesced = 0,

        /// <summary>Each committed change is sent reliably and in order as soon as its transaction commits.</summary>
        Immediate = 1
    }

    /// <summary>Presence flags carried in a participant's replicated pose.</summary>
    [Flags]
    public enum MultiplayerPoseFlags
    {
        /// <summary>No flags are set.</summary>
        None = 0,

        /// <summary>The player is crouching.</summary>
        Crouching = 1,

        /// <summary>The player is transmitting voice.</summary>
        Talking = 2,

        /// <summary>The player is typing in chat.</summary>
        Typing = 4,

        /// <summary>The player has been idle long enough to count as away.</summary>
        Away = 8
    }

    /// <summary>
    /// A participant's latest replicated pose. The owning process is authoritative for it; the host checks it for
    /// plausibility. Read it through <see cref="IMultiplayerSession.TryGetPose"/> or <see cref="IPlayer.TryGetSnapshot"/>.
    /// </summary>
    public readonly struct MultiplayerPose : IEquatable<MultiplayerPose>
    {
        /// <summary>Gets the maximum length of <see cref="HeldItemId"/>.</summary>
        public const int MaximumHeldItemIdLength = 128;

        /// <summary>Creates a pose. Providers and test rigs normally construct this value.</summary>
        public MultiplayerPose(
            NetworkTick sampledAt,
            Vec3 position,
            Ray aim,
            Vec3 velocity,
            float health,
            float maximumHealth,
            MultiplayerPoseFlags flags = MultiplayerPoseFlags.None,
            string? heldItemId = null)
        {
            if (!IsFinite(position) || !IsFinite(aim.Origin) || !IsFinite(aim.Direction) || !IsFinite(velocity))
            {
                throw new ArgumentException("A replicated pose requires finite vectors.");
            }

            if (float.IsNaN(maximumHealth) || float.IsInfinity(maximumHealth) || maximumHealth < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumHealth));
            }

            if (float.IsNaN(health) || health < 0 || health > maximumHealth)
            {
                throw new ArgumentOutOfRangeException(nameof(health));
            }

            if ((flags & ~(MultiplayerPoseFlags.Crouching | MultiplayerPoseFlags.Talking |
                           MultiplayerPoseFlags.Typing | MultiplayerPoseFlags.Away)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(flags));
            }

            if (heldItemId != null && (heldItemId.Length > MaximumHeldItemIdLength || HasControlCharacter(heldItemId)))
            {
                throw new ArgumentException("A held item id must be at most 128 characters without control characters.", nameof(heldItemId));
            }

            SampledAt = sampledAt;
            Position = position;
            Aim = aim;
            Velocity = velocity;
            Health = health;
            MaximumHealth = maximumHealth;
            Flags = flags;
            HeldItemId = string.IsNullOrEmpty(heldItemId) ? null : heldItemId;
        }

        /// <summary>Gets the canonical tick at which the owner sampled this pose.</summary>
        public NetworkTick SampledAt { get; }

        /// <summary>Gets the player's world position.</summary>
        public Vec3 Position { get; }

        /// <summary>Gets the player's aim ray.</summary>
        public Ray Aim { get; }

        /// <summary>Gets the player's velocity in metres per second.</summary>
        public Vec3 Velocity { get; }

        /// <summary>Gets the player's current health.</summary>
        public float Health { get; }

        /// <summary>Gets the player's maximum health.</summary>
        public float MaximumHealth { get; }

        /// <summary>Gets the presence flags.</summary>
        public MultiplayerPoseFlags Flags { get; }

        /// <summary>Gets the held item's catalog id, or null when the player holds nothing.</summary>
        public string? HeldItemId { get; }

        /// <inheritdoc/>
        public bool Equals(MultiplayerPose other) =>
            SampledAt.Equals(other.SampledAt) &&
            Position.Equals(other.Position) &&
            Aim.Equals(other.Aim) &&
            Velocity.Equals(other.Velocity) &&
            Health.Equals(other.Health) &&
            MaximumHealth.Equals(other.MaximumHealth) &&
            Flags == other.Flags &&
            string.Equals(HeldItemId, other.HeldItemId, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is MultiplayerPose other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = SampledAt.GetHashCode();
                hash = (hash * 397) ^ Position.GetHashCode();
                hash = (hash * 397) ^ Aim.GetHashCode();
                hash = (hash * 397) ^ (int)Flags;
                return hash;
            }
        }

        private static bool IsFinite(Vec3 value) =>
            !(float.IsNaN(value.X) || float.IsInfinity(value.X) ||
              float.IsNaN(value.Y) || float.IsInfinity(value.Y) ||
              float.IsNaN(value.Z) || float.IsInfinity(value.Z));

        private static bool HasControlCharacter(string value)
        {
            foreach (var character in value)
            {
                if (char.IsControl(character)) return true;
            }

            return false;
        }
    }
}
