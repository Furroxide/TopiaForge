using System;
using System.Globalization;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Unity-free naming and formatting rules shared by explicit UI diagnostics: bounded text, the
    /// <c>#rrggbb</c> colour text of measured graphics, and the reserved tag ids that the safe
    /// renderer, the window chrome and the toast host publish. Lives in Core so the offline harness
    /// pins every rule without a Unity dependency.
    /// </summary>
    public static class TopiaForgeUiDiagnosticFormat
    {
        /// <summary>Maximum characters retained for any declared or measured diagnostic string.</summary>
        public const int MaxTextLength = 256;

        /// <summary>Surface id of process-wide toasts on the toast host owner.</summary>
        public const string ToastSurface = "$toast";

        /// <summary>Node id prefix of toast nodes; the suffix is a process-monotonic sequence.</summary>
        public const string ToastNodePrefix = "toast-";

        /// <summary>Node id of the window/fullscreen chrome close button on its surface.</summary>
        public const string CloseTag = "$close";

        /// <summary>Node id prefix of scroll containers created by the safe renderer.</summary>
        public const string ScrollTagPrefix = "$scroll-";

        /// <summary>Renderer control kind of a tagged scroll container.</summary>
        public const string ScrollKind = "scroll";

        /// <summary>Renderer control kind of a tagged toast.</summary>
        public const string ToastKind = "toast";

        /// <summary>Minimum alpha for an Image to count as a measured widget background.</summary>
        public const float MinimumBackgroundAlpha = 0.5f;

        /// <summary>Bounds a possibly null string to <see cref="MaxTextLength"/> characters.</summary>
        public static string Bound(string? value)
        {
            if (value == null) return string.Empty;
            return value.Length <= MaxTextLength ? value : value.Substring(0, MaxTextLength);
        }

        /// <summary>Tag of the <paramref name="index"/>-th scroll container, counting from 0 in render order.</summary>
        public static string ScrollTag(int index)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), "Scroll containers count from 0.");
            return ScrollTagPrefix + index.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Node id of the toast presented as the <paramref name="sequence"/>-th toast of the process.</summary>
        public static string ToastNode(long sequence)
        {
            if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence), "Toast sequences start at 1.");
            return ToastNodePrefix + sequence.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Formats normalized colour channels as lowercase <c>#rrggbb</c>; alpha is not encoded.</summary>
        public static string Hex(float red, float green, float blue)
        {
            return "#" + Channel(red) + Channel(green) + Channel(blue);
        }

        /// <summary>Whether an Image alpha is opaque enough to report as a background.</summary>
        public static bool IsOpaqueBackground(float alpha)
        {
            return !float.IsNaN(alpha) && alpha >= MinimumBackgroundAlpha;
        }

        /// <summary>Whether a value is a well-formed diagnostic colour: empty or lowercase <c>#rrggbb</c>.</summary>
        public static bool IsColorText(string? value)
        {
            if (value == null) return false;
            if (value.Length == 0) return true;
            if (value.Length != 7 || value[0] != '#') return false;
            for (var index = 1; index < value.Length; index++)
            {
                var character = value[index];
                var digit = character >= '0' && character <= '9';
                var lower = character >= 'a' && character <= 'f';
                if (!digit && !lower) return false;
            }

            return true;
        }

        /// <summary>Culture-invariant text of a measured slider value.</summary>
        public static string InvariantNumber(float value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Channel(float value)
        {
            if (float.IsNaN(value)) value = 0f;
            var clamped = value < 0f ? 0f : value > 1f ? 1f : value;
            var scaled = (int)Math.Round(clamped * 255.0, MidpointRounding.AwayFromZero);
            return scaled.ToString("x2", CultureInfo.InvariantCulture);
        }
    }
}
