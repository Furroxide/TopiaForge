using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Unity-free color value used by the Core token/scheme layer so that palette and
    /// contrast logic stays testable in the net8.0 test exe. Converted to
    /// UnityEngine.Color once, inside TopiaForgeResolvedTheme.
    /// </summary>
    public readonly struct TopiaForgeRgba : IEquatable<TopiaForgeRgba>
    {
        public readonly float R;
        public readonly float G;
        public readonly float B;
        public readonly float A;

        public TopiaForgeRgba(float r, float g, float b, float a = 1f)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        /// <summary>Creates a color from a 0xRRGGBB literal (exact byte values, no rounding drift).</summary>
        public static TopiaForgeRgba Hex(uint rgb, float alpha = 1f)
        {
            var r = (byte)((rgb >> 16) & 0xFF);
            var g = (byte)((rgb >> 8) & 0xFF);
            var b = (byte)(rgb & 0xFF);
            return new TopiaForgeRgba(r / 255f, g / 255f, b / 255f, alpha);
        }

        public TopiaForgeRgba WithAlpha(float alpha)
        {
            return new TopiaForgeRgba(R, G, B, alpha);
        }

        public static TopiaForgeRgba Lerp(TopiaForgeRgba from, TopiaForgeRgba to, float t)
        {
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return new TopiaForgeRgba(
                from.R + ((to.R - from.R) * t),
                from.G + ((to.G - from.G) * t),
                from.B + ((to.B - from.B) * t),
                from.A + ((to.A - from.A) * t));
        }

        public TopiaForgeRgba Scale(float factor)
        {
            return new TopiaForgeRgba(R * factor, G * factor, B * factor, A);
        }

        public bool Equals(TopiaForgeRgba other)
        {
            return R == other.R && G == other.G && B == other.B && A == other.A;
        }

        public override bool Equals(object? obj)
        {
            return obj is TopiaForgeRgba other && Equals(other);
        }

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

        public static bool operator ==(TopiaForgeRgba left, TopiaForgeRgba right) => left.Equals(right);

        public static bool operator !=(TopiaForgeRgba left, TopiaForgeRgba right) => !left.Equals(right);

        public override string ToString()
        {
            return FormattableString.Invariant($"TopiaForgeRgba({R:0.###}, {G:0.###}, {B:0.###}, {A:0.###})");
        }
    }
}
