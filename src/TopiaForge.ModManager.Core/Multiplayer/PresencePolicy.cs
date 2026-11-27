using System;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>
    /// The box players stay inside, in grid units. Positions are compared exactly, so every peer agrees at the edges. The
    /// default is the whole quantization range, which every pose fits.
    /// </summary>
    internal readonly struct PresenceBounds : IEquatable<PresenceBounds>
    {
        private PresenceBounds(int minimumX, int minimumY, int minimumZ, int maximumX, int maximumY, int maximumZ, bool isLimited)
        {
            MinimumX = minimumX;
            MinimumY = minimumY;
            MinimumZ = minimumZ;
            MaximumX = maximumX;
            MaximumY = maximumY;
            MaximumZ = maximumZ;
            IsLimited = isLimited;
        }

        /// <summary>Gets bounds that let in every pose the wire can carry.</summary>
        public static PresenceBounds Everywhere => new PresenceBounds(
            NetQuantization.PositionMinimumUnits, NetQuantization.PositionMinimumUnits, NetQuantization.PositionMinimumUnits,
            NetQuantization.PositionMaximumUnits, NetQuantization.PositionMaximumUnits, NetQuantization.PositionMaximumUnits, false);

        public int MinimumX { get; }

        public int MinimumY { get; }

        public int MinimumZ { get; }

        public int MaximumX { get; }

        public int MaximumY { get; }

        public int MaximumZ { get; }

        /// <summary>Gets whether the bounds are narrower than the quantization range.</summary>
        public bool IsLimited { get; }

        /// <summary>
        /// Makes bounds from a box in metres, widened by <paramref name="marginMeters"/> sideways and below and by
        /// <paramref name="headroomMeters"/> above, and kept within the quantization range. A box that is not finite, or
        /// whose minimum passes its maximum, gives <see cref="Everywhere"/>.
        /// </summary>
        public static PresenceBounds FromMeters(NetVector3 minimum, NetVector3 maximum, float marginMeters, float headroomMeters)
        {
            if (!minimum.IsFinite || !maximum.IsFinite || !(minimum.X <= maximum.X && minimum.Y <= maximum.Y && minimum.Z <= maximum.Z) ||
                float.IsNaN(marginMeters) || marginMeters < 0f || float.IsNaN(headroomMeters) || headroomMeters < 0f)
            {
                return Everywhere;
            }

            return new PresenceBounds(
                NetQuantization.QuantizePositionAxis(minimum.X - marginMeters),
                NetQuantization.QuantizePositionAxis(minimum.Y - marginMeters),
                NetQuantization.QuantizePositionAxis(minimum.Z - marginMeters),
                NetQuantization.QuantizePositionAxis(maximum.X + marginMeters),
                NetQuantization.QuantizePositionAxis(maximum.Y + headroomMeters),
                NetQuantization.QuantizePositionAxis(maximum.Z + marginMeters),
                true);
        }

        public bool Contains(NetQuantizedPosition position) =>
            position.X >= MinimumX && position.X <= MaximumX &&
            position.Y >= MinimumY && position.Y <= MaximumY &&
            position.Z >= MinimumZ && position.Z <= MaximumZ;

        public bool Equals(PresenceBounds other) =>
            MinimumX == other.MinimumX && MinimumY == other.MinimumY && MinimumZ == other.MinimumZ &&
            MaximumX == other.MaximumX && MaximumY == other.MaximumY && MaximumZ == other.MaximumZ && IsLimited == other.IsLimited;

        public override bool Equals(object? obj) => obj is PresenceBounds other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(MinimumX, MinimumY, MinimumZ, MaximumX, MaximumY, MaximumZ);
    }

    /// <summary>
    /// The host's rules for players' poses. Speeds are checked with a distance budget, as motion ownership's are: credit
    /// accrues at the speed limit, up to a bank, and each pose spends its displacement. Falling has a budget of its own,
    /// since a player falls faster than it may run. Immutable.
    /// </summary>
    internal sealed class PresencePolicy
    {
        public const int DefaultMaximumSpeedMetersPerSecond = 30;
        public const int DefaultMaximumFallSpeedMetersPerSecond = 64;
        public const long DefaultSpeedBankMicros = 2_000_000;
        public const int DefaultToleranceUnits = 256;
        public const int DefaultStrikesBeforeKick = 5;
        public const long DefaultStrikeForgivenessMicros = 10_000_000;
        public const int DefaultTeleportBurst = 2;
        public const int DefaultTeleportRefillSeconds = 10;
        public const long DefaultCorrectionGraceMicros = 1_000_000;
        public const int DefaultPlacementToleranceUnits = 2 * NetQuantization.PositionUnitsPerMeter;
        public const long DefaultPlacementWindowMicros = 10_000_000;
        public const long DefaultStaleAfterMicros = 2_500_000;
        public const int DefaultAwayStillUnits = NetQuantization.PositionUnitsPerMeter / 2;
        public const float DefaultMaximumHealth = 1_000_000f;
        public const float DefaultHealthGainFractionPerSecond = 0.25f;

        public PresencePolicy(
            int maximumSpeedMetersPerSecond = DefaultMaximumSpeedMetersPerSecond,
            int maximumFallSpeedMetersPerSecond = DefaultMaximumFallSpeedMetersPerSecond,
            long speedBankMicros = DefaultSpeedBankMicros,
            int toleranceUnits = DefaultToleranceUnits,
            int strikesBeforeKick = DefaultStrikesBeforeKick,
            long strikeForgivenessMicros = DefaultStrikeForgivenessMicros,
            int teleportBurst = DefaultTeleportBurst,
            int teleportRefillSeconds = DefaultTeleportRefillSeconds,
            long correctionGraceMicros = DefaultCorrectionGraceMicros,
            int placementToleranceUnits = DefaultPlacementToleranceUnits,
            long placementWindowMicros = DefaultPlacementWindowMicros,
            long staleAfterMicros = DefaultStaleAfterMicros,
            int awayStillUnits = DefaultAwayStillUnits,
            float maximumHealth = DefaultMaximumHealth,
            float healthGainFractionPerSecond = DefaultHealthGainFractionPerSecond)
        {
            Require(maximumSpeedMetersPerSecond, 1, 1_000, nameof(maximumSpeedMetersPerSecond));
            Require(maximumFallSpeedMetersPerSecond, 1, 1_000, nameof(maximumFallSpeedMetersPerSecond));
            Require(speedBankMicros, 1, 60_000_000, nameof(speedBankMicros));
            Require(toleranceUnits, 0, 64 * NetQuantization.PositionUnitsPerMeter, nameof(toleranceUnits));
            Require(strikesBeforeKick, 1, 1_000, nameof(strikesBeforeKick));
            Require(strikeForgivenessMicros, 1, 3_600_000_000, nameof(strikeForgivenessMicros));
            Require(teleportBurst, 1, 1_000, nameof(teleportBurst));
            Require(teleportRefillSeconds, 1, 3_600, nameof(teleportRefillSeconds));
            Require(correctionGraceMicros, 0, 60_000_000, nameof(correctionGraceMicros));
            Require(placementToleranceUnits, 0, 64 * NetQuantization.PositionUnitsPerMeter, nameof(placementToleranceUnits));
            Require(placementWindowMicros, 1, 600_000_000, nameof(placementWindowMicros));
            Require(staleAfterMicros, 1, 600_000_000, nameof(staleAfterMicros));
            Require(awayStillUnits, 0, 64 * NetQuantization.PositionUnitsPerMeter, nameof(awayStillUnits));
            if (float.IsNaN(maximumHealth) || maximumHealth <= 0f || float.IsInfinity(maximumHealth)) throw new ArgumentOutOfRangeException(nameof(maximumHealth));
            if (float.IsNaN(healthGainFractionPerSecond) || healthGainFractionPerSecond < 0f || healthGainFractionPerSecond > 1_000f)
            {
                throw new ArgumentOutOfRangeException(nameof(healthGainFractionPerSecond));
            }

            MaximumSpeedMetersPerSecond = maximumSpeedMetersPerSecond;
            MaximumFallSpeedMetersPerSecond = maximumFallSpeedMetersPerSecond;
            SpeedBankMicros = speedBankMicros;
            ToleranceUnits = toleranceUnits;
            StrikesBeforeKick = strikesBeforeKick;
            StrikeForgivenessMicros = strikeForgivenessMicros;
            TeleportBurst = teleportBurst;
            TeleportRefillSeconds = teleportRefillSeconds;
            CorrectionGraceMicros = correctionGraceMicros;
            PlacementToleranceUnits = placementToleranceUnits;
            PlacementWindowMicros = placementWindowMicros;
            StaleAfterMicros = staleAfterMicros;
            AwayStillUnits = awayStillUnits;
            MaximumHealth = maximumHealth;
            HealthGainFractionPerSecond = healthGainFractionPerSecond;
            SpeedUnitsPerSecond = (long)maximumSpeedMetersPerSecond * NetQuantization.PositionUnitsPerMeter;
            FallUnitsPerSecond = (long)maximumFallSpeedMetersPerSecond * NetQuantization.PositionUnitsPerMeter;
            BankUnits = SpeedUnitsPerSecond * speedBankMicros / 1_000_000;
            FallBankUnits = FallUnitsPerSecond * speedBankMicros / 1_000_000;
        }

        public static PresencePolicy Default { get; } = new PresencePolicy();

        /// <summary>The fastest a player may run, jump or climb.</summary>
        public int MaximumSpeedMetersPerSecond { get; }

        /// <summary>The fastest a player may fall. The velocity format carries up to 64 m/s.</summary>
        public int MaximumFallSpeedMetersPerSecond { get; }

        /// <summary>How much travel time a player may bank while standing or walking slowly: two seconds at full speed.</summary>
        public long SpeedBankMicros { get; }

        /// <summary>Slack, in grid units, for quantization and timing noise: half a metre.</summary>
        public int ToleranceUnits { get; }

        /// <summary>The strikes at which a player is removed rather than corrected.</summary>
        public int StrikesBeforeKick { get; }

        /// <summary>How long a player must keep to the rules for one strike to be forgiven.</summary>
        public long StrikeForgivenessMicros { get; }

        /// <summary>How many flagged teleports a player may make at once.</summary>
        public int TeleportBurst { get; }

        /// <summary>How long one flagged teleport takes to come back.</summary>
        public int TeleportRefillSeconds { get; }

        /// <summary>How long poses sent before a correction arrived are ignored rather than struck.</summary>
        public long CorrectionGraceMicros { get; }

        /// <summary>How close to a placement the host made the next pose must land to be taken as that placement.</summary>
        public int PlacementToleranceUnits { get; }

        /// <summary>How long a placement waits for the pose that lands on it.</summary>
        public long PlacementWindowMicros { get; }

        /// <summary>How long a validated position stays good without a new accepted pose; a player at rest sends one a second.</summary>
        public long StaleAfterMicros { get; }

        /// <summary>How far a player may move between poses and still say it is away.</summary>
        public int AwayStillUnits { get; }

        /// <summary>The largest maximum health a player may report.</summary>
        public float MaximumHealth { get; }

        /// <summary>How much of its maximum health a player may regain in a second, unless the host healed it.</summary>
        public float HealthGainFractionPerSecond { get; }

        public long SpeedUnitsPerSecond { get; }

        public long FallUnitsPerSecond { get; }

        /// <summary>The most running credit a player can bank, in grid units.</summary>
        public long BankUnits { get; }

        /// <summary>The most falling credit a player can bank, in grid units.</summary>
        public long FallBankUnits { get; }

        private static void Require(long value, long minimum, long maximum, string parameterName)
        {
            if (value < minimum || value > maximum) throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
