using System;

namespace TopiaForge.Mods
{
    /// <summary>
    /// A Unity-free RGBA colour (each component 0..1) used by robot-agent visual overrides so the
    /// abstractions assembly never references <c>UnityEngine</c>. Framework mods convert to and from
    /// <c>UnityEngine.Color</c> on the implementation side.
    /// </summary>
    /// <remarks>
    /// Mirrors the role <see cref="Vec3"/> plays for positions: allocation-free, fixed shape, and
    /// self-documenting. <see cref="A"/> defaults to fully opaque when constructed without it.
    /// </remarks>
    public readonly struct RobotColor : IEquatable<RobotColor>
    {
        /// <summary>Creates a colour from its components (each expected in the 0..1 range).</summary>
        public RobotColor(float r, float g, float b, float a = 1f)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        /// <summary>The red component (0..1).</summary>
        public float R { get; }

        /// <summary>The green component (0..1).</summary>
        public float G { get; }

        /// <summary>The blue component (0..1).</summary>
        public float B { get; }

        /// <summary>The alpha component (0..1); 1 is fully opaque.</summary>
        public float A { get; }

        /// <summary>Opaque white.</summary>
        public static RobotColor White => new RobotColor(1f, 1f, 1f, 1f);

        /// <inheritdoc/>
        public bool Equals(RobotColor other)
        {
            return R.Equals(other.R) && G.Equals(other.G) && B.Equals(other.B) && A.Equals(other.A);
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            return obj is RobotColor other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = R.GetHashCode();
                hash = (hash * 397) ^ G.GetHashCode();
                hash = (hash * 397) ^ B.GetHashCode();
                hash = (hash * 397) ^ A.GetHashCode();
                return hash;
            }
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return "(" + R + ", " + G + ", " + B + ", " + A + ")";
        }
    }
}
