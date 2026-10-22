using System;
using System.Globalization;

namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// What holds the session's time where it is. The values mirror Chronos' public <c>TimeMode</c> one for one, so the
    /// core SDK assembly needs no reference to the Chronos contract.
    /// </summary>
    internal enum InternalTimeMode : byte
    {
        /// <summary>Real time: the world runs at scale one and nothing holds it.</summary>
        Realtime = 0,

        /// <summary>Slowed, or ramped by a driver: a scale below one.</summary>
        Slowed = 1,

        /// <summary>Frozen: scale zero.</summary>
        Paused = 2,

        /// <summary>A turn scheduler holds the world: frozen between turns, running while an actor acts.</summary>
        TurnBased = 3
    }

    /// <summary>
    /// The session's time (WS3.13): the scale the shared world runs at, and what holds it there. A listen host's time
    /// authority, Chronos, publishes it, and every joined player follows it. It is valid by construction: a finite scale
    /// from zero, frozen, to one, real time, which are the bounds Chronos keeps a slow to, with a mode that agrees with the
    /// scale. The default value is real time.
    /// </summary>
    internal readonly struct InternalSessionTime : IEquatable<InternalSessionTime>
    {
        private readonly float scale;
        private readonly bool specified;

        /// <param name="scale">The world's scale: zero is frozen, one is real time.</param>
        /// <param name="mode">What holds the world at that scale.</param>
        /// <exception cref="ArgumentOutOfRangeException">The pair is not valid: see <see cref="IsValid"/>.</exception>
        internal InternalSessionTime(float scale, InternalTimeMode mode)
        {
            if (!IsValid(scale, mode))
            {
                throw new ArgumentOutOfRangeException(nameof(scale), "The session's time is a finite scale from zero to one, with a mode that agrees with it.");
            }

            // Negative zero is zero: one frozen time has one form, on the wire and in comparisons.
            this.scale = scale == 0f ? 0f : scale;
            Mode = mode;
            specified = true;
        }

        /// <summary>Gets real time: scale one, held by nothing.</summary>
        internal static InternalSessionTime Realtime => default;

        /// <summary>Gets the world's scale: zero is frozen, one is real time.</summary>
        internal float Scale => specified ? scale : 1f;

        /// <summary>Gets what holds the world at <see cref="Scale"/>.</summary>
        internal InternalTimeMode Mode { get; }

        /// <summary>Gets whether the world is frozen.</summary>
        internal bool IsFrozen => Scale <= 0f;

        /// <summary>
        /// Gets whether <paramref name="scale"/> and <paramref name="mode"/> make a session time: the scale is finite and from
        /// zero to one, as Chronos bounds a slow, and the mode agrees with it, as Chronos derives it. Real time is scale one, a
        /// pause is scale zero, a slow is below one, and turn-based play may run at any scale in between.
        /// </summary>
        internal static bool IsValid(float scale, InternalTimeMode mode)
        {
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0f || scale > 1f) return false;
            switch (mode)
            {
                case InternalTimeMode.Realtime:
                    return scale == 1f;
                case InternalTimeMode.Slowed:
                    return scale < 1f;
                case InternalTimeMode.Paused:
                    return scale == 0f;
                case InternalTimeMode.TurnBased:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Makes a session time when the pair is valid, without throwing.</summary>
        internal static bool TryCreate(float scale, InternalTimeMode mode, out InternalSessionTime time)
        {
            if (!IsValid(scale, mode))
            {
                time = default;
                return false;
            }

            time = new InternalSessionTime(scale, mode);
            return true;
        }

        public bool Equals(InternalSessionTime other) => Scale == other.Scale && Mode == other.Mode;

        public override bool Equals(object? obj) => obj is InternalSessionTime other && Equals(other);

        public override int GetHashCode() => (Scale.GetHashCode() * 397) ^ (int)Mode;

        public override string ToString() => Scale.ToString("0.###", CultureInfo.InvariantCulture) + " (" + Mode + ")";
    }
}
